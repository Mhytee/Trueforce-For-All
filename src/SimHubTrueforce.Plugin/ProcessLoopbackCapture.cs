// Per-process audio loopback via the Windows process-loopback API.
//
// Uses ActivateAudioInterfaceAsync with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
// to capture only the audio rendered by a specific process tree. Available on
// Windows 10 build 20348+ (most installs as of 2024).
//
// The public API mirrors NAudio's WasapiLoopbackCapture (WaveFormat property,
// DataAvailable event, StartRecording/StopRecording) so AudioCaptureSource can
// use it as a drop-in. We use polling rather than event-mode capture — at 5 ms
// polling latency the wheel still feels live, and event-mode would require an
// extra Win32 SetEvent handle and more interop.
//
// References:
//   https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync
//   https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording
//   https://github.com/microsoft/Windows-classic-samples/.../ApplicationLoopback

using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace SimHubTrueforce.Plugin
{
    public enum ProcessLoopbackMode
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1,
    }

    public sealed class ProcessLoopbackCapture : IDisposable
    {
        // Capture format we ask for. The audio engine will resample whatever the
        // process is rendering into this format for us. 48 kHz / float / stereo
        // is the safest "always works" choice for shared-mode loopback.
        public const int CaptureSampleRate  = 48000;
        public const int CaptureChannels    = 2;
        public const int CaptureBitsPerSamp = 32;

        public WaveFormat WaveFormat { get; }

        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;

        private readonly int _processId;
        private readonly ProcessLoopbackMode _mode;
        private IAudioClient _audioClient;
        private IAudioCaptureClient _captureClient;
        private Thread _captureThread;
        private volatile bool _running;

        public ProcessLoopbackCapture(int processId, ProcessLoopbackMode mode = ProcessLoopbackMode.IncludeTargetProcessTree)
        {
            _processId = processId;
            _mode = mode;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(CaptureSampleRate, CaptureChannels);
        }

        public void StartRecording()
        {
            if (_running) return;

            ActivateForProcessLoopback();
            InitializeClient();

            int hr = _audioClient.Start();
            if (hr < 0) throw new COMException("IAudioClient.Start failed", hr);

            _running = true;
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = $"ProcessLoopback({_processId})",
                Priority = ThreadPriority.AboveNormal,
            };
            _captureThread.Start();
        }

        public void StopRecording()
        {
            if (!_running) return;
            _running = false;
            try { _audioClient?.Stop(); } catch { }
            try { _captureThread?.Join(500); } catch { }
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose()
        {
            StopRecording();
            if (_captureClient != null) { Marshal.ReleaseComObject(_captureClient); _captureClient = null; }
            if (_audioClient   != null) { Marshal.ReleaseComObject(_audioClient);   _audioClient   = null; }
        }

        // ---------- activation ----------

        private const string VirtualDevicePath = "VAD\\Process_Loopback";
        private static readonly Guid IID_IAudioClient        = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        private void ActivateForProcessLoopback()
        {
            // Build AUDIOCLIENT_ACTIVATION_PARAMS.
            var aclParams = new AudioClientActivationParams
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = (uint)_processId,
                    ProcessLoopbackMode = _mode,
                }
            };

            int aclSize = Marshal.SizeOf(typeof(AudioClientActivationParams));
            IntPtr aclBuf = Marshal.AllocCoTaskMem(aclSize);
            IntPtr propVar = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(aclParams, aclBuf, false);

                // Wrap the struct pointer in a PROPVARIANT(VT_BLOB). The PROPVARIANT
                // layout differs by architecture (24 bytes on x64, 16 on x86), so
                // build it byte-by-byte rather than relying on a managed struct.
                int propVarSize = IntPtr.Size == 8 ? 24 : 16;
                propVar = Marshal.AllocCoTaskMem(propVarSize);
                for (int i = 0; i < propVarSize; i++) Marshal.WriteByte(propVar, i, 0);
                const ushort VT_BLOB = 0x0041;
                Marshal.WriteInt16(propVar, 0, unchecked((short)VT_BLOB));
                // BLOB at offset 8: cbSize (4 bytes), then pBlobData pointer (4 or 8).
                Marshal.WriteInt32(propVar, 8, aclSize);
                int blobDataOffset = IntPtr.Size == 8 ? 16 : 12;
                Marshal.WriteIntPtr(propVar, blobDataOffset, aclBuf);

                Guid iid = IID_IAudioClient;
                var handler = new CompletionHandler();
                ActivateAudioInterfaceAsync(VirtualDevicePath, ref iid, propVar,
                    handler, out IActivateAudioInterfaceAsyncOperation op);

                // The activation truly is async (the system spins up the audio
                // tap on a background apartment), so we MUST wait for the
                // completion handler before asking for the result.
                if (!handler.Done.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Process loopback activation timed out (5 s).");

                int hr = op.GetActivateResult(out int activateResult, out object activated);
                if (hr < 0)         throw new COMException("ActivateAudioInterfaceAsync (op) failed", hr);
                if (activateResult < 0) throw new COMException($"Process loopback activation failed (HRESULT 0x{activateResult:X8}). Is the target PID running and rendering audio?", activateResult);

                _audioClient = (IAudioClient)activated;
            }
            finally
            {
                if (propVar != IntPtr.Zero) Marshal.FreeCoTaskMem(propVar);
                Marshal.FreeCoTaskMem(aclBuf);
            }
        }

        private void InitializeClient()
        {
            // Build a WAVEFORMATEX matching CaptureSampleRate / Channels / BitsPerSample,
            // IEEE float. Pack=2 to match the native struct layout.
            var fmt = new WAVEFORMATEX
            {
                wFormatTag      = 3,                        // WAVE_FORMAT_IEEE_FLOAT
                nChannels       = (ushort)CaptureChannels,
                nSamplesPerSec  = (uint)CaptureSampleRate,
                wBitsPerSample  = (ushort)CaptureBitsPerSamp,
                nBlockAlign     = (ushort)(CaptureChannels * CaptureBitsPerSamp / 8),
                cbSize          = 0,
            };
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;

            int fmtSize = Marshal.SizeOf(typeof(WAVEFORMATEX));
            IntPtr fmtBuf = Marshal.AllocCoTaskMem(fmtSize);
            try
            {
                Marshal.StructureToPtr(fmt, fmtBuf, false);

                // 200 ms shared-mode buffer, polling-driven (no event handle).
                const int  AUDCLNT_SHAREMODE_SHARED      = 0;
                const int  AUDCLNT_STREAMFLAGS_LOOPBACK  = 0x00020000;
                const long bufferDuration100ns           = 200 * 10000;

                int hr = _audioClient.Initialize(
                    AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_LOOPBACK,
                    bufferDuration100ns,
                    0,
                    fmtBuf,
                    IntPtr.Zero);
                if (hr < 0) throw new COMException($"IAudioClient.Initialize failed (0x{hr:X8})", hr);

                Guid capIid = IID_IAudioCaptureClient;
                hr = _audioClient.GetService(ref capIid, out object svc);
                if (hr < 0) throw new COMException("GetService(IAudioCaptureClient) failed", hr);
                _captureClient = (IAudioCaptureClient)svc;
            }
            finally
            {
                Marshal.FreeCoTaskMem(fmtBuf);
            }
        }

        // ---------- capture loop ----------

        private void CaptureLoop()
        {
            try
            {
                int blockAlign = WaveFormat.BlockAlign;
                byte[] frameBuf = new byte[blockAlign * 4096];

                while (_running)
                {
                    uint packetFrames;
                    int hr = _captureClient.GetNextPacketSize(out packetFrames);
                    if (hr < 0 || !_running) break;

                    while (packetFrames > 0 && _running)
                    {
                        hr = _captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags,
                                                       out long _, out long _);
                        if (hr < 0) break;
                        try
                        {
                            int byteCount = (int)frames * blockAlign;
                            // AUDCLNT_BUFFERFLAGS_SILENT = 2 — buffer is silent; data may be undefined.
                            // We still emit zeros so the consumer's pipeline keeps flowing.
                            if ((flags & 2) != 0)
                            {
                                if (frameBuf.Length < byteCount) frameBuf = new byte[byteCount];
                                Array.Clear(frameBuf, 0, byteCount);
                            }
                            else
                            {
                                if (frameBuf.Length < byteCount) frameBuf = new byte[byteCount];
                                Marshal.Copy(data, frameBuf, 0, byteCount);
                            }
                            DataAvailable?.Invoke(this, new WaveInEventArgs(frameBuf, byteCount));
                        }
                        finally
                        {
                            _captureClient.ReleaseBuffer(frames);
                        }

                        hr = _captureClient.GetNextPacketSize(out packetFrames);
                        if (hr < 0) break;
                    }

                    Thread.Sleep(5);
                }
            }
            catch (Exception ex)
            {
                RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
                return;
            }
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }

        // ---------- COM / P/Invoke ----------

        [DllImport("Mmdevapi.dll", PreserveSig = false)]
        private static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [In] ref Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation operation);

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig]
            int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        // No [ComImport] — we *implement* this interface from managed code so the
        // CLR can hand a CCW to native ActivateAudioInterfaceAsync. ComImport would
        // mark it as a type to consume from native, not implement.
        [Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig]
            int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                Done.Set();
                return 0; // S_OK
            }
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                                          IntPtr pFormat, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint bufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
            [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService([In] ref Guid riid, [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                                         out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParams
        {
            public AudioClientActivationType ActivationType;
            public AudioClientProcessLoopbackParams ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParams
        {
            public uint TargetProcessId;
            public ProcessLoopbackMode ProcessLoopbackMode;
        }

        private enum AudioClientActivationType { Default = 0, ProcessLoopback = 1 }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint   nSamplesPerSec;
            public uint   nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }
    }
}
