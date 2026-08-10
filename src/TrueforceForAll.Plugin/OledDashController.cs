// Owns the WheelOledChannel and decides what to put on the wheel base's
// Dynamic OLED screen.
//
// EXPERIMENTAL, and gated exactly like the rim rev lights, for exactly the
// same reason: the OLED rides the same HID++ pipe, and a non-force write to
// that pipe cuts any force flowing on it (WheelOledChannel's header has the
// references). So the single gate computed in TrueforcePlugin.DataUpdate is
// "Telemetry Based FFB (Mode B) armed AND the FFB tap PROVES the game's own
// FFB quiet AND the session is live", plus the user's opt-in
// TrueforceSettings.ModeBOledEnabled (default OFF while this is unproven).
//
// The channel open is a probe (enumerate + getFeature with timeouts) so it can
// take a beat; it runs once on a background task, never on SimHub's DataUpdate
// thread. Frames are formatted here and handed to the channel, which paces the
// actual writes.
//
// TWO HARDWARE OBSERVATIONS, from A/B frames run on a G PRO on 2026-08-06.
// Both contradict what the RS50 captures implied, so do not "fix" the code
// back toward the documentation without re-testing on hardware:
//
//  1. LARGE ROWS DRAW IN TWO ZONES: the first character is pinned to the left
//     and everything after it is right-aligned. It applies to the two-row
//     layout's lower row and the right-aligned four-row layout's large rows.
//     Small wide rows draw normally, and the CENTERED four-row layout centers
//     the whole string instead.
//
//     This is what made layout 7 look broken. "112%" drew "1" at the left and
//     "12%" at the right with a gap between, while a full-width ruler closed
//     the gap and read as one field. Both observations are the same rule. The
//     wheel's own descriptor calls it one 10-character field, so this is a
//     renderer behaviour on a correctly described payload, and PeposCJ's
//     documentation is not wrong.
//
//     Spaces are content, so both edges are steerable: a LEADING space skips
//     the left zone and gives a clean right-aligned value, and TRAILING spaces
//     push that value back leftward off the right edge. Together they place
//     text anywhere along the row, center included. Compose adds the leading
//     space for every field except custom text, which is passed through
//     exactly as typed so both zones and the padding stay usable on purpose.
//     Used deliberately these layouts give a one-character value on the left
//     and a longer one on the right, which is a gear-and-speed row.
//
//  2. Positional control exists only where a field has those two zones, and
//     there it is character-exact in both directions. In the small
//     side-by-side layouts padding does nothing at all. The largest font
//     (37px) lives only in those side-by-side layouts, so it still cannot be
//     centered; the four-row centered layout's 18px remains the largest CENTERED
//     text the panel can draw.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    /// <summary>Everything a frame needs beyond the telemetry itself. Grouped
    /// so the call from DataUpdate stays readable as this grew past the point
    /// where a positional argument list said anything.</summary>
    public struct OledFrameContext
    {
        public bool GateOpen;
        public OledScreen Screen;
        public bool UseMph;
        /// <summary>Custom arrangement, used only when Screen is Custom.</summary>
        public OledLayoutKind CustomLayout;
        public string[] CustomSlots;
        public string[] CustomTexts;
        /// <summary>False when the running game never reports a lap delta, from
        /// the same evidence the dash's box picker greys boxes out with.</summary>
        public bool DeltaSupported;
        public double? LapDelta;
        public string ReadoutLabel;
        public string ReadoutValue;
        public bool GameFfbContending;

        // Race context, straight off SimHub's status object. Zero means the
        // game does not report it.
        public int Position, CurrentLap, TotalLaps, LastLapMs, BestLapMs;
        /// <summary>Set on the one frame a lap time lands.</summary>
        public bool LapJustCompleted;

        public bool ShiftFlashEnabled;
        public OledFlashStyle FlashStyle;
        public bool LapResultEnabled;
        public bool GreetingEnabled;
        public string GreetingText;
        /// <summary>Minimum gap between writes to the panel, in ms.</summary>
        public int WriteIntervalMs;

        // Meter sources that do not live on the telemetry frame: the pedals
        // come off SimHub's status object, and the last two are ours alone,
        // read from the device and the Trueforce stream.
        public double Brake01, Clutch01, Handbrake01, FfbTorque01, TrueforceLevel01;
    }

    public sealed class OledDashController : IDisposable
    {
        private readonly Action<string> _log;
        private readonly WheelOledChannel _channel;

        private int _openState;        // 0=idle 1=opening 2=open-ok 3=open-failed
        private int _openFails;        // completed failed open attempts (auto path only)
        private long _nextOpenRetryMs; // no automatic re-probe before this
        private const int OpenRetryCooldownMs = 30_000;
        private const int OpenMaxAttempts = 5;
        private bool _showing;         // something is on screen (drives the release path)
        private volatile bool _testing;
        private volatile string _testStatus = "";

        // Contention alert state. The plugin's ModeBContentionDetected is a
        // LATCH, not an event: once the game is caught streaming its own FFB it
        // stays set until Mode B disarms or the game changes. Holding the alert
        // for as long as the latch is set would eat the screen for the rest of
        // the session, so this announces on the rising edge, gets out of the
        // way, and comes back at a slow interval while the problem persists.
        private const int AlertHoldMs = 5000;
        private const int AlertRepeatMs = 30000;
        private bool _wasContending;
        private int _alertAtTick;

        // A finished lap is worth more of the screen than a value echo, and
        // less than a wheel that is fighting itself. Long enough to read a lap
        // time and a delta without still being up at the next corner.
        private const int LapResultMs = 4000;
        private int _lapResultAtTick;
        private byte[] _lapResultFrame;

        // The shift flash. Short: it is a confirmation, not a readout. It has
        // to outlast a couple of write intervals or it can be swallowed before
        // it is seen, which is why this is not shorter still.
        private const int ShiftFlashMs = 600;
        private string _prevGear;
        private int _shiftAtTick;

        private const int ComposeMinMs = 50;
        private int _lastComposeTick;

        // Releasing the panel is VISIBLE: fn2 hands it back and the wheel's own
        // view appears. So a gate that closes for a single frame flashes that
        // view and takes it straight back, which is exactly what a flicker
        // looks like. Both inputs to the gate can flap: the FFB tap can see one
        // stray packet, and a session can read inactive for a frame. Same shape
        // as the FH6 pause-gate dropout, and the same fix: make the gate prove
        // it means it before letting go.
        private const int ReleaseDebounceMs = 750;
        private bool _gateClosed;
        private int _gateClosedAtTick;
        private int _flapsAbsorbed;
        private int _lastFlapLogTick;

        // Settings-panel preview. Each call supersedes the last, so dragging
        // through the layout list or typing custom text does not queue a pile
        // of releases behind it: only the newest token gets to clear.
        private const int PreviewMs = 6000;
        private int _previewToken;
        private int _previewAtTick;
        private int _lastDriveTick;   // last time OnFrame actually drew

        /// <summary>A settings-panel preview is on screen and owns the panel.
        /// The idle path has to honour this: it runs on every DataUpdate and
        /// would otherwise clear the preview on the very next tick, which reads
        /// as the preview flickering on and off rather than holding.</summary>
        private bool PreviewHolding
        {
            get
            {
                if (_previewAtTick == 0) return false;
                int age = unchecked(Environment.TickCount - _previewAtTick);
                return age >= 0 && age < PreviewMs;
            }
        }

        // The hello. The step is its OWN rate, deliberately not the channel's:
        // tying them together meant turning the write interval down made the
        // greeting scroll faster AND finish sooner, so at the fastest setting
        // it flashed past in about a second and read as not scrolling at all.
        // The channel resends every frame anyway, so a slower step just means
        // the same window goes out more than once, which is free.
        // Once per plugin run rather than per driving session: greeting you
        // every time you leave the pits would stop being a greeting.
        private const int GreetStepMs = 160;
        // Whole laps only, so the message never stops mid-scroll. One lap of a
        // default-length greeting is about 2.4 s, which is the whole message
        // read once; a 3.2 s minimum rounded that up to TWO laps and nearly
        // five seconds of hello, which is a greeting outstaying its welcome.
        private const int GreetMinMs = 2000;
        private const int GreetGapChars = 4;
        private bool _greeted;
        private int _greetAtTick;
        private int _greetSteps;

        public OledDashController(Action<string> log)
        {
            _log = log ?? (_ => { });
            _channel = new WheelOledChannel(_log);
        }

        public bool IsReady => _channel.IsReady;
        public bool IsTesting => _testing;
        public string Status =>
            _testing ? _testStatus
          : _openState == 2 ? $"open ({_channel.ResolvedInfo})"
          : _openState == 3 ? "no OLED wheel found (see log)"
          : _openState == 1 ? "opening…"
          : "idle";

        /// <summary>Called every telemetry frame. <paramref name="gateOpen"/> is
        /// the Mode B + quiet-FFB + setting-enabled gate. Off-gate hands the
        /// panel back to the wheel's own firmware once and does nothing
        /// else.</summary>
        public void OnFrame(in TelemetryFrame frame, in OledFrameContext ctx)
        {
            if (_testing) return;   // the test sequence owns the panel while it runs

            if (!ctx.GateOpen)
            {
                if (!_gateClosed) { _gateClosed = true; _gateClosedAtTick = Environment.TickCount; }
                // Hold the panel while the gate is only briefly shut. The
                // sender keeps resending the last frame, so nothing on screen
                // changes and the wheel's own view never gets a look in.
                int closedFor = unchecked(Environment.TickCount - _gateClosedAtTick);
                if (closedFor >= 0 && closedFor < ReleaseDebounceMs) return;

                _wasContending = false;   // re-announce if it comes back
                _lapResultFrame = null;
                _prevGear = null;         // do not flash the gear on re-entry
                // Release for real (game switch, pause, Mode B disarm). Clear()
                // also STOPS the sender thread, so we stop writing the HID++
                // pipe entirely and the wheel's own menu has the screen back.
                // Once released this no-ops.
                if (_showing && _channel.IsReady)
                {
                    try { _channel.Clear(); } catch { }
                }
                _showing = false;
                return;
            }

            if (_gateClosed)
            {
                // Reopened. If it was shut for less than the debounce we just
                // absorbed a flap: worth knowing about, since a gate that
                // chatters means the tap or the session state is unsteady, but
                // not worth a log line every time it happens.
                _gateClosed = false;
                if (_showing) _flapsAbsorbed++;
                int sinceLog = unchecked(Environment.TickCount - _lastFlapLogTick);
                if (_flapsAbsorbed > 0 && (sinceLog < 0 || sinceLog > 10000))
                {
                    _lastFlapLogTick = Environment.TickCount;
                    _log($"[OLED] gate flapped {_flapsAbsorbed}x; held the screen through it "
                         + "(FFB tap or session state is chattering).");
                    _flapsAbsorbed = 0;
                }
            }

            if (!EnsureOpening()) return;
            if (!_channel.IsReady) return;
            if (ctx.WriteIntervalMs > 0) _channel.WriteIntervalMs = ctx.WriteIntervalMs;

            try
            {
                // Priority, highest first:
                //   1. the contention alert. It is the only thing here that
                //      changes what you would DO right now, and it explains a
                //      wheel that already feels wrong, so it outranks a value
                //      you just asked for.
                //   2. a readout: a control that changes a number takes the
                //      screen over while it reports, the same way it takes over
                //      the dash's bar. This is the case the panel is best at:
                //      you press a wheel button without looking at anything and
                //      the answer appears where you were already looking.
                //   3. the lap you just finished. Worth more than a value echo,
                //      and it only competes with one for a few seconds a lap.
                //   4. the shift flash, lowest of the interruptions: it fires
                //      constantly, so it must never bury something rarer.
                //   5. the driving screen you picked.
                // Levels 1 to 3 use layout H, which puts the label small over
                // the value large: the dash's readout bar stood on end.
                // Edge detection runs on EVERY frame: a gear change or a lap
                // time can land on any one of them, and a missed edge is a
                // missed flash. Only the drawing below is throttled.
                NoteLap(in ctx);
                NoteShift(in ctx, frame.Gear);

                // Composing allocates, and telemetry can arrive several hundred
                // times a second while the channel only ever writes five. 20 Hz
                // is far finer than anything the panel can show and keeps the
                // garbage off the telemetry thread.
                // Compose twice as often as the channel can write, so a frame is
                // always fresh when its turn comes and the throttle never
                // becomes the thing limiting the refresh rate.
                int composeMin = ctx.WriteIntervalMs > 0 ? ctx.WriteIntervalMs / 2 : ComposeMinMs;
                if (composeMin < 8) composeMin = 8;
                int since = unchecked(Environment.TickCount - _lastComposeTick);
                if (since >= 0 && since < composeMin) return;
                _lastComposeTick = Environment.TickCount;

                // First time the screen comes up in this run, say hello. Armed
                // here rather than at open, so the clock starts when the panel
                // is actually being drawn and the whole marquee is seen.
                if (!_greeted)
                {
                    _greeted = true;
                    if (ctx.GreetingEnabled)
                    {
                        _greetAtTick = Environment.TickCount;
                        // Whole laps of the message, enough of them to last
                        // long enough to actually be read.
                        int lap = GreetingLoop(ctx.GreetingText).Length;
                        _greetSteps = lap;
                        while (_greetSteps * GreetStepMs < GreetMinMs) _greetSteps += lap;
                    }
                }

                byte[] setter;
                if (AlertDue(ctx.GameFfbContending))
                    // The instruction goes in the small row and the alarm in
                    // the large one. "SET IT TO 0" does not fit the 10-character
                    // large row, and shortened to fit it stops saying what to
                    // set, so the two swap jobs instead.
                    setter = Stacked("SET GAME FFB TO ZERO", "FFB CLASH");
                else if (GreetingDue(in ctx, out byte[] greeting))
                    setter = greeting;
                else if (!string.IsNullOrEmpty(ctx.ReadoutLabel) || !string.IsNullOrEmpty(ctx.ReadoutValue))
                    setter = Stacked(ctx.ReadoutLabel, ctx.ReadoutValue);
                else if (LapResultDue())
                    setter = _lapResultFrame;
                else if (ShiftFlashDue(in ctx))
                    // Two honest options, because the panel cannot give both at
                    // once: the biggest font it has, which only exists in a
                    // layout that sits left and has room for the speed beside
                    // it, or a centered labelled gear at the middle size.
                    setter = ctx.FlashStyle == OledFlashStyle.BigGearAndSpeed
                        // BigSmall's first field is ONE character. A gear that
                        // needs more than that takes the labelled flash instead,
                        // whatever the setting says: the point of the flash is
                        // to tell you the gear, and half a gear does not.
                        ? (((frame.Gear ?? "").Trim().Length > 1)
                            ? Stacked("GEAR", OledScreenModel.GearText(frame.Gear, 10))
                            : _channel.BuildBigSmall(
                                OledScreenModel.GearText(frame.Gear, 1),
                                OledScreenModel.SpeedText(frame.SpeedKmh, ctx.UseMph)))
                        : Stacked("GEAR", OledScreenModel.GearText(frame.Gear, 10));
                else
                    setter = Compose(frame, ctx);

                if (setter != null)
                {
                    _channel.Show(setter);
                    _showing = true;
                    _lastDriveTick = Environment.TickCount;
                }
            }
            catch (Exception ex) { _log($"[OLED] compose error: {ex.Message}"); }
        }

        /// <summary>True while the contention alert should own the screen.
        /// Fires on the rising edge, holds briefly, then repeats slowly for as
        /// long as the game keeps fighting.</summary>
        private bool AlertDue(bool contending)
        {
            if (!contending) { _wasContending = false; return false; }

            if (!_wasContending)
            {
                _wasContending = true;
                _alertAtTick = Environment.TickCount;
                return true;
            }

            int age = unchecked(Environment.TickCount - _alertAtTick);
            if (age < 0) { _alertAtTick = Environment.TickCount; return true; }
            if (age <= AlertHoldMs) return true;
            if (age >= AlertRepeatMs) { _alertAtTick = Environment.TickCount; return true; }
            return false;
        }

        /// <summary>The message plus a run of blanks, so the window is never
        /// empty and the text visibly leaves before it comes back round.</summary>
        private static string GreetingLoop(string text)
        {
            string msg = (text ?? "").Trim().ToUpperInvariant();
            if (msg.Length == 0) msg = "HELLO WORLD";
            string loop = msg + new string(' ', GreetGapChars);
            // A message shorter than the window would leave dead space in it,
            // so repeat until it more than fills one screenful.
            while (loop.Length < 12) loop += loop;
            return loop;
        }

        /// <summary>Something time-boxed is still on screen and has not had its
        /// moment yet. All of these expire on their own, so this only ever
        /// delays a release rather than preventing one.</summary>
        private bool TransientHolding()
        {
            int now = Environment.TickCount;
            if (_lapResultFrame != null)
            {
                int age = unchecked(now - _lapResultAtTick);
                if (age >= 0 && age <= LapResultMs) return true;
            }
            if (_shiftAtTick != 0)
            {
                int age = unchecked(now - _shiftAtTick);
                if (age >= 0 && age <= ShiftFlashMs) return true;
            }
            if (_wasContending)
            {
                int age = unchecked(now - _alertAtTick);
                if (age >= 0 && age <= AlertHoldMs) return true;
            }
            if (_greetSteps > 0)
            {
                int age = unchecked(now - _greetAtTick);
                if (age >= 0 && age < _greetSteps * GreetStepMs) return true;
            }
            return false;
        }

        /// <summary>The scrolling hello, for one full lap of the message.
        /// There is no animation in the protocol, so this is just a different
        /// ten-character window of the same string on each write, which is
        /// what a marquee is.</summary>
        private bool GreetingDue(in OledFrameContext ctx, out byte[] setter)
        {
            setter = null;
            if (!ctx.GreetingEnabled || _greetSteps <= 0) return false;
            int age = unchecked(Environment.TickCount - _greetAtTick);
            if (age < 0 || age >= _greetSteps * GreetStepMs) { _greetSteps = 0; return false; }

            string loop = GreetingLoop(ctx.GreetingText);
            int step = age / GreetStepMs;
            var window = new System.Text.StringBuilder(10);
            for (int i = 0; i < 10; i++) window.Append(loop[(step + i) % loop.Length]);
            setter = _channel.BuildFourRowCenter("TRUEFORCE FOR ALL", window.ToString(), "", "");
            return true;
        }

        /// <summary>No game is running, so there is no force anywhere and the
        /// panel is free. Readouts are still worth seeing here: a gain you just
        /// changed with a wheel button, or a message. Driven from DataUpdate
        /// rather than the telemetry path, because without a game there are no
        /// telemetry frames at all.
        ///
        /// With nothing to say it hands the screen back rather than parking
        /// something on it, so the wheel's own menu stays visible until there
        /// is news. That also means the HID channel is never opened just
        /// because SimHub is running.</summary>
        public void OnIdle(string readoutLabel, string readoutValue)
        {
            if (_testing) return;
            bool has = !string.IsNullOrEmpty(readoutLabel) || !string.IsNullOrEmpty(readoutValue);
            // A live preview keeps the panel until it expires. A readout still
            // wins, because that is the user asking for something newer.
            if (!has && PreviewHolding) return;
            if (has) _previewAtTick = 0;
            if (!has)
            {
                // Telemetry is declared stalled after only 500 ms, and when
                // frames stop OnFrame is not called at all, so THIS is the only
                // path that can release the panel and it needs the release
                // discipline OnFrame's gate has. Without it a brief gap (a
                // pause, a hitch, an FH6 keepalive flap) clears the screen out
                // from under a lap card or a shift flash and it comes straight
                // back when frames resume.
                if (TransientHolding()) return;
                int sinceDrive = unchecked(Environment.TickCount - _lastDriveTick);
                if (_lastDriveTick != 0 && sinceDrive >= 0 && sinceDrive < ReleaseDebounceMs) return;
            }
            if (!has)
            {
                if (_showing && _channel.IsReady)
                {
                    try { _channel.Clear(); } catch { }
                }
                _showing = false;
                return;
            }
            if (!EnsureOpening()) return;
            if (!_channel.IsReady) return;
            try { _channel.Show(Stacked(readoutLabel, readoutValue)); _showing = true; }
            catch (Exception ex) { _log($"[OLED] idle readout error: {ex.Message}"); }
        }

        /// <summary>Put the screen being edited on the wheel with stand-in
        /// telemetry, so choosing a layout or filling a slot can be judged
        /// where it will actually be read. Holds briefly, then releases unless
        /// a newer preview has replaced it.
        ///
        /// Uses the SAME Compose path as driving, so the preview cannot
        /// flatter the real thing. Returns how long it will be up, or 0 if the
        /// channel is not ready yet (the first click opens it; the next one
        /// draws).</summary>
        public int ShowPreview(OledFrameContext ctx)
        {
            if (_testing) return 0;
            if (!EnsureOpening()) return 0;
            if (!_channel.IsReady) return 0;

            var sample = new TelemetryFrame { Gear = "3", SpeedKmh = 128.0 };
            try
            {
                var setter = Compose(sample, ctx);
                if (setter == null) return 0;
                _channel.Show(setter);
                _showing = true;
            }
            catch (Exception ex) { _log($"[OLED] preview error: {ex.Message}"); return 0; }

            _previewAtTick = Environment.TickCount;
            int token = Interlocked.Increment(ref _previewToken);
            Task.Run(() =>
            {
                Thread.Sleep(PreviewMs);
                // Only the newest preview releases, and never while a session
                // has taken the screen back over.
                if (Volatile.Read(ref _previewToken) != token || _testing) return;
                try { if (_channel.IsReady) _channel.Clear(); } catch { }
                _showing = false;
            });
            return PreviewMs;
        }

        /// <summary>A label small over a value large: every takeover on this
        /// panel wants that shape, so they all come through here.
        ///
        /// Built on the FOUR-row layout with its bottom pair left empty, not on
        /// the two-row one that is nominally exactly this. Layout H was
        /// documented from an RS50 as a small row above a larger row, but on a
        /// G PRO it came out as two fields side by side, splitting "112%" into
        /// "1" and "12%". The four-row layout renders as documented on both, so
        /// it is the one to trust until H is understood. If H is ever fixed,
        /// this is the single place to change.</summary>
        private byte[] Stacked(string label, string value) =>
            _channel.BuildFourRowCenter(label ?? "", value ?? "", "", "");

        /// <summary>Build the lap card on the frame a lap time lands, and hold
        /// it for a few seconds. A personal best gets the headline to itself
        /// because that is the whole news; any other lap gets the time with the
        /// delta under it, which is the question you were actually asking.</summary>
        private void NoteLap(in OledFrameContext ctx)
        {
            if (!ctx.LapResultEnabled || !ctx.LapJustCompleted || ctx.LastLapMs <= 0) return;

            // SimHub folds the finished lap into BestLapTime before we see it,
            // so a lap that equals the best IS the best. Without a best yet,
            // the first timed lap is trivially one and says so.
            bool newBest = ctx.BestLapMs <= 0 || ctx.LastLapMs <= ctx.BestLapMs;
            string time = OledScreenModel.LapTimeText(ctx.LastLapMs);

            _lapResultFrame = newBest
                ? Stacked("NEW BEST LAP", time)
                : _channel.BuildFourRowCenter(
                    "LAST LAP", time,
                    ctx.DeltaSupported ? "LAP DELTA" : "",
                    ctx.DeltaSupported ? OledScreenModel.DeltaText(ctx.LapDelta) : "");
            _lapResultAtTick = Environment.TickCount;
        }

        private bool LapResultDue()
        {
            if (_lapResultFrame == null) return false;
            int age = unchecked(Environment.TickCount - _lapResultAtTick);
            if (age >= 0 && age <= LapResultMs) return true;
            _lapResultFrame = null;
            return false;
        }

        /// <summary>Arm the flash on a gear change. Skipped when the screen
        /// already shows the gear: flashing a value that is on screen anyway
        /// only takes the other one away.</summary>
        private void NoteShift(in OledFrameContext ctx, string gear)
        {
            string g = OledScreenModel.GearText(gear, 10);
            string was = _prevGear;
            _prevGear = g;
            if (!ctx.ShiftFlashEnabled || was == null || was == g) return;
            if (ScreenShowsGear(in ctx)) return;
            // Neutral and reverse are not shifts worth flashing mid-corner;
            // they are what you get rolling to a stop or backing out of a wall.
            if (g == "N" || g == "R" || g == "-") return;
            _shiftAtTick = Environment.TickCount;
        }

        private bool ShiftFlashDue(in OledFrameContext ctx)
        {
            if (!ctx.ShiftFlashEnabled || _shiftAtTick == 0) return false;
            int age = unchecked(Environment.TickCount - _shiftAtTick);
            return age >= 0 && age <= ShiftFlashMs;
        }

        private static bool ScreenShowsGear(in OledFrameContext ctx)
        {
            OledLayoutKind kind;
            string[] slots, texts;
            if (ctx.Screen == OledScreen.Custom)
                slots = OledScreenModel.SanitizeSlots(ctx.CustomSlots, ctx.CustomLayout);
            else
                OledScreenModel.Preset(ctx.Screen, ctx.UseMph, ctx.DeltaSupported,
                                       out kind, out slots, out texts);
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] == "Gear") return true;
            return false;
        }

        /// <summary>One renderer for every screen. A preset resolves to the
        /// same layout-and-slots shape the editor produces, so a shipped screen
        /// and a hand-built one can never diverge in how they draw.</summary>
        private byte[] Compose(in TelemetryFrame frame, in OledFrameContext ctx)
        {
            OledLayoutKind kind;
            string[] slots, texts;
            if (ctx.Screen == OledScreen.Custom)
            {
                kind = ctx.CustomLayout;
                slots = OledScreenModel.SanitizeSlots(ctx.CustomSlots, kind);
                texts = OledScreenModel.SanitizeTexts(ctx.CustomTexts, slots.Length);
            }
            else
            {
                OledScreenModel.Preset(ctx.Screen, ctx.UseMph, ctx.DeltaSupported,
                                       out kind, out slots, out texts);
                // A gear that will not fit the arrangement wins over the
                // arrangement. Presets only; a custom screen is left alone.
                OledScreenModel.FitGear(frame.Gear, ref kind, ref slots, ref texts);
            }

            int[] widths = OledScreenModel.SlotWidths(kind);
            var cell = new string[slots.Length];
            var meter = new double[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                if (OledScreenModel.SlotIsGauge(kind, i))
                {
                    meter[i] = OledScreenModel.RenderGauge(
                        slots[i], frame.Throttle01, frame.RpmPercent,
                        frame.FrontGrip01, frame.RearGrip01, frame.SteeringAngle,
                        ctx.Brake01, ctx.Clutch01, ctx.Handbrake01,
                        ctx.FfbTorque01, ctx.TrueforceLevel01);
                    cell[i] = "";
                    continue;
                }
                cell[i] = OledScreenModel.Render(
                    slots[i], i < texts.Length ? texts[i] : null,
                    frame.Gear, frame.SpeedKmh, ctx.UseMph,
                    ctx.LapDelta, ctx.DeltaSupported,
                    ctx.Position, ctx.CurrentLap, ctx.TotalLaps, ctx.LastLapMs,
                    widths[i]);
                // A split slot draws its first character on the left and the
                // rest against the right edge, so a plain "128" would come out
                // as "1" and "28" at opposite ends. Give the left zone a space
                // to swallow and the value lands whole on the right. Custom
                // text is left exactly as typed: someone who knows about the
                // two zones is entitled to use both.
                if (OledScreenModel.SlotSplits(kind, i)
                    && slots[i] != OledScreenModel.FieldCustom)
                    cell[i] = OledScreenModel.RightAlignInSplitSlot(cell[i]);
            }

            string S(int i) => i < cell.Length ? (cell[i] ?? "") : "";
            double G(int i) => i < meter.Length ? meter[i] : 0.0;
            switch (kind)
            {
                // The meter layouts. E draws its 7-character field on the LEFT
                // and its 3-character one on the RIGHT, which is the opposite
                // of their payload order, so the slots are listed left to right
                // here and handed to the builder the way it expects them.
                case OledLayoutKind.Gauge: return _channel.BuildGauge(G(0));
                case OledLayoutKind.GaugeLabel: return _channel.BuildGaugeLabel(G(0), G(1), S(2));
                case OledLayoutKind.GaugeTwoText:
                    return _channel.BuildGaugeTwoText(G(0), G(1), right3: S(3), left7: S(2));
                case OledLayoutKind.BigRight: return _channel.BuildSmallBig(S(0), S(1));
                case OledLayoutKind.Stacked: return _channel.BuildTwoRow(S(0), S(1));
                case OledLayoutKind.FourCenter: return _channel.BuildFourRowCenter(S(0), S(1), S(2), S(3));
                case OledLayoutKind.FourRight: return _channel.BuildFourRowRight(S(0), S(1), S(2), S(3));
                default: return _channel.BuildBigSmall(S(0), S(1));
            }
        }

        /// <summary>Kick a background open if we haven't yet. Returns true once
        /// an open attempt has completed successfully. A failed attempt is
        /// retried on a cooldown a few times before latching for the session:
        /// losing the HID++ pipe to another talker at startup is not proof the
        /// wheel lacks the feature (the "OLED dead until SimHub restart" bug,
        /// 2026-08-08).</summary>
        private bool EnsureOpening()
        {
            int prev = Interlocked.CompareExchange(ref _openState, 1, 0);
            if (prev == 3
                && _openFails < OpenMaxAttempts
                && NowMs() >= _nextOpenRetryMs
                && Interlocked.CompareExchange(ref _openState, 1, 3) == 3)
                prev = 0;   // claimed the retry slot: kick a fresh probe below
            if (prev == 0)
            {
                Task.Run(() =>
                {
                    bool ok = false;
                    try { ok = _channel.OpenAndResolve(); }
                    catch (Exception ex) { _log($"[OLED] open threw: {ex.Message}"); }
                    if (ok) _openFails = 0;
                    else
                    {
                        _openFails++;
                        _nextOpenRetryMs = NowMs() + OpenRetryCooldownMs;
                        if (_openFails < OpenMaxAttempts)
                            _log($"[OLED] open attempt {_openFails} failed; retrying in "
                                 + $"{OpenRetryCooldownMs / 1000} s");
                        else
                            _log($"[OLED] open failed {_openFails} times; giving up for this session.");
                    }
                    Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                });
                return false;
            }
            return prev == 2;
        }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>Walk the panel through the screens for the settings "Test"
        /// button, so the user can confirm the wheel answers with nothing
        /// running. Forces the channel open regardless of game. Returns the
        /// total duration in ms (0 if it can't be opened).</summary>
        public int RunTest()
        {
            if (_testing) return 0;

            const int holdMs = 2500;
            int total = 5 * holdMs + 800;

            // Mark testing BEFORE returning so the UI's status-poll timer sees
            // the test immediately.
            _testing = true;
            Task.Run(() =>
            {
                bool opened = _channel.IsReady;
                try
                {
                    // Open here, not on the caller's (UI) thread: OpenAndResolve
                    // can block for a few seconds on a wheel that doesn't answer.
                    if (!opened)
                    {
                        bool ok;
                        try { ok = _channel.OpenAndResolve(); }
                        catch (Exception ex) { _log($"[OLED] test open threw: {ex.Message}"); ok = false; }
                        Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                        opened = ok;
                    }
                    if (!opened)
                    {
                        _testStatus = "could not open the OLED channel (see log)";
                        _log("[OLED] Test: could not open the channel. Check the log above "
                             + "for which interfaces were probed. Only the G PRO and RS50 "
                             + "have this screen.");
                        return;
                    }

                    // What the feature actually draws, in the order you would
                    // meet it. The layout probes that lived here have served
                    // their purpose and are recorded in the file header rather
                    // than left on a button a user presses.
                    Hold("▶ hello",         Stacked("TRUEFORCE FOR ALL", "OLED"), holdMs);
                    Hold("▶ gear + speed",  _channel.BuildBigSmall("3", "128"), holdMs);
                    Hold("▶ speed + delta", _channel.BuildFourRowCenter("SPEED KM/H", "128", "LAP DELTA", "-0.42"), holdMs);
                    Hold("▶ gear flash",    Stacked("GEAR", "3"), holdMs);
                    Hold("▶ gain readout",  Stacked("TRUEFORCE GAIN", "112%"), holdMs);
                }
                catch (Exception ex) { _log($"[OLED] test error: {ex.Message}"); }
                finally
                {
                    if (opened)
                    {
                        try { _channel.Clear(); } catch { }
                        _showing = false;
                        _testStatus = "test finished - screen released";
                        _log("[OLED] Test: finished, screen handed back to the wheel.");
                    }
                    _testing = false;
                }
            });
            return total;
        }

        /// <summary>Two experiments that together settle what this wheel's
        /// layouts really are, rather than what one RS50's captures implied.
        ///
        ///   1. The wheel's own layout table, logged raw. Authoritative, and
        ///      directly comparable with the same dump from another wheel.
        ///   2. A ruler frame per interesting layout: every payload byte is a
        ///      different character, so whatever the panel draws names the
        ///      offsets each field starts at. Report what you SEE for each and
        ///      the field boundaries fall straight out.
        ///
        /// Layout 7 is the one in question and layout 9 is the control, since
        /// it has always rendered as documented.</summary>
        public int RunLayoutReport()
        {
            if (_testing) return 0;
            const int holdMs = 6000;
            int total = 4 * holdMs + 1500;

            _testing = true;
            Task.Run(() =>
            {
                bool opened = _channel.IsReady;
                try
                {
                    if (!opened)
                    {
                        bool ok;
                        try { ok = _channel.OpenAndResolve(); }
                        catch (Exception ex) { _log($"[OLED] report open threw: {ex.Message}"); ok = false; }
                        Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                        opened = ok;
                    }
                    if (!opened)
                    {
                        _testStatus = "could not open the OLED channel (see log)";
                        return;
                    }

                    _testStatus = "▶ reading the wheel's layout table…";
                    _log("[OLED] ---- layout table for THIS wheel ----");
                    _log("[OLED] " + _channel.ReadLayoutTable());
                    _log("[OLED] ---- end layout table ----");

                    // Look for a function that CLAIMS the display. The panel
                    // keeps reverting to the wheel's own view between our
                    // writes, and the gate is not to blame (it never flapped),
                    // so we are feeding a surface we never took ownership of.
                    // Only fn0-fn3 are documented; anything above is unexplored.
                    _testStatus = "▶ probing undocumented functions…";
                    _log("[OLED] ---- function probe fn4-fn15 ----");
                    _channel.ProbeFunctions(4, 15);
                    _log("[OLED] ---- end function probe ----");

                    // Ruler offsets, so a photograph can be read without
                    // counting: A=5, B=6 ... Z=30, 0=31 ... 9=40, a=41 ... w=63.
                    _log("[OLED] ruler key: A=offset 5, B=6, ... Z=30, 0=31, ... 9=40, a=41, ... w=63");
                    _testStatus = "▶ 1/4 layout 7 ruler - is the lower row one run of letters?";
                    _channel.Show(_channel.BuildRuler(OledLayout.TwoRow));
                    Thread.Sleep(holdMs);

                    // The split was reported with a SHORT string and denied with
                    // a full-width ruler. A full field would look contiguous
                    // even if the firmware drew it as two adjacent sub-fields,
                    // so only a short string can tell them apart. These three
                    // isolate it: the original case verbatim, the same without
                    // its label in case the label is what moves things, and the
                    // same string on the layout that has always looked right.
                    _testStatus = "▶ 2/4 layout 7, \"112%\" with a label - GAP or no gap?";
                    _channel.Show(_channel.BuildTwoRow("TWO ROW 112%", "112%"));
                    Thread.Sleep(holdMs);
                    _testStatus = "▶ 3/4 layout 7, \"112%\" alone - GAP or no gap?";
                    _channel.Show(_channel.BuildTwoRow("", "112%"));
                    Thread.Sleep(holdMs);
                    _testStatus = "▶ 4/4 layout 9, \"112%\" alone - the control";
                    _channel.Show(_channel.BuildFourRowCenter("", "112%", "", ""));
                    Thread.Sleep(holdMs);
                }
                catch (Exception ex) { _log($"[OLED] layout report error: {ex.Message}"); }
                finally
                {
                    if (opened)
                    {
                        try { _channel.Clear(); } catch { }
                        _showing = false;
                        _testStatus = "layout report finished - see the log";
                    }
                    _testing = false;
                }
            });
            return total;
        }

        private void Hold(string status, byte[] setter, int ms)
        {
            if (!_channel.IsReady) return;
            _testStatus = status;
            _channel.Show(setter);
            Thread.Sleep(ms);
        }

        /// <summary>Hand the screen back now (feature unchecked / plugin
        /// disabled). No telemetry frames arrive after that to drive the
        /// gate-off path, so callers must invoke this explicitly.</summary>
        public void ForceOff()
        {
            if (_testing) return;
            try { if (_channel.IsReady) _channel.Clear(); } catch { }
            _showing = false;
        }

        public void Dispose()
        {
            try { _channel.Dispose(); } catch { }
        }
    }
}
