// ChildProcessJob is the orphan-prevention layer for spawned capture
// children (USBPcapCMD, the loopback helper). The kill-on-job-close
// behavior itself only fires when THIS process dies, which a unit test
// can't observe from the inside — so these tests cover the two things a
// test process CAN observe: assignment succeeds on a real child, and the
// orphan reaper correctly distinguishes live-parent children (ours — must
// survive) from dead-parent orphans.

using System;
using System.Diagnostics;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class ChildProcessJobTests
    {
        private static Process SpawnSleeper()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping 127.0.0.1 -n 30 >nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return Process.Start(psi);
        }

        [Fact]
        public void TryAssign_SucceedsOnLiveChild()
        {
            var child = SpawnSleeper();
            try
            {
                Assert.True(ChildProcessJob.TryAssign(child));
            }
            finally
            {
                try { child.Kill(); } catch { }
                child.Dispose();
            }
        }

        [Fact]
        public void KillOrphans_LeavesChildrenOfLiveParentsAlone()
        {
            // Our own child: parent (this test process) is alive, so the
            // reaper must not touch it even though the name matches.
            var child = SpawnSleeper();
            try
            {
                ChildProcessJob.KillOrphans("cmd");
                Assert.False(child.HasExited);
            }
            finally
            {
                try { child.Kill(); } catch { }
                child.Dispose();
            }
        }

        [Fact]
        public void KillOrphans_ReapsDeadParentOrphans()
        {
            // Build a real orphan: a middle cmd.exe spawns a grandchild ping
            // and exits immediately, leaving the grandchild with a dead
            // parent PID. `start /b ping` detaches so the middle process
            // doesn't wait for it.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start /b ping.exe 127.0.0.1 -n 30 >nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var middle = Process.Start(psi))
            {
                middle.WaitForExit(5000);
            }

            // The grandchild ping.exe is now an orphan (its parent cmd is
            // gone). The reaper should find and kill it. Poll briefly: PID
            // teardown of the middle process can lag a few ms.
            bool reaped = false;
            for (int attempt = 0; attempt < 10 && !reaped; attempt++)
            {
                ChildProcessJob.KillOrphans("ping");
                System.Threading.Thread.Sleep(100);
                reaped = Process.GetProcessesByName("ping").Length == 0;
            }
            Assert.True(reaped, "orphaned ping.exe (dead parent) was not reaped");
        }
    }
}
