// Accord Direct Show Library
// The Accord.NET Framework
// http://accord-framework.net
//
// Copyright © César Souza, 2009-2017
// cesarsouza at gmail.com
//
// AForge Direct Show Library
// AForge.NET framework
// http://www.aforgenet.com/framework/
//
// Copyright © AForge.NET, 2009-2013
// contacts@aforgenet.com
//
//    This library is free software; you can redistribute it and/or
//    modify it under the terms of the GNU Lesser General Public
//    License as published by the Free Software Foundation; either
//    version 2.1 of the License, or (at your option) any later version.
//
//    This library is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
//    Lesser General Public License for more details.
//
//    You should have received a copy of the GNU Lesser General Public
//    License along with this library; if not, write to the Free Software
//    Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA  02110-1301  USA
//

namespace Accord.Video.DirectShow
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.Threading;
    using System.Runtime.InteropServices;

    using Accord.Video;
    using Accord.Video.DirectShow.Internals;
    using Accord.Imaging;
    using System.ComponentModel;

    /// <summary>
    /// Video source for local video capture device (for example USB webcam).
    /// </summary>
    /// 
    /// <remarks>
    ///   <para>This video source class captures video data from local video capture device,
    ///   like USB web camera (or internal), frame grabber, capture board - anything which
    ///   supports <b>DirectShow</b> interface. For devices which has a shutter button or
    ///   support external software triggering, the class also allows to do snapshots. Both
    ///   video size and snapshot size can be configured.</para>
    /// </remarks>
    /// 
    /// <example>
    /// <code source="Unit Tests\Accord.Tests.Video\VideoCaptureDeviceTest.cs" region="doc_part_1" />
    /// <para>
    ///   The video_NewFrame event could have been defined as in any of the following examples:</para>
    /// <code source="Unit Tests\Accord.Tests.Video\VideoCaptureDeviceTest.cs" region="doc_part_2" />
    /// <code source="Unit Tests\Accord.Tests.Video\VideoCaptureDeviceTest.cs" region="doc_part_3" />
    /// </example>
    /// 
    public class VideoCaptureDevice : IVideoSource, IDisposable
    {
        // moniker string of video capture device
        private string deviceMoniker;
        // received frames count
        private int framesReceived;
        // recieved byte count
        private long bytesReceived;

        // pixel format
        private PixelFormat pixelFormat = PixelFormat.Format24bppRgb;

        // video and snapshot resolutions to set
        private VideoCapabilities videoResolution = null;
        private VideoCapabilities snapshotResolution = null;
        private int averageTimePerFrame = 0;

        // provide snapshots or not
        private bool provideSnapshots = false;

        private Thread thread = null;
        private bool shouldStop;

        private AutoResetEvent shouldWake = new AutoResetEvent(false);
        private AutoResetEvent hasFinished = new AutoResetEvent(false);

        private VideoCapabilities[] videoCapabilities;
        private VideoCapabilities[] snapshotCapabilities;

        private bool needToSetVideoInput = false;
        private bool needToSimulateTrigger = false;
        private bool needToDisplayPropertyPage = false;
        private bool needToDisplayCrossBarPropertyPage = false;
        private IntPtr parentWindowForPropertyPage = IntPtr.Zero;

        // video capture source object
        private object sourceObject = null;

        // time of starting the DirectX graph
        private DateTime startTime = new DateTime();

        // dummy object to lock for synchronization
        private object sync = new object();

        // flag specifying if IAMCrossbar interface is supported by the running graph/source object
        private bool? isCrossbarAvailable = null;

        private VideoInput[] crossbarVideoInputs = null;
        private VideoInput crossbarVideoInput = VideoInput.Default;

        // cache for video/snapshot capabilities and video inputs
        private static Dictionary<string, VideoCapabilities[]> cacheVideoCapabilities = new Dictionary<string, VideoCapabilities[]>();
        private static Dictionary<string, VideoCapabilities[]> cacheSnapshotCapabilities = new Dictionary<string, VideoCapabilities[]>();
        private static Dictionary<string, VideoInput[]> cacheCrossbarVideoInputs = new Dictionary<string, VideoInput[]>();

        /// <summary>
        /// Current video input of capture card.
        /// </summary>
        /// 
        /// <remarks><para>The property specifies video input to use for video devices like capture cards
        /// (those which provide crossbar configuration). List of available video inputs can be obtained
        /// from <see cref="AvailableCrossbarVideoInputs"/> property.</para>
        /// 
        /// <para>To check if the video device supports crossbar configuration, the <see cref="CheckIfCrossbarAvailable"/>
        /// method can be used.</para>
        /// 
        /// <para><note>This property can be set as before running video device, as while running it.</note></para>
        /// 
        /// <para>By default this property is set to <see cref="VideoInput.Default"/>, which means video input
        /// will not be set when running video device, but currently configured will be used. After video device
        /// is started this property will be updated anyway to tell current video input.</para>
        /// </remarks>
        /// 
        public VideoInput CrossbarVideoInput
        {
            get { return crossbarVideoInput; }
            set
            {
                needToSetVideoInput = true;
                crossbarVideoInput = value;
            }
        }

        /// <summary>
        /// Available inputs of the video capture card.
        /// </summary>
        /// 
        /// <remarks><para>The property provides list of video inputs for devices like video capture cards.
        /// Such devices usually provide several video inputs, which can be selected using crossbar.
        /// If video device represented by the object of this class supports crossbar, then this property
        /// will list all video inputs. However if it is a regular USB camera, for example, which does not
        /// provide crossbar configuration, the property will provide zero length array.</para>
        /// 
        /// <para>Video input to be used can be selected using <see cref="CrossbarVideoInput"/>. See also
        /// <see cref="DisplayCrossbarPropertyPage"/> method, which provides crossbar configuration dialog.</para>
        /// 
        /// <para><note>It is recomended not to call this property immediately after <see cref="Start"/> method, since
        /// device may not start yet and provide its information. It is better to call the property
        /// before starting device or a bit after (but not immediately after).</note></para>
        /// </remarks>
        /// 
        public VideoInput[] AvailableCrossbarVideoInputs
        {
            get
            {
                if (crossbarVideoInputs == null)
                {
                    lock (cacheCrossbarVideoInputs)
                    {
                        if ((!string.IsNullOrEmpty(deviceMoniker)) && (cacheCrossbarVideoInputs.ContainsKey(deviceMoniker)))
                        {
                            crossbarVideoInputs = cacheCrossbarVideoInputs[deviceMoniker];
                        }
                    }

                    if (crossbarVideoInputs == null)
                    {
                        if (!IsRunning)
                        {
                            // create graph without playing to collect available inputs
                            DoWork(runGraph: false);
                        }
                        else
                        {
                            for (int i = 0; i < 500 && crossbarVideoInputs == null; i++)
                                Thread.Sleep(10);
                        }
                    }
                }
                // don't return null even if capabilities are not provided for some reason
                return (crossbarVideoInputs != null) ? crossbarVideoInputs : new VideoInput[0];
            }
        }

        /// <summary>
        /// Specifies if snapshots should be provided or not.
        /// </summary>
        /// 
        /// <remarks><para>Some USB cameras/devices may have a shutter button, which may result into snapshot if it
        /// is pressed. So the property specifies if the video source will try providing snapshots or not - it will
        /// check if the camera supports providing still image snapshots. If camera supports snapshots and the property
        /// is set to <see langword="true"/>, then snapshots will be provided through <see cref="SnapshotFrame"/>
        /// event.</para>
        /// 
        /// <para>Check supported sizes of snapshots using <see cref="SnapshotCapabilities"/> property and set the
        /// desired size using <see cref="SnapshotResolution"/> property.</para>
        /// 
        /// <para><note>The property must be set before running the video source to take effect.</note></para>
        /// 
        /// <para>Default value of the property is set to <see langword="false"/>.</para>
        /// </remarks>
        ///
        public bool ProvideSnapshots
        {
            get { return provideSnapshots; }
            set { provideSnapshots = value; }
        }

        /// <summary>
        /// New frame event.
        /// </summary>
        /// 
        /// <remarks><para>Notifies clients about new available frame from video source.</para>
        /// 
        /// <para><note>Since video source may have multiple clients, each client is responsible for
        /// making a copy (cloning) of the passed video frame, because the video source disposes its
        /// own original copy after notifying of clients.</note></para>
        /// </remarks>
        /// 
        public event NewFrameEventHandler NewFrame;

        /// <summary>
        /// Snapshot frame event.
        /// </summary>
        /// 
        /// <remarks><para>Notifies clients about new available snapshot frame - the one which comes when
        /// camera's snapshot/shutter button is pressed.</para>
        /// 
        /// <para>See documentation to <see cref="ProvideSnapshots"/> for additional information.</para>
        /// 
        /// <para><note>Since video source may have multiple clients, each client is responsible for
        /// making a copy (cloning) of the passed snapshot frame, because the video source disposes its
        /// own original copy after notifying of clients.</note></para>
        /// </remarks>
        /// 
        /// <seealso cref="ProvideSnapshots"/>
        /// 
        public event NewFrameEventHandler SnapshotFrame;

        /// <summary>
        /// Video source error event.
        /// </summary>
        /// 
        /// <remarks>This event is used to notify clients about any type of errors occurred in
        /// video source object, for example internal exceptions.</remarks>
        /// 
        public event VideoSourceErrorEventHandler VideoSourceError;

        /// <summary>
        /// Video playing finished event.
        /// </summary>
        /// 
        /// <remarks><para>This event is used to notify clients that the video playing has finished.</para>
        /// </remarks>
        /// 
        public event PlayingFinishedEventHandler PlayingFinished;

        /// <summary>
        /// Video source.
        /// </summary>
        /// 
        /// <remarks>Video source is represented by moniker string of video capture device.</remarks>
        /// 
        public virtual string Source
        {
            get { return deviceMoniker; }
            set
            {
                deviceMoniker = value;

                videoCapabilities = null;
                snapshotCapabilities = null;
                crossbarVideoInputs = null;
                isCrossbarAvailable = null;
            }
        }

        /// <summary>
        /// Received frames count.
        /// </summary>
        /// 
        /// <remarks>Number of frames the video source provided from the moment of the last
        /// access to the property.
        /// </remarks>
        /// 
        public int FramesReceived
        {
            get
            {
                int frames = framesReceived;
                framesReceived = 0;
                return frames;
            }
        }

        /// <summary>
        /// Received bytes count.
        /// </summary>
        /// 
        /// <remarks>Number of bytes the video source provided from the moment of the last
        /// access to the property.
        /// </remarks>
        /// 
        public long BytesReceived
        {
            get
            {
                long bytes = bytesReceived;
                bytesReceived = 0;
                return bytes;
            }
        }

        /// <summary>
        /// State of the video source.
        /// </summary>
        /// 
        /// <remarks>Current state of video source object - running or not.</remarks>
        /// 
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Obsolete - no longer in use
        /// </summary>
        /// 
        /// <remarks><para>The property is obsolete. Use <see cref="VideoResolution"/> property instead.
        /// Setting this property does not have any effect.</para></remarks>
        /// 
        [Obsolete]
        public Size DesiredFrameSize
        {
            get { return Size.Empty; }
            set { }
        }

        /// <summary>
        /// Obsolete - no longer in use
        /// </summary>
        /// 
        /// <remarks><para>The property is obsolete. Use <see cref="SnapshotResolution"/> property instead.
        /// Setting this property does not have any effect.</para></remarks>
        /// 
        [Obsolete]
        public Size DesiredSnapshotSize
        {
            get { return Size.Empty; }
            set { }
        }

        /// <summary>
        /// Obsolete - no longer in use.
        /// </summary>
        /// 
        /// <remarks><para>The property is obsolete. Setting this property does not have any effect.</para></remarks>
        /// 
        [Obsolete]
        public int DesiredFrameRate
        {
            get { return 0; }
            set { }
        }

        /// <summary>
        /// Gets a value indicating whether this instance has been disposed.
        /// </summary>
        /// <value><c>true</c> if this instance is disposed; otherwise, <c>false</c>.</value>
        public bool IsDisposed { get { return disposedValue; } }

        /// <summary>
        ///   The desired average display time of the video frames, in 100-nanosecond units.
        ///   There is no guarantee that the device will actually respect this setting, however
        ///   some devices will not work unless this property is set. See remarks for details.
        /// </summary>
        /// 
        /// <remarks>
        /// <para>
        ///   This property controls the initialization of the AvgTimePerFrame member of the 
        ///   <a href="https://msdn.microsoft.com/en-us/library/windows/desktop/dd407325(v=vs.85).aspx">
        ///   DirectShow's VideoInfoHeader structure</a>. In normal situations this property
        ///   does not need to be set, but some combinations of hardware and DirectShow filters
        ///   might require this property to be set to 0 to achieve a good frame rate. If this
        ///   property is set to -1, the AvgTimePerFrame member will not be initialized at all.</para>  
        ///   
        /// <para>
        ///   The default value for this property is 0 (AvgTimePerFrame will be initialized with 0).</para>
        /// </remarks>
        /// 
        public int DesiredAverageTimePerFrame
        {
            get { return averageTimePerFrame; }
            set { averageTimePerFrame = value; }
        }

        /// <summary>
        /// Video resolution to set.
        /// </summary>
        /// 
        /// <remarks><para>The property allows to set one of the video resolutions supported by the camera.
        /// Use <see cref="VideoCapabilities"/> property to get the list of supported video resolutions.</para>
        /// 
        /// <para><note>The property must be set before camera is started to make any effect.</note></para>
        /// 
        /// <para>Default value of the property is set to <see langword="null"/>, which means default video
        /// resolution is used.</para>
        /// </remarks>
        /// 
        public VideoCapabilities VideoResolution
        {
            get { return videoResolution; }
            set { videoResolution = value; }
        }

        /// <summary>
        /// Snapshot resolution to set.
        /// </summary>
        /// 
        /// <remarks><para>The property allows to set one of the snapshot resolutions supported by the camera.
        /// Use <see cref="SnapshotCapabilities"/> property to get the list of supported snapshot resolutions.</para>
        /// 
        /// <para><note>The property must be set before camera is started to make any effect.</note></para>
        /// 
        /// <para>Default value of the property is set to <see langword="null"/>, which means default snapshot
        /// resolution is used.</para>
        /// </remarks>
        /// 
        public VideoCapabilities SnapshotResolution
        {
            get { return snapshotResolution; }
            set { snapshotResolution = value; }
        }

        /// <summary>
        /// Video capabilities of the device.
        /// </summary>
        /// 
        /// <remarks><para>The property provides list of device's video capabilities.</para>
        /// 
        /// <para><note>It is recomended not to call this property immediately after <see cref="Start"/> method, since
        /// device may not start yet and provide its information. It is better to call the property
        /// before starting device or a bit after (but not immediately after).</note></para>
        /// </remarks>
        /// 
        public VideoCapabilities[] VideoCapabilities
        {
            get
            {
                if (videoCapabilities == null)
                {
                    lock (cacheVideoCapabilities)
                    {
                        if ((!string.IsNullOrEmpty(deviceMoniker)) && (cacheVideoCapabilities.ContainsKey(deviceMoniker)))
                            videoCapabilities = cacheVideoCapabilities[deviceMoniker];
                    }

                    if (videoCapabilities == null)
                    {
                        if (!IsRunning)
                        {
                            // create graph without playing to get the video/snapshot capabilities only.
                            // not very clean but it works
                            DoWork(runGraph: false);
                        }
                        else
                        {
                            for (int i = 0; i < 500 && videoCapabilities == null; i++)
                                Thread.Sleep(10);
                        }
                    }
                }

                // don't return null even capabilities are not provided for some reason
                return (videoCapabilities != null) ? videoCapabilities : new VideoCapabilities[0];
            }
        }

        /// <summary>
        /// Snapshot capabilities of the device.
        /// </summary>
        /// 
        /// <remarks><para>The property provides list of device's snapshot capabilities.</para>
        /// 
        /// <para>If the array has zero length, then it means that this device does not support making
        /// snapshots.</para>
        /// 
        /// <para>See documentation to <see cref="ProvideSnapshots"/> for additional information.</para>
        /// 
        /// <para><note>It is recomended not to call this property immediately after <see cref="Start"/> method, since
        /// device may not start yet and provide its information. It is better to call the property
        /// before starting device or a bit after (but not immediately after).</note></para>
        /// </remarks>
        /// 
        /// <seealso cref="ProvideSnapshots"/>
        /// 
        public VideoCapabilities[] SnapshotCapabilities
        {
            get
            {
                if (snapshotCapabilities == null)
                {
                    lock (cacheSnapshotCapabilities)
                    {
                        if (!String.IsNullOrEmpty(deviceMoniker) && cacheSnapshotCapabilities.ContainsKey(deviceMoniker))
                            snapshotCapabilities = cacheSnapshotCapabilities[deviceMoniker];
                    }

                    if (snapshotCapabilities == null)
                    {
                        if (!IsRunning)
                        {
                            // create graph without playing to get the video/snapshot capabilities only.
                            // not very clean but it works
                            DoWork(runGraph: false);
                        }
                        else
                        {
                            for (int i = 0; i < 500 && snapshotCapabilities == null; i++)
                                Thread.Sleep(10);
                        }
                    }
                }
                // don't return null even capabilities are not provided for some reason
                return (snapshotCapabilities != null) ? snapshotCapabilities : new VideoCapabilities[0];
            }
        }

        /// <summary>
        /// Source COM object of camera capture device.
        /// </summary>
        /// 
        /// <remarks><para>The source COM object of camera capture device is exposed for the
        /// case when user may need get direct access to the object for making some custom
        /// configuration of camera through DirectShow interface, for example.
        /// </para>
        /// 
        /// <para>If camera is not running, the property is set to <see langword="null"/>.</para>
        /// </remarks>
        /// 
        public object SourceObject
        {
            get { return sourceObject; }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoCaptureDevice"/> class.
        /// </summary>
        /// 
        public VideoCaptureDevice() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoCaptureDevice"/> class.
        /// </summary>
        /// 
        /// <param name="deviceMoniker">Moniker string of video capture device.</param>
        /// 
        public VideoCaptureDevice(string deviceMoniker)
        {
            this.deviceMoniker = deviceMoniker;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoCaptureDevice"/> class.
        /// </summary>
        /// 
        /// <param name="deviceMoniker">Moniker string of video capture device.</param>
        /// <param name="pixelFormat">Pixel format of video.</param>
        /// 
        public VideoCaptureDevice(string deviceMoniker, PixelFormat pixelFormat)
        {
            this.deviceMoniker = deviceMoniker;
            this.pixelFormat = pixelFormat;
        }

        /// <summary>
        /// Start video source.
        /// </summary>
        /// 
        /// <remarks>Starts video source and return execution to caller. Video source
        /// object creates background thread and notifies about new frames with the
        /// help of <see cref="NewFrame"/> event.</remarks>
        /// 
        public void Start()
        {
            if (IsRunning)
                throw new InvalidOperationException("The video capture device is already running.");

            if (IsDisposed)
                throw new ObjectDisposedException("The video capture device has already been disposed.");

            // check source
            if (String.IsNullOrEmpty(deviceMoniker))
                throw new ArgumentException("Video source is not specified.");

            framesReceived = 0;
            bytesReceived = 0;
            isCrossbarAvailable = null;
            needToSetVideoInput = true;
            shouldStop = false;

            lock (sync)
            {
                // create and start new thread
                if (thread == null)
                {
                    thread = new Thread(WorkerThread);
                    thread.Name = deviceMoniker; // mainly for debugging
                    thread.Start();
                }
                else
                {
                    this.shouldWake.Set();
                }
            }
        }

        /// <summary>
        /// Signal video source to stop its work.
        /// </summary>
        /// 
        /// <remarks>Signals video source to stop its background thread, stop to
        /// provide new frames and free resources.</remarks>
        /// 
        public void SignalToStop()
        {
            if (IsRunning)
            {
                // signal to stop
                this.shouldStop = true;
            }
        }

        /// <summary>
        /// Wait for video source has stopped.
        /// </summary>
        /// 
        /// <remarks>Waits for source stopping after it was signalled to stop using
        /// <see cref="SignalToStop"/> method.</remarks>
        /// 
        public void WaitForStop()
        {
            if (IsRunning)
            {
                // wait until the thread signals it has stopped
                this.hasFinished.WaitOne();
            }
        }

        /// <summary>
        /// Stop video source.
        /// </summary>
        /// 
        /// <remarks><para>Stops video source aborting its thread.</para>
        /// 
        /// <para><note>Since the method aborts background thread, its usage is highly not preferred
        /// and should be done only if there are no other options. The correct way of stopping camera
        /// is <see cref="SignalToStop">signaling it stop</see> and then
        /// <see cref="WaitForStop">waiting</see> for background thread's completion.</note></para>
        /// </remarks>
        /// 
        public void Stop()
        {
            if (thread != null)
            {
                thread.Abort();
                thread.Join();
                thread = null;
            }
        }

        /// <summary>
        /// Display property window for the video capture device providing its configuration
        /// capabilities.
        /// </summary>
        /// 
        /// <param name="parentWindow">Handle of parent window.</param>
        /// 
        /// <remarks><para><note>If you pass parent window's handle to this method, then the
        /// displayed property page will become modal window and none of the controls from the
        /// parent window will be accessible. In order to make it modeless it is required
        /// to pass <see cref="IntPtr.Zero"/> as parent window's handle.
        /// </note></para>
        /// </remarks>
        /// 
        /// <exception cref="NotSupportedException">The video source does not support configuration property page.</exception>
        /// 
        public void DisplayPropertyPage(IntPtr parentWindow)
        {
            // check source
            if (String.IsNullOrEmpty(deviceMoniker))
                throw new ArgumentException("Video source is not specified.");

            lock (sync)
            {
                if (IsRunning)
                {
                    // pass the request to backgroud thread if video source is running
                    parentWindowForPropertyPage = parentWindow;
                    needToDisplayPropertyPage = true;
                    return;
                }

                object tempSourceObject = null;

                // create source device's object
                try
                {
                    tempSourceObject = FilterInfo.CreateFilter(deviceMoniker);
                }
                catch
                {
                    throw new ApplicationException("Failed creating device object for moniker.");
                }

                if (!(tempSourceObject is ISpecifyPropertyPages))
                    throw new NotSupportedException("The video source does not support configuration property page.");

                DisplayPropertyPage(parentWindow, tempSourceObject);

                release(ref tempSourceObject);
            }
        }

        /// <summary>
        /// Display property page of video crossbar (Analog Video Crossbar filter).
        /// </summary>
        /// 
        /// <param name="parentWindow">Handle of parent window.</param>
        /// 
        /// <remarks><para>The Analog Video Crossbar filter is modeled after a general switching matrix,
        /// with n inputs and m outputs. For example, a video card might have two external connectors:
        /// a coaxial connector for TV, and an S-video input. These would be represented as input pins on
        /// the filter. The displayed property page allows to configure the crossbar by selecting input
        /// of a video card to use.</para>
        /// 
        /// <para><note>This method can be invoked only when video source is running (<see cref="IsRunning"/> is
        /// <see langword="true"/>). Otherwise it generates exception.</note></para>
        /// 
        /// <para>Use <see cref="CheckIfCrossbarAvailable"/> method to check if running video source provides
        /// crossbar configuration.</para>
        /// </remarks>
        /// 
        /// <exception cref="ApplicationException">The video source must be running in order to display crossbar property page.</exception>
        /// <exception cref="NotSupportedException">Crossbar configuration is not supported by currently running video source.</exception>
        /// 
        public void DisplayCrossbarPropertyPage(IntPtr parentWindow)
        {
            lock (sync)
            {
                // wait max 5 seconds till the flag gets initialized
                for (int i = 0; i < 500 && !isCrossbarAvailable.HasValue && IsRunning; i++)
                    Thread.Sleep(10);

                if (!IsRunning || !isCrossbarAvailable.HasValue)
                    throw new ApplicationException("The video source must be running in order to display crossbar property page.");

                if (!isCrossbarAvailable.Value)
                    throw new NotSupportedException("Crossbar configuration is not supported by currently running video source.");

                // pass the request to background thread if video source is running
                parentWindowForPropertyPage = parentWindow;
                needToDisplayCrossBarPropertyPage = true;
            }
        }

        /// <summary>
        /// Check if running video source provides crossbar for configuration.
        /// </summary>
        /// 
        /// <returns>Returns <see langword="true"/> if crossbar configuration is available or
        /// <see langword="false"/> otherwise.</returns>
        /// 
        /// <remarks><para>The method reports if the video source provides crossbar configuration
        /// using <see cref="DisplayCrossbarPropertyPage"/>.</para>
        /// </remarks>
        ///
        public bool CheckIfCrossbarAvailable()
        {
            lock (sync)
            {
                if (!isCrossbarAvailable.HasValue)
                {
                    if (!IsRunning)
                    {
                        // create graph without playing to collect available inputs
                        DoWork(runGraph: false);
                    }
                    else
                    {
                        for (int i = 0; i < 500 && !isCrossbarAvailable.HasValue; i++)
                            Thread.Sleep(10);
                    }
                }

                return (!isCrossbarAvailable.HasValue) ? false : isCrossbarAvailable.Value;
            }
        }


        /// <summary>
        /// Simulates an external trigger.
        /// </summary>
        /// 
        /// <remarks><para>The method simulates external trigger for video cameras, which support
        /// providing still image snapshots. The effect is equivalent as pressing camera's shutter
        /// button - a snapshot will be provided through <see cref="SnapshotFrame"/> event.</para>
        /// 
        /// <para><note>The <see cref="ProvideSnapshots"/> property must be set to <see langword="true"/>
        /// to enable receiving snapshots.</note></para>
        /// </remarks>
        /// 
        public void SimulateTrigger()
        {
            needToSimulateTrigger = true;
        }

        /// <summary>
        /// Sets a specified property on the camera.
        /// </summary>
        /// 
        /// <param name="property">Specifies the property to set.</param>
        /// <param name="value">Specifies the new value of the property.</param>
        /// <param name="controlFlags">Specifies the desired control setting.</param>
        /// 
        /// <returns>Returns true on sucee or false otherwise.</returns>
        /// 
        /// <exception cref="ArgumentException">Video source is not specified - device moniker is not set.</exception>
        /// <exception cref="ApplicationException">Failed creating device object for moniker.</exception>
        /// <exception cref="NotSupportedException">The video source does not support camera control.</exception>
        /// 
        public bool SetCameraProperty(CameraControlProperty property, int value, CameraControlFlags controlFlags)
        {
            bool ret = true;

            // check if source was set
            if (String.IsNullOrEmpty(deviceMoniker))
                throw new ArgumentException("Video source is not specified.");

            lock (sync)
            {
                object tempSourceObject = null;

                // create source device's object
                try
                {
                    tempSourceObject = FilterInfo.CreateFilter(deviceMoniker);
                }
                catch
                {
                    throw new ApplicationException("Failed creating device object for moniker.");
                }

                if (!(tempSourceObject is IAMCameraControl))
                    throw new NotSupportedException("The video source does not support camera control.");

                IAMCameraControl pCamControl = (IAMCameraControl)tempSourceObject;
                int hr = pCamControl.Set(property, value, controlFlags);

                ret = (hr >= 0);

                release(ref tempSourceObject);
            }

            return ret;
        }

        /// <summary>
        /// Gets the current setting of a camera property.
        /// </summary>
        /// 
        /// <param name="property">Specifies the property to retrieve.</param>
        /// <param name="value">Receives the value of the property.</param>
        /// <param name="controlFlags">Receives the value indicating whether the setting is controlled manually or automatically</param>
        /// 
        /// <returns>Returns true on sucee or false otherwise.</returns>
        /// 
        /// <exception cref="ArgumentException">Video source is not specified - device moniker is not set.</exception>
        /// <exception cref="ApplicationException">Failed creating device object for moniker.</exception>
        /// <exception cref="NotSupportedException">The video source does not support camera control.</exception>
        /// 
        public bool GetCameraProperty(CameraControlProperty property, out int value, out CameraControlFlags controlFlags)
        {
            bool ret = true;

            // check if source was set
            if (String.IsNullOrEmpty(deviceMoniker))
                throw new ArgumentException("Video source is not specified.");

            lock (sync)
            {
                object tempSourceObject = null;

                // create source device's object
                try
                {
                    tempSourceObject = FilterInfo.CreateFilter(deviceMoniker);
                }
                catch
                {
                    throw new ApplicationException("Failed creating device object for moniker.");
                }

                if (!(tempSourceObject is IAMCameraControl))
                    throw new NotSupportedException("The video source does not support camera control.");

                IAMCameraControl pCamControl = (IAMCameraControl)tempSourceObject;
                int hr = pCamControl.Get(property, out value, out controlFlags);

                ret = (hr >= 0);

                release(ref tempSourceObject);
            }

            return ret;
        }

        /// <summary>
        /// Gets the range and default value of a specified camera property.
        /// </summary>
        /// 
        /// <param name="property">Specifies the property to query.</param>
        /// <param name="minValue">Receives the minimum value of the property.</param>
        /// <param name="maxValue">Receives the maximum value of the property.</param>
        /// <param name="stepSize">Receives the step size for the property.</param>
        /// <param name="defaultValue">Receives the default value of the property.</param>
        /// <param name="controlFlags">Receives a member of the <see cref="CameraControlFlags"/> enumeration, indicating whether the property is controlled automatically or manually.</param>
        /// 
        /// <returns>Returns true on sucee or false otherwise.</returns>
        /// 
        /// <exception cref="ArgumentException">Video source is not specified - device moniker is not set.</exception>
        /// <exception cref="ApplicationException">Failed creating device object for moniker.</exception>
        /// <exception cref="NotSupportedException">The video source does not support camera control.</exception>
        /// 
        public bool GetCameraPropertyRange(CameraControlProperty property, out int minValue, out int maxValue, out int stepSize, out int defaultValue, out CameraControlFlags controlFlags)
        {
            bool ret = true;

            // check if source was set
            if (String.IsNullOrEmpty(deviceMoniker))
                throw new ArgumentException("Video source is not specified.");

            lock (sync)
            {
                object tempSourceObject = null;

                // create source device's object
                try
                {
                    tempSourceObject = FilterInfo.CreateFilter(deviceMoniker);
                }
                catch
                {
                    throw new ApplicationException("Failed creating device object for moniker.");
                }

                if (!(tempSourceObject is IAMCameraControl))
                    throw new NotSupportedException("The video source does not support camera control.");

                IAMCameraControl pCamControl = (IAMCameraControl)tempSourceObject;
                int hr = pCamControl.GetRange(property, out minValue, out maxValue, out stepSize, out defaultValue, out controlFlags);

                ret = (hr >= 0);

                release(ref tempSourceObject);
            }

            return ret;
        }

        /// <summary>
        /// Worker thread.
        /// </summary>
        /// 
        private void WorkerThread()
        {
            do
            {
                if (this.disposedValue)
                    return;

                try
                {
                    this.IsRunning = true;
                    DoWork(true);
                }
                finally
                {
                    this.IsRunning = false;
                    this.hasFinished.Set();
                }
            } while (shouldWake.WaitOne());
        }

        private void DoWork(bool runGraph)
        {
            var reasonToStop = ReasonToFinishPlaying.StoppedByUser;
            bool isSnapshotSupported = false;

            // objects
            object captureGraphObject = null;
            object graphObject = null;
            object videoGrabberObject = null;
            object snapshotGrabberObject = null;
            object crossbarObject = null;

            // interfaces
            ICaptureGraphBuilder2 captureGraph = null;
            IFilterGraph2 graph = null;
            IBaseFilter sourceBase = null;
            IBaseFilter videoGrabberBase = null;
            IBaseFilter snapshotGrabberBase = null;
            ISampleGrabber videoSampleGrabber = null;
            ISampleGrabber snapshotSampleGrabber = null;
            IMediaControl mediaControl = null;
            IAMVideoControl videoControl = null;
            IMediaEventEx mediaEvent = null;
            IPin pinStillImage = null;
            IAMCrossbar crossbar = null;

            try
            {
                // get type of capture graph builder
                Type type = Type.GetTypeFromCLSID(Clsid.CaptureGraphBuilder2);
                if (type == null)
                    throw new ApplicationException("Failed creating capture graph builder");

                // create capture graph builder
                captureGraphObject = Activator.CreateInstance(type);
                captureGraph = (ICaptureGraphBuilder2)captureGraphObject;

                // get type of filter graph
                type = Type.GetTypeFromCLSID(Clsid.FilterGraph);
                if (type == null)
                    throw new ApplicationException("Failed creating filter graph");

                // create filter graph
                graphObject = Activator.CreateInstance(type);
                graph = (IFilterGraph2)graphObject;

                // set filter graph to the capture graph builder
                captureGraph.SetFiltergraph((IGraphBuilder)graph);

                // create source device's object
                sourceObject = FilterInfo.CreateFilter(deviceMoniker);
                if (sourceObject == null)
                    throw new ApplicationException("Failed creating device object for moniker");

                // get base filter interface of source device
                sourceBase = (IBaseFilter)sourceObject;

                // get video control interface of the device
                try
                {
                    videoControl = (IAMVideoControl)sourceObject;
                }
                catch
                {
                    // some camera drivers may not support IAMVideoControl interface
                }

                // get type of sample grabber
                type = Type.GetTypeFromCLSID(Clsid.SampleGrabber);
                if (type == null)
                    throw new ApplicationException("Failed creating sample grabber");

                // create sample grabber used for video capture
                videoGrabberObject = Activator.CreateInstance(type);
                videoSampleGrabber = (ISampleGrabber)videoGrabberObject;
                videoGrabberBase = (IBaseFilter)videoGrabberObject;
                // create sample grabber used for snapshot capture
                snapshotGrabberObject = Activator.CreateInstance(type);
                snapshotSampleGrabber = (ISampleGrabber)snapshotGrabberObject;
                snapshotGrabberBase = (IBaseFilter)snapshotGrabberObject;

                // add source and grabber filters to graph
                graph.AddFilter(sourceBase, "source");
                graph.AddFilter(videoGrabberBase, "grabber_video");
                graph.AddFilter(snapshotGrabberBase, "grabber_snapshot");

                // set media type
                using (AMMediaType mediaType = new AMMediaType())
                {
                    mediaType.MajorType = MediaType.Video;
                    mediaType.SubType = MediaSubType.ConvertFrom(pixelFormat);

                    videoSampleGrabber.SetMediaType(mediaType);
                    snapshotSampleGrabber.SetMediaType(mediaType);

                    // get crossbar object to to allows configuring pins of capture card
                    captureGraph.FindInterface(FindDirection.UpstreamOnly, Guid.Empty, sourceBase, typeof(IAMCrossbar).GUID, out crossbarObject);
                    if (crossbarObject != null)
                        crossbar = (IAMCrossbar)crossbarObject;
                    isCrossbarAvailable = (crossbar != null);
                    crossbarVideoInputs = CollectCrossbarVideoInputs(crossbar);

                    if (videoControl != null)
                    {
                        // find Still Image output pin of the video device
                        captureGraph.FindPin(sourceObject, PinDirection.Output,
                            PinCategory.StillImage, MediaType.Video, false, 0, out pinStillImage);
                        // check if it support trigger mode
                        if (pinStillImage != null)
                        {
                            VideoControlFlags caps;
                            videoControl.GetCaps(pinStillImage, out caps);
                            isSnapshotSupported = (((caps & VideoControlFlags.ExternalTriggerEnable) != 0) ||
                                                   ((caps & VideoControlFlags.Trigger) != 0));
                        }
                    }

                    // grabber
                    using (Grabber videoGrabber = new Grabber(this, snapshotMode: false, pixelFormat: this.pixelFormat))
                    using (Grabber snapshotGrabber = new Grabber(this, snapshotMode: true, pixelFormat: this.pixelFormat))
                    {
                        // configure video sample grabber
                        videoSampleGrabber.SetBufferSamples(false);
                        videoSampleGrabber.SetOneShot(false);
                        videoSampleGrabber.SetCallback(videoGrabber, 1);

                        // configure snapshot sample grabber
                        snapshotSampleGrabber.SetBufferSamples(true);
                        snapshotSampleGrabber.SetOneShot(false);
                        snapshotSampleGrabber.SetCallback(snapshotGrabber, 1);

                        // configure pins
                        GetPinCapabilitiesAndConfigureSizeAndRate(captureGraph, sourceBase,
                            PinCategory.Capture, videoResolution, ref videoCapabilities);

                        if (isSnapshotSupported)
                        {
                            GetPinCapabilitiesAndConfigureSizeAndRate(captureGraph, sourceBase,
                                PinCategory.StillImage, snapshotResolution, ref snapshotCapabilities);
                        }
                        else
                        {
                            snapshotCapabilities = new VideoCapabilities[0];
                        }

                        // put video/snapshot capabilities into cache
                        lock (cacheVideoCapabilities)
                        {
                            if (videoCapabilities != null && !cacheVideoCapabilities.ContainsKey(deviceMoniker))
                                cacheVideoCapabilities.Add(deviceMoniker, videoCapabilities);
                        }

                        lock (cacheSnapshotCapabilities)
                        {
                            if (snapshotCapabilities != null && !cacheSnapshotCapabilities.ContainsKey(deviceMoniker))
                                cacheSnapshotCapabilities.Add(deviceMoniker, snapshotCapabilities);
                        }

                        if (runGraph)
                        {
                            // render capture pin
                            captureGraph.RenderStream(PinCategory.Capture, MediaType.Video, sourceBase, null, videoGrabberBase);

                            if (videoSampleGrabber.GetConnectedMediaType(mediaType) == 0)
                            {
                                VideoInfoHeader vih = (VideoInfoHeader)Marshal.PtrToStructure(mediaType.FormatPtr, typeof(VideoInfoHeader));
                                videoGrabber.Width = vih.BmiHeader.Width;
                                videoGrabber.Height = vih.BmiHeader.Height;
                            }

                            if (isSnapshotSupported && provideSnapshots)
                            {
                                // render snapshot pin
                                captureGraph.RenderStream(PinCategory.StillImage, MediaType.Video, sourceBase, null, snapshotGrabberBase);

                                if (snapshotSampleGrabber.GetConnectedMediaType(mediaType) == 0)
                                {
                                    VideoInfoHeader vih = (VideoInfoHeader)Marshal.PtrToStructure(mediaType.FormatPtr, typeof(VideoInfoHeader));
                                    snapshotGrabber.Width = vih.BmiHeader.Width;
                                    snapshotGrabber.Height = vih.BmiHeader.Height;
                                }
                            }
                        }

                        // get media control
                        mediaControl = (IMediaControl)graphObject;

                        // get media events' interface
                        mediaEvent = (IMediaEventEx)graphObject;

                        IntPtr p1, p2;
                        DsEvCode code;

                        // run
                        mediaControl.Run();

                        if (isSnapshotSupported && provideSnapshots)
                        {
                            startTime = DateTime.Now;
                            videoControl.SetMode(pinStillImage, VideoControlFlags.ExternalTriggerEnable);
                        }

                        do
                        {
                            if (mediaEvent != null)
                            {
                                if (mediaEvent.GetEvent(out code, out p1, out p2, 0) >= 0)
                                {
                                    mediaEvent.FreeEventParams(code, p1, p2);

                                    if (code == DsEvCode.DeviceLost)
                                    {
                                        reasonToStop = ReasonToFinishPlaying.DeviceLost;
                                        break;
                                    }
                                }
                            }

                            if (needToSetVideoInput)
                            {
                                needToSetVideoInput = false;
                                // set/check current input type of a video card (frame grabber)
                                if (isCrossbarAvailable.Value)
                                {
                                    SetCurrentCrossbarInput(crossbar, crossbarVideoInput);
                                    crossbarVideoInput = GetCurrentCrossbarInput(crossbar);
                                }
                            }

                            if (needToSimulateTrigger)
                            {
                                needToSimulateTrigger = false;

                                if (isSnapshotSupported && provideSnapshots)
                                    videoControl.SetMode(pinStillImage, VideoControlFlags.Trigger);
                            }

                            if (needToDisplayPropertyPage)
                            {
                                needToDisplayPropertyPage = false;
                                DisplayPropertyPage(parentWindowForPropertyPage, sourceObject);

                                if (crossbar != null)
                                    crossbarVideoInput = GetCurrentCrossbarInput(crossbar);
                            }

                            if (needToDisplayCrossBarPropertyPage)
                            {
                                needToDisplayCrossBarPropertyPage = false;

                                if (crossbar != null)
                                {
                                    DisplayPropertyPage(parentWindowForPropertyPage, crossbar);
                                    crossbarVideoInput = GetCurrentCrossbarInput(crossbar);
                                }
                            }
                        }
                        while (!shouldStop && runGraph);

                        mediaControl.Stop();
                    }
                }
            }
            catch (ThreadAbortException)
            {

            }
            catch (Exception exception)
            {
                // provide information to clients
                if (VideoSourceError != null)
                    VideoSourceError(this, new VideoSourceErrorEventArgs(exception.Message));
                else throw;
            }
            finally
            {
                // release all objects
                captureGraph = null;
                graph = null;
                sourceBase = null;
                mediaControl = null;
                videoControl = null;
                mediaEvent = null;
                pinStillImage = null;
                crossbar = null;

                videoGrabberBase = null;
                snapshotGrabberBase = null;
                videoSampleGrabber = null;
                snapshotSampleGrabber = null;

                release(ref graphObject);
                release(ref sourceObject);
                release(ref videoGrabberObject);
                release(ref snapshotGrabberObject);
                release(ref captureGraphObject);
                release(ref crossbarObject);

#if !NET35
                Marshal.CleanupUnusedObjectsInCurrentContext();
#endif
            }

            if (PlayingFinished != null)
                PlayingFinished(this, reasonToStop);
        }

        private static void release(ref object obj)
        {
            if (obj != null)
            {
                Marshal.FinalReleaseComObject(obj);
                obj = null;
            }
        }

        // Set resolution for the specified stream configuration
        private void SetResolution(IAMStreamConfig streamConfig, VideoCapabilities resolution = null)
        {
            // iterate through device's capabilities to find mediaType for desired resolution
            int capabilitiesCount = 0, capabilitySize = 0;
            AMMediaType newMediaType = null;
            var caps = new VideoStreamConfigCaps();

            streamConfig.GetNumberOfCapabilities(out capabilitiesCount, out capabilitySize);

            for (int i = 0; i < capabilitiesCount; i++)
            {
                try
                {
                    var vc = new VideoCapabilities(streamConfig, i);

                    if (resolution == vc || resolution == null)
                    {
                        if (streamConfig.GetStreamCaps(i, out newMediaType, caps) == 0)
                            break;
                    }
                }
                catch
                {
                }
            }

            // set the new format
            if (newMediaType != null)
            {
                if (averageTimePerFrame >= 0)
                {
                    unsafe
                    {
                        VideoInfoHeader* vih = (VideoInfoHeader*)newMediaType.FormatPtr;
                        vih->AverageTimePerFrame = averageTimePerFrame;
                    }
                }

                streamConfig.SetFormat(newMediaType);
                newMediaType.Dispose();
                newMediaType = null;
            }
        }

        // Configure specified pin and collect its capabilities if required
        private void GetPinCapabilitiesAndConfigureSizeAndRate(ICaptureGraphBuilder2 graphBuilder, IBaseFilter baseFilter,
            Guid pinCategory, VideoCapabilities resolutionToSet, ref VideoCapabilities[] capabilities)
        {
            object streamConfigObject;
            graphBuilder.FindInterface(pinCategory, MediaType.Video, baseFilter, typeof(IAMStreamConfig).GUID, out streamConfigObject);

            if (streamConfigObject != null)
            {
                IAMStreamConfig streamConfig = null;

                try
                {
                    streamConfig = (IAMStreamConfig)streamConfigObject;
                }
                catch (InvalidCastException)
                {
                }

                if (streamConfig != null)
                {
                    if (capabilities == null)
                    {
                        try
                        {
                            // get all video capabilities
                            capabilities = Accord.Video.DirectShow.VideoCapabilities.FromStreamConfig(streamConfig);
                        }
                        catch
                        {
                        }
                    }

                    // Call the SetResolution method even with a null resolution, 
                    // so that the first video mode will be used.
                    SetResolution(streamConfig, resolutionToSet);
                }

                release(ref streamConfigObject);
            }

            // if failed resolving capabilities, then just create empty capabilities array,
            // so we don't try again
            if (capabilities == null)
                capabilities = new VideoCapabilities[0];
        }

        // Display property page for the specified object
        private void DisplayPropertyPage(IntPtr parentWindow, object sourceObject)
        {
            var caGUID = new CAUUID();

            try
            {
                // retrieve ISpecifyPropertyPages interface of the device
                ISpecifyPropertyPages pPropPages = (ISpecifyPropertyPages)sourceObject;

                // get property pages from the property bag
                pPropPages.GetPages(out caGUID);

                // get filter info
                FilterInfo filterInfo = new FilterInfo(deviceMoniker);

                // create and display the OlePropertyFrame
                Win32.OleCreatePropertyFrame(parentWindow, 0, 0, filterInfo.Name, 1,
                    ref sourceObject, caGUID.cElems, caGUID.pElems, 0, 0, IntPtr.Zero);
            }
            catch
            {
            }
            finally
            {
                if (caGUID.pElems != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(caGUID.pElems);
            }
        }

        // Collect all video inputs of the specified crossbar
        private VideoInput[] CollectCrossbarVideoInputs(IAMCrossbar crossbar)
        {
            lock (cacheCrossbarVideoInputs)
            {
                if (cacheCrossbarVideoInputs.ContainsKey(deviceMoniker))
                    return cacheCrossbarVideoInputs[deviceMoniker];

                var videoInputsList = new List<VideoInput>();

                if (crossbar != null)
                {
                    int inPinsCount, outPinsCount;

                    // gen number of pins in the crossbar
                    if (crossbar.get_PinCounts(out outPinsCount, out inPinsCount) == 0)
                    {
                        // collect all video inputs
                        for (int i = 0; i < inPinsCount; i++)
                        {
                            int pinIndexRelated;
                            PhysicalConnectorType type;

                            if (crossbar.get_CrossbarPinInfo(true, i, out pinIndexRelated, out type) != 0)
                                continue;

                            if (type < PhysicalConnectorType.AudioTuner)
                                videoInputsList.Add(new VideoInput(i, type));
                        }
                    }
                }

                var videoInputs = videoInputsList.ToArray();
                cacheCrossbarVideoInputs.Add(deviceMoniker, videoInputs);
                return videoInputs;
            }
        }

        // Get type of input connected to video output of the crossbar
        private VideoInput GetCurrentCrossbarInput(IAMCrossbar crossbar)
        {
            VideoInput videoInput = VideoInput.Default;

            int inPinsCount, outPinsCount;

            // gen number of pins in the crossbar
            if (crossbar.get_PinCounts(out outPinsCount, out inPinsCount) == 0)
            {
                int videoOutputPinIndex = -1;
                int pinIndexRelated;
                PhysicalConnectorType type;

                // find index of the video output pin
                for (int i = 0; i < outPinsCount; i++)
                {
                    if (crossbar.get_CrossbarPinInfo(false, i, out pinIndexRelated, out type) != 0)
                        continue;

                    if (type == PhysicalConnectorType.VideoDecoder)
                    {
                        videoOutputPinIndex = i;
                        break;
                    }
                }

                if (videoOutputPinIndex != -1)
                {
                    int videoInputPinIndex;

                    // get index of the input pin connected to the output
                    if (crossbar.get_IsRoutedTo(videoOutputPinIndex, out videoInputPinIndex) == 0)
                    {
                        PhysicalConnectorType inputType;
                        crossbar.get_CrossbarPinInfo(true, videoInputPinIndex, out pinIndexRelated, out inputType);
                        videoInput = new VideoInput(videoInputPinIndex, inputType);
                    }
                }
            }

            return videoInput;
        }

        // Set type of input connected to video output of the crossbar
        private void SetCurrentCrossbarInput(IAMCrossbar crossbar, VideoInput videoInput)
        {
            if (videoInput.Type != PhysicalConnectorType.Default)
            {
                int inPinsCount, outPinsCount;

                // gen number of pins in the crossbar
                if (crossbar.get_PinCounts(out outPinsCount, out inPinsCount) == 0)
                {
                    int videoOutputPinIndex = -1;
                    int videoInputPinIndex = -1;
                    int pinIndexRelated;
                    PhysicalConnectorType type;

                    // find index of the video output pin
                    for (int i = 0; i < outPinsCount; i++)
                    {
                        if (crossbar.get_CrossbarPinInfo(false, i, out pinIndexRelated, out type) != 0)
                            continue;

                        if (type == PhysicalConnectorType.VideoDecoder)
                        {
                            videoOutputPinIndex = i;
                            break;
                        }
                    }

                    // find index of the required input pin
                    for (int i = 0; i < inPinsCount; i++)
                    {
                        if (crossbar.get_CrossbarPinInfo(true, i, out pinIndexRelated, out type) != 0)
                            continue;

                        if ((type == videoInput.Type) && (i == videoInput.Index))
                        {
                            videoInputPinIndex = i;
                            break;
                        }
                    }

                    // try connecting pins
                    if ((videoInputPinIndex != -1) && (videoOutputPinIndex != -1) &&
                         (crossbar.CanRoute(videoOutputPinIndex, videoInputPinIndex) == 0))
                        crossbar.Route(videoOutputPinIndex, videoInputPinIndex);
                }
            }
        }

        /// <summary>
        /// Notifies clients about new frame.
        /// </summary>
        /// 
        /// <param name="args">Event arguments containing the new frame's image.</param>
        /// 
        private void OnNewFrame(NewFrameEventArgs args)
        {
            framesReceived++;
            bytesReceived += args.Frame.Width * args.Frame.Height * (Bitmap.GetPixelFormatSize(args.Frame.PixelFormat) >> 3);

            if (NewFrame != null && !this.shouldStop)
                NewFrame(this, args);
        }

        /// <summary>
        /// Notifies clients about new snapshot frame.
        /// </summary>
        /// 
        /// <param name="args">Event arguments containing the new frame's image.</param>
        /// 
        private void OnSnapshotFrame(NewFrameEventArgs args)
        {
            TimeSpan timeSinceStarted = args.CaptureFinished - args.CaptureStarted;

            // TODO: need to find better way to ignore the first snapshot, 
            // which is sent automatically (or better disable it)
            if (timeSinceStarted.TotalSeconds >= 4)
            {
                if (SnapshotFrame != null && !this.shouldStop)
                    SnapshotFrame(this, args);
            }
        }

        //
        // Video grabber
        //
        private class Grabber : ISampleGrabberCB, IDisposable
        {
            private VideoCaptureDevice parent;
            private bool snapshotMode;
            private PixelFormat pixelFormat;
            private Bitmap image;
            private NewFrameEventArgs args;

            public int Width { get; set; }

            public int Height { get; set; }

            // Constructor
            public Grabber(VideoCaptureDevice parent, bool snapshotMode, PixelFormat pixelFormat = PixelFormat.Format24bppRgb)
            {
                this.parent = parent;
                this.snapshotMode = snapshotMode;
                this.pixelFormat = pixelFormat;
            }

            // Callback to receive samples
            public int SampleCB(double sampleTime, IntPtr sample)
            {
                return 0;
            }

            // Callback method that receives a pointer to the sample buffer
            public int BufferCB(double sampleTime, IntPtr buffer, int bufferLen)
            {
                if (parent.NewFrame != null)
                {
                    if (this.image == null)
                    {
                        // create new image
                        this.image = new Bitmap(Width, Height, this.pixelFormat);
                        this.args = new NewFrameEventArgs(this.image);
                    }

                    args.CaptureStarted = parent.startTime;
                    args.CaptureFinished = DateTime.Now;

                    // lock bitmap data
                    BitmapData imageData = image.LockBits(ImageLockMode.WriteOnly);

                    // copy image data
                    int srcStride = imageData.Stride;
                    int dstStride = imageData.Stride;

                    unsafe
                    {
                        byte* dst = (byte*)imageData.Scan0.ToPointer() + dstStride * (Height - 1);
                        byte* src = (byte*)buffer.ToPointer();

                        for (int y = 0; y < Height; y++)
                        {
                            Win32.memcpy(dst, src, srcStride);
                            dst -= dstStride;
                            src += srcStride;
                        }
                    }

                    // unlock bitmap data
                    image.UnlockBits(imageData);

                    // notify parent
                    if (snapshotMode)
                    {
                        parent.OnSnapshotFrame(args);
                    }
                    else
                    {
                        parent.OnNewFrame(args);
                    }

                    args.FrameIndex++;
                }

                return 0;
            }

            #region IDisposable Support
            private bool disposedValue = false;

            protected virtual void Dispose(bool disposing)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        if (image != null)
                            image.Dispose();
                    }

                    image = null;

                    disposedValue = true;
                }
            }

            public void Dispose()
            {
                Dispose(true);
            }
            #endregion
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                disposedValue = true;

                if (disposing)
                {
                    this.Stop();

                    this.hasFinished.Close();
                    this.shouldWake.Close();
                }

                this.thread = null;
                this.hasFinished = null;
                this.shouldWake = null;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}
