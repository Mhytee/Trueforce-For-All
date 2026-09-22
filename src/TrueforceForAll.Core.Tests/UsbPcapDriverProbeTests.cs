// AnyUsbPcapInterfacePresent answers "is USBPcap's capture driver actually
// attached", which the self-test and the Reinstall button now gate on. It
// reads the DOS device namespace through a P/Invoke, so the failure mode
// worth guarding is a bad marshalling signature: the probe catches broadly
// and reports true on failure, which looks exactly like a healthy machine.
//
// A pure unit test cannot manufacture a driver, so these check the two
// things a test process CAN observe: the probe is side-effect free and
// stable, and where USBPcap is installed it agrees with the interface
// enumeration that discovery itself depends on. The second one is the real
// invariant; it self-skips on a machine without USBPcap.

using System;
using System.Diagnostics;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class UsbPcapDriverProbeTests
    {
        [Fact]
        public void Probe_IsStableAndSideEffectFree()
        {
            bool first = WheelUsbDiscovery.AnyUsbPcapInterfacePresent();
            for (int i = 0; i < 5; i++)
                Assert.Equal(first, WheelUsbDiscovery.AnyUsbPcapInterfacePresent());
        }

        [Fact]
        public void Probe_AgreesWithUsbPcapCmdEnumeration()
        {
            string cmd = UsbPcapFfbTap.LocateUsbPcapCmd();
            if (cmd == null) return;   // no USBPcap on this machine; nothing to compare against

            bool cmdSeesAny = EnumeratedInterfaceCount(cmd) > 0;
            Assert.Equal(cmdSeesAny, WheelUsbDiscovery.AnyUsbPcapInterfacePresent());
        }

        // Mirrors WheelUsbDiscovery's own --extcap-interfaces parse. Duplicated
        // rather than exposed: the point is to compare the probe against an
        // INDEPENDENT reading of the same truth, not against shared code.
        private static int EnumeratedInterfaceCount(string usbPcapCmdPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = usbPcapCmdPath,
                Arguments = "--extcap-interfaces",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            int n = 0;
            using (var proc = Process.Start(psi))
            {
                if (proc == null) return 0;
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                foreach (string raw in stdout.Split('\n'))
                    if (raw.IndexOf(@"{value=\\.\USBPcap", StringComparison.OrdinalIgnoreCase) >= 0)
                        n++;
            }
            return n;
        }
    }
}
