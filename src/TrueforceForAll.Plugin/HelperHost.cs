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
            _helper.EnableRaisingEvents = true;
            _helper.Exited += (_, __) => HelperExited?.Invoke(this, EventArgs.Empty);

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
                SimHub.Logging.Current.Error("[Trueforce] Failed to send PID to helper", ex);
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
            try
            {
                var stream = _helper.StandardOutput.BaseStream;
                // 16 KB chunks: at 48 kHz × 2 ch × 4 bytes = 384 KB/s steady-state
                // → ~24 reads per second. Plenty granular for haptics latency.
                var buf = new byte[16384];
                while (!_shuttingDown && !_helper.HasExited)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buf, n));
                }
            }
            catch (Exception ex)
            {
                if (!_shuttingDown)
                    SimHub.Logging.Current.Error("[Trueforce] Helper audio read error", ex);
            }
        }

        private void StderrLogLoop()
        {
            try
            {
                string line;
                while ((line = _helper.StandardError.ReadLine()) != null)
                {
                    SimHub.Logging.Current.Info($"[Trueforce-helper] {line}");
                }
            }
            catch { }
        }
    }
}
