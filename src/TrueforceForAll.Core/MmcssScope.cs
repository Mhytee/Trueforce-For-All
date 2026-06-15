// Registers the current thread with the Windows Multimedia Class Scheduler
// Service (MMCSS) for the life of a `using` scope.
//
// MMCSS lifts a thread into the multimedia real-time scheduling band (above
// what ThreadPriority reaches in a normal-priority-class process), the same
// mechanism the Windows audio engine uses to keep its own callbacks on time.
// We use it to protect the real-time threads in the haptic pipeline (the 1 kHz
// pump, the synthesis producer, the loopback stdout pump, the FFB reader) from
// game spikes, GC pauses, and background apps, which otherwise show up as
// audible clicks (and push the ring auto-ratchet, and thus latency, up).
//
// Best-effort and null-safe: if avrt.dll or the "Pro Audio" task profile is
// unavailable the scope is a harmless no-op and the thread simply runs at the
// ThreadPriority it was created with. Dispose reverts; the OS also auto-reverts
// when the thread exits, so even a missed Dispose is safe for thread-lifetime
// scopes.
//
// TrueforceDevice.StreamLoop keeps its own inline equivalent (it predates this
// helper and is the most timing-critical thread, so it is left untouched).

using System;
using System.Runtime.InteropServices;

namespace TrueforceForAll.Core
{
    public sealed class MmcssScope : IDisposable
    {
        // AVRT_PRIORITY values (avrt.h).
        public const int PriorityLow      = -1;
        public const int PriorityNormal   =  0;
        public const int PriorityHigh     =  1;
        public const int PriorityCritical =  2;

        private IntPtr _handle;

        private MmcssScope(IntPtr handle) { _handle = handle; }

        /// <summary>Enter the MMCSS band for <paramref name="taskName"/> (e.g.
        /// "Pro Audio") at the given AVRT priority on the CURRENT thread. Always
        /// returns a scope; if registration failed (older Windows, restricted
        /// host) the scope's handle is null and Dispose is a no-op, so callers
        /// can use it in a plain <c>using</c> without null checks.</summary>
        public static MmcssScope Enter(string taskName, int priority)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                uint taskIndex = 0;
                handle = AvSetMmThreadCharacteristics(taskName, ref taskIndex);
                if (handle != IntPtr.Zero)
                    AvSetMmThreadPriority(handle, priority);
            }
            catch
            {
                // avrt.dll missing / call rejected: fall back to plain priority.
                handle = IntPtr.Zero;
            }
            return new MmcssScope(handle);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                try { AvRevertMmThreadCharacteristics(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }

        [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, int priority);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
    }
}
