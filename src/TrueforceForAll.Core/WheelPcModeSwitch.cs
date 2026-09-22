// Switch a PlayStation-mode G923 into its PC (classic) mode, without G HUB.
//
// A G923 PlayStation edition boots as 046d:c267. In that mode it exposes no
// MI_02 Trueforce interface, so the plugin cannot see it at all. Running G HUB
// switches it: it re-enumerates as 046d:c266 with the full interface set. The
// wheel does NOT keep that across a power cycle (owner, 2026-09-19), which is
// why the workaround was never "install G HUB once" but "run G HUB again every
// time you start the computer". It is the only step in our troubleshooting
// that asks for Logitech software, and the only one that never stops.
//
// The switch needs nothing from Logitech. It is the classic lg4ff multimode
// command: a SET_REPORT under report id 0x30 carrying
// F8 09 07 01 01 00 00 ("switch mode to G923 with detach"). The wheel
// USB-resets and comes back under its PC-mode product id. Source: mescon's
// logitech-trueforce-linux-driver (mainline/dd-lg4ff.c,
// dd_lg4ff_switch_from_ps_mode + dd_lg4ff_mode_switch_30_g923), ported there
// verbatim from berarma's new-lg4ff (hid-lg4ff.c:418-421, :1577-1598). Both
// run it unconditionally at every probe; neither ports a way back, which
// matters less than it sounds, because the wheel finds its own way back at the
// next power cycle.
//
// UNPROVEN ON WINDOWS as of 2026-09-19, and the failure mode is a refusal
// rather than damage. Windows validates a report id and length against the
// report descriptor, while the Linux driver sidesteps that by taking an
// existing output report and overwriting its id to 0x30. If the PS-mode
// descriptor never declares 0x30, every write here is rejected. That is what
// Result.Refused and the per-collection logging are for: a field report then
// tells us whether the wheel refused us, or whether the write landed and the
// wheel ignored it.
//
// Gated on product id 0xC267 exactly, never on "wheel-like with an
// unsupported product id". The F8 09 family is understood by the whole
// classic Logitech wheel line, and F8 09 07 means "become a G923" to anything
// that parses it, so firing it at an unrecognized wheel could leave someone's
// G29 or DFGT in a mode they have no obvious way to undo.

using System;
using System.Collections.Generic;
using HidSharp;

namespace TrueforceForAll.Core
{
    public static class WheelPcModeSwitch
    {
        /// <summary>G923 PlayStation edition, PlayStation mode. Becomes 0xC266
        /// (which WheelDiscovery supports) once switched.</summary>
        public const ushort G923PsPid = 0xC267;

        // Report id the wheel expects while still in PlayStation mode. The Linux
        // driver forces it explicitly rather than using whichever id the
        // descriptor's output report carries, so it is part of the command.
        private const byte ReportId = 0x30;

        private static readonly byte[] SwitchToG923 = { 0xF8, 0x09, 0x07, 0x01, 0x01, 0x00, 0x00 };

        public enum Result
        {
            /// <summary>Nothing in PlayStation mode on the bus. The common case,
            /// and not worth a log line.</summary>
            NoDevice,
            /// <summary>A collection took the write. That is not proof the wheel
            /// switched, only that Windows accepted the report: watch for the
            /// 0xC266 arrival.</summary>
            Sent,
            /// <summary>The wheel is there and every collection refused.</summary>
            Refused,
        }

