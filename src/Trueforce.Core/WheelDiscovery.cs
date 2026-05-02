// Find the Logitech direct-drive wheel's Trueforce HID interface.
//
// On Windows, multi-interface USB devices show each interface as its own HID
// collection. We want interface 2 specifically (the audio-haptic endpoint).
// The wheel exposes vendor-defined usage page 0xFFFD / usage 0xFD01 on that
// interface, and the device path string contains "&MI_02".

using System;
using System.Collections.Generic;
using HidSharp;

namespace SimHubTrueforce.Core
{
    public sealed class WheelMatch
    {
        public HidDevice Device;
        public ushort Vid;
        public ushort Pid;
        public string Model;
    }

    public static class WheelDiscovery
    {
        public const ushort LogitechVid = 0x046D;

        public static readonly (ushort Pid, string Model)[] SupportedPids =
        {
            (0xC272, "Logitech G PRO Racing Wheel (Xbox/PC)"),
            (0xC268, "Logitech G PRO Racing Wheel (PS/PC)"),
            (0xC276, "Logitech RS50"),
        };

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
