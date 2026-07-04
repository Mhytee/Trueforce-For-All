// Manages the lifetime of TrueforceForAll.LoopbackHelper.exe (a separate child
// process that performs per-process audio loopback in modern .NET, where the
// COM interop for ActivateAudioInterfaceAsync works reliably).
//
// Communication:
//   - Helper is spawned with our PID as arg, stdin/stdout redirected.
//   - We write 4-byte LE uint32 target PIDs to its stdin (0 = stop).
//   - It writes raw 48 kHz / 2-channel / 32-bit IEEE float audio to its stdout.
//   - Bytes received are republished as DataAvailable, mirroring NAudio's
//     WasapiLoopbackCapture API so AudioCaptureSource can consume them.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace TrueforceForAll.Plugin
{
    public sealed class HelperHost : IDisposable
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<EventArgs>       HelperExited;

        private readonly string _helperExePath;
        private Process _helper;
        private Thread  _stdoutThread;
        private Thread  _stderrThread;
        private volatile bool _shuttingDown;

        public bool IsRunning => _helper != null && !_helper.HasExited;
        public int  CurrentPid { get; private set; }

        public HelperHost(string helperExePath)
        {
            _helperExePath = helperExePath;
        }

        /// <summary>
        /// Launch the helper process. Idempotent.
        /// </summary>
        public void Spawn()
        {
            if (_helper != null) return;
            if (!File.Exists(_helperExePath))
                throw new FileNotFoundException(
                    $"Loopback helper exe not found at {_helperExePath}. " +
                    "Did the deploy step include TrueforceForAll.LoopbackHelper.exe?");

            var psi = new ProcessStartInfo
            {
                FileName  = _helperExePath,
                Arguments = Process.GetCurrentProcess().Id.ToString(),
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            _helper = Process.Start(psi);
            // Order matters: subscribe before EnableRaisingEvents so that if
            // the helper has already exited, the property setter dispatches
            // the event to our handler synchronously. Then a HasExited check
            // covers the residual window where the helper exited between
            // Process.Start returning and EnableRaisingEvents being set.
            int exitFired = 0;
            void RaiseOnce()
            {
                if (System.Threading.Interlocked.Exchange(ref exitFired, 1) == 0)
                    HelperExited?.Invoke(this, EventArgs.Empty);
            }
            _helper.Exited += (_, __) => RaiseOnce();
            _helper.EnableRaisingEvents = true;
            if (_helper.HasExited) RaiseOnce();

            _stdoutThread = new Thread(StdoutPumpLoop)
            {
                IsBackground = true,
                Name = "TrueforceHelperStdout",
                Priority = ThreadPriority.AboveNormal,
            };
            _stdoutThread.Start();

            _stderrThread = new Thread(StderrLogLoop)
            {
                IsBackground = true,
                Name = "TrueforceHelperStderr",
            };
            _stderrThread.Start();
        }

        /// <summary>
        /// Tell the helper to start capturing the given PID. Pass 0 to stop.
        /// </summary>
        public void SetTargetPid(int pid)
        {
            if (_helper == null || _helper.HasExited) return;
            CurrentPid = pid;
            byte[] buf = new byte[4];
            uint u = (uint)pid;
            buf[0] = (byte)u;
            buf[1] = (byte)(u >> 8);
            buf[2] = (byte)(u >> 16);
            buf[3] = (byte)(u >> 24);
            try
            {
                _helper.StandardInput.BaseStream.Write(buf, 0, 4);
                _helper.StandardInput.BaseStream.Flush();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[TF4ALL] Failed to send PID to helper", ex);
            }
        }

        public void Dispose()
        {
            _shuttingDown = true;
            try { _helper?.StandardInput.Close(); } catch { }   // helper detects EOF, exits
            try { _helper?.WaitForExit(2000); }     catch { }
            try { if (_helper != null && !_helper.HasExited) _helper.Kill(); } catch { }
            try { _helper?.Dispose(); } catch { }
            _helper = null;
        }

        // ---------- pump loops ----------

        private void StdoutPumpLoop()
        {
            // Run the pipe-drain + DSP fan-out in the MMCSS "Pro Audio" band so
            // this thread (which feeds the audio capture ring) matches the
            // capture thread and the 1 kHz pump that already use it, instead of
            // being the weakest scheduling link. NORMAL priority; best-effort.
            using (TrueforceForAll.Core.MmcssScope.Enter(
                       "Pro Audio", TrueforceForAll.Core.MmcssScope.PriorityNormal))
                StdoutPumpLoopCore();
        }

        private void StdoutPumpLoopCore()
        {
            // Snapshot the process: Dispose() nulls _helper from another thread,
            // so dereferencing the field mid-loop could NRE. Bound to the local,
            // the loop sees a stable handle and exits via _shuttingDown / EOF.
            var helper = _helper;
            if (helper == null) return;
            try
            {
                var stream = helper.StandardOutput.BaseStream;
                // 1 KB chunks ≈ 2.7 ms of audio at 48 kHz × 2 ch × 4 bytes
                // (= 384 bytes/ms). Each read fires one DataAvailable burst, and
                // the downstream audio ring must hold a whole burst without
                // lapping, so the burst size sets where the ring auto-ratchet
                // settles. A 2 KB read delivered ~21 decimated samples per burst,
                // which forced the 4 kHz ring up to 32 (8 ms); 1 KB delivers ~10,
                // which the ring holds at 16 (4 ms), halving that buffering stage.
                // Smaller reads also tighten the p99 stall accumulation: a brief
                // plugin-thread stall (GC pause, etc.) now caps the catch-up read
                // at ~2.7 ms instead of ~5 ms. (The previous 16 KB buffer could
                // accumulate ~42 ms in one chunk and overflow the ring; 2 KB fixed
                // that, and 1 KB tightens it further.) Cost is ~2× the read rate
                // (still well under 1k reads/s), negligible CPU.
                const int bytesPerFrame = 8;  // float32 stereo
                var buf = new byte[1024];
                int leftover = 0;
                while (!_shuttingDown && !helper.HasExited)
                {
                    // Carry any sub-frame tail from the previous read forward
                    // pipe Reads can return non-frame-aligned counts, and dropping
                    // those bytes would permanently desync L/R for the session.
                    int n = stream.Read(buf, leftover, buf.Length - leftover);
                    if (n <= 0) break;
                    int total = leftover + n;
                    int aligned = total - (total % bytesPerFrame);
                    if (aligned > 0)
                        DataAvailable?.Invoke(this, new WaveInEventArgs(buf, aligned));
                    leftover = total - aligned;
                    if (leftover > 0)
                        Buffer.BlockCopy(buf, aligned, buf, 0, leftover);
                }
            }
            catch (Exception ex)
            {
                if (!_shuttingDown)
                    SimHub.Logging.Current.Error("[TF4ALL] Helper audio read error", ex);
            }
        }

        private void StderrLogLoop()
        {
            var helper = _helper;
            if (helper == null) return;
            try
            {
                string line;
                while ((line = helper.StandardError.ReadLine()) != null)
                {
                    SimHub.Logging.Current.Info($"[Trueforce-helper] {line}");
                }
            }
            catch { }
        }
    }
}