        /// <summary>True when a G923 is sitting on the bus in PlayStation mode.
        /// Cheap enough for the rediscovery tick; used to re-arm the one-shot.</summary>
        public static bool AnyPresent()
        {
            try
            {
                foreach (var unused in DeviceList.Local.GetHidDevices(WheelDiscovery.LogitechVid, G923PsPid))
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>Send the PC-mode switch to a PlayStation-mode G923, if one is
        /// present. Tries every collection the wheel exposes, because which one
        /// carries a writable report is exactly what we do not know yet on
        /// Windows. Stops at the first accepted write: the wheel is about to
        /// detach, so a second send would go to a stale handle.</summary>
        public static Result TrySwitch(Action<string> log)
        {
            var devices = new List<HidDevice>();
            try
            {
                foreach (var d in DeviceList.Local.GetHidDevices(WheelDiscovery.LogitechVid, G923PsPid))
                    devices.Add(d);
            }
            catch (Exception ex)
            {
                Log(log, "enumeration failed: " + ex.Message);
                return Result.NoDevice;
            }

            if (devices.Count == 0) return Result.NoDevice;

            Log(log, $"G923 in PlayStation mode (0x{G923PsPid:X4}) with {devices.Count} interface(s). "
                   + "Sending the PC-mode switch so the wheel comes back as 0xC266; no G HUB needed.");

            foreach (var dev in devices)
                if (TrySend(dev, log)) return Result.Sent;

            Log(log, "every interface refused the switch. The wheel stays in PlayStation mode; "
                   + "open G HUB once and let it detect the wheel, then close it.");
            return Result.Refused;
        }

        private static bool TrySend(HidDevice dev, Action<string> log)
        {
            string path; try { path = dev.DevicePath ?? "(no path)"; } catch { path = "(no path)"; }
            int outLen  = SafeLen(dev, true);
            int featLen = SafeLen(dev, false);

            HidStream s;
            try { s = dev.Open(new OpenConfiguration()); }
            catch (Exception ex) { Log(log, $"open refused ({path}): {ex.Message}"); return false; }

            using (s)
            {
                s.WriteTimeout = 500;

                // Output report first: that is where the Linux driver's SET_REPORT
                // lands. A collection declaring no output report cannot carry it,
                // so do not spend a write on one.
                if (outLen > 0)
                {
                    var buf = BuildSwitchReport(outLen);
                    try
                    {
                        s.Write(buf);
                        Log(log, $"switch accepted as an output report (len={buf.Length}) on {path}. "
                               + "Expect the wheel to drop off the bus and return as 0xC266.");
                        return true;
                    }
                    catch (Exception ex)
                    { Log(log, $"output-report write refused (len={buf.Length}) on {path}: {ex.Message}"); }
                }

                // Feature fallback. Windows routes a feature write over the control
                // pipe, which is the transport the Linux SET_REPORT uses anyway, so
                // a descriptor that declares 0x30 as a feature report rather than an
                // output report still works.
                if (featLen > 0)
                {
                    var buf = BuildSwitchReport(featLen);
                    try
                    {
                        s.SetFeature(buf);
                        Log(log, $"switch accepted as a feature report (len={buf.Length}) on {path}. "
                               + "Expect the wheel to drop off the bus and return as 0xC266.");
                        return true;
                    }
                    catch (Exception ex)
                    { Log(log, $"feature write refused (len={buf.Length}) on {path}: {ex.Message}"); }
                }

                if (outLen <= 0 && featLen <= 0)
                    Log(log, $"no writable report on {path} (output={outLen}, feature={featLen}).");
            }
            return false;
        }

        /// <summary>Report id, then the seven command bytes, zero-padded to the
        /// collection's report length the way every other F8 writer here does it
        /// (see LegacyLedF8Channel.Send). A collection whose report is shorter
        /// than the command cannot carry it; the 8-byte floor keeps the buffer
        /// well-formed and lets Windows do the rejecting. Public so the wire
        /// format is unit-testable: a typo in these bytes is the one failure we
        /// cannot catch on hardware we own.</summary>
        public static byte[] BuildSwitchReport(int len)
        {
            var buf = new byte[len < 8 ? 8 : len];
            buf[0] = ReportId;
            Array.Copy(SwitchToG923, 0, buf, 1, SwitchToG923.Length);
            return buf;
        }

        private static int SafeLen(HidDevice d, bool output)
        {
            try { return output ? d.GetMaxOutputReportLength() : d.GetMaxFeatureReportLength(); }
            catch { return -1; }
        }

        private static void Log(Action<string> log, string msg)
        {
            if (log == null) return;
            try { log("PC-mode switch: " + msg); } catch { }
        }
    }
}
