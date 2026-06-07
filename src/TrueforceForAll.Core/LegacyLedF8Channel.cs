// Legacy "F8 12" rev-LED path  --  the experiment behind the F8SWEEP test code.
//
// Background: our production rev-LED path (WheelLedChannel, HID++ page 0x807A)
// shares the wheel's single HID++ command processor with the game's HID++ FFB
// (page 0x8123), so driving LEDs while a sim outputs FFB starves the force
// (see project_led_ffb_contention_model). That's why production LEDs are gated
// to iRacing + MAIRA-passthrough only.
//
// guivdh/forza-wheel-leds drives G29/G920/G923 rev LEDs with the LEGACY Logitech
// "set RPM LEDs" output report  ->  00 F8 12 <byte> 00 00 00 00  <-  on the wheel's
// gamepad/DirectInput collection, NOT HID++. An RS50 owner reported that adding
// the RS50 PID to that tool lit the LEDs WITHOUT FFB dropouts (just flickering /
// inaccurate, which is forza-wheel-leds writing every frame with no debounce).
// If the legacy command lands on a collection separate from the HID++ FFB pipe,
// it's a NON-contending LED path we could use in every game, not just iRacing.
//
// This class reproduces that exact write via HidSharp (same hidclass.sys path as
// hidapi -- the library doesn't matter, only the report + collection do) so we
// can confirm on a G PRO whether F8 12 (a) lights the strip and (b) coexists with
// live FFB. It is a TEST path: heavily logged, opened on demand, off by default.

using System;
using System.Collections.Generic;
using System.Threading;
using HidSharp;

namespace TrueforceForAll.Core
{
    public sealed class LegacyLedF8Channel : IDisposable
    {
        private const byte CmdPrefix = 0xF8;   // Logitech extended-command prefix
        private const byte CmdSetLeds = 0x12;  // "set RPM LEDs" sub-command

        // Generic Desktop (0x01) joystick-ish usages -- the gamepad/DirectInput
        // collection the legacy LED command rides on (same one as PID FFB).
        private const ushort UsagePageGenericDesktop = 0x01;
        private static readonly HashSet<ushort> JoystickUsages =
            new HashSet<ushort> { 0x04 /*Joystick*/, 0x05 /*Gamepad*/, 0x08 /*Multi-axis*/ };

        private readonly Action<string> _log;
        private readonly object _io = new object();

        private HidStream _stream;
        private int _reportLen = 8;     // output report length incl. report ID
        private string _info = "(not opened)";

        private Thread _sweep;
        private volatile bool _sweepStop;
        private volatile bool _sweeping;
        // 0 = write only when the level changes (our paced footprint).
        // >0 = resend the current value every N ms regardless (forza-wheel-leds
        // writes every ~16 ms frame with no change-detection -> set 16 to mimic).
        private volatile int _resendMs;

        public LegacyLedF8Channel(Action<string> log) { _log = log ?? (_ => { }); }

        public bool IsOpen => _stream != null;
        public bool IsSweeping => _sweeping;
        public string ResolvedInfo => _info;
        public string ModeLabel => _resendMs <= 0
            ? "paced (write-on-change, ~7 Hz)"
            : $"forza-speed (resend every {_resendMs} ms, ~{1000 / _resendMs} Hz)";

        // ---- Open the gamepad/DirectInput collection -----------------------

