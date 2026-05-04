// Per-process audio loopback via the Windows process-loopback API.
//
// This is the second attempt. The first used CLR auto-marshaling (typed
// interfaces with [ComImport], typed completion handler with [ComVisible]) and
// failed with E_ILLEGAL_METHOD_CALL from ActivateAudioInterfaceAsync itself —
// despite MTA pinning, IAgileObject markers, and explicit CoInitializeEx.
//
// This rewrite does it all by hand:
//   - ExactSpelling=true on the DllImport so the CLR doesn't append A/W suffixes.
//   - The native call signature uses raw IntPtrs; we marshal completion handler
//     and PROPVARIANT ourselves.
//   - The completion handler is implemented by emitting a VTable pointer
//     manually (via Marshal.AllocHGlobal + delegate function pointers) so we
//     have full control over QueryInterface — including reporting IAgileObject
//     so the activation accepts our handler as agile.
//   - The activation runs on a thread that's both MTA-state and CoInitializeEx'd.
//
// Helper-side variant: emits raw byte buffers via DataAvailable so we don't
// need an NAudio dependency in the single-file helper exe. The capture format
// is locked to 48 kHz / 2-channel / 32-bit IEEE float, same as the plugin's
// pipeline expects.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrueforceForAll.LoopbackHelper
{
    public sealed class AudioFrameEventArgs : EventArgs
    {
        public byte[] Buffer;
        public int    BytesRecorded;
    }

    public sealed class CaptureStoppedEventArgs : EventArgs
    {
        public Exception Exception;
        public CaptureStoppedEventArgs(Exception ex = null) { Exception = ex; }
    }

    public enum ProcessLoopbackMode
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1,
    }

    public sealed class ProcessLoopbackCapture : IDisposable
    {
        public const int CaptureSampleRate  = 48000;
        public const int CaptureChannels    = 2;
        public const int CaptureBitsPerSamp = 32;
        public const int BlockAlign         = CaptureChannels * CaptureBitsPerSamp / 8;  // 8 bytes / frame

        public event EventHandler<AudioFrameEventArgs>     DataAvailable;
        public event EventHandler<CaptureStoppedEventArgs> RecordingStopped;

        private readonly int _processId;
        private readonly ProcessLoopbackMode _mode;

        private IntPtr _audioClientPtr;          // IAudioClient*
        private IntPtr _captureClientPtr;        // IAudioCaptureClient*
        private IntPtr _sampleReadyEvent = IntPtr.Zero;
        private CompletionHandlerVtbl _handler;  // hand-built CCW; keep alive
        private Thread _captureThread;
        private volatile bool _running;

        public ProcessLoopbackCapture(int processId, ProcessLoopbackMode mode = ProcessLoopbackMode.IncludeTargetProcessTree)
        {
            _processId = processId;
            _mode = mode;
        }

        public void StartRecording()
        {
            if (_running) return;
            ActivateForProcessLoopback();
            InitializeClient();
            int hr = AudioClient_Start(_audioClientPtr);
            if (hr < 0) throw new COMException($"IAudioClient::Start failed (0x{hr:X8})", hr);

            _running = true;
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = $"ProcessLoopback({_processId})",
                Priority = ThreadPriority.AboveNormal,
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }

        public void StopRecording()
        {
            if (!_running) return;
            _running = false;
            try { if (_audioClientPtr != IntPtr.Zero) AudioClient_Stop(_audioClientPtr); } catch { }
            try { _captureThread?.Join(500); } catch { }
            RecordingStopped?.Invoke(this, new CaptureStoppedEventArgs());
        }

        public void Dispose()
        {
            StopRecording();
            if (_captureClientPtr != IntPtr.Zero) { Marshal.Release(_captureClientPtr); _captureClientPtr = IntPtr.Zero; }
            if (_audioClientPtr   != IntPtr.Zero) { Marshal.Release(_audioClientPtr);   _audioClientPtr   = IntPtr.Zero; }
            if (_sampleReadyEvent != IntPtr.Zero) { CloseHandle(_sampleReadyEvent); _sampleReadyEvent = IntPtr.Zero; }
            _handler?.Dispose();
            _handler = null;
        }

        // ---------- activation ----------

        private const string VirtualDevicePath = "VAD\\Process_Loopback";
        private static readonly Guid IID_IUnknown            = new Guid("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IAgileObject        = new Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
        private static readonly Guid IID_IAudioClient        = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioClient3       = new Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        private static readonly Guid IID_IActivateAudioInterfaceCompletionHandler =
            new Guid("41D949AB-9862-444A-80F6-C261334DA5EB");

        private void ActivateForProcessLoopback()
        {
            // 1) Build AUDIOCLIENT_ACTIVATION_PARAMS in unmanaged memory.
            int aclSize = Marshal.SizeOf(typeof(AudioClientActivationParams));
            IntPtr aclBuf = Marshal.AllocCoTaskMem(aclSize);
            IntPtr propVar = IntPtr.Zero;
            try
            {
                var aclParams = new AudioClientActivationParams
                {
                    ActivationType = AudioClientActivationType.ProcessLoopback,
                    ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                    {
                        TargetProcessId = (uint)_processId,
                        ProcessLoopbackMode = _mode,
                    }
                };
                Marshal.StructureToPtr(aclParams, aclBuf, false);

                // 2) Build PROPVARIANT(VT_BLOB) wrapping the struct pointer.
                int propVarSize = IntPtr.Size == 8 ? 24 : 16;
                propVar = Marshal.AllocCoTaskMem(propVarSize);
                for (int i = 0; i < propVarSize; i++) Marshal.WriteByte(propVar, i, 0);
                const ushort VT_BLOB = 0x0041;
                Marshal.WriteInt16(propVar, 0, unchecked((short)VT_BLOB));
                Marshal.WriteInt32(propVar, 8, aclSize);
                int blobDataOffset = IntPtr.Size == 8 ? 16 : 12;
                Marshal.WriteIntPtr(propVar, blobDataOffset, aclBuf);

                // 3) Build a hand-rolled completion handler with QI for IAgileObject.
                _handler = new CompletionHandlerVtbl();

                // 4) Call ActivateAudioInterfaceAsync.
                Guid iid = IID_IAudioClient;
                IntPtr opPtr;
                int hr = ActivateAudioInterfaceAsync(VirtualDevicePath, ref iid, propVar,
                                                     _handler.IUnknownPtr, out opPtr);
                if (hr < 0) throw new COMException($"ActivateAudioInterfaceAsync failed (0x{hr:X8})", hr);
                if (opPtr == IntPtr.Zero) throw new COMException("ActivateAudioInterfaceAsync returned null operation");

                try
                {
                    // 5) Wait for the completion handler to fire.
                    if (!_handler.Done.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Process loopback activation timed out (5 s).");

                    // 6) Operation's GetActivateResult is method index 3 in IUnknown's vtable order.
                    //    [0]=QI, [1]=AddRef, [2]=Release, [3]=GetActivateResult.
                    int activateResult;
                    IntPtr activatedUnknown;
                    int rh = AsyncOp_GetActivateResult(opPtr, out activateResult, out activatedUnknown);
                    if (rh < 0)
                        throw new COMException($"GetActivateResult call failed (0x{rh:X8})", rh);
                    if (activateResult < 0)
                        throw new COMException($"Process loopback activation reported failure 0x{activateResult:X8}. Is the target PID rendering audio?", activateResult);
                    if (activatedUnknown == IntPtr.Zero)
                        throw new COMException("Activation returned null IUnknown");

                    // 7) QI for IAudioClient on the activated IUnknown (just to be tidy; in practice
                    //    they're the same object). Release the IUnknown either way.
                    Guid clientIid = IID_IAudioClient;
                    int qhr = Marshal.QueryInterface(activatedUnknown, ref clientIid, out _audioClientPtr);
                    Marshal.Release(activatedUnknown);
                    if (qhr < 0)
                        throw new COMException($"QI(IAudioClient) failed (0x{qhr:X8})", qhr);
                }
                finally
                {
                    Marshal.Release(opPtr);
                }
            }
            finally
            {
                if (propVar != IntPtr.Zero) Marshal.FreeCoTaskMem(propVar);
                Marshal.FreeCoTaskMem(aclBuf);
            }
        }

        private void InitializeClient()
        {
            // WAVEFORMATEX in unmanaged memory.
            var fmt = new WAVEFORMATEX
            {
                wFormatTag      = 3,                                   // WAVE_FORMAT_IEEE_FLOAT
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

                const int  AUDCLNT_SHAREMODE_SHARED          = 0;
                const int  AUDCLNT_STREAMFLAGS_LOOPBACK      = 0x00020000;
                const int  AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
                int streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK;

                // Try IAudioClient3 first — gives us a shared-mode engine period
                // as low as ~3ms (vs 10ms default). Falls back to legacy
                // IAudioClient::Initialize at 10ms if IAudioClient3 is
                // unavailable (older Windows, or process-loopback restriction).
                bool initialized = false;
                IntPtr ac3 = IntPtr.Zero;
                Guid ac3Iid = IID_IAudioClient3;
                int hr = Unknown_QueryInterface(_audioClientPtr, ref ac3Iid, out ac3);
                if (hr >= 0 && ac3 != IntPtr.Zero)
                {
                    try
                    {
                        uint defFr, fundFr, minFr, maxFr;
                        int qhr = AudioClient3_GetSharedModeEnginePeriod(ac3, fmtBuf,
                            out defFr, out fundFr, out minFr, out maxFr);
                        if (qhr >= 0 && minFr > 0)
                        {
                            int ihr = AudioClient3_InitializeSharedAudioStream(ac3,
                                streamFlags, minFr, fmtBuf, IntPtr.Zero);
                            if (ihr >= 0)
                            {
                                Console.Error.WriteLine(
                                    $"[helper] IAudioClient3 init OK; engine period {minFr} frames " +
                                    $"({minFr * 1000.0 / CaptureSampleRate:F2} ms; default {defFr}, max {maxFr})");
                                initialized = true;
                            }
                            else
                            {
                                Console.Error.WriteLine($"[helper] IAudioClient3.InitializeSharedAudioStream failed (0x{ihr:X8}), falling back");
                            }
                        }
                        else
                        {
                            Console.Error.WriteLine($"[helper] IAudioClient3.GetSharedModeEnginePeriod failed (0x{qhr:X8}) or minFr=0, falling back");
                        }
                    }
                    finally
                    {
                        Unknown_Release(ac3);
                    }
                }
                else
                {
                    Console.Error.WriteLine($"[helper] IAudioClient3 not available (QI 0x{hr:X8}), using legacy 10ms IAudioClient");
                }

                if (!initialized)
                {
                    const long bufferDuration100ns = 10 * 10000;
                    int lhr = AudioClient_Initialize(_audioClientPtr,
                        AUDCLNT_SHAREMODE_SHARED, streamFlags,
                        bufferDuration100ns, 0, fmtBuf, IntPtr.Zero);
                    if (lhr < 0) throw new COMException($"IAudioClient::Initialize failed (0x{lhr:X8})", lhr);
                }

                _sampleReadyEvent = CreateEventEx(IntPtr.Zero, null, 0, EVENT_MODIFY_STATE | SYNCHRONIZE);
                if (_sampleReadyEvent == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateEventEx failed");

                hr = AudioClient_SetEventHandle(_audioClientPtr, _sampleReadyEvent);
                if (hr < 0) throw new COMException($"IAudioClient::SetEventHandle failed (0x{hr:X8})", hr);

                Guid capIid = IID_IAudioCaptureClient;
                hr = AudioClient_GetService(_audioClientPtr, ref capIid, out _captureClientPtr);
                if (hr < 0) throw new COMException($"IAudioClient::GetService(IAudioCaptureClient) failed (0x{hr:X8})", hr);
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
                int blockAlign = BlockAlign;
                byte[] frameBuf = new byte[blockAlign * 4096];

                while (_running)
                {
                    uint waitRes = WaitForSingleObject(_sampleReadyEvent, 100);
                    if (waitRes == WAIT_FAILED) break;

                    uint packetFrames;
                    int hr = CaptureClient_GetNextPacketSize(_captureClientPtr, out packetFrames);
                    if (hr < 0 || !_running) break;

                    while (packetFrames > 0 && _running)
                    {
                        IntPtr data;
                        uint frames, flags;
                        long _devPos, _qpcPos;
                        hr = CaptureClient_GetBuffer(_captureClientPtr, out data, out frames, out flags, out _devPos, out _qpcPos);
                        if (hr < 0) break;
                        try
                        {
                            int byteCount = (int)frames * blockAlign;
                            if (frameBuf.Length < byteCount) frameBuf = new byte[byteCount];
                            if ((flags & 2) != 0) Array.Clear(frameBuf, 0, byteCount); // SILENT
                            else Marshal.Copy(data, frameBuf, 0, byteCount);
                            DataAvailable?.Invoke(this, new AudioFrameEventArgs { Buffer = frameBuf, BytesRecorded = byteCount });
                        }
                        finally
                        {
                            CaptureClient_ReleaseBuffer(_captureClientPtr, frames);
                        }

                        hr = CaptureClient_GetNextPacketSize(_captureClientPtr, out packetFrames);
                        if (hr < 0) break;
                    }
                }
            }
            catch (Exception ex)
            {
                RecordingStopped?.Invoke(this, new CaptureStoppedEventArgs(ex));
                return;
            }
            RecordingStopped?.Invoke(this, new CaptureStoppedEventArgs());
        }

        // ---------- hand-built completion handler CCW ----------

        // We hand-roll a COM object (vtable) for IActivateAudioInterfaceCompletionHandler.
        // This sidesteps CLR auto-CCW behavior that may be the cause of the upfront
        // E_ILLEGAL_METHOD_CALL: namely, the auto-CCW's QueryInterface for IAgileObject
        // returns E_NOINTERFACE, marking the handler as non-agile. Hand-rolled, our QI
        // returns S_OK for IID_IAgileObject so the activation engine accepts us.
        private sealed class CompletionHandlerVtbl : IDisposable
        {
            // Delegate types matching the vtable layout. All are stdcall on x86 (CLR
            // emits the right calling convention from UnmanagedFunctionPointer).
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int QueryInterfaceFn(IntPtr pThis, ref Guid riid, out IntPtr ppv);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate uint AddRefReleaseFn(IntPtr pThis);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int ActivateCompletedFn(IntPtr pThis, IntPtr operation);

            private readonly QueryInterfaceFn   _qi;
            private readonly AddRefReleaseFn    _addRef;
            private readonly AddRefReleaseFn    _release;
            private readonly ActivateCompletedFn _activateCompleted;

            private IntPtr _vtable;            // unmanaged vtable buffer (4 entries)
            private IntPtr _instance;          // unmanaged instance buffer (1 entry: vtable pointer)
            private GCHandle _selfHandle;      // pin so this object stays alive
            private int _refCount = 1;

            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public IntPtr IUnknownPtr => _instance;

            public CompletionHandlerVtbl()
            {
                _selfHandle = GCHandle.Alloc(this);

                _qi                = QI;
                _addRef            = AddRef;
                _release           = Release;
                _activateCompleted = ActivateCompleted;

                int slot = IntPtr.Size;
                _vtable = Marshal.AllocHGlobal(slot * 4);
                Marshal.WriteIntPtr(_vtable, slot * 0, Marshal.GetFunctionPointerForDelegate(_qi));
                Marshal.WriteIntPtr(_vtable, slot * 1, Marshal.GetFunctionPointerForDelegate(_addRef));
                Marshal.WriteIntPtr(_vtable, slot * 2, Marshal.GetFunctionPointerForDelegate(_release));
                Marshal.WriteIntPtr(_vtable, slot * 3, Marshal.GetFunctionPointerForDelegate(_activateCompleted));

                // The instance is just a struct whose first field is a pointer to the vtable.
                _instance = Marshal.AllocHGlobal(slot);
                Marshal.WriteIntPtr(_instance, _vtable);
            }

            private int QI(IntPtr pThis, ref Guid riid, out IntPtr ppv)
            {
                if (riid == IID_IUnknown ||
                    riid == IID_IActivateAudioInterfaceCompletionHandler ||
                    riid == IID_IAgileObject)
                {
                    Interlocked.Increment(ref _refCount);
                    ppv = pThis;
                    return 0; // S_OK
                }
                ppv = IntPtr.Zero;
                return unchecked((int)0x80004002); // E_NOINTERFACE
            }
            private uint AddRef(IntPtr pThis)  => (uint)Interlocked.Increment(ref _refCount);
            private uint Release(IntPtr pThis) => (uint)Interlocked.Decrement(ref _refCount);
            private int ActivateCompleted(IntPtr pThis, IntPtr operation)
            {
                Done.Set();
                return 0;
            }

            public void Dispose()
            {
                if (_instance != IntPtr.Zero) { Marshal.FreeHGlobal(_instance); _instance = IntPtr.Zero; }
                if (_vtable   != IntPtr.Zero) { Marshal.FreeHGlobal(_vtable);   _vtable   = IntPtr.Zero; }
                if (_selfHandle.IsAllocated)  _selfHandle.Free();
                Done.Dispose();
            }
        }

        // ---------- IAudioClient / IAudioCaptureClient method-by-vtable-slot ----------
        //
        // We don't use C# COM-imported interfaces here — the CLR's auto-marshaling for
        // those was implicated in the original failure. Instead we call the vtable
        // entries directly. Slot indices are taken from the public Windows headers.
        // IAudioClient inherits IUnknown (3 slots), then has 12 methods. IAudioCaptureClient
        // inherits IUnknown (3 slots), then has 3 methods.

        private static T GetMethod<T>(IntPtr pUnk, int slotIndex) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(pUnk);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slotIndex * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        // IAudioClient (after 3 IUnknown slots):
        //  3 Initialize, 4 GetBufferSize, 5 GetStreamLatency, 6 GetCurrentPadding,
        //  7 IsFormatSupported, 8 GetMixFormat, 9 GetDevicePeriod,
        //  10 Start, 11 Stop, 12 Reset, 13 SetEventHandle, 14 GetService.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IAudioClient_Initialize_t(IntPtr pThis, int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr pFormat, IntPtr sessionGuid);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IAudioClient_NoArg_t(IntPtr pThis);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IAudioClient_SetEventHandle_t(IntPtr pThis, IntPtr eventHandle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IAudioClient_GetService_t(IntPtr pThis, ref Guid riid, out IntPtr ppv);

        private static int AudioClient_Initialize(IntPtr p, int sm, int sf, long bd, long per, IntPtr fmt, IntPtr sg)
            => GetMethod<IAudioClient_Initialize_t>(p, 3)(p, sm, sf, bd, per, fmt, sg);
        private static int AudioClient_Start(IntPtr p) => GetMethod<IAudioClient_NoArg_t>(p, 10)(p);
        private static int AudioClient_Stop(IntPtr p)  => GetMethod<IAudioClient_NoArg_t>(p, 11)(p);
        private static int AudioClient_SetEventHandle(IntPtr p, IntPtr ev)
            => GetMethod<IAudioClient_SetEventHandle_t>(p, 13)(p, ev);
        private static int AudioClient_GetService(IntPtr p, ref Guid iid, out IntPtr ppv)
            => GetMethod<IAudioClient_GetService_t>(p, 14)(p, ref iid, out ppv);

        // IAudioClient3 (after 14 IAudioClient slots + 3 IAudioClient2 slots = 17):
        //  17 GetSharedModeEnginePeriod, 18 GetCurrentSharedModeEnginePeriod,
        //  19 InitializeSharedAudioStream.
        // IAudioClient3 lets us request a shared-mode engine period below the
        // default 10ms — typically as low as 3ms on Win10 RS1+. It supersedes
        // IAudioClient::Initialize for low-latency capture.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IAudioClient3_GetSharedModeEnginePeriod_t(
            IntPtr pThis, IntPtr pFormat,
            out uint defaultFrames, out uint fundamentalFrames,
            out uint minFrames, out uint maxFrames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IAudioClient3_InitializeSharedAudioStream_t(
            IntPtr pThis, int streamFlags, uint periodInFrames,
            IntPtr pFormat, IntPtr sessionGuid);

        private static int AudioClient3_GetSharedModeEnginePeriod(IntPtr p, IntPtr fmt,
            out uint defFr, out uint fundFr, out uint minFr, out uint maxFr)
            => GetMethod<IAudioClient3_GetSharedModeEnginePeriod_t>(p, 17)(p, fmt, out defFr, out fundFr, out minFr, out maxFr);
        private static int AudioClient3_InitializeSharedAudioStream(IntPtr p,
            int streamFlags, uint periodInFrames, IntPtr fmt, IntPtr sessionGuid)
            => GetMethod<IAudioClient3_InitializeSharedAudioStream_t>(p, 19)(p, streamFlags, periodInFrames, fmt, sessionGuid);

        // IUnknown::QueryInterface (slot 0). Used to upgrade IAudioClient → IAudioClient3.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IUnknown_QueryInterface_t(IntPtr pThis, ref Guid riid, out IntPtr ppv);
        private static int Unknown_QueryInterface(IntPtr p, ref Guid iid, out IntPtr ppv)
            => GetMethod<IUnknown_QueryInterface_t>(p, 0)(p, ref iid, out ppv);
        private static int Unknown_Release(IntPtr p) => GetMethod<IAudioClient_NoArg_t>(p, 2)(p);

        // IAudioCaptureClient (after 3 IUnknown slots):
        //  3 GetBuffer, 4 ReleaseBuffer, 5 GetNextPacketSize.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ICaptureClient_GetBuffer_t(IntPtr pThis, out IntPtr data, out uint frames, out uint flags, out long devPos, out long qpcPos);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ICaptureClient_ReleaseBuffer_t(IntPtr pThis, uint frames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ICaptureClient_GetNextPacketSize_t(IntPtr pThis, out uint frames);

        private static int CaptureClient_GetBuffer(IntPtr p, out IntPtr d, out uint f, out uint fl, out long dp, out long qp)
            => GetMethod<ICaptureClient_GetBuffer_t>(p, 3)(p, out d, out f, out fl, out dp, out qp);
        private static int CaptureClient_ReleaseBuffer(IntPtr p, uint frames)
            => GetMethod<ICaptureClient_ReleaseBuffer_t>(p, 4)(p, frames);
        private static int CaptureClient_GetNextPacketSize(IntPtr p, out uint frames)
            => GetMethod<ICaptureClient_GetNextPacketSize_t>(p, 5)(p, out frames);

        // IActivateAudioInterfaceAsyncOperation::GetActivateResult is slot 3 (after IUnknown).
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IAsyncOp_GetActivateResult_t(IntPtr pThis, out int activateResult, out IntPtr ppActivatedInterface);
        private static int AsyncOp_GetActivateResult(IntPtr p, out int activateResult, out IntPtr ppv)
            => GetMethod<IAsyncOp_GetActivateResult_t>(p, 3)(p, out activateResult, out ppv);

        // ---------- kernel32 ----------

        private const uint EVENT_MODIFY_STATE = 0x0002;
        private const uint SYNCHRONIZE        = 0x00100000;
        private const uint WAIT_FAILED        = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEventEx(IntPtr lpEventAttributes, string lpName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ---------- mmdevapi ----------
        // ExactSpelling=true: the function has no A/W suffix; without ExactSpelling the
        // CLR may probe ActivateAudioInterfaceAsyncW first, which doesn't exist.

        [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [In] ref Guid riid,
            IntPtr activationParams,
            IntPtr completionHandler,
            out IntPtr operation);

        // ---------- struct definitions ----------

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
