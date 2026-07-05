// tffa-fakegame - a game substitute for validating TFFAUsbFilter without a sim.
//
// It talks to the Logitech wheel exactly like a game's FFB does: HID++ writes
// on the wheel's control interface. Three jobs:
//
//   1. RESOLVE  - HID++ root.getFeature(0x8123) to read the wheel's real FFB
//                 feature index (ground truth; the G923's index was unknown).
//   2. CLAIM    - open \\.\TFFAControl + PING so the driver marks a wheel owner
//                 (INTERCEPTED only fires when an owner is set).
//   3. INJECT   - send game-shaped FFB writes (0x11 FF <idx> 0x2x ...) so the
//                 driver's classifier acts on them.
//
// What you should see in DebugView (kernel capture), given the driver default
// feature index is 0x0E:
//   * inject at 0x0E while owner is claimed  -> "INTERCEPTED"     (driver drops it; wheel never sees it)
//   * inject at the resolved G923 index      -> "FFB_LEAK_PASS featIdx=0xNN" (would-be game leak)
//
// Usage:
//   tffa-fakegame                 resolve + claim + 2-phase demo (default)
//   tffa-fakegame --resolve       just print the wheel's FFB feature index and exit
//   tffa-fakegame --index 0x0b    inject only, at a specific feature index
//   tffa-fakegame --no-claim      inject without claiming ownership
//   tffa-fakegame --seconds 5     duration per inject phase (default 3)
//   tffa-fakegame --rate 200      writes per second (default 200)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using HidSharp;

namespace TffaFakeGame
{
    internal static class Program
    {
        // HID++ constants (mirror WheelLedChannel / TFFADriverChannel).
        const byte RepShort = 0x10; const int LenShort = 7;
        const byte RepLong  = 0x11; const int LenLong  = 20;
        const byte RepVLong = 0x12; const int LenVLong = 64;
        const byte DevWired = 0xFF;
        const byte RootIndex = 0x00;
        const byte RootGetFn = 0x0B;         // root.getFeature, sw-id 0x0B
        const ushort FfbPage = 0x8123;       // G-series force feedback feature page
        const byte FfbFnByte = 0x21;         // fn2 | sw-id 1 -> high nibble 0x2_ (what the driver keys on)
        const byte DriverDefaultIdx = 0x0E;  // TFFAUsbFilter's built-in g_FfbFeatureIndex

        const ushort LogitechVid = 0x046D;
        static readonly ushort[] SupportedPids =
            { 0xC272, 0xC268, 0xC276, 0xC266, 0xC26D, 0xC26E };

        // Rev-LED protocol (mirrors WheelLedChannel): page 0x807A, sw-id 0x0D.
        const ushort PageRevLights = 0x807A;
        const byte LedSwId = 0x0D;
        static byte LedFn(int fn) => (byte)((fn << 4) | LedSwId);

