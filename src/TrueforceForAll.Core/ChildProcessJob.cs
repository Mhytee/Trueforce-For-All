// Ties spawned child processes (USBPcapCMD) to SimHub's lifetime so they can
// never outlive us. Without this, `taskkill /F SimHubWPF` (the deploy script,
// a crash, task manager) leaves the capture child running as an orphan that:
//   (a) keeps the \\.\USBPcapN capture device open, so the NEXT SimHub
//       session's tap discovery fails with access-denied (error 5) forever;
//   (b) holds inherited copies of every socket SimHub had open (Winsock
//       handles are inheritable by default and .NET spawns stdio-redirected
//       children with bInheritHandles=TRUE), so e.g. SimHub's Forza UDP
//       listener port (8000) stays bound under the DEAD SimHub PID and the
//       next session can't receive telemetry.
// Seen live twice on 2026-07-05/06: two consecutive restarts each left a
// zombie USBPcapCMD hogging port 8000 + the USBPcap device.
//
// Fix: a single job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Children
// are assigned to it right after spawn; when this process dies (cleanly or
// via taskkill /F), the OS closes the job handle and kills every member.
// Plus a startup sweep that reaps orphans left by builds predating this fix
// (or by a SimHub that died before assignment could run).

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrueforceForAll.Core
{
    public static class ChildProcessJob
    {
        private static readonly object _lock = new object();
        private static IntPtr _job = IntPtr.Zero;
        private static bool _createFailed;

        /// <summary>Assign a freshly spawned child to the kill-on-close job.
        /// Returns false (and logs) if the job can't be created or the
        /// assignment fails; the caller should proceed anyway — the child is
        /// merely unprotected, exactly the pre-fix behavior.</summary>
        public static bool TryAssign(Process child, Action<string> log = null)
        {
            if (child == null) return false;
            try
            {
                IntPtr job = GetOrCreateJob(log);
                if (job == IntPtr.Zero) return false;
                if (!AssignProcessToJobObject(job, child.Handle))
                {
                    log?.Invoke($"ChildProcessJob: assign failed (Win32 {Marshal.GetLastWin32Error()}); child {child.Id} will not auto-die with SimHub.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"ChildProcessJob: assign threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>Kill processes named <paramref name="processName"/> whose
        /// parent process is gone — orphans from a previous SimHub session.
        /// A live parent (some other tool legitimately running a capture)
        /// is left alone. Elevated orphans can't be killed from a
        /// non-elevated SimHub; that failure is logged with the manual fix.</summary>
        public static void KillOrphans(string processName, Action<string> log = null)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(processName); }
            catch { return; }

            foreach (var p in procs)
            {
                try
                {
                    int parentPid = GetParentPid(p);
                    if (parentPid > 0 && IsProcessAlive(parentPid)) continue;

                    p.Kill();
                    log?.Invoke($"ChildProcessJob: reaped orphaned {processName} (pid {p.Id}, dead parent {parentPid}) left by a previous session.");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"ChildProcessJob: couldn't reap orphaned {processName} pid {p.Id}: {ex.Message}. If it was spawned by an elevated SimHub, kill it manually: taskkill /F /PID {p.Id} (as admin).");
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        private static IntPtr GetOrCreateJob(Action<string> log)
        {
            lock (_lock)
            {
                if (_job != IntPtr.Zero || _createFailed) return _job;

                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    _createFailed = true;
                    log?.Invoke($"ChildProcessJob: CreateJobObject failed (Win32 {Marshal.GetLastWin32Error()}).");
                    return IntPtr.Zero;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };
                int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr infoPtr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, infoPtr, (uint)len))
                    {
                        _createFailed = true;
                        log?.Invoke($"ChildProcessJob: SetInformationJobObject failed (Win32 {Marshal.GetLastWin32Error()}).");
                        CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }

                // Never closed while the process lives: the handle closing IS
                // the kill signal, and process teardown closes it for us.
                _job = job;
                return _job;
            }
        }

        private static int GetParentPid(Process p)
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(p.Handle, 0, ref pbi,
                Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out _);
            return status == 0 ? pbi.InheritedFromUniqueProcessId.ToInt32() : -1;
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (ArgumentException) { return false; }   // no such process
            catch { return true; }                        // exists but inaccessible
        }

        // ---------- P/Invoke ----------

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
    }
}
