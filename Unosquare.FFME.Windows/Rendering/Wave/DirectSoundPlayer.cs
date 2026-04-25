namespace Unosquare.FFME.Rendering.Wave
{
    using Diagnostics;
    using Primitives;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Runtime.InteropServices;
    using System.Security;
    using System.Threading;

    /// <summary>
    /// NativeDirectSoundOut using DirectSound COM interop.
    /// Contact author: Alexandre Mutel - alexandre_mutel at yahoo.fr
    /// Modified by: Graham "Gee" Plumb.
    /// </summary>
    internal sealed class DirectSoundPlayer : WorkerBase, IWavePlayer, ILoggingSource
    {
        #region Fields

        /// <summary>
        /// DirectSound default playback device GUID.
        /// </summary>
        public static readonly Guid DefaultPlaybackDeviceId = new Guid("DEF00000-9C6D-47ED-AAF1-4DDA8F2B5C03");

        // Device enumerations
        private static readonly object DevicesEnumLock = new object();
        private static List<DirectSoundDeviceData> EnumeratedDevices;

        // Diagnostic counters. Values are surfaced through the log lines
        // that increment them (OnDisposing.exit and cycle.gap); no public
        // read path needed.
        private static long TotalReleased;
        private static long TotalGaps;

        // Instance fields
        private readonly long DiagId = DirectSoundDiagnostics.NextInstanceId();
        private readonly EventWaitHandle CancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset);

        private readonly WaveFormat WaveFormat;
        private int SamplesTotalSize;
        private int SamplesFrameSize;
        private int NextSamplesWriteIndex;
        private Guid DeviceId;
        private byte[] Samples;
        private DirectSound.IDirectSound DirectSoundDriver;
        private DirectSound.IDirectSoundBuffer AudioRenderBuffer;
        private DirectSound.IDirectSoundBuffer AudioBackBuffer;
        private EventWaitHandle FrameStartEventWaitHandle;
        private EventWaitHandle FrameEndEventWaitHandle;
        private EventWaitHandle PlaybackEndedEventWaitHandle;
        private WaitHandle[] PlaybackWaitHandles;
        private long LastCycleTicks;
        private double LastCycleWaitMs;
        private double LastCycleFeedMs;
        private Thread CycleThread;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectSoundPlayer" /> class.
        /// (40ms seems to work under Vista).
        /// </summary>
        /// <param name="renderer">The renderer.</param>
        /// <param name="deviceId">Selected device.</param>
        public DirectSoundPlayer(AudioRenderer renderer, Guid deviceId)
            : base(nameof(DirectSoundPlayer))
        {
            Renderer = renderer;
            DeviceId = deviceId == Guid.Empty ? DefaultPlaybackDeviceId : deviceId;
            WaveFormat = renderer.WaveFormat;
            DirectSoundDiagnostics.Log(DiagId, "ctor", "DeviceId=" + DeviceId);
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="DirectSoundPlayer"/> class.
        /// Logs the finalization so we can correlate the next finalizer-thread
        /// dsound RCW Release against the instance that leaked it.
        /// </summary>
        ~DirectSoundPlayer()
        {
            DirectSoundDiagnostics.Log(DiagId, "~Finalizer", "DirectSoundPlayer finalized; COM RCW Release imminent on this thread");
        }

        #endregion

        #region Properties

        /// <inheritdoc />
        ILoggingHandler ILoggingSource.LoggingHandler => Renderer?.MediaCore;

        /// <inheritdoc />
        public AudioRenderer Renderer { get; }

        /// <inheritdoc />
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

        /// <inheritdoc />
        public bool IsRunning => WorkerState == WorkerState.Running;

        /// <inheritdoc />
        public int DesiredLatency { get; private set; } = 100;

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets the DirectSound output devices in the system.
        /// </summary>
        /// <returns>The available DirectSound devices.</returns>
        public static List<DirectSoundDeviceData> EnumerateDevices()
        {
            lock (DevicesEnumLock)
            {
                EnumeratedDevices = new List<DirectSoundDeviceData>(32);
                NativeMethods.DirectSoundEnumerateA(EnumerateDevicesCallback, IntPtr.Zero);
                return EnumeratedDevices;
            }
        }

        /// <inheritdoc />
        public void Start()
        {
            if (DirectSoundDriver != null || IsDisposed)
                throw new InvalidOperationException($"{nameof(DirectSoundPlayer)} was already started");

            DirectSoundDiagnostics.Log(DiagId, "Start.enter", "IsDisposed=" + IsDisposed);

            InitializeDirectSound();
            AudioBackBuffer.SetCurrentPosition(0);
            NextSamplesWriteIndex = 0;

            // Give the buffer initial samples to work with
            if (FeedBackBuffer(SamplesTotalSize) <= 0)
                throw new InvalidOperationException($"Method {nameof(FeedBackBuffer)} could not write samples.");

            // Set the state to playing
            PlaybackState = PlaybackState.Playing;

            // Begin notifications on playback wait events
            AudioBackBuffer.Play(0, 0, DirectSound.DirectSoundPlayFlags.Looping);

            StartAsync();

            // Run the cycle on a dedicated Highest-priority thread instead
            // of via StepTimer + ThreadPool. The pool path was the stutter
            // driver: a Task.Run dispatch lag of 500+ ms (observed in the
            // dsound logs) starves the dsound back buffer mid-playback.
            // A dedicated thread is immune to ThreadPool saturation and
            // its run-loop is naturally paced by WaitAny on the dsound
            // notification handles (~50 ms cycle). Audio-only — video and
            // subtitle workers stay on the existing IntervalWorkerBase
            // path.
            CycleThread = new Thread(CycleLoop)
            {
                Name = nameof(DirectSoundPlayer) + ".cycle",
                Priority = ThreadPriority.Highest,
                IsBackground = true,
            };
            CycleThread.Start();

            DirectSoundDiagnostics.Log(DiagId, "Start.exit", "SamplesTotalSize=" + SamplesTotalSize + " FrameSize=" + SamplesFrameSize);
        }

        /// <inheritdoc />
        public void Clear() => ClearBackBuffer();

        #endregion

        #region Worker Methods

        /// <inheritdoc />
        protected override void ExecuteCycleLogic(CancellationToken ct)
        {
            const int FrameStartHandle = 0;
            const int PlaybackEndHandle = 2;
            const int CancelHandle = 3;
            const int TimeoutHandle = WaitHandle.WaitTimeout;

            // Per-cycle GC snapshot; end-of-cycle deltas tell us whether a
            // collection fired during this specific cycle.
            var gen0Start = GC.CollectionCount(0);
            var gen1Start = GC.CollectionCount(1);
            var gen2Start = GC.CollectionCount(2);

            // Preemption detector: if the interval between consecutive cycle
            // entries is noticeably longer than the expected ~50 ms, the
            // DirectSound circular buffer can drain and cause audible stutter.
            // On gap, attribute: how much was our own work (prev wait+feed)
            // vs. dispatch/scheduling delay (gap minus work minus ~50ms
            // expected wait). Also capture thread identity, ThreadPool
            // saturation, and full GC counts to test the starvation
            // hypothesis.
            // Threshold: normal DirectSound cycles run ~94-110 ms (half-buffer
            // notification cadence + small alignment jitter), so 75 ms catches
            // every cycle and floods the log. 150 ms only fires on real
            // anomalies — a missed half-buffer worth of timing.
            var now = Environment.TickCount64;
            if (LastCycleTicks != 0)
            {
                var gap = now - LastCycleTicks;
                if (gap > 150)
                {
                    Interlocked.Increment(ref TotalGaps);

                    var t = Thread.CurrentThread;
                    ThreadPool.GetAvailableThreads(out var poolWorkers, out var poolIO);
                    ThreadPool.GetMaxThreads(out var poolWorkersMax, out var poolIOMax);
                    var workerUsage = poolWorkersMax - poolWorkers;
                    var ioUsage = poolIOMax - poolIO;

                    DirectSoundDiagnostics.Log(
                        DiagId,
                        "cycle.gap",
                        "ms=" + gap
                        + " schedLagMs=" + StepTimer.LastDispatchLagMs
                        + " prevWaitMs=" + LastCycleWaitMs.ToString("F1", CultureInfo.InvariantCulture)
                        + " prevFeedMs=" + LastCycleFeedMs.ToString("F1", CultureInfo.InvariantCulture)
                        + " pool=" + (t.IsThreadPoolThread ? "y" : "n")
                        + " prio=" + t.Priority
                        + " workers=" + workerUsage + "/" + poolWorkersMax
                        + " io=" + ioUsage + "/" + poolIOMax
                        + " gen0=" + gen0Start
                        + " gen1=" + gen1Start
                        + " gen2=" + gen2Start
                        + " gaps=" + Interlocked.Read(ref TotalGaps));
                }
            }

            LastCycleTicks = now;

            // Split the cycle into timed phases. Slow WaitAny means the
            // dsound event signal was late (hardware or driver side); slow
            // FeedBackBuffer means we blocked in our own Lock/Copy/Unlock
            // path (CPU or GC pause mid-cycle).
            var waitStart = Stopwatch.GetTimestamp();
            var handleIndex = WaitHandle.WaitAny(PlaybackWaitHandles, DesiredLatency * 3, false);
            var waitMs = (Stopwatch.GetTimestamp() - waitStart) * 1000.0 / Stopwatch.Frequency;

            // Not ready yet
            if (handleIndex == TimeoutHandle)
            {
                LastCycleWaitMs = waitMs;
                LastCycleFeedMs = 0;
                DirectSoundDiagnostics.Log(
                    DiagId,
                    "cycle.timeout",
                    "waitMs=" + waitMs.ToString("F1", CultureInfo.InvariantCulture)
                    + " gen2=" + GC.CollectionCount(2));
                return;
            }

            // Handle cancel events
            if (handleIndex == CancelHandle || handleIndex == PlaybackEndHandle)
            {
                LastCycleWaitMs = waitMs;
                LastCycleFeedMs = 0;
                WantedWorkerState = WorkerState.Stopped;
                return;
            }

            NextSamplesWriteIndex = handleIndex == FrameStartHandle ? SamplesFrameSize : default;

            // Only carry on playing if we can read more samples
            var feedStart = Stopwatch.GetTimestamp();
            var fed = FeedBackBuffer(SamplesFrameSize);
            var feedMs = (Stopwatch.GetTimestamp() - feedStart) * 1000.0 / Stopwatch.Frequency;

            LastCycleWaitMs = waitMs;
            LastCycleFeedMs = feedMs;

            var gen0Delta = GC.CollectionCount(0) - gen0Start;
            var gen1Delta = GC.CollectionCount(1) - gen1Start;
            var gen2Delta = GC.CollectionCount(2) - gen2Start;
            var gcMidCycle = gen0Delta != 0 || gen1Delta != 0 || gen2Delta != 0;
            var slowPhase = waitMs > 120 || feedMs > 10;

            if (fed > 0 && fed < SamplesFrameSize)
            {
                DirectSoundDiagnostics.Log(
                    DiagId,
                    "feed.underrun",
                    "got=" + fed + " want=" + SamplesFrameSize
                    + " waitMs=" + waitMs.ToString("F1", CultureInfo.InvariantCulture)
                    + " feedMs=" + feedMs.ToString("F1", CultureInfo.InvariantCulture)
                    + " gc0=" + gen0Delta + " gc1=" + gen1Delta + " gc2=" + gen2Delta);
            }
            else if (gcMidCycle || slowPhase)
            {
                DirectSoundDiagnostics.Log(
                    DiagId,
                    "cycle.detail",
                    "waitMs=" + waitMs.ToString("F1", CultureInfo.InvariantCulture)
                    + " feedMs=" + feedMs.ToString("F1", CultureInfo.InvariantCulture)
                    + " gc0=" + gen0Delta + " gc1=" + gen1Delta + " gc2=" + gen2Delta);
            }

            if (fed <= 0)
                throw new InvalidOperationException($"Method {nameof(FeedBackBuffer)} could not write samples.");
        }

        /// <inheritdoc />
        protected override void OnCycleException(Exception ex)
        {
            DirectSoundDiagnostics.Log(DiagId, "cycle.exception", ex.GetType().Name + ": " + ex.Message);
            this.LogError(Aspects.AudioRenderer, $"{nameof(DirectSoundPlayer)} faulted.", ex);
        }

        /// <inheritdoc />
        protected override void OnDisposing()
        {
            DirectSoundDiagnostics.Log(DiagId, "OnDisposing.enter", "WorkerState=" + WorkerState + " PlaybackState=" + PlaybackState);

            // Signal Completion
            PlaybackState = PlaybackState.Stopped;
            CancelEvent.Set(); // causes the WaitAny to exit

            TryLogged(DiagId, "Stop.Render", () => AudioRenderBuffer.Stop());
            TryLogged(DiagId, "ClearBack", ClearBackBuffer);
            TryLogged(DiagId, "Stop.Back", () => AudioBackBuffer.Stop());

            // Release COM RCWs on the dispose thread instead of leaving them
            // for the finalizer thread. FFME's original Dispose left these
            // alive, so the CLR queued them for finalizer-thread Release on
            // the next GC; under lifecycle churn the finalizer queue would
            // burst-release many dsound RCWs in close succession and one of
            // them would fault inside dsound's internal error path, killing
            // the process. Order matters: children (buffers) before parent
            // (driver). FinalReleaseComObject drops the ref count in one
            // shot, so the RCW is removed from the finalizer queue entirely.
            if (AudioBackBuffer != null)
            {
                TryLogged(DiagId, "Release.Back", () => Marshal.FinalReleaseComObject(AudioBackBuffer));
                AudioBackBuffer = null;
            }

            if (AudioRenderBuffer != null)
            {
                TryLogged(DiagId, "Release.Render", () => Marshal.FinalReleaseComObject(AudioRenderBuffer));
                AudioRenderBuffer = null;
            }

            if (DirectSoundDriver != null)
            {
                TryLogged(DiagId, "Release.Driver", () => Marshal.FinalReleaseComObject(DirectSoundDriver));
                DirectSoundDriver = null;
            }

            var totalReleased = Interlocked.Increment(ref TotalReleased);

            var exitMsg = "Driver=" + DescribeRcw(DirectSoundDriver)
                + " Render=" + DescribeRcw(AudioRenderBuffer)
                + " Back=" + DescribeRcw(AudioBackBuffer)
                + " totalReleased=" + totalReleased;
            DirectSoundDiagnostics.Log(DiagId, "OnDisposing.exit", exitMsg);
        }

        /// <inheritdoc />
        protected override void Dispose(bool alsoManaged)
        {
            DirectSoundDiagnostics.Log(DiagId, "Dispose.enter", "alsoManaged=" + alsoManaged);

            base.Dispose(alsoManaged);

            if (alsoManaged)
            {
                // Dispose DirectSound buffer wait handles
                PlaybackEndedEventWaitHandle?.Dispose();
                FrameStartEventWaitHandle?.Dispose();
                FrameEndEventWaitHandle?.Dispose();
                CancelEvent.Dispose();
                DirectSoundDiagnostics.Log(DiagId, "WaitHandles", "disposed");
            }

            var exitMsg = "Driver=" + DescribeRcw(DirectSoundDriver)
                + " Render=" + DescribeRcw(AudioRenderBuffer)
                + " Back=" + DescribeRcw(AudioBackBuffer);
            DirectSoundDiagnostics.Log(DiagId, "Dispose.exit", exitMsg);
        }

        /// <summary>
        /// Describes a COM RCW field's liveness for log output. Guards nulls
        /// and swallows <see cref="Marshal.IsComObject"/> failures.
        /// </summary>
        /// <param name="obj">The RCW to describe.</param>
        /// <returns>Short string: "null", "RCW(alive)", "notRCW", or "?".</returns>
        private static string DescribeRcw(object obj)
        {
            if (obj == null) return "null";
            try { return Marshal.IsComObject(obj) ? "RCW(alive)" : "notRCW"; }
            catch { return "?"; }
        }

        /// <summary>
        /// Runs <paramref name="action"/>, logs success or the caught exception.
        /// Replaces the pre-instrumentation <c>catch { /* ignore */ }</c>
        /// pattern so silent failures show up in the dsound log.
        /// </summary>
        /// <param name="diagId">Instance diagnostic id.</param>
        /// <param name="site">Tag for the log entry.</param>
        /// <param name="action">Operation to run.</param>
        private static void TryLogged(long diagId, string site, Action action)
        {
            try
            {
                action();
                DirectSoundDiagnostics.Log(diagId, site, "ok");
            }
            catch (Exception ex)
            {
                DirectSoundDiagnostics.Log(diagId, site, "threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Enumerates the devices.
        /// </summary>
        /// <param name="deviceGuidPtr">The device unique identifier pointer.</param>
        /// <param name="descriptionPtr">The description string pointer.</param>
        /// <param name="modulePtr">The module string pointer.</param>
        /// <param name="contextPtr">The context pointer.</param>
        /// <returns>The devices.</returns>
        private static bool EnumerateDevicesCallback(IntPtr deviceGuidPtr, IntPtr descriptionPtr, IntPtr modulePtr, IntPtr contextPtr)
        {
            var device = new DirectSoundDeviceData();
            if (deviceGuidPtr == IntPtr.Zero)
            {
                device.Guid = Guid.Empty;
            }
            else
            {
                var guidBytes = new byte[16];
                Marshal.Copy(deviceGuidPtr, guidBytes, 0, 16);
                device.Guid = new Guid(guidBytes);
            }

            device.Description = descriptionPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(descriptionPtr) : default;
            device.ModuleName = modulePtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(modulePtr) : default;

            EnumeratedDevices.Add(device);
            return true;
        }

        /// <summary>
        /// Creates a DirectSound position notification.
        /// </summary>
        /// <param name="eventHandle">The event handle.</param>
        /// <param name="offset">The offset.</param>
        /// <returns>A DirectSound Position Notification.</returns>
        private static DirectSound.DirectSoundBufferPositionNotify CreatePositionNotification(WaitHandle eventHandle, uint offset) =>
            new DirectSound.DirectSoundBufferPositionNotify
            {
                Offset = offset,
                NotifyHandle = eventHandle.SafeWaitHandle.DangerousGetHandle()
            };

        /// <summary>
        /// Initializes the direct sound.
        /// </summary>
        private void InitializeDirectSound()
        {
            // We will have 2 buffers: one for immediate audio out rendering, and another where we will
            // feed the samples. We will copy audio data from the back buffer into the immediate render
            // buffer. We first open the DirectSound driver, create the buffers and start the playback!
            // Open DirectSound
            DirectSoundDriver = null;
            var createDriverResult = NativeMethods.DirectSoundCreate(ref DeviceId, out DirectSoundDriver, IntPtr.Zero);

            if (DirectSoundDriver == null || createDriverResult != 0)
                return;

            // Set Cooperative Level to PRIORITY (priority level can call the SetFormat and Compact methods)
            DirectSoundDriver.SetCooperativeLevel(NativeMethods.GetDesktopWindow(),
                DirectSound.DirectSoundCooperativeLevel.Normal);

            // Fill BufferDescription for immediate, rendering buffer
            var renderBuffer = new DirectSound.BufferDescription
            {
                Size = Marshal.SizeOf<DirectSound.BufferDescription>(),
                BufferBytes = 0,
                Flags = DirectSound.DirectSoundBufferCaps.PrimaryBuffer,
                Reserved = 0,
                FormatHandle = IntPtr.Zero,
                AlgorithmId = Guid.Empty
            };

            // Create the Render Buffer (Immediate audio out)
            DirectSoundDriver.CreateSoundBuffer(renderBuffer, out var audioRenderBuffer, IntPtr.Zero);
            AudioRenderBuffer = audioRenderBuffer as DirectSound.IDirectSoundBuffer;

            // Play & Loop on the render buffer
            AudioRenderBuffer?.Play(0, 0, DirectSound.DirectSoundPlayFlags.Looping);

            // A frame of samples equals to Desired Latency
            SamplesFrameSize = MillisToBytes(DesiredLatency);
            var waveFormatHandle = GCHandle.Alloc(WaveFormat, GCHandleType.Pinned);

            // Fill BufferDescription for sample-receiving back buffer
            var backBuffer = new DirectSound.BufferDescription
            {
                Size = Marshal.SizeOf<DirectSound.BufferDescription>(),
                BufferBytes = (uint)(SamplesFrameSize * 2),
                Flags = DirectSound.DirectSoundBufferCaps.GetCurrentPosition2
                        | DirectSound.DirectSoundBufferCaps.ControlNotifyPosition
                        | DirectSound.DirectSoundBufferCaps.GlobalFocus
                        | DirectSound.DirectSoundBufferCaps.ControlVolume
                        | DirectSound.DirectSoundBufferCaps.StickyFocus
                        | DirectSound.DirectSoundBufferCaps.GetCurrentPosition2,
                Reserved = 0,
                FormatHandle = waveFormatHandle.AddrOfPinnedObject(),
                AlgorithmId = Guid.Empty
            };

            // Create back buffer where samples will be fed
            DirectSoundDriver.CreateSoundBuffer(backBuffer, out audioRenderBuffer, IntPtr.Zero);
            AudioBackBuffer = audioRenderBuffer as DirectSound.IDirectSoundBuffer;
            waveFormatHandle.Free();

            // Get effective SecondaryBuffer size
            var bufferCapabilities = new DirectSound.BufferCaps { Size = Marshal.SizeOf<DirectSound.BufferCaps>() };
            AudioBackBuffer?.GetCaps(bufferCapabilities);

            NextSamplesWriteIndex = 0;
            SamplesTotalSize = bufferCapabilities.BufferBytes;
            Samples = new byte[SamplesTotalSize];
            Debug.Assert(SamplesTotalSize == (2 * SamplesFrameSize), "Invalid SamplesTotalSize vs SamplesFrameSize");

            // Create double buffering notifications.
            // Use DirectSoundNotify at Position [0, 1/2] and Stop Position (0xFFFFFFFF)
            var notifier = audioRenderBuffer as DirectSound.IDirectSoundNotify;

            FrameStartEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
            FrameEndEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
            PlaybackEndedEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
            PlaybackWaitHandles = new WaitHandle[] { FrameStartEventWaitHandle, FrameEndEventWaitHandle, PlaybackEndedEventWaitHandle, CancelEvent };

            var notificationEvents = new[]
            {
                CreatePositionNotification(FrameStartEventWaitHandle, 0),
                CreatePositionNotification(FrameEndEventWaitHandle, (uint)SamplesFrameSize),
                CreatePositionNotification(PlaybackEndedEventWaitHandle, 0xFFFFFFFF)
            };

            notifier?.SetNotificationPositions((uint)notificationEvents.Length, notificationEvents);
        }

        /// <summary>
        /// Determines whether the SecondaryBuffer is lost.
        /// </summary>
        /// <returns>
        /// <c>true</c> if [is buffer lost]; otherwise, <c>false</c>.
        /// </returns>
        private bool IsBufferLost() =>
            AudioBackBuffer.GetStatus().HasFlag(DirectSound.DirectSoundBufferStatus.BufferLost);

        /// <summary>
        /// Convert ms to bytes size according to WaveFormat.
        /// </summary>
        /// <param name="millis">The milliseconds.</param>
        /// <returns>number of bytes.</returns>
        private int MillisToBytes(int millis)
        {
            var bytes = millis * (WaveFormat.AverageBytesPerSecond / 1000);
            bytes -= bytes % WaveFormat.BlockAlign;
            return bytes;
        }

        /// <summary>
        /// Clean up the SecondaryBuffer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In DirectSound, when playback is started,
        /// the rest of the sound that was played last time is played back as noise.
        /// This happens even if the secondary buffer is completely silenced,
        /// so it seems that the buffer in the primary buffer or higher is not cleared.
        /// </para>
        /// <para>
        /// To solve this problem fill the secondary buffer with silence data when stop playback.
        /// </para>
        /// </remarks>
        private void ClearBackBuffer()
        {
            if (AudioBackBuffer == null)
                return;

            var silence = new byte[SamplesTotalSize];

            // Lock the SecondaryBuffer
            AudioBackBuffer.Lock(0,
                (uint)SamplesTotalSize,
                out var wavBuffer1,
                out var nbSamples1,
                out var wavBuffer2,
                out var nbSamples2,
                DirectSound.DirectSoundBufferLockFlag.None);

            // Copy silence data to the SecondaryBuffer. Same wraparound
            // handling as FeedBackBuffer — when wavBuffer2 is non-null, the
            // second segment must also be silenced, otherwise old audio in
            // that region survives the "clear" and plays through on resume.
            if (wavBuffer1 != IntPtr.Zero)
            {
                Marshal.Copy(silence, 0, wavBuffer1, nbSamples1);
                if (wavBuffer2 != IntPtr.Zero)
                {
                    Marshal.Copy(silence, 0, wavBuffer2, nbSamples2);
                }
            }

            // Unlock the SecondaryBuffer
            AudioBackBuffer.Unlock(wavBuffer1, nbSamples1, wavBuffer2, nbSamples2);
        }

        /// <summary>
        /// Feeds the SecondaryBuffer with the WaveStream.
        /// </summary>
        /// <param name="bytesToCopy">number of bytes to feed.</param>
        /// <returns>The number of bytes that were read.</returns>
        private int FeedBackBuffer(int bytesToCopy)
        {
            // Restore the buffer if lost
            if (IsBufferLost())
            {
                DirectSoundDiagnostics.Log(DiagId, "buffer.lost", "restoring");
                AudioBackBuffer.Restore();
            }

            // Read data from stream (Should this be inserted between the lock / unlock?)
            var bytesRead = Renderer?.Read(Samples, 0, bytesToCopy) ?? 0;

            // Write silence
            if (bytesRead <= 0)
            {
                Array.Clear(Samples, 0, Samples.Length);
                return 0;
            }

            // Lock a portion of the SecondaryBuffer (starting from 0 or 1/2 the buffer)
            AudioBackBuffer.Lock(NextSamplesWriteIndex,
                (uint)bytesRead,  // (uint)bytesToCopy,
                out var wavBuffer1,
                out var nbSamples1,
                out var wavBuffer2,
                out var nbSamples2,
                DirectSound.DirectSoundBufferLockFlag.None);

            // Copy back to the SecondaryBuffer. DirectSound's Lock returns a
            // second segment (wavBuffer2) when the requested range wraps past
            // the play cursor or the end of the circular buffer; the second
            // chunk must receive the samples continuing from offset
            // nbSamples1 of Samples, not a repeat of the first chunk.
            if (wavBuffer1 != IntPtr.Zero)
            {
                Marshal.Copy(Samples, 0, wavBuffer1, nbSamples1);
                if (wavBuffer2 != IntPtr.Zero)
                {
                    Marshal.Copy(Samples, nbSamples1, wavBuffer2, nbSamples2);
                }
            }

            // Unlock the SecondaryBuffer
            AudioBackBuffer.Unlock(wavBuffer1, nbSamples1, wavBuffer2, nbSamples2);

            return bytesRead;
        }

        /// <summary>
        /// Runs <see cref="WorkerBase.ExecuteCycleLogic"/> on a dedicated
        /// Highest-priority thread. Replaces the StepTimer/ThreadPool
        /// dispatch path that <see cref="IntervalWorkerBase"/> would have
        /// provided. Loop exits when <see cref="WorkerBase.TryBeginCycle"/>
        /// returns false (worker stopped or disposed); the worker state
        /// transitions are handled inside that method.
        /// </summary>
        private void CycleLoop()
        {
            while (TryBeginCycle())
            {
                ExecuteCyle();

                // If the worker is paused, throttle the spin to a cheap
                // ~15 ms cadence — matches StepTimer's resolution so the
                // paused behavior is equivalent to the IntervalWorkerBase
                // path.
                if (WorkerState != WorkerState.Running)
                    Thread.Sleep(15);
            }
        }

        #endregion

        #region Native DirectSound COM Interface

        private static class DirectSound
        {
            /// <summary>
            /// DirectSound default capture device GUID.
            /// </summary>
            public static readonly Guid DefaultCaptureDeviceId = new Guid("DEF00001-9C6D-47ED-AAF1-4DDA8F2B5C03");

            /// <summary>
            /// DirectSound default device for voice playback.
            /// </summary>
            public static readonly Guid DefaultVoicePlaybackDeviceId = new Guid("DEF00002-9C6D-47ED-AAF1-4DDA8F2B5C03");

            /// <summary>
            /// DirectSound default device for voice capture.
            /// </summary>
            public static readonly Guid DefaultVoiceCaptureDeviceId = new Guid("DEF00003-9C6D-47ED-AAF1-4DDA8F2B5C03");

            /// <summary>
            /// The DSEnumCallback function is an application-defined callback function that enumerates the DirectSound drivers.
            /// The system calls this function in response to the application's call to the DirectSoundEnumerate or DirectSoundCaptureEnumerate function.
            /// </summary>
            /// <param name="deviceGuidPtr">Address of the GUID that identifies the device being enumerated, or NULL for the primary device. This value can be passed to the DirectSoundCreate8 or DirectSoundCaptureCreate8 function to create a device object for that driver. </param>
            /// <param name="descriptionPtr">Address of a null-terminated string that provides a textual description of the DirectSound device. </param>
            /// <param name="modulePtr">Address of a null-terminated string that specifies the module name of the DirectSound driver corresponding to this device. </param>
            /// <param name="contextPtr">Address of application-defined data. This is the pointer passed to DirectSoundEnumerate or DirectSoundCaptureEnumerate as the lpContext parameter. </param>
            /// <returns>Returns TRUE to continue enumerating drivers, or FALSE to stop.</returns>
            public delegate bool EnumerateDevicesDelegate(IntPtr deviceGuidPtr, IntPtr descriptionPtr, IntPtr modulePtr, IntPtr contextPtr);

            public enum DirectSoundCooperativeLevel : uint
            {
                Normal = 0x00000001,
                Priority = 0x00000002,
                Exclusive = 0x00000003,
                WritePrimary = 0x00000004
            }

            [Flags]
            public enum DirectSoundPlayFlags : uint
            {
                Looping = 0x00000001,
                LocHardware = 0x00000002,
                LocSoftware = 0x00000004,
                TerminateByTime = 0x00000008,
                TerminateByDistance = 0x000000010,
                TerminateByPriority = 0x000000020
            }

            [Flags]
            public enum DirectSoundBufferLockFlag : uint
            {
                None = 0,
                FromWriteCursor = 0x00000001,
                EntireBuffer = 0x00000002
            }

            [Flags]
            public enum DirectSoundBufferStatus : uint
            {
                Playing = 0x00000001,
                BufferLost = 0x00000002,
                Looping = 0x00000004,
                LocHardware = 0x00000008,
                LocSoftware = 0x00000010,
                Terminated = 0x00000020
            }

            [Flags]
            public enum DirectSoundBufferCaps : uint
            {
                PrimaryBuffer = 0x00000001,
                StaticBuffer = 0x00000002,
                LocHardware = 0x00000004,
                LocSoftware = 0x00000008,
                Control3D = 0x00000010,
                ControlFrequency = 0x00000020,
                ControlPan = 0x00000040,
                ControlVolume = 0x00000080,
                ControlNotifyPosition = 0x00000100,
                ControlEffects = 0x00000200,
                StickyFocus = 0x00004000,
                GlobalFocus = 0x00008000,
                GetCurrentPosition2 = 0x00010000,
                Mute3dAtMaxDistance = 0x00020000,
                LocDefer = 0x00040000
            }

            /// <summary>
            /// IDirectSound interface.
            /// </summary>
            [ComImport]
            [Guid("279AFA83-4981-11CE-A521-0020AF0BE560")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            [SuppressUnmanagedCodeSecurity]
            public interface IDirectSound
            {
                void CreateSoundBuffer([In] BufferDescription desc, [Out, MarshalAs(UnmanagedType.Interface)] out object dsDSoundBuffer, IntPtr pUnkOuter);

                void GetCaps(IntPtr caps);

                void DuplicateSoundBuffer([In, MarshalAs(UnmanagedType.Interface)] IDirectSoundBuffer bufferOriginal, [In, MarshalAs(UnmanagedType.Interface)] IDirectSoundBuffer bufferDuplicate);

                void SetCooperativeLevel(IntPtr windowHandle, [In, MarshalAs(UnmanagedType.U4)] DirectSoundCooperativeLevel dwLevel);

                void Compact();

                void GetSpeakerConfig(IntPtr pdwSpeakerConfig);

                void SetSpeakerConfig(uint pdwSpeakerConfig);

                void Initialize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guid);
            }

            /// <summary>
            /// IDirectSoundBuffer interface.
            /// </summary>
            [ComImport]
            [Guid("279AFA85-4981-11CE-A521-0020AF0BE560")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            [SuppressUnmanagedCodeSecurity]
            public interface IDirectSoundBuffer
            {
                void GetCaps([MarshalAs(UnmanagedType.LPStruct)] BufferCaps pBufferCaps);

                void GetCurrentPosition([Out] out uint currentPlayCursor, [Out] out uint currentWriteCursor);

                void GetFormat();

                [return: MarshalAs(UnmanagedType.I4)]
                int GetVolume();

                void GetPan([Out] out uint pan);

                [return: MarshalAs(UnmanagedType.I4)]
                int GetFrequency();

                [return: MarshalAs(UnmanagedType.U4)]
                DirectSoundBufferStatus GetStatus();

                void Initialize([In, MarshalAs(UnmanagedType.Interface)] IDirectSound directSound, [In] BufferDescription desc);

                void Lock(int dwOffset, uint dwBytes, [Out] out IntPtr audioPtr1, [Out] out int audioBytes1, [Out] out IntPtr audioPtr2, [Out] out int audioBytes2, [MarshalAs(UnmanagedType.U4)] DirectSoundBufferLockFlag dwFlags);

                void Play(uint dwReserved1, uint dwPriority, [In, MarshalAs(UnmanagedType.U4)] DirectSoundPlayFlags dwFlags);

                void SetCurrentPosition(uint dwNewPosition);

                void SetFormat([In] WaveFormat waveFormat);

                void SetVolume(int volume);

                void SetPan(uint pan);

                void SetFrequency(uint frequency);

                void Stop();

                void Unlock(IntPtr pvAudioPtr1, int dwAudioBytes1, IntPtr pvAudioPtr2, int dwAudioBytes2);

                void Restore();
            }

            /// <summary>
            /// IDirectSoundNotify interface.
            /// </summary>
            [ComImport]
            [Guid("b0210783-89cd-11d0-af08-00a0c925cd16")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            [SuppressUnmanagedCodeSecurity]
            public interface IDirectSoundNotify
            {
                void SetNotificationPositions(uint dwPositionNotifies, [In, MarshalAs(UnmanagedType.LPArray)] DirectSoundBufferPositionNotify[] pcPositionNotifies);
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct DirectSoundBufferPositionNotify : IEquatable<DirectSoundBufferPositionNotify>
            {
                public uint Offset;

                public IntPtr NotifyHandle;

                /// <inheritdoc />
                public bool Equals(DirectSoundBufferPositionNotify other) =>
                    NotifyHandle == other.NotifyHandle;

                /// <inheritdoc />
                public override bool Equals(object obj)
                {
                    if (obj is DirectSoundBufferPositionNotify other)
                        return Equals(other);

                    return false;
                }

                /// <inheritdoc />
                public override int GetHashCode() =>
                    throw new NotSupportedException($"{nameof(DirectSoundBufferPositionNotify)} does not support hashing.");
            }

#pragma warning disable SA1401 // Fields must be private
#pragma warning disable 649 // Field is never assigned

            [StructLayout(LayoutKind.Sequential, Pack = 2)]
            public class BufferDescription
            {
                public int Size;

                [MarshalAs(UnmanagedType.U4)]
                public DirectSoundBufferCaps Flags;

                public uint BufferBytes;

                public int Reserved;

                public IntPtr FormatHandle;

                public Guid AlgorithmId;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 2)]
            public class BufferCaps
            {
                public int Size;
                public int Flags;
                public int BufferBytes;
                public int UnlockTransferRate;
                public int PlayCpuOverhead;
            }

#pragma warning restore 649 // Field is never assigned
#pragma warning restore SA1401 // Fields must be private
        }

        private static class NativeMethods
        {
            private const string DirectSoundLib = "dsound.dll";
            private const string User32Lib = "user32.dll";

            /// <summary>
            /// Instantiate DirectSound from the DLL.
            /// </summary>
            /// <param name="deviceGuid">The GUID.</param>
            /// <param name="directSound">The direct sound.</param>
            /// <param name="pUnkOuter">The p unk outer.</param>
            /// <returns>The result code.</returns>
            [DllImport(DirectSoundLib, EntryPoint = nameof(DirectSoundCreate), SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern int DirectSoundCreate(ref Guid deviceGuid, [Out, MarshalAs(UnmanagedType.Interface)] out DirectSound.IDirectSound directSound, IntPtr pUnkOuter);

            /// <summary>
            /// The DirectSoundEnumerate function enumerates the DirectSound drivers installed in the system.
            /// </summary>
            /// <param name="lpDSEnumCallback">callback function.</param>
            /// <param name="lpContext">User context.</param>
            [DllImport(DirectSoundLib, EntryPoint = nameof(DirectSoundEnumerateA), SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern void DirectSoundEnumerateA(DirectSound.EnumerateDevicesDelegate lpDSEnumCallback, IntPtr lpContext);

            /// <summary>
            /// Gets the HANDLE of the desktop window.
            /// </summary>
            /// <returns>HANDLE of the Desktop window.</returns>
            [DllImport(User32Lib)]
            public static extern IntPtr GetDesktopWindow();
        }

        #endregion
    }
}
