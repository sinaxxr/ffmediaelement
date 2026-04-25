namespace Unosquare.FFME.Rendering.Wave
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;

    /// <summary>
    /// Asynchronous file logger for DirectSound diagnostics. The audio
    /// thread is real-time-sensitive — doing <c>File.AppendAllText</c> per
    /// log call (open + write + close) inside the cycle adds 10-30 ms of
    /// disk I/O to <c>feedMs</c>, which gets folded back into the very
    /// timings we're trying to measure and amplifies hitches.
    ///
    /// Producers enqueue formatted lines in O(1); a single background
    /// thread drains the queue and batches writes every ~100 ms. All
    /// failures are swallowed so the logger never throws from fragile
    /// paths (OnDisposing, finalizer thread). The log lives next to the
    /// host app exe at <c>logs/dsound/dsound_{startupTimestamp}.log</c>.
    ///
    /// Disabled by default. Set environment variable
    /// <c>FFME_DSOUND_DIAG=1</c> (any non-empty value) before launching
    /// the host process to enable. When disabled <see cref="Log"/> is a
    /// no-op, no flush thread is spawned, and no log file or directory is
    /// created. Hot call sites can also gate on <see cref="IsEnabled"/>
    /// to skip string-formatting work entirely.
    /// </summary>
    internal static class DirectSoundDiagnostics
    {
        /// <summary>
        /// True when the <c>FFME_DSOUND_DIAG</c> environment variable is
        /// set to a non-empty value. Read once at type init.
        /// </summary>
        public static readonly bool IsEnabled = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("FFME_DSOUND_DIAG"));

        private const int FlushIntervalMs = 100;

        private static readonly string LogPath;
        private static readonly ConcurrentQueue<string> PendingLines;
        private static readonly Thread FlushThread;

        private static long InstanceCounter;

        static DirectSoundDiagnostics()
        {
            if (!IsEnabled)
                return;

            PendingLines = new ConcurrentQueue<string>();
            LogPath = BuildLogPath();
            FlushThread = new Thread(FlushLoop)
            {
                Name = "DirectSoundDiagnostics.flush",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            FlushThread.Start();

            // Best-effort flush on shutdown so trailing log lines aren't
            // lost when the process exits before the next ~100 ms tick.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DrainOnce();
        }

        /// <summary>
        /// Returns a fresh monotonic id; callers hold it for the lifetime of
        /// the <see cref="DirectSoundPlayer"/> instance.
        /// </summary>
        /// <returns>The new instance id.</returns>
        public static long NextInstanceId() => Interlocked.Increment(ref InstanceCounter);

        /// <summary>
        /// Enqueues one line for the diagnostic log. Returns immediately —
        /// the actual file write happens on a background thread. Safe to
        /// call from any thread, including the finalizer thread. Never
        /// throws.
        /// </summary>
        /// <param name="instanceId">The DirectSoundPlayer instance id.</param>
        /// <param name="site">Short tag identifying the call site.</param>
        /// <param name="message">Free-form detail.</param>
        public static void Log(long instanceId, string site, string message)
        {
            if (!IsEnabled)
                return;

            try
            {
                var t = Thread.CurrentThread;
                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff} | tid={1,-4} name={2,-20} | inst={3,-3} | {4,-22} | {5}",
                    DateTime.UtcNow,
                    t.ManagedThreadId,
                    t.Name ?? "<unnamed>",
                    instanceId,
                    site,
                    message ?? string.Empty);

                PendingLines.Enqueue(line);
            }
            catch
            {
                // Logger must never throw — this runs from OnDisposing,
                // Dispose, and ~DirectSoundPlayer (finalizer thread).
            }
        }

        private static void FlushLoop()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(FlushIntervalMs);
                    DrainOnce();
                }
                catch
                {
                    // Never let the flush loop die. Diagnostic-only.
                }
            }
        }

        private static void DrainOnce()
        {
            if (PendingLines.IsEmpty)
                return;

            try
            {
                var sb = new StringBuilder();
                while (PendingLines.TryDequeue(out var line))
                {
                    sb.Append(line).Append(Environment.NewLine);
                }

                if (sb.Length > 0)
                    File.AppendAllText(LogPath, sb.ToString());
            }
            catch
            {
                // Disk full / locked / etc — drop on the floor.
            }
        }

        private static string BuildLogPath()
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "dsound");
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                return Path.Combine(dir, "dsound_" + stamp + ".log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "ffme_dsound_fallback.log");
            }
        }
    }
}