        /// <summary>Find the wheel's gamepad/DirectInput HID collection (where the
        /// legacy F8 command lives -- NOT mi_02 Trueforce audio, NOT the HID++
        /// collections) and open it. Idempotent. Returns true once open.</summary>
        public bool Open()
        {
            lock (_io)
            {
                if (_stream != null) return true;

                HidDevice chosen = null, firstNonAudio = null;
                string why = "joystick usage 0x01/0x04";
                try
                {
                    var list = DeviceList.Local;
                    foreach (var (pid, model) in WheelDiscovery.SupportedPids)
                    {
                        foreach (var dev in list.GetHidDevices(WheelDiscovery.LogitechVid, pid))
                        {
                            string path = dev.DevicePath ?? string.Empty;
                            if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;   // Trueforce audio interface, never the LED bar
                            _log($"[F8-LED] candidate: {model} maxOut={SafeOutLen(dev)} path={path}");
                            if (firstNonAudio == null) firstNonAudio = dev;
                            if (chosen == null && IsJoystickCollection(dev)) chosen = dev;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[F8-LED] enumeration failed: {ex.Message}");
                    return false;
                }

                // Fallback mirrors forza-wheel-leds' "open first match": if we
                // couldn't identify the joystick collection by usage, take the
                // first non-audio collection and let the firmware sort it out.
                if (chosen == null && firstNonAudio != null)
                {
                    chosen = firstNonAudio;
                    why = "first non-audio collection (no joystick usage found)";
                }

                if (chosen == null)
                {
                    _log("[F8-LED] no gamepad/DirectInput collection found for the wheel.");
                    return false;
                }

                try
                {
                    var s = chosen.Open(new OpenConfiguration());
                    s.WriteTimeout = 250;
                    _stream = s;
                    _reportLen = Math.Max(8, SafeOutLen(chosen) > 0 ? SafeOutLen(chosen) : 8);
                    _info = $"{SafeName(chosen)}  outLen={_reportLen}  via {why}";
                    _log($"[F8-LED] opened {_info}  path={chosen.DevicePath}");
                    return true;
                }
                catch (Exception ex)
                {
                    _log($"[F8-LED] open refused: {ex.Message}");
                    _stream = null;
                    return false;
                }
            }
        }

        private static bool IsJoystickCollection(HidDevice dev)
        {
            try
            {
                var desc = dev.GetReportDescriptor();
                foreach (var item in desc.DeviceItems)
                {
                    foreach (uint usage in item.Usages.GetAllValues())
                    {
                        ushort page = (ushort)((usage >> 16) & 0xFFFF);
                        ushort id   = (ushort)(usage & 0xFFFF);
                        if (page == UsagePageGenericDesktop && JoystickUsages.Contains(id))
                            return true;
                    }
                }
            }
            catch { /* some HidSharp versions throw on odd descriptors */ }
            return false;
        }

        private static int SafeOutLen(HidDevice d)
        {
            try { return d.GetMaxOutputReportLength(); } catch { return -1; }
        }

        private static string SafeName(HidDevice d)
        {
            try { return d.GetFriendlyName(); } catch { return "(wheel)"; }
        }

        // ---- Send one F8 12 report -----------------------------------------

        /// <summary>Write  00 F8 12 &lt;value&gt; 00 ...  padded to the collection's
        /// output report length (this is what hidapi does internally too).
        /// `value` is the LED byte: forza-wheel-leds uses a progressive bitmask
        /// (1&lt;&lt;n)-1; the firmware decides how that maps onto the strip.</summary>
        public bool Send(int value)
        {
            HidStream s = _stream;
            if (s == null) return false;
            var report = new byte[_reportLen];   // zero-filled
            report[0] = 0x00;                    // report ID (0 / unnumbered, like forza-wheel-leds)
            report[1] = CmdPrefix;               // 0xF8
            report[2] = CmdSetLeds;              // 0x12
            report[3] = (byte)(value & 0xFF);    // LED byte
            try
            {
                s.Write(report);
                return true;
            }
            catch (Exception ex)
            {
                _log($"[F8-LED] write failed (value=0x{value & 0xFF:X2}): {ex.Message}");
                return false;
            }
        }

        public void AllOff() { try { Send(0x00); } catch { } }

        // ---- Sweep test ----------------------------------------------------

        /// <summary>Start a continuous empty-&gt;full-&gt;empty sweep on a background
        /// thread so the user can drive a sim and watch both the LEDs and the FFB.
        /// Toggle: call StopSweep() to end it (LEDs off). Opens the channel if
        /// needed. Returns true if the sweep is running.</summary>
        public bool StartSweep(int resendMs)
        {
            lock (_io)
            {
                _resendMs = resendMs < 0 ? 0 : resendMs;
                if (_sweeping) return true;     // live-updated; the loop picks up _resendMs
                if (!Open()) return false;
                _sweepStop = false;
                _sweeping = true;
                _sweep = new Thread(SweepLoop) { IsBackground = true, Name = "F8LedSweep" };
                _sweep.Start();
                _log($"[F8-LED] sweep started: {ModeLabel}. Drive a sim with FFB and "
                     + "watch for cutouts/flicker; type F8SWEEP again to stop.");
                return true;
            }
        }

        public void StopSweep()
        {
            _sweepStop = true;
            var t = _sweep; _sweep = null;
            try { t?.Join(500); } catch { }
            _sweeping = false;
            AllOff();
            _log("[F8-LED] sweep stopped, LEDs off.");
        }

        // The level climbs/falls on a fixed VISUAL cadence (stepMs); how often we
        // actually WRITE is the variable under test. _resendMs == 0 -> write only
        // when the level changes (our paced footprint, ~7 Hz). _resendMs > 0 ->
        // resend the current value every _resendMs ms (set ~16 to mimic
        // forza-wheel-leds' every-frame spam). Same sweep either way, so flicker
        // that shows up ONLY in fast mode pins the cause on write rate, not the
        // command. _resendMs is volatile, so F8FAST/F8SLOW switch it live.
        private void SweepLoop()
        {
            const int tickMs = 8;
            const int stepMs = 150;                  // level advances this often
            int[] counts = BuildUpDown(8);
            int idx = 0, sentVal = -1, logCount = 0;
            long lastStep = NowMs(), lastWrite = 0;
            while (!_sweepStop)
            {
                long now = NowMs();
                if (now - lastStep >= stepMs) { idx = (idx + 1) % counts.Length; lastStep = now; }
                int val = (1 << counts[idx]) - 1;    // 0x00,0x01,0x03,...,0xFF
                int resend = _resendMs;
                bool due = (val != sentVal) || (resend > 0 && (now - lastWrite) >= resend);
                if (due)
                {
                    bool ok = Send(val);
                    sentVal = val; lastWrite = now;
                    if ((logCount++ % 40) == 0)
                        _log($"[F8-LED] sweep level={counts[idx]}/8 byte=0x{val:X2} "
                             + $"resend={resend}ms write={(ok ? "ok" : "FAIL")}");
                }
                Thread.Sleep(tickMs);
            }
        }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        private static int[] BuildUpDown(int max)
        {
            var list = new List<int>();
            for (int i = 0; i <= max; i++) list.Add(i);
            for (int i = max - 1; i >= 1; i--) list.Add(i);
            return list.ToArray();
        }

        public void Dispose()
        {
            try { StopSweep(); } catch { }
            lock (_io)
            {
                try { _stream?.Dispose(); } catch { }
                _stream = null;
            }
        }
    }
}
