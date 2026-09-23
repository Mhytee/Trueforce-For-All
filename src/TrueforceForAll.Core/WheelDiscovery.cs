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
        // USB product string as reported by the device, null if the read
        // failed. Read at discovery because it can outrank the PID: an RS50
        // in G PRO compatibility mode spoofs the G PRO PID (c272) but keeps
        // its own product string ("RS50 Base for PlayStation/PC"), while a
        // real G PRO reports "PRO Racing Wheel" (per mescon, 2026-07).
        public string ProductString;
        // True when the PID is supported by inference (shared HID++ family)
        // but not hardware-confirmed. The UI surfaces a "report back" notice
        // so a user can tell us if Trueforce works but FFB pass-through
        // doesn't on these.
        public bool Unverified;
    }

    public static class WheelDiscovery
    {
        public const ushort LogitechVid = 0x046D;

        // Serializes HID++ discovery probes (root getFeature) across channels.
        // The wheel's HID++ processor handles one transaction at a time, and a
        // getFeature reply does not echo the page it answers: two concurrent
        // probes mean one times out, or worse, reads the other's reply as its
        // own (the rev-light probe latched the OLED display's feature index
        // this way, 2026-08-08). Hold this for the probe only, never for
        // steady-state writes.
        internal static readonly object HidppProbeGate = new object();

        public static readonly (ushort Pid, string Model)[] SupportedPids =
        {
            (0xC272, "Logitech G PRO Racing Wheel (Xbox/PC)"),
            (0xC268, "Logitech G PRO Racing Wheel (PS/PC)"),
            (0xC276, "Logitech RS50"),
            // G923 is hardware-confirmed on both transports. PS/PC (C266) from
            // ACC + FH5 captures (2026-05-17): Trueforce ep3 protocol identical
            // to G PRO, non-Trueforce FFB on ep01 report 0x11/0x08. Xbox/PC
            // (C26D primary, C26E firmware variant) confirmed working by owners:
            // its FFB is HID++ feature 0x0b on the ep1 interrupt endpoint.
            (0xC266, "Logitech G923 (PS/PC)"),
            (0xC26D, "Logitech G923 (Xbox/PC)"),
            (0xC26E, "Logitech G923 (Xbox/PC)"),
        };

        // PIDs that resolve + stream by inference but aren't hardware-proven.
        // Logitech's Xbox wheel variants have historically diverged from their
        // PS siblings in init/handshake (cf. G920 vs G29), so when we add a new
        // wheel on inference alone we list it here and ask the user to report
        // whether FFB pass-through works. Empty today: every supported PID is
        // owner-confirmed. The G923 Xbox PIDs (C26D/C26E) were here until users
        // verified them.
        private static readonly HashSet<ushort> UnverifiedPids = new HashSet<ushort>();

        public static bool IsUnverified(ushort pid) => UnverifiedPids.Contains(pid);

        /// <summary>Bare chassis label for the Account session list / "last used wheel":
        /// strips the "Logitech " prefix and the " Racing Wheel"/console-transport suffix,
        /// e.g. "Logitech G PRO Racing Wheel (Xbox/PC)" -> "G PRO", "Logitech RS50" -> "RS50",
        /// "Logitech G923 (PS/PC)" -> "G923". Keeps VID/PID out of the human-facing label.</summary>
        public static string ShortModel(string fullModel)
        {
            if (string.IsNullOrEmpty(fullModel)) return null;
            string s = fullModel.Trim();
            const string prefix = "Logitech ";
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(prefix.Length);
            int cut = s.IndexOf(" Racing Wheel", StringComparison.OrdinalIgnoreCase);
            if (cut < 0) cut = s.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) s = s.Substring(0, cut);
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        // True when (vid,pid) is one of our supported Trueforce wheels. Used to
        // tell a "USBPcap can't see the FFB" problem apart from "the user pinned
        // a device that isn't even a wheel" so we give the right guidance.
        public static bool IsSupportedWheel(ushort vid, ushort pid)
        {
            if (vid != LogitechVid) return false;
            foreach (var (p, _) in SupportedPids) if (p == pid) return true;
            return false;
        }

        /// <summary>Chassis identity derived from the USB product string.
        /// Strings may only ADD identity (a positive match), never subtract
        /// support: Unknown means "trust the PID". Console-mode wheels can
        /// enumerate with no product string at all, and the G923 variants'
        /// exact strings aren't recorded yet, so anything unrecognized falls
        /// through to PID behavior.</summary>
        public enum WheelChassis { Unknown, GPro, Rs50, G923 }

        public static WheelChassis ClassifyProduct(string productString)
        {
            if (string.IsNullOrEmpty(productString)) return WheelChassis.Unknown;
            string n = productString.ToLowerInvariant();
            if (n.Contains("rs50")) return WheelChassis.Rs50;
            if (n.Contains("g923")) return WheelChassis.G923;
            // A real G PRO reports "PRO Racing Wheel" (mescon, 2026-07,
            // verified live on an RS50 and against a real-G PRO capture).
            if (n.Contains("pro racing wheel")) return WheelChassis.GPro;
            return WheelChassis.Unknown;
        }

        /// <summary>True when the wheel is positively an RS50: the native RS50
        /// PID, or an RS50 product string on any PID. The second case is an
        /// RS50 in G PRO compatibility mode, which spoofs the G PRO PID (c272)
        /// but keeps its own product string (mescon, 2026-07).</summary>
        public static bool IsRs50(ushort pid, string productString) =>
            pid == 0xC276 || ClassifyProduct(productString) == WheelChassis.Rs50;

        public static bool IsG923Pid(ushort pid) =>
            pid == 0xC266 || pid == 0xC26D || pid == 0xC26E;

        /// <summary>True for a G PRO switched to G923 compatibility mode in
        /// G HUB. The wheel re-enumerates under a G923 PID (C26E for the
        /// Xbox/PC edition; owner, 2026-08-29) but keeps its own product
        /// string "PRO Racing Wheel" and its G PRO interface layout (HID++ on
        /// mi_01, Trueforce on mi_02). The HID++ feature table follows the
        /// G923: force on index 0x0B (which the tap's PID pin expects), the
        /// rev bar 0x807A at 0x07 reporting a FIVE-step strip, and neither
        /// the pattern slots (0x807B) nor the screen (0x8130) answer at all
        /// (measured 2026-08-29). So the lights work through the G PRO
        /// interface, while the screen and LIGHTSYNC stay off as on a real
        /// G923. Anything keyed on the PID alone would skip the very
        /// interface the lights live on.</summary>
        public static bool IsGProInG923Mode(ushort pid, string productString) =>
            IsG923Pid(pid) && ClassifyProduct(productString) == WheelChassis.GPro;

        /// <summary>Seed for the FFB tap's HID++ 0x8123 feature-index
        /// resolver: RS50 = 0x10, everything else = the G PRO's 0x0e (the
        /// historical seed for all wheels; G923 keeps it because its indices
        /// are not capture-confirmed). Seed only: the tap's resolver stays
        /// live, and a dead-silent wrong seed self-heals (see
        /// UsbPcapFfbTap.SetFfbFeatureIndexSeed for the confirm-lock
        /// limit).</summary>
        public static byte FfbFeatureIndexSeedFor(ushort pid, string productString) =>
            IsRs50(pid, productString) ? (byte)0x10 : (byte)0x0e;

        /// <summary>User-facing model label for a discovered wheel: the
        /// PID-table model, except an RS50 masquerading on a non-RS50 PID
        /// (G PRO compatibility mode, identified by product string; mescon,
        /// 2026-07) is labeled as such. Single source of truth so the wheel
        /// status, self-test, and notices can never disagree about what was
        /// found.</summary>
        public static string DisplayModel(WheelMatch m)
        {
            if (m == null) return null;
            if (m.Pid != 0xC276 && ClassifyProduct(m.ProductString) == WheelChassis.Rs50)
                return "Logitech RS50 (G PRO compatibility mode)";
            if (IsGProInG923Mode(m.Pid, m.ProductString))
                return "Logitech G PRO Racing Wheel (G923 compatibility mode)";
            return m.Model;
        }

        /// <summary>A Logitech HID device present on the bus that looks like a
        /// racing wheel (by product name) but whose PID isn't one we support.
        /// The usual cause is the wheel being switched to PlayStation/Xbox
        /// console mode instead of PC mode, which makes it enumerate under a
        /// different PID (or not as our Trueforce interface). Surfaced by the
        /// self-test so "wheel not detected" can suggest the PC-mode switch.</summary>
        public sealed class UnsupportedWheel
        {
            public ushort Pid;
            public string Name;
        }

        /// <summary>Enumerate Logitech-VID HID devices whose product name looks
        /// like a wheel but whose PID isn't supported (likely console mode).
        /// Best-effort and conservative: matches on wheel-ish name substrings
        /// so unrelated Logitech gear (mice, keyboards, headsets) isn't
        /// mistaken for a wheel. Empty list = nothing wheel-like in an
        /// unsupported mode. Note: a wheel in Xbox/PS mode may instead present
        /// as an XInput / console controller under a non-Logitech VID, in
        /// which case it won't appear here at all (the caller still hints to
        /// check PC mode).</summary>
        public static List<UnsupportedWheel> FindUnsupportedWheelLike()
        {
            var found = new List<UnsupportedWheel>();
            var supported = new HashSet<ushort>();
            foreach (var (pid, _) in SupportedPids) supported.Add(pid);

            try
            {
                foreach (var dev in DeviceList.Local.GetHidDevices(LogitechVid))
                {
                    ushort pid;
                    try { pid = (ushort)dev.ProductID; }
                    catch { continue; }
                    if (supported.Contains(pid)) continue;

                    string name;
                    try { name = dev.GetProductName() ?? string.Empty; }
                    catch { name = string.Empty; }
                    if (!LooksLikeWheel(name)) continue;

                    bool dup = false;
                    foreach (var f in found) if (f.Pid == pid) { dup = true; break; }
                    if (dup) continue;

                    found.Add(new UnsupportedWheel
                    {
                        Pid  = pid,
                        Name = string.IsNullOrEmpty(name) ? "(unknown)" : name,
                    });
                }
            }
            catch { }

            return found;
        }

        /// <summary>Supported wheels (right VID+PID) that ARE present on the
        /// bus but whose Trueforce haptic HID interface (MI_02 / vendor output
        /// endpoint) we couldn't find. The wheel is recognized, yet the
        /// endpoint we stream to is missing: G HUB may be holding it, a driver
        /// may not have fully attached, or the device enumerated only
        /// partially. This is the "wheel there but no HID endpoint" case,
        /// distinct from console mode (wrong PID) and from a clean
        /// not-plugged-in. Empty = no such half-enumerated wheel.</summary>
        public static List<WheelMatch> FindSupportedWithoutTrueforceInterface()
        {
            var results = new List<WheelMatch>();
            var list = DeviceList.Local;
            foreach (var (pid, model) in SupportedPids)
            {
                bool anyDevice = false, anyTrueforce = false;
                foreach (var dev in list.GetHidDevices(LogitechVid, pid))
                {
                    anyDevice = true;
                    if (IsTrueforceInterface(dev)) { anyTrueforce = true; break; }
                }
                if (anyDevice && !anyTrueforce)
                    results.Add(new WheelMatch
                    {
                        Vid = LogitechVid, Pid = pid, Model = model,
                        Unverified = IsUnverified(pid),
                    });
            }
            return results;
        }

        private static bool LooksLikeWheel(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("wheel") || n.Contains("racing") || n.Contains("trueforce")
                || n.Contains("g923")  || n.Contains("g pro")  || n.Contains("g920")
                || n.Contains("g29")   || n.Contains("rs50");
        }

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

                    // Best-effort product string; identity is additive-only,
                    // so a failed read (null) just means PID behavior.
                    string productString = null;
                    try { productString = dev.GetProductName(); }
                    catch { }

                    results.Add(new WheelMatch
                    {
                        Device = dev,
                        Vid = LogitechVid,
                        Pid = pid,
                        Model = model,
                        ProductString = productString,
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
