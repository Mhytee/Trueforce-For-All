// Find the Logitech direct-drive wheel's Trueforce HID interface.
//
// On Windows, multi-interface USB devices show each interface as its own HID
// collection. We want interface 2 specifically (the audio-haptic endpoint).
// The wheel exposes vendor-defined usage page 0xFFFD / usage 0xFD01 on that
// interface, and the device path string contains "&MI_02".

using System;
using System.Collections.Generic;
using HidSharp;

namespace TrueforceForAll.Core
{
    public sealed class WheelMatch
    {
        public HidDevice Device;
        public ushort Vid;
        public ushort Pid;
        public string Model;
        // True when the PID is supported by inference (shared HID++ family)
        // but not hardware-confirmed. The UI surfaces a "report back" notice
        // so a user can tell us if Trueforce works but FFB pass-through
        // doesn't on these.
        public bool Unverified;
    }

    public static class WheelDiscovery
    {
        public const ushort LogitechVid = 0x046D;

        public static readonly (ushort Pid, string Model)[] SupportedPids =
        {
            (0xC272, "Logitech G PRO Racing Wheel (Xbox/PC)"),
            (0xC268, "Logitech G PRO Racing Wheel (PS/PC)"),
            (0xC276, "Logitech RS50"),
            // G923 PS/PC is hardware-confirmed (ACC + FH5 captures, 2026-05-17):
            // Trueforce ep3 protocol identical to G PRO; non-Trueforce FFB on
            // ep01 report 0x11/0x08. Xbox/PC PIDs (C26D primary, C26E firmware
            // variant) share the HID++ family per the Linux lg4ff driver but
            // are NOT hardware-tested; flagged Unverified below.
            (0xC266, "Logitech G923 (PS/PC)"),
            (0xC26D, "Logitech G923 (Xbox/PC)"),
            (0xC26E, "Logitech G923 (Xbox/PC)"),
        };

        // PIDs that resolve + stream by inference but aren't hardware-proven.
        // Logitech's Xbox wheel variants have historically diverged from
        // their PS siblings in init/handshake (cf. G920 vs G29), so we ship
        // these but ask the user to report whether FFB pass-through works.
        private static readonly HashSet<ushort> UnverifiedPids = new HashSet<ushort>
        {
            0xC26D, 0xC26E,
        };

        public static bool IsUnverified(ushort pid) => UnverifiedPids.Contains(pid);

        // Trueforce HID descriptor on interface 2: usage page 0xFFFD, usage 0xFD01,
        // 64-byte output reports (1 report ID byte + 63 data).
        private const int TrueforceOutputReportLength = 64;

        public static List<WheelMatch> FindAll()
        {
            var results = new List<WheelMatch>();
            var list = DeviceList.Local;

            foreach (var (pid, model) in SupportedPids)
            {
                foreach (var dev in list.GetHidDevices(LogitechVid, pid))
                {
                    if (!IsTrueforceInterface(dev))
                        continue;

                    results.Add(new WheelMatch
                    {
                        Device = dev,
                        Vid = LogitechVid,
                        Pid = pid,
                        Model = model,
                        Unverified = IsUnverified(pid),
                    });
                }
            }

            return results;
        }

        private static bool IsTrueforceInterface(HidDevice dev)
        {
            // Primary discriminator: device path contains the multi-interface
            // descriptor for interface 2.
            string path = dev.DevicePath ?? string.Empty;
            if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Fallback: match by output report length + vendor usage page.
            // Some HID stacks don't expose MI_XX in the path.
            int outLen;
            try { outLen = dev.GetMaxOutputReportLength(); }
            catch { return false; }

            if (outLen != TrueforceOutputReportLength)
                return false;

            try
            {
                var desc = dev.GetReportDescriptor();
                foreach (var item in desc.DeviceItems)
                {
                    foreach (uint usage in item.Usages.GetAllValues())
                    {
                        ushort usagePage = (ushort)((usage >> 16) & 0xFFFF);
                        ushort usageId   = (ushort)(usage & 0xFFFF);
                        if (usagePage == 0xFFFD && usageId == 0xFD01)
                            return true;
                    }
                }
            }
            catch
            {
                // Some HidSharp versions throw if the descriptor can't be parsed.
                // Fall through to false.
            }

            return false;
        }
    }
}