        // ---- control device P/Invoke ---------------------------------------
        const uint GENERIC_RW = 0xC0000000; const uint OPEN_EXISTING = 3;
        const uint IOCTL_TFFA_PING = 0x222000u; const uint PING_MAGIC = 0x54464641u;
        const uint IOCTL_TFFA_SET_FFB_INDEX = 0x222008u;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr sa, uint create, uint flags, IntPtr template);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code,
            byte[] inBuf, uint inLen, byte[] outBuf, uint outLen, out uint returned, IntPtr ov);

        static int Main(string[] args)
        {
            // LED check: drive the rev bar over HID++. No driver / cert / reboot
            // needed - pure user-mode. Run it on any machine where the wheel is
            // in PC mode (G HUB present). Close SimHub first so nothing else is
            // holding the HID++ pipe.
            if (Has(args, "--leds")) return RunLeds(GetInt(args, "--cycles", 3));

            bool resolveOnly = Has(args, "--resolve");
            bool noClaim     = Has(args, "--no-claim");
            int seconds      = GetInt(args, "--seconds", 3);
            int rate         = GetInt(args, "--rate", 200);
            int explicitIdx  = GetHex(args, "--index", -1);
            if (Has(args, "--help") || Has(args, "-h")) { PrintHelp(); return 0; }

            Console.WriteLine("tffa-fakegame  -  HID++ FFB write generator for TFFAUsbFilter validation\n");

            HidStream shortS = null, longS = null, vlongS = null; string name = null;
            if (!TryOpenWheel(ref shortS, ref longS, ref vlongS, ref name))
            {
                Console.WriteLine("ERROR: no supported Logitech wheel HID++ interface found.");
                Console.WriteLine("       Is the G923 plugged in and in PC mode? (VID 046D, PID C266/C26D/C26E)");
                return 2;
            }
            Console.WriteLine($"Wheel HID++ open: {name}  (short={shortS!=null} long={longS!=null} vlong={vlongS!=null})");

            // --- RESOLVE ---
            int ffbIdx = ResolveFeature(shortS, longS, vlongS, FfbPage);
            if (ffbIdx > 0)
                Console.WriteLine($"RESOLVED: FFB feature (page 0x{FfbPage:X4}) index = 0x{ffbIdx:X2}   <-- your wheel's real FFB index");
            else
                Console.WriteLine($"RESOLVE: wheel did not answer getFeature(0x{FfbPage:X4}); will fall back to 0x0B for the leak demo.");

            if (resolveOnly) { Cleanup(shortS, longS, vlongS, null); return ffbIdx > 0 ? 0 : 3; }
            if (longS == null) { Console.WriteLine("ERROR: no LONG (0x11) collection to send FFB writes on."); Cleanup(shortS, longS, vlongS, null); return 4; }

            // --- CLAIM ownership so INTERCEPTED can fire ---
            SafeFileHandle ctrl = null;
            if (!noClaim)
            {
                ctrl = CreateFileW(@"\\.\TFFAControl", GENERIC_RW, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (ctrl.IsInvalid)
                {
                    Console.WriteLine($"CLAIM: could not open \\\\.\\TFFAControl (Win32 {Marshal.GetLastWin32Error()}). "
                        + "Is the driver installed and the machine rebooted? Continuing WITHOUT ownership "
                        + "(you'll see FFB_LEAK_PASS but not INTERCEPTED).");
                    ctrl.Dispose(); ctrl = null;
                }
                else
                {
                    var outb = new byte[4];
                    if (DeviceIoControl(ctrl, IOCTL_TFFA_PING, null, 0, outb, 4, out _, IntPtr.Zero))
                    {
                        uint magic = BitConverter.ToUInt32(outb, 0);
                        Console.WriteLine(magic == PING_MAGIC
                            ? "CLAIM: PING ok - this process is now the wheel owner."
                            : $"CLAIM: PING returned 0x{magic:X8} (expected 0x{PING_MAGIC:X8}).");
                    }
                    else Console.WriteLine($"CLAIM: PING failed (Win32 {Marshal.GetLastWin32Error()}).");
                }
            }

            // Push the resolved index down so the driver intercepts the REAL game
            // FFB (not just the 0x0E default). This is what closes the loop.
            if (ctrl != null && ffbIdx > 0)
            {
                var inb = BitConverter.GetBytes((uint)ffbIdx);
                if (DeviceIoControl(ctrl, IOCTL_TFFA_SET_FFB_INDEX, inb, 4, null, 0, out _, IntPtr.Zero))
                    Console.WriteLine($"SET: driver FFB index -> 0x{ffbIdx:X2} (now intercepts your real FFB).");
                else
                    Console.WriteLine($"SET: set-index failed (Win32 {Marshal.GetLastWin32Error()}) - is this the fixed driver build?");
            }

            // --- INJECT ---
            if (explicitIdx >= 0)
            {
                Console.WriteLine($"\nINJECT: {seconds}s at feature index 0x{explicitIdx:X2}, {rate} writes/s ...");
                InjectFfb(longS, (byte)explicitIdx, seconds, rate);
            }
            else
            {
                int realIdx = ffbIdx > 0 ? ffbIdx : DriverDefaultIdx;
                bool armed = ctrl != null && ffbIdx > 0;

                Console.WriteLine($"\nPhase 1: inject at your REAL FFB index 0x{realIdx:X2} -> expect "
                    + (armed ? "INTERCEPTED (driver drops your real game FFB = sole-writer)"
                             : "FFB_LEAK_PASS (index wasn't set - claim or resolve failed)"));
                InjectFfb(longS, (byte)realIdx, seconds, rate);

                // A different index proves selectivity: only the configured FFB is
                // dropped; every other HID++ write still reaches the wheel.
                byte otherIdx = (byte)(realIdx == DriverDefaultIdx ? 0x10 : DriverDefaultIdx);
                Console.WriteLine($"\nPhase 2: inject at a DIFFERENT index 0x{otherIdx:X2} -> expect "
                    + $"FFB_LEAK_PASS featIdx=0x{otherIdx:X2} (proves only your real FFB is dropped, nothing else)");
                InjectFfb(longS, otherIdx, seconds, rate);
            }

            Console.WriteLine("\nDone. Read the DebugView log:");
            Console.WriteLine("  INTERCEPTED                 = driver dropped the game-shaped write (sole-writer works)");
            Console.WriteLine("  FFB_LEAK_PASS featIdx=0xNN  = an FFB write the driver let through; 0xNN is the index to bake in");
            Cleanup(shortS, longS, vlongS, ctrl);
            return 0;
        }

        // ---- wheel discovery (mirrors WheelLedChannel grouping) --------------
        static bool TryOpenWheel(ref HidStream shortS, ref HidStream longS, ref HidStream vlongS, ref string name)
        {
            var groups = new Dictionary<string, List<HidDevice>>(StringComparer.OrdinalIgnoreCase);
            var list = DeviceList.Local;
            foreach (var pid in SupportedPids)
                foreach (var dev in list.GetHidDevices(LogitechVid, pid))
                {
                    string path = dev.DevicePath ?? "";
                    if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0) continue; // Trueforce audio, not HID++
                    int col = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
                    string stem = col > 0 ? path.Substring(0, col) : path;
                    if (!groups.TryGetValue(stem, out var g)) groups[stem] = g = new List<HidDevice>();
                    g.Add(dev);
                }

            foreach (var kv in groups)
            {
                HidStream s = null, l = null, v = null;
                foreach (var dev in kv.Value)
                {
                    int outLen; try { outLen = dev.GetMaxOutputReportLength(); } catch { continue; }
                    HidStream st; try { st = dev.Open(new OpenConfiguration()); } catch { continue; }
                    st.ReadTimeout = 250; st.WriteTimeout = 250;
                    if (outLen == LenShort && s == null) { s = st; name = SafeName(dev); }
                    else if (outLen == LenLong && l == null) l = st;
                    else if (outLen == LenVLong && v == null) v = st;
                    else st.Dispose();
                }
                if (l != null || v != null) { shortS = s; longS = l; vlongS = v; if (name == null) name = "wheel"; return true; }
                s?.Dispose(); l?.Dispose(); v?.Dispose();
            }
            return false;
        }

        static string SafeName(HidDevice d) { try { return d.GetFriendlyName(); } catch { return "wheel"; } }

        // ---- HID++ getFeature(page) -> feature index -------------------------
        static int ResolveFeature(HidStream shortS, HidStream longS, HidStream vlongS, ushort page)
        {
            // Request over SHORT if present, else pad into a LONG (G923 has no
            // 7-byte SHORT collection - mirrors the WheelLedChannel G923 fix).
            try
            {
                if (shortS != null)
                {
                    var req = new byte[LenShort];
                    req[0] = RepShort; req[1] = DevWired; req[2] = RootIndex; req[3] = RootGetFn;
                    req[4] = (byte)(page >> 8); req[5] = (byte)(page & 0xFF);
                    shortS.Write(req);
                }
                else
                {
                    var req = new byte[LenLong];
                    req[0] = RepLong; req[1] = DevWired; req[2] = RootIndex; req[3] = RootGetFn;
                    req[4] = (byte)(page >> 8); req[5] = (byte)(page & 0xFF);
                    (longS ?? vlongS).Write(req);
                }
            }
            catch { return -1; }

            foreach (var s in new[] { longS, vlongS, shortS })
            {
                if (s == null) continue;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    var resp = new byte[LenVLong]; int n;
                    try { n = s.Read(resp, 0, resp.Length); }
                    catch (TimeoutException) { break; }
                    catch { break; }
                    if (n < 5) continue;
                    if (resp[1] != DevWired || resp[2] != RootIndex) continue;
                    if (resp[3] == 0xFF) return -1;         // HID++ error
                    int idx = resp[4];
                    if (idx > 0 && idx < 0x80) return idx;
                }
            }
            return -1;
        }

        // ---- inject game-shaped FFB writes -----------------------------------
        static void InjectFfb(HidStream longS, byte featIdx, int seconds, int rate)
        {
            int gapMs = rate > 0 ? Math.Max(1, 1000 / rate) : 5;
            long ticks = (long)seconds * rate; if (ticks <= 0) ticks = 1;
            int sent = 0;
            for (long i = 0; i < ticks; i++)
            {
                // A moving int16 force so the frames look like a live FFB stream.
                short force = (short)(Math.Sin(i * 0.05) * 12000);
                var f = new byte[LenLong];
                f[0] = RepLong; f[1] = DevWired; f[2] = featIdx; f[3] = FfbFnByte;
                f[10] = (byte)(force >> 8); f[11] = (byte)(force & 0xFF);   // BE16 force target at bytes 10-11 (where the decoder reads it)
                try { longS.Write(f); sent++; }
                catch (Exception ex) { Console.WriteLine($"  write failed after {sent}: {ex.Message}"); break; }
                Thread.Sleep(gapMs);
            }
            Console.WriteLine($"  sent {sent} FFB-shaped writes at idx 0x{featIdx:X2}.");
        }

        static void Cleanup(HidStream s, HidStream l, HidStream v, SafeFileHandle ctrl)
        {
            try { s?.Dispose(); } catch { } try { l?.Dispose(); } catch { } try { v?.Dispose(); } catch { }
            try { ctrl?.Dispose(); } catch { }
        }

        // ---- LED check (drive the rev bar over HID++) ------------------------
        static int RunLeds(int cycles)
        {
            HidStream shortS = null, longS = null, vlongS = null; string name = null;
            if (!TryOpenWheel(ref shortS, ref longS, ref vlongS, ref name))
            {
                Console.WriteLine("ERROR: wheel not found. Is it in PC mode (G HUB installed) and is SimHub closed?");
                return 2;
            }
            Console.WriteLine($"Wheel HID++ open: {name}");

            int idx = ResolveFeature(shortS, longS, vlongS, PageRevLights);
            if (idx <= 0)
            {
                Console.WriteLine("This wheel did not answer getFeature(0x807A) - it may not expose HID++ rev LEDs.");
                Cleanup(shortS, longS, vlongS, null);
                return 3;
            }
            Console.WriteLine($"Rev-LED feature (page 0x807A) index = 0x{idx:X2}. Lighting the rim - {cycles} cycles...\n");

            // Pad SHORT (0x10) into a LONG (0x11) when the wheel has no 7-byte
            // collection (G923). Same trick as the WheelLedChannel G923 fix.
            void WS(byte[] r)
            {
                if (shortS != null) { shortS.Write(r); return; }
                var p = new byte[LenLong]; p[0] = RepLong; Array.Copy(r, 1, p, 1, r.Length - 1);
                (longS ?? vlongS).Write(p);
            }
            void WL(byte[] r) { if (longS != null) longS.Write(r); else if (vlongS != null) vlongS.Write(r); else WS(r); }

            void SendPair(int lvl)
            {
                WS(new byte[] { RepShort, DevWired, (byte)idx, LedFn(2), 0, 0, 0 });
                var f6 = new byte[LenLong];
                f6[0] = RepLong; f6[1] = DevWired; f6[2] = (byte)idx; f6[3] = LedFn(6);
                f6[4] = 0; f6[5] = 1; f6[6] = 0; f6[7] = 0x0A; f6[8] = 0; f6[9] = (byte)lvl;
                WL(f6);
            }
            void Bar(int lvl)
            {
                SendPair(lvl);
                Console.Write("\r  revs: [" + new string('#', lvl).PadRight(10) + $"] {lvl,2}/10 ");
            }

            try
            {
                // Arm once (G HUB's sequence), then sweep the bar up and down.
                WS(new byte[] { RepShort, DevWired, (byte)idx, LedFn(0), 0, 0, 0 }); Thread.Sleep(4);
                WS(new byte[] { RepShort, DevWired, (byte)idx, LedFn(1), 0, 0, 0 }); Thread.Sleep(4);
                WS(new byte[] { RepShort, DevWired, (byte)idx, LedFn(2), 0, 0, 0 }); Thread.Sleep(4);
                WS(new byte[] { RepShort, DevWired, (byte)idx, LedFn(3), 0x02, 0, 0 }); Thread.Sleep(4);
                WS(new byte[] { RepShort, DevWired, (byte)idx, LedFn(0), 0, 0, 0 }); Thread.Sleep(4);

                for (int c = 0; c < cycles; c++)
                {
                    for (int l = 0; l <= 10; l++) { Bar(l); Thread.Sleep(110); }
                    for (int l = 9; l >= 0; l--) { Bar(l); Thread.Sleep(110); }
                }
                SendPair(0);
                Console.WriteLine("\n\n  Done. If the rim lit up and swept, your G923 rev LEDs work over HID++.");
            }
            catch (Exception ex) { Console.WriteLine($"\n  LED write failed: {ex.Message}"); }
            Cleanup(shortS, longS, vlongS, null);
            return 0;
        }

        // ---- arg helpers -----------------------------------------------------
        static bool Has(string[] a, string f)
        { foreach (var x in a) if (string.Equals(x, f, StringComparison.OrdinalIgnoreCase)) return true; return false; }

        static int GetInt(string[] a, string f, int def)
        {
            for (int i = 0; i < a.Length - 1; i++)
                if (string.Equals(a[i], f, StringComparison.OrdinalIgnoreCase) && int.TryParse(a[i + 1], out int v)) return v;
            return def;
        }

        static int GetHex(string[] a, string f, int def)
        {
            for (int i = 0; i < a.Length - 1; i++)
                if (string.Equals(a[i], f, StringComparison.OrdinalIgnoreCase))
                {
                    string s = a[i + 1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? a[i + 1].Substring(2) : a[i + 1];
                    if (int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v)) return v;
                }
            return def;
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"tffa-fakegame - send game-shaped HID++ FFB writes to validate TFFAUsbFilter.

  (no args)        resolve FFB index + claim ownership + 2-phase inject demo
  --resolve        print the wheel's FFB feature index (page 0x8123) and exit
  --leds           light up the rev bar over HID++ (no driver/cert/reboot needed)
  --cycles N       number of up/down LED sweeps for --leds (default 3)
  --index 0xNN     inject only, at feature index NN
  --no-claim       do not claim \\.\TFFAControl ownership
  --seconds N      duration per inject phase (default 3)
  --rate N         writes per second (default 200)
  --help           this text

Watch DebugView (kernel capture) for 'TFFAUsbFilter:' lines while this runs.");
        }
    }
}
