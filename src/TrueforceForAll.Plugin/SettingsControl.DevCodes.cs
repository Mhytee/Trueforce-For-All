// The hidden access codes typed into the Settings tab's access box: the
// HELP catalog, the box's commit path, the scrollable code browser and the
// dispatch that runs a code word. Developer tooling, not user-facing UI.
//
// Its own partial class so the localization literal sweep can exclude
// developer tooling by file (docs/localization-plan.md Phase 0, agent-audit
// S5). Every string in here stays English on purpose: the codes are read
// back as words, their results are pasted into logs and issue threads, and
// none of it is meant to be translated.
//
// Moved verbatim from SettingsControl.xaml.cs. Nothing here was reworded.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        // WARNEMAIL dev code: which warning stage (1..5) the next trigger sends. Cycles 1->5->1.
        private int _warnPreviewStage = 1;

        // Email the stage-N backup-deletion warning to the signed-in user's own address, so we
        // can preview the escalating copy. The server scopes it to the JWT email claim and never
        // touches the real entitlement / retention timer.
        private async Task SendWarnPreviewAndReport(int stage)
        {
            if (_plugin == null) return;
            try
            {
                var (ok, message) = await _plugin.SendWarnEmailPreviewAsync(stage, System.Threading.CancellationToken.None);
                if (AccessCodeStatus != null) AccessCodeStatus.Text = message;
            }
            catch (Exception ex) { if (AccessCodeStatus != null) AccessCodeStatus.Text = "Couldn't send the email. Check your connection and try again."; TrueforceDialog.LogError("Warning email", ex); }
        }

        // Single source of truth for the HELP / CODES listing. When you add a
        // new access code in CommitAccessCode below, add a line here too so
        // HELP stays accurate as the set of codes grows.
        private const string TestCodeCatalog =
            "Trueforce For All test codes (type one in the access box):\n\n" +
            "HELP / CODES / ?   Show this list.\n" +
            "SUPPORT        Preview the periodic Patreon support modal now (pacing untouched).\n" +
            "SUPPORTRESET   Reset the support-prompt ladder back to its first rung.\n" +
            "STALL          Simulate a Forza 'no packets' stall + open the troubleshooter + show the UDP setup banner (toggle).\n" +
            "CAPTURE        Toggle the aligned telemetry+FFB capture CSV (v2 golden fixture format) under Documents\\TrueforceForAll.\n" +
            "FZBANNERS      Toggle the two info-tier Forza banners (SimHub-fallback notice + discovered-port) on to eyeball their button styling.\n" +
            "SPRINGTEST     Desk test of the stationary spring (motor pushes one way, then the other).\n" +
            "WHATSNEW       Re-show the 'What's new' banner and all NEW effect badges.\n" +
            "WELCOME        Reset the networked-welcome modal AND the Mode B intro seen state and re-trigger them now (HasSeenNetworkedWelcome / WelcomeDeclineCount / WelcomeNextShowAt / HasSeenModeBIntro all cleared).\n" +
            "MOTDFLUSH      Clear the Message-of-the-day cache + all MOTD dismissals and refetch now (so dismissed/edited messages reappear; bypasses the ~6h cache).\n" +
            "MOTDROLL       Preview the MOTD strip on a RANDOM upcoming day (shows which day in the strip + status). Run again to re-roll. Dismissals + nag cooldown bypassed.\n" +
            "MOTDDATE<MMDDYYYY>  Preview the MOTD strip as if it were that date, e.g. MOTDDATE12252026, to see upcoming messages before they trigger.\n" +
            "MOTDLIVE       Leave MOTD preview and return to today's real messages.\n" +
            "CACHEFACTS     Clear the community car-facts cache (names/engine types/redlines); it refetches per car.\n" +
            "CLEARCACHES    Clear ALL re-fetchable network caches at once: MOTD (+dismissals), community car-facts, browse lists. Leaves your car corrections, local detections, and sign-in alone.\n" +
            "PREVIEW        Render the release-notes markdown on your clipboard exactly as the in-app 'What's new' will (copy the GitHub notes first, then type PREVIEW).\n" +
            "UPDATE         Simulate an available update (banner + update dialog).\n" +
            "CLOSESIM       Pick an installer and run it with /CloseSimHub=1 to test the silent SimHub auto-close. Closes SimHub.\n" +
            "UPDATEDIRTY    Simulate an update with unsaved changes, then run a locally-picked installer instead of downloading (tests the pre-update 'unsaved changes' warning and the silent SimHub close). Closes SimHub.\n" +
            "UPDATEPOLL     Simulate a release shipping AFTER launch: arms a fake newer release that only a BACKGROUND re-check applies, on a fast cadence (every 5s; UPDATEPOLL<n> for n seconds), so the 'Update to vX.Y.Z' banner appears on its own within seconds, no restart. Tests the periodic re-check end-to-end. Run again to stop + clear. Toggle.\n" +
            "FAULT          Force a stream fault to test auto-reconnect.\n" +
            "NOFFB          Simulate the FFB tap capturing no game force feedback while driving (tests the whole-bus retry + 'try another USB port' notice). Toggle.\n" +
            "CSPFFB         Assetto Corsa: the TF4ALL CSP Bridge is used AUTOMATICALLY when its script is installed (install it from Settings > Game mods, the on-screen prompt, or the guide), otherwise the USB capture is used. This code is a DEV force-off: type it to make AC use the capture even with the bridge installed, type again for automatic. Sub-commands pick the read field: VALUE (default, post-gain, keeps your CSP tweaks), PURE or TORQUE (pre-gain, work at in-game gain 0), FINAL, FINALFF; 'CSPFFB NM 8' sets full-scale torque for TORQUE; 'CSPFFB SUP/NOSUP' is a suppression diagnostic; 'CSPFFB DAMP' toggles the synthesized damper; 'CSPFFB DAMPK <x>' sets its strength (0 to 5, default 1.0); 'CSPFFB DAMPSIGN' flips its direction; 'CSPFFB DAMPTEST' runs a 28 s damper wiggle (off/on flips, then ramps). Persists.\n" +
            "R3EFFB         RaceRoom: drive the wheel from the sim's own pre-gain steering force (read straight from its shared memory) instead of the USB capture; set in-game FFB intensity to 0 first. Frees the HID++ pipe for the rev lights and screen the way CSPFFB does in Assetto Corsa. Enabling also opts RaceRoom into Telemetry Based FFB, so this one code is the whole A/B switch against the tap route. 'R3EFFB INV' flips the force sign, 'R3EFFB NM 15' reads the raw SteeringForce channel with that full scale, 'R3EFFB PCT' returns to the percentage channel (those three are session only), 'R3EFFB AUTO' toggles per-car auto-strength (persisted, on by default; RaceRoom's percentage tops out well below full scale) and 'R3EFFB APPLY' commits this car's max iRacing-style (drive a couple of clean laps, watch the strength confidence, then apply; nothing drifts under you until you do). Bindable through the Auto force and Peak force up/down bindings on the Controls tab. Persists per car. Toggle.\n" +
            "R3EPROBE       RaceRoom signal probe: '[TF4ALL] R3EPROBE' lines every ~2 s with the sim's SteeringForce and percentage (current + min/max), steering input, tick rate and control state. For verifying, before trusting R3EFFB, that the force survives in-game FFB intensity 0 and that its sign matches the steering direction. Session only. Toggle.\n" +
            "LMUFFB         Le Mans Ultimate: drive the wheel from the sim's own steering shaft torque (read straight from its official shared memory) instead of the USB capture; set the game's Vendor Specific Force Feedback (its Trueforce) to Off first, its strength can stay. Frees the HID++ pipe for the rev lights and screen the way R3EFFB does in RaceRoom, and it is the same switch as the FFB tab's take-over checkbox. Sub-commands: INV (flip the sign, session only), NM <n> (the shaft torque in Nm that is full force until a car has its own number, persists), APPLY (take this car's learned peak force in Nm, as the FFB tab's Auto button) and DAMP (as R3EFFB). Persists. Toggle.\n" +
            "LMUPROBE       Le Mans Ultimate signal probe: '[TF4ALL] LMUPROBE' lines every ~2 s with the sim's shaft torque and its own FFB value (current + min/max), their update rates, steering, the wheel ranges and the session state. For checking the torque's sign against the steering direction and what the cars push. Session only. Toggle.\n" +
            "SPRING         Unlock the stationary spring outside Assetto Corsa, RaceRoom, Le Mans Ultimate and Forza, where it is locked while it is retested game by game (it has misbehaved before, and the per-game rework has only been driven in those four). Unlocked, tick the spring while each game is running and that game keeps its own enabled/strength/cutoff. Locking again returns every other game to off without deleting what you tuned. Assetto Corsa, RaceRoom, Le Mans Ultimate and Forza are unaffected either way. Persists, does not travel in a backup. Toggle.\n" +
            "ARCADE         Unlock the shelved arcade cabinet path: the TeknoParrot and FFB Arcade Plugin force sources, the Initial D 8 memory map (rpm, gear, speed, steering, slip) and its in-game leaderboards and ladder. Off in shipping builds: the arcade work was built against TeknoParrot, and the other way people run Initial D 8 is micetools plus a server emulator, which fills the game's own leaderboards from a real server and renders its own force feedback. Turning it off also puts the game's own leaderboard rows back. Restart SimHub after typing it: the Arcade.* dash properties are attached at startup. Persists. Toggle.\n" +
            "ID8SCAN [text] Arcade: search the running Initial D 8 cabinet's memory for a string (SEGA by default, the placeholder name that fills its leaderboard while the cabinet has no network) and log what surrounds every hit, so the row layout can be mapped. Read-only, one shot, and it takes a few seconds.\n" +
            "ARCADEFX [OFF] Arcade: name the live arcade force effects in the log as you drive and as you move through the cabinet's menus, so which effect carries which feel is read off a run instead of inferred. Session only; ARCADEFX OFF stops it.\n" +
            "MENUFX [OFF]   Arcade: knock the wheel as a cabinet's menus are used, a thud when you confirm and a tick as you move through the options. A cabinet has no keyboard and its menus are driven from the wheel, so the wheel is where the feedback belongs. Persists; MENUFX OFF stops it.\n" +
            "DRIVER         Driver testing mode: route FFB through the kernel filter driver (sole wheel ownership). Needs the TFFA filter driver installed. Persists. Toggle.\n" +
            "DIDAMP [pct]   DEV: drive the wheel's NATIVE DirectInput damper from the plugin (default 75%) with the Trueforce stream fully stopped (the wheel exactly as without the plugin), and log the position read rate: the DAMPCAL feasibility spike. DIDAMP OFF ends it (auto-off after 60 s).\n" +
            "DAMPCAL        Damper calibration wizard, NO GAME NEEDED: three conditions x three hand flicks (the plugin stands aside and drives the wheel's own damper = the native reference; stream at raw zero = friction only; synthesized at the current gain). Fits each flick's decay on the wheel's DirectInput position, cancels friction and inertia, and sets the synthesized gain to match the native damper for this session. Progress on the status line and the wheel screen. DAMPCAL OFF cancels. CSPFFB DAMPSIGN flips the damper if it feels like an anti-damper.\n" +
            "DICOND         A/B: the game's DirectInput condition effects (damper, spring, friction, inertia) and rumble, decoded from the USB wire and rendered into the Trueforce stream (the wheel firmware ignores them while any stream is live). ON by default; type to disable or re-enable. Session only.\n" +
            "CLASSICCOND    G923 PS/PC only: render the game's classic-protocol damper, friction and spring slots (the DirectInput effects a game plays on this wheel, which the firmware ignores while Trueforce streams) through the same condition engine the HID++ wheels use. Expect centering as well as damping: the game's spring now plays on top of the streamed force instead of only in spring mode, so a game that sends road force and an autocenter together gives you both. Spring mode is unchanged and still takes over when it arms, and the captured spring stands down while it does. Unvalidated on hardware: the first decoded effect of each type is logged with its raw bytes, DICOND is the A/B, CSPFFB DAMPSIGN flips the direction, DAMPCAL sets the strength. Persists, does not travel in a backup. Toggle.\n" +
            "FXTEST         Shows or hides the effect test bench at the bottom of the FFB tab (type it again to hide it). NO GAME NEEDED: the bench plays the wheel's own DirectInput effect with the Trueforce stream fully STOPPED, so the firmware renders it exactly as it would without the plugin (the reference feel), then the identical effect through the plugin's renderer, so you can alternate the two and tune until they match. It also carries the hands-free Auto-tune. The typed forms still work: 'FXTEST NATIVE <effect>' and 'FXTEST ENGINE <effect>'; effects DAMPER, SPRING, FRICTION, INERTIA, SINE, SQUARE, TRIANGLE, SAWUP, SAWDOWN, RAMP, with optional strength% (default 50) and period ms (default 250). FXTEST OFF ends a running test; auto-off after 30 s.\n" +
            "FXDUMP         Effect-download trace: one log line per effect the wheel is asked to download, decoded straight off the USB wire, with its type byte and its raw parameters (coefficients, saturations, deadband, centre, or magnitude and period). Answers whether the wheel was asked for what you think you asked for: on the bench a native effect passes through DirectInput, Windows and Logitech's driver first, and a substituted type or reshaped parameter cannot be told apart by feel. Session only. Toggle.\n" +
            "SOFTLOCK       Soft-lock diagnostic (Assetto Corsa, iRacing and RaceRoom): a '[TF4ALL] SOFTLOCK' line twice a second with the steering position, how far the soft lock has engaged, the force it is aiming for, and what the stationary spring is contributing. Fires from 0.9 of the car's steering limit whether or not a lock results, so a lock that never engages shows up as clearly as one that does. In Assetto Corsa it needs CUSTOM_SOFT_LOCK enabled in CSP's FFB Tweaks and the TF4ALL CSP Bridge installed; in iRacing and RaceRoom (takeover on) it needs the Soft lock option on. Session only. Toggle.\n" +
            "ACLEDS         Rev-light contention diagnostic: every 2 s, a '[REVLIGHT]' line with the level writes the GAME landed on the wheel's rev-light feature (measured off the USB wire), the longest gap between two of them, the level they left, and what our own LEDs and base screen were allowed to do at the time. In Assetto Corsa it also reports whether CSP's own rev-light module is driving the bar. For lights that stick, go dark, then catch up seconds later. Session only. Toggle.\n" +
            "FRESH          Filter the Presets tab to built-in (factory) presets only, to preview the fresh-install library. Hides your own presets without deleting them. Toggle.\n" +
            "DEV            Unlock the Developer tools bar (Presets tab) + per-row 'Set as built-in' promote buttons: maintain the file-based built-in folder (validate / open / promote selected or checked). Persists. Toggle.\n" +
            "SLOTRESTORE<n> Put your own colors back into custom slot n (1-5, default 5) from the backup taken before the plugin first wrote the slot. A slot left borrowed by a crashed session is also restored automatically at the next launch.\n" +
            "LEDRATE<ms>    DEV: how often the rev lights may update, 10 to 1000 ms (default 40, measured safe on a G PRO; G HUB itself uses 160). Live only, resets on restart. For finding whether a faster bar costs anything on the shared HID++ pipe: drive it and watch the FORCE.\n" +
            "SLOTBLANK<n>   DEV: make custom slot n (1-5) read as NEVER PROGRAMMED, so the factory-wheel first run can be tested. Backs the slot up FIRST and refuses if it cannot. PERSISTS across a restart, which is the point, and your colors stay held in the backup the whole time. SLOTRESTORE<n> puts them back. SLOTBLANKALL does all five at once (the real factory-wheel case) and SLOTRESTOREALL gives them all back.\n" +
            "SLOTPICK<n>    Designate which LIGHTSYNC custom slot the plugin may borrow for the live pattern: 1-5 for CUSTOM n, 0 for automatic (the first slot never programmed, or CUSTOM 5 when all five are in use). The slot's contents are backed up before the first write and SLOTRESTORE<n> puts them back. Persists and travels in backups.\n" +
            "SLOTPROBE      Ask the wheel whether it supports per-slot LIGHTSYNC colors (HID++ 0x807B) and dump what each of the five custom slots currently holds. READ-ONLY: writes nothing, selects nothing, safe with a game running. Answers whether custom colors are possible on this wheel at all.\n" +
            "LIGHTSYNC      Hide the LIGHTSYNC & OLED tab and move the wheel lights + screen controls back onto the Telemetry FFB tab (nothing is duplicated). Type again to bring the tab back. On by default. Persists. Toggle.\n" +
            "CYCLEHINT      Re-arm the LIGHTSYNC intro modal and force the cycle-binding hint on screen even though the pattern cycle action is already bound. For testing them, since anyone working on them has it bound. Session only. Toggle.\n" +
            "MANUALPIN      Reveal the Diagnostics 'Pick device manually...' control (hidden by default; auto-discovery + self-heal handle almost every case). Persists. Toggle.\n" +
            "F8SWEEP        Experimental: sweep the rev lights via the legacy F8 12 command on the wheel's gamepad collection (off the HID++ FFB pipe). Writes at forza-wheel-leds' ~60 Hz rate by default (worst-case FFB test): drive a sim and check the LEDs sweep AND the FFB stays solid. Toggle. 'F8SWEEP FAST' = resend every 16 ms; 'F8SWEEP SLOW' = paced write-on-change (our footprint, for comparison); 'F8SWEEP <ms>' = custom resend interval (0-1000).\n" +
            "F8ANY          G923 PS/PC only: run the legacy F8 rev lights in ANY game, including ones driving their own force feedback, so you can answer this by playing and revving rather than watching a test sweep. The question is whether the lights come on AND the game's force stays solid; if the force cuts, that is the answer, not a fault. Persists. Toggle.\n" +
            "TRACE          Toggle the high-rate FFB signal-chain trace (game force vs plugin output vs steering, full provider rate); second TRACE dumps the CSV under Documents\\TrueforceForAll.\n" +
            "SWEEP          Motor characterization: 15 s log-sine force sweep 8-300 Hz through the wheel (hands lightly on the rim). SWEEP1..SWEEP6 = one octave band each (~5 s): 8-16, 16-32, 32-63, 63-125, 125-250, 250-400 Hz.\n" +
            "B* <value>     Live Mode B tuning, e.g. 'BDIRK 0.5': MBCPD 1/0 direct centering, BDIRK center feel, BLOCKPT lockup slip point, BGTRIM braking-grip trim, BCIRCLE 1/0 friction-circle braking, BLEARN 1/0 auto braking grip per car (these persist); BFULL full-slip point + BSPD full-force speed km/h are live-only. The other tuning values now have sliders or checkboxes on the Telemetry Based FFB tab and the phone dash.\n" +
            "OLEDTEST       Show sample wheel-screen frames on the OLED so you can check it works (no game running).\n" +
            "OLEDMS <ms>    How often the wheel's OLED may be redrawn, in milliseconds (20-1000; default 20 = 50 per second). Lower is smoother and uses more of the wheel's shared command channel. Type OLEDMS with no number to read the current value. Persists.\n" +
            "WARNEMAIL / WARN   Email yourself the backup-deletion warning, cycling 6mo -> 3mo -> 1mo -> 1wk -> 1day each use. Preview only; never changes your real data or timer.\n" +
            "IRRAW          iRacing raw-data support probe: logs what the user's SimHub build exposes (whether SimHub's raw data object reaches us live per tick, whether SteeringWheelTorque carries force while iRacing's own force feedback is disabled, and whether the 360 Hz SteeringWheelTorque_ST array is reachable). One arming dump plus one '[TF4ALL] IRRAW' line every 5 s in SimHub.txt. Toggle.\n" +
            "LOCSTATUS      Language runtime status: the active language and where it came from (requested, parent or English), the English key count and how many keys the language lacks.\n" +
            "LOCLANG <tag>  Switch the plugin's language for this session (LOCLANG es, LOCLANG de-DE); bound labels re-render live. Does not persist; a restart returns to the SimHub setting.\n" +
            "LOCREPORT      Write missing-keys.<tag>.txt for the active language into PluginsData\\Common\\TrueforceForAll-Languages: every English key the language lacks, as JSON lines ready to paste into <tag>.json.\n" +
            "LOCMARK        Bracket every string that fell back to English because the active language lacks it, in ⟦ ⟧, so untranslated text stands out on screen. Toggle.\n" +
            "PSEUDO         Pseudo-localize the loaded panel's hard-coded and code-assigned text only: each such label is accented and made 30 percent longer, then every text is measured and each one that no longer fits is logged as '[TF4ALL] pseudo-clip' in SimHub.txt. Bound {loc:T} text is left alone; exercise it with tools\\loc\\pseudo.ps1 plus LOCLANG qps-ploc. Dev-only; reopen the panel for the real text.\n" +
            "PSEUDOCJK      The same walk with a Hangul and Han fill of the same length, for font fallback and line height. Hard-coded and code-assigned text only, like PSEUDO; bound {loc:T} text is exercised with pseudo.ps1 plus LOCLANG qps-ploc.";

        private void CommitAccessCode()
        {
            if (AccessCodeBox == null) return;
            ExecuteAccessCode((AccessCodeBox.Text ?? string.Empty).Trim());
        }

        // Open the scrollable test-code browser. It carries its own input box so
        // codes can be run without closing it; each run reuses ExecuteAccessCode
        // and surfaces the resulting status back into the window.
        private void ShowTestCodesWindow()
        {
            var win = new TestCodesWindow(TestCodeCatalog, c =>
            {
                if (string.IsNullOrWhiteSpace(c)) return null;
                string code = c.Trim();
                // Don't recurse into another codes window from inside this one.
                if (code.Equals("HELP", StringComparison.OrdinalIgnoreCase)
                    || code.Equals("CODES", StringComparison.OrdinalIgnoreCase) || code == "?")
                    return "Already showing the code list.";
                if (AccessCodeStatus != null) AccessCodeStatus.Text = string.Empty;
                ExecuteAccessCode(code);
                return AccessCodeStatus?.Text;
            })
            { Owner = Window.GetWindow(this) };
            win.ShowDialog();
        }

        // Validate a calendar date from MOTDDATE's MMDDYYYY digits.
        private static bool TryBuildDate(int year, int month, int day, out DateTime date)
        {
            date = default(DateTime);
            if (year < 1 || year > 9999 || month < 1 || month > 12) return false;
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
            date = new DateTime(year, month, day);
            return true;
        }

        private void ExecuteAccessCode(string code)
        {
            if (_suppressEvents || _plugin?.Settings == null || AccessCodeBox == null) return;
            if (string.IsNullOrEmpty(code)) return;

            // Live Mode B tuning: "NAME value" (e.g. "BDIRK 0.5", "BLOCKPT 0.2",
            // "MBCPD 1"). Two tokens with a numeric second token; single-word
            // codes below never match. Dispatches to the plugin-side
            // clamp+apply switch (SetModeBParam) and echoes its status. Names
            // that map to a Settings field persist; BFULL/BSPD are live-only
            // model probes. The other Mode B tunables have sliders or
            // checkboxes on the Telemetry Based FFB tab and the phone dash.
            {
                var parts = code.Split(new[] { ' ', '=', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && float.TryParse(parts[1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float mbVal))
                {
                    string pn = parts[0].ToUpperInvariant();
                    if (pn == "MBCPD" || pn == "BDIRK" || pn == "BLOCKPT" || pn == "BGTRIM"
                        || pn == "BCIRCLE" || pn == "BLEARN" || pn == "BFULL" || pn == "BSPD")
                    {
                        string st = _plugin.SetModeBParam(pn, mbVal);
                        // Re-sync ALL controls from the now-updated settings
                        // (RefreshFromPlugin suppresses its own events).
                        // Without this, a code-set value sat in Settings while
                        // its slider/checkbox stayed stale, and a later drag
                        // of that stale slider (ModeBSlider_ValueChanged) or
                        // the write-all ModeBFeel_Changed silently persisted
                        // the stale control state back over it, reverting the
                        // code.
                        RefreshFromPlugin();
                        AccessCodeBox.Text = string.Empty;
                        if (AccessCodeStatus != null) AccessCodeStatus.Text = "Set " + st + " (live).";
                        return;
                    }
                }
            }

            // Self-documenting: open the scrollable test-code browser.
            if (code.Equals("HELP", StringComparison.OrdinalIgnoreCase)
                || code.Equals("CODES", StringComparison.OrdinalIgnoreCase)
                || code == "?")
            {
                AccessCodeBox.Text = string.Empty;
                ShowTestCodesWindow();
                if (AccessCodeStatus != null) AccessCodeStatus.Text = "Opened the test-code list.";
                return;
            }
            // Dev-only: flush the Message-of-the-day cache + every dismissal so a
            // fresh fetch repopulates and previously-dismissed (or edited)
            // messages reappear. Bypasses the ~6h cache TTL for testing.
            if (code.Equals("MOTDFLUSH", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.MotdCache = new MotdCacheData();
                _plugin.Settings.MotdDismissedIds?.Clear();
                _plugin.Settings.MotdPoolDismissedOn?.Clear();
                _plugin.Settings.MotdRecurringDismissedOcc?.Clear();
                _plugin.SaveMotdState();
                AccessCodeBox.Text = string.Empty;
                _motdStrip?.Refresh(forceFetch: true);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "MOTD cache + dismissals cleared; refetching.";
                return;
            }
            // Dev-only: preview the MOTD strip as if it were a random upcoming day
            // (date-stable rolls shift to it; dismissals + nag cooldown bypassed).
            // Lets you eyeball pool variety + upcoming recurring messages on demand.
            if (code.Equals("MOTDROLL", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string summary = _motdStrip?.SimulateRandomDay();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = summary != null
                        ? "MOTD preview, random day " + summary + ". MOTDROLL again to re-roll, MOTDLIVE to exit."
                        : "MOTD strip unavailable.";
                return;
            }
            // Dev-only: preview the MOTD strip as if it were a specific date, entered
            // as MMDDYYYY right after the code (e.g. MOTDDATE12252026), to check
            // upcoming recurring messages before they trigger.
            if (code.StartsWith("MOTDDATE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string digits = code.Substring("MOTDDATE".Length).Trim();
                DateTime simDate = default(DateTime);
                bool parsedDate = digits.Length == 8
                    && int.TryParse(digits.Substring(0, 2), out int mm)
                    && int.TryParse(digits.Substring(2, 2), out int dd)
                    && int.TryParse(digits.Substring(4, 4), out int yyyy)
                    && TryBuildDate(yyyy, mm, dd, out simDate);
                if (parsedDate)
                {
                    string summary = _motdStrip?.SimulateDate(simDate);
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "MOTD preview " + summary + ". MOTDLIVE to exit.";
                }
                else if (AccessCodeStatus != null)
                {
                    AccessCodeStatus.Text = "Enter the date as MMDDYYYY, e.g. MOTDDATE12252026.";
                }
                return;
            }
            // Dev-only: leave MOTD preview and return to today's real messages.
            if (code.Equals("MOTDLIVE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                _motdStrip?.ClearSimulation();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "MOTD preview off; showing today's messages.";
                return;
            }
            // Dev-only: clear the community car-facts cache (names/engine types/
            // redlines). Refetches per car on the next car open.
            if (code.Equals("CACHEFACTS", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.CommunityFactCache?.Clear();
                _engineCommunityFetchedKey = null;   // active car re-evaluates next tick
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Community car-facts cache cleared; it refetches per car.";
                return;
            }
            // Dev-only: clear every re-fetchable NETWORK cache at once. Excludes
            // the destructive / local ones on purpose (car corrections, local
            // cylinder detections, sign-in).
            if (code.Equals("CLEARCACHES", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.MotdCache = new MotdCacheData();
                _plugin.Settings.MotdDismissedIds?.Clear();
                _plugin.Settings.MotdPoolDismissedOn?.Clear();
                _plugin.Settings.MotdRecurringDismissedOcc?.Clear();
                _plugin.Settings.CommunityFactCache?.Clear();
                _engineCommunityFetchedKey = null;
                _plugin.ClearBrowseCache();
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                _motdStrip?.Refresh(forceFetch: true);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Cleared all network caches (MOTD, community facts, browse lists).";
                return;
            }
            // Dev-only: launch a chosen installer with /CloseSimHub=1 so the
            // installer's silent SimHub auto-close (what the in-app Update
            // button passes once the user accepts) can be tested without a real
            // update download. WARNING: the picked installer closes SimHub.
            if (code.Equals("CLOSESIM", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = "Pick the TrueforceForAll-Setup .exe to run with /CloseSimHub=1",
                    Filter = "Installer (*.exe)|*.exe",
                };
                if (dlg.ShowDialog() == true)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName)
                        {
                            UseShellExecute = true,
                            Arguments = "/CloseSimHub=1",
                        });
                        if (AccessCodeStatus != null)
                            AccessCodeStatus.Text = "Launched installer with /CloseSimHub=1 (test).";
                    }
                    catch (Exception ex)
                    {
                        TrueforceDialog.ShowError(Window.GetWindow(this),
                            "Couldn't launch. See the SimHub log, then try again.",
                            ex);
                    }
                }
                return;
            }
            // Dev-only: show the periodic support modal right now, ignoring the
            // seat-time ladder, the idle gate and the ever-supported latch. Pacing
            // is NOT advanced, so this is a pure preview and repeatable.
            if (code.Equals("SUPPORT", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Support prompt shown (preview; pacing untouched).";
                ShowSupportPrompt(recordPacing: false);
                return;
            }
            // Dev-only: put the support-prompt ladder back to the start (next ask
            // due at the first rung again) and clear the decline back-off.
            if (code.Equals("SUPPORTRESET", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.SupportPromptCount = 0;
                _plugin.Settings.SupportPromptDeclineCount = 0;
                _plugin.Settings.SupportPromptLastUtc = null;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Support-prompt pacing reset.";
                return;
            }
            // Dev-only: toggle the aligned telemetry+FFB capture log (v2 golden
            // format, the replay-harness fixture recorder). Pure observation;
            // safe while driving. See TrueforcePlugin.ToggleFfbCapture.
            if (code.Equals("CAPTURE", StringComparison.OrdinalIgnoreCase))
            {
                string status = _plugin.ToggleFfbCapture();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = status;
                return;
            }

            // Dev-only: simulate a Forza "no packets" stall so the status line
            // reads stalled and the "Not receiving packets?" troubleshooter
            // auto-opens, without needing a live (broken) Forza session. Toggle:
            // type STALL again to clear. The actual UI change lands on the next
            // refresh tick (re-arming the auto-expand latch each toggle).
            if (code.Equals("STALL", StringComparison.OrdinalIgnoreCase))
            {
                _forceForzaStall = !_forceForzaStall;
                _forzaTroubleshootAutoExpanded = false;
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _forceForzaStall
                        ? "Forza stall simulated: status reads stalled, the troubleshooter auto-opens, and the UDP setup banner shows (type STALL again to clear)."
                        : "Forza stall simulation cleared.";
                return;
            }

            // Toggle the two info-tier Forza banners (SimHub-fallback notice +
            // discovered-port banner) so their InfoBannerButton styling can be
            // checked without getting Forza into those telemetry states. Lands
            // on the next refresh tick.
            if (code.Equals("FZBANNERS", StringComparison.OrdinalIgnoreCase))
            {
                _forceForzaInfoBanners = (_forceForzaInfoBanners + 1) % 2;
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text =
                        _forceForzaInfoBanners == 1 ? "Forza info banners forced on (type FZBANNERS again to clear)."
                      : "Forza info banners cleared.";
                return;
            }

            // Dev-only: filter the preset library to built-ins only, so we can
            // see the fresh-install library a brand-new user gets. Toggle: type
            // FRESH again to restore the full view. Nothing is deleted; the
            // user's own presets are just hidden while it's on.
            if (code.Equals("FRESH", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (_presetManager != null)
                {
                    _presetManager.BuiltinsOnly = !_presetManager.BuiltinsOnly;
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = _presetManager.BuiltinsOnly
                            ? "Built-ins-only view ON: the Presets tab now shows only the shipped factory presets (your own are hidden, not deleted). Type FRESH again to restore."
                            : "Built-ins-only view OFF: your full preset library is back.";
                }
                return;
            }

            // Dev-only: email yourself the backup-deletion warning, cycling stage 1..5
            // (6mo / 3mo / 1mo / 1wk / 1day) on each use so you can see the escalating copy.
            // Sends to your signed-in address; never changes your real entitlement / timer.
            if (code.Equals("WARNEMAIL", StringComparison.OrdinalIgnoreCase)
                || code.Equals("WARN", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (_plugin == null || !_plugin.AuthIsSignedIn)
                {
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = "Sign in first to test the warning email.";
                    return;
                }
                int stage = _warnPreviewStage;
                _warnPreviewStage = stage >= 5 ? 1 : stage + 1;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Sending warning email stage " + stage + "/5 to your address...";
                _ = SendWarnPreviewAndReport(stage);
                return;
            }

            // Dev-only: toggle the experimental legacy "F8 12" rev-LED sweep on
            // the wheel's gamepad/DirectInput collection (off the HID++ FFB pipe).
            // Confirms on hardware whether the legacy LED command lights the strip
            // AND coexists with live FFB: a non-contending LED path we could use
            // in every game, not just iRacing. Toggle: type it again to stop
            // (LEDs off). "F8SWEEP FAST" resends every 16 ms, "F8SWEEP SLOW" is
            // paced write-on-change, "F8SWEEP <ms>" sets a custom resend
            // interval. See LegacyLedF8Channel + project_led_ffb_contention_model.
            {
                var f8parts = code.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string f8cmd = f8parts.Length > 0 ? f8parts[0].ToUpperInvariant() : string.Empty;
                if (f8cmd == "F8SWEEP")
                {
                    AccessCodeBox.Text = string.Empty;
                    int? resend = null;        // null => simple on/off toggle (paced)
                    if (f8parts.Length > 1)
                    {
                        string a = f8parts[1].ToUpperInvariant();
                        if (a == "FAST") resend = 16;
                        else if (a == "SLOW") resend = 0;
                        else if (int.TryParse(f8parts[1], out int ms))
                            resend = Math.Max(0, Math.Min(1000, ms));
                    }
                    string status = _plugin?.ToggleF8LedSweep(resend) ?? "(plugin not ready)";
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = status;
                    return;
                }
            }

            // Dev-only: unlock the Developer panel (built-in folder
            // maintenance: export/import/reseed/validate/open). Persisted so it
            // stays on across restarts on a dev machine. Toggle.
            if (code.Equals("DEV", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.DevModeUnlocked = !_plugin.Settings.DevModeUnlocked;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                ApplyDevModeVisibility();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _plugin.Settings.DevModeUnlocked
                        ? "Developer mode ON: the Presets tab now shows the Developer tools bar + per-row 'Set as built-in' promote buttons. Type DEV again to hide."
                        : "Developer mode OFF.";
                return;
            }

            // Dev-only: feel the stationary spring on the desk without a game.
            // Drives a synthetic centering force that flips direction every
            // ~1.5 s for ~6 s, bypassing the enabled/speed/steering gates.
            // Lets us verify strength + the force-vs-position direction. It
            // does NOT verify a given game's steering sign (that needs a
            // session); it confirms the spring's own mapping is correct.
            // SPRINGTEST, not SPRING: two handlers answered to SPRING and this
            // one ran first, so the unlock below was unreachable. Typing SPRING
            // at the rig ran a six-second desk test and left the spring locked
            // (2026-09-12).
            if (code.Equals("SPRINGTEST", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.StartStationarySpringTest();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Stationary-spring test (~6 s): hold the wheel; the motor pushes one way, then the other, every ~1.5 s, so you can feel the spring strength and direction. (Needs the wheel connected and streaming.)";
                return;
            }

            // Dev-only: re-show the "What's new" changelog banner and every
            // per-effect NEW badge by clearing the seen state. Lets us verify
            // the release UX (banner copy + the new RevLimiter badge) without
            // hand-editing the settings file.
            if (code.Equals("WHATSNEW", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugResetChangelogSeen();
                RefreshChangelogBanner();
                RefreshNewBadges();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Changelog state reset: the 'What's new' banner and all NEW effect badges are showing again (banner lists full history).";
                return;
            }

            // Dev-only: reset the networked-welcome modal seen state so
            // the pitch fires again immediately. Clears all three
            // gating fields: HasSeenNetworkedWelcome (the hard latch),
            // WelcomeDeclineCount (the second-decline-locks counter),
            // and WelcomeNextShowAt (the 14-day re-show timer).
            if (code.Equals("WELCOME", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.HasSeenNetworkedWelcome = false;
                _plugin.Settings.WelcomeDeclineCount     = 0;
                _plugin.Settings.WelcomeNextShowAt       = null;
                // Also reset the Mode B intro so both first-run modals can be retested.
                _plugin.Settings.HasSeenModeBIntro       = false;
                try { _plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Networked-welcome reset (opening now); Mode B intro re-armed for the Telemetry Based FFB tab.";
                // Re-trigger via the same gate the normal startup path
                // uses so any preconditions (backend URL configured,
                // etc.) apply identically. Clear the per-session guard
                // first or the reset would no-op after an earlier show.
                WelcomeWindow.ShownThisSession = false;
                MaybeShowNetworkedWelcome();
                // The Mode B intro is NOT force-shown here: it would pop over
                // whatever screen you're on (and stack on the welcome, which is
                // the exact behavior we moved it off of). Clearing HasSeenModeBIntro
                // above re-arms it; it shows when the Telemetry Based FFB tab opens.
                return;
            }

            // Dev/test: preview the EXACT in-app render of release-notes
            // markdown from the clipboard. Since the plugin can't fetch an
            // unpublished draft from GitHub, copy the draft's markdown body,
            // type PREVIEW, and see it rendered through the same code path the
            // live What's-new uses.
            if (code.Equals("PREVIEW", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string md = null;
                try { if (System.Windows.Clipboard.ContainsText()) md = System.Windows.Clipboard.GetText(); }
                catch { }
                if (string.IsNullOrWhiteSpace(md))
                {
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "Clipboard has no text. Copy the release-notes markdown first, then type PREVIEW.";
                    return;
                }
                ShowNotesPreview(md);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Previewed the clipboard markdown as the in-app What's new would render it.";
                return;
            }

            // Dev-only: pretend a newer release exists so the update banner +
            // update modal can be verified locally. Appears on the next status
            // refresh tick (~1 s).
            if (code.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.UpdateChecker?.DebugSimulateUpdateAvailable();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _plugin.UpdateChecker != null
                        ? "Simulated update available: the update banner appears within ~1 s; click it to see the update modal."
                        : "Update checker unavailable.";
                return;
            }

            // Dev-only: like UPDATE, but also (1) reports unsaved changes so the
            // pre-update warning fires, and (2) opens the update modal in a mode
            // where "Update now" runs a locally-picked installer instead of
            // downloading from GitHub. Exercises the whole in-app update flow
            // (unsaved-changes guard, then launch the installer with
            // /CloseSimHub=1) without a real release. WARNING: the picked
            // installer closes SimHub.
            if (code.Equals("UPDATEDIRTY", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                var checker = _plugin.UpdateChecker;
                if (checker == null)
                {
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = "Update checker unavailable.";
                    return;
                }
                checker.DebugSimulateUpdateAvailable();
                _updateLocalInstallerTest = true;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Local-installer update test: the unsaved-changes warning is armed; pick an installer when you click Update now.";
                ShowUpdateModal();
                return;
            }

            // Dev-only: simulate a release shipping AFTER launch. Arms a fake
            // newer release that only the background re-check applies, plus a
            // fast cadence, so the update banner appears on its own within a few
            // seconds (no restart, no real release) instead of after hours.
            // Optional trailing number sets the cadence in seconds (e.g.
            // UPDATEPOLL3); defaults to 5s. Run again to stop + clear.
            if (code.StartsWith("UPDATEPOLL", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string rest = code.Substring("UPDATEPOLL".Length).Trim();
                int seconds = 5;
                if (rest.Length > 0 && int.TryParse(rest, out int parsed) && parsed > 0)
                    seconds = parsed;
                int active = _plugin.DebugToggleFastUpdatePolling(seconds);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = active > 0
                        ? $"Armed: no banner yet, but within ~{active}s a background re-check discovers a simulated newer release and the 'Update to vX.Y.Z' banner appears on its own. No restart. Type UPDATEPOLL again to stop + clear."
                        : "Stopped: simulated release cleared, back to normal update cadence.";
                return;
            }

            // Dev-only: force the wheel into the stream-fault state so the
            // recovery watchdog re-attaches it. Verifies the "Stream lost -
            // auto-reconnecting" status + transparent recovery without
            // physically unplugging.
            if (code.Equals("FAULT", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugForceStreamFault();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Stream fault forced: status should read 'Stream lost, auto-reconnecting', then recover within a few seconds (watchdog re-attaches the wheel).";
                return;
            }

            // Dev/tester: high-rate FFB signal-chain trace (game force vs the
            // plugin's output vs steering, at the full provider rate). Type
            // TRACE to start, reproduce the issue, TRACE again to dump the
            // CSV under Documents\TrueforceForAll.
            if (code.Equals("TRACE", StringComparison.OrdinalIgnoreCase))
            {
                string traceStatus = _plugin.ToggleFfbTrace();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null) AccessCodeStatus.Text = traceStatus;
                return;
            }

            // iRacing raw-data support probe. Answers, from SimHub.txt on a
            // user's rig, what their SimHub build exposes: whether SimHub's raw
            // iRacing object reaches a third-party plugin per tick, whether
            // SteeringWheelTorque carries force while iRacing's own force
            // feedback is disabled, and whether the 360 Hz SteeringWheelTorque_ST
            // array is reachable through the telemetry object's dictionary base.
            // Toggle. The probe itself lives in
            // TrueforcePlugin.DebugToggleIracingRawProbe.
            if (code.Equals("IRRAW", StringComparison.OrdinalIgnoreCase))
            {
                bool irRawOn = _plugin.DebugToggleIracingRawProbe();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = irRawOn
                        ? "iRacing raw-telemetry probe armed. Get on track and drive: it writes one arming dump plus one '[TF4ALL] IRRAW' line every 5 seconds to SimHub.txt. Drive about 30 s with iRacing's own force feedback DISABLED, then about 30 s with it enabled, then type IRRAW again to stop."
                        : "iRacing raw-telemetry probe stopped. Look for '[TF4ALL] IRRAW' lines in SimHub.txt.";
                return;
            }

            // Motor-characterization sweeps. Plain SWEEP: 15 s log sine, 8 to
            // 300 Hz, even time per octave. SWEEP1..SWEEP6: one octave band
            // each (~5 s) so a tester can judge bands independently instead
            // of tracking seconds inside one long run. The length guard keeps
            // this from matching F8SWEEP (the legacy rev-LED sweep) or any
            // longer SWEEP-prefixed input.
            if (code.StartsWith("SWEEP", StringComparison.OrdinalIgnoreCase)
                && (code.Length == 5 || (code.Length == 6 && code[5] >= '1' && code[5] <= '6')))
            {
                var sweep = _plugin.MotorSweep;
                string what;
                if (code.Length == 6)
                {
                    // Octave bands: 1:8-16, 2:16-32, 3:32-63, 4:63-125,
                    // 5:125-250, 6:250-400 (the top band overshoots 300 on
                    // purpose to find the true ceiling).
                    float[] edges = { 8f, 16f, 32f, 63f, 125f, 250f, 400f };
                    int band = code[5] - '1';
                    sweep.StartHz    = edges[band];
                    sweep.EndHz      = edges[band + 1];
                    sweep.DurationMs = 5000;
                    what = $"Band {band + 1}/6: {edges[band]:0}-{edges[band + 1]:0} Hz for 5 s.";
                }
                else
                {
                    sweep.StartHz    = 8f;
                    sweep.EndHz      = 300f;
                    sweep.DurationMs = 15000;
                    what = "Full sweep (15 s): 8-300 Hz, even time per octave. For band-by-band judging use SWEEP1..SWEEP6.";
                }
                _plugin.TestEffect(sweep);
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = what + " Hands lightly on the rim; rate it strong / buzzy / weak / dead.";
                return;
            }

            // Simulate "tap sees the wheel + FFB on the wire but can't extract
            // it." Drive after enabling: the tap retries in whole-bus mode after
            // ~8 s and surfaces the no-FFB notice after ~15 s. Toggle off to
            // restore real FFB pass-through. FFB will be limp while it's on.
            if (code.Equals("NOFFB", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.DebugToggleSimulateNoFfb();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Simulating no-FFB capture. Drive for ~15s: the FFB tap should switch to whole-bus capture (~8s), then the FFB-tap status should show a 'try a different USB port' notice. Type NOFFB again to stop (FFB stays limp until you do)."
                        : "Stopped simulating no-FFB capture. Force feedback pass-through restored.";
                return;
            }

            // CSP bridge force: the sim's pre-gain force from the TF4ALL CSP
            // script's shared-memory block, ahead of finalFF and the tap.
            // "CSPFFB" toggles it; "CSPFFB PURE|TORQUE|FINAL|VALUE" picks the
            // field; "CSPFFB NM <n>" sets the full-scale torque for TORQUE.
            if (code.Equals("CSPFFB", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("CSPFFB ", StringComparison.OrdinalIgnoreCase))
            {
                var cspParts = code.Split(new[] { ' ', '=', '	' }, StringSplitOptions.RemoveEmptyEntries);
                AccessCodeBox.Text = string.Empty;
                if (cspParts.Length == 1)
                {
                    // The AC CSP bridge is automatic (used whenever its script
                    // is installed, else the tap). This bare toggle is only a dev
                    // force-off. Field/diagnostic sub-commands are below.
                    bool on = _plugin.ToggleCspBridgeFfb();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = on
                            ? "CSP bridge ON (automatic): the plugin uses the TF4ALL CSP Bridge when its script is installed, otherwise the USB capture. Install the script from the Assetto Corsa prompt, the guide, or the settings button. Type CSPFFB again to force it off."
                            : "CSP bridge FORCED OFF: the plugin uses the USB capture in Assetto Corsa even if the bridge script is installed. Type CSPFFB again to return to automatic.";
                    return;
                }
                string arg = cspParts[1].ToUpperInvariant();
                if (arg == "NM")
                {
                    if (cspParts.Length >= 3 && double.TryParse(cspParts[2],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double nm))
                    {
                        double set = _plugin.SetCspBridgeMaxNm(nm);
                        if (AccessCodeStatus != null)
                            AccessCodeStatus.Text = $"CSP bridge full-scale torque set to {set:F1} Nm (that many Nm of column torque = full wheel force, for the TORQUE field).";
                    }
                    else if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "Usage: CSPFFB NM <number>, e.g. CSPFFB NM 8.";
                    return;
                }
                if (arg == "SUP" || arg == "NOSUP")
                {
                    bool sup = _plugin.SetCspSuppress(arg == "SUP");
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = sup
                            ? "CSP wheel-output suppression ON: the script returns 0 to the wheel (normal takeover)."
                            : "CSP wheel-output suppression OFF: the game keeps driving the wheel while the plugin only reads the bridge. Diagnostic for whether our 0 output is what zeroes AC's ffb fields; expect the game and plugin to fight the wheel meanwhile.";
                    return;
                }
                if (arg == "DAMP")
                {
                    bool off = _plugin.ToggleCspZeroDamper();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = off
                            ? "CSP synthesized damper OFF (A/B: the wheel runs undamped). Applies instantly; type CSPFFB DAMP again to turn it back on. Session only."
                            : "CSP synthesized damper ON: the plugin renders AC's damper into the force stream (the wheel ignores the classic damper channel while Trueforce streams).";
                    return;
                }
                if (arg == "DAMPSIGN")
                {
                    bool flipped = _plugin.ToggleDamperSign();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = flipped
                            ? "Synthesized damper sign FLIPPED (for a wheel whose DirectInput axis runs opposite to the stream's torque direction). Session only."
                            : "Synthesized damper sign back to normal.";
                    return;
                }
                if (arg == "DAMPK")
                {
                    if (cspParts.Length >= 3 && double.TryParse(cspParts[2],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double dk))
                    {
                        double set = _plugin.SetCspDamperGain(dk);
                        if (AccessCodeStatus != null)
                            AccessCodeStatus.Text = $"Synthesized damper gain set to {set:F2} (0 to 5, default 1.0). Session only.";
                    }
                    else if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "Usage: CSPFFB DAMPK <number>, e.g. CSPFFB DAMPK 0.4.";
                    return;
                }
                if (arg == "DAMPTEST")
                {
                    bool started = _plugin.StartCspDamperTest();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = started
                            ? "CSP damper test running for 28 seconds: damper flips fully off and on every three seconds for 12 s, then ramps down and up twice over 16 s, then back to normal. Alt-tab into the game and feel the wheel; the force itself should never cut."
                            : "CSP damper test needs Assetto Corsa to be the active game.";
                    return;
                }
                if (arg == "PURE" || arg == "TORQUE" || arg == "FINAL" || arg == "VALUE" || arg == "FINALFF")
                {
                    string f = _plugin.SetCspBridgeField(arg);
                    string note = f == "finalff"
                        ? "  (AC's vanilla finalFF, the field FFB Clip reads; post-gain and keeps your AC tuning; the CSP script only frees the pipe)"
                        : (f == "final" || f == "value")
                        ? "  (CSP post-gain field: reads 0 at in-game gain 0, for comparison with the gain up)"
                        : (f == "torque"
                            ? "  (raw column torque in Nm, scaled by CSPFFB NM; feels proportional to the car)"
                            : "  (pre-gain, normalized to the car; carries force at gain 0)");
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = $"CSP force now reading the {f} field{note}.";
                    return;
                }
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "CSPFFB fields: FINALFF, VALUE, PURE, TORQUE, FINAL; CSPFFB NM <n>; CSPFFB SUP/NOSUP.";
                return;
            }

            // RaceRoom shared-memory FFB route (dev A/B against the USB tap):
            // the sim's pre-gain steering force read straight from "$R3E" and
            // reshaped onto the wheel the way the iRacing path does. "R3EFFB"
            // toggles the route; INV flips the sign, NM <n> reads the raw
            // SteeringForce channel with that full scale, PCT returns to the
            // percentage channel (INV/NM/PCT are session only).
            if (code.Equals("R3EFFB", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("R3EFFB ", StringComparison.OrdinalIgnoreCase))
            {
                var r3eParts = code.Split(new[] { ' ', '=', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                AccessCodeBox.Text = string.Empty;
                if (r3eParts.Length == 1)
                {
                    bool on = _plugin.ToggleR3ESharedMemoryFfb();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = on
                            ? "R3E shared-memory FFB ON: in RaceRoom the wheel is driven from the sim's own steering force, read straight from its shared memory, instead of the USB capture. Set RaceRoom's FFB intensity to 0 so the game is not also driving the wheel. Type R3EFFB again for the tap route."
                            : "R3E shared-memory FFB OFF: RaceRoom is back on the USB capture. Restore your in-game FFB intensity.";
                    // Reflect the new takeover state on the FFB tab right away; the
                    // checkbox and section only re-read plugin state in RefreshFromPlugin.
                    RefreshFromPlugin();
                    return;
                }
                string r3eArg = r3eParts[1].ToUpperInvariant();
                if (r3eArg == "INV")
                {
                    bool inv = _plugin.ToggleR3EForceInvert();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = inv
                            ? "R3E force sign INVERTED (session only). If the wheel now pulls into corners instead of centering, type R3EFFB INV again."
                            : "R3E force sign back to normal.";
                    return;
                }
                if (r3eArg == "NM")
                {
                    if (r3eParts.Length >= 3 && double.TryParse(r3eParts[2],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double r3eNm))
                    {
                        double set = _plugin.SetR3ERawFullScaleNm(r3eNm);
                        if (AccessCodeStatus != null)
                            AccessCodeStatus.Text = $"R3E force now reading the raw SteeringForce channel, full scale {set:F1} (that much force = full wheel force). Session only; R3EFFB PCT returns to the percentage channel.";
                    }
                    else if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "Usage: R3EFFB NM <number>, e.g. R3EFFB NM 15.";
                    return;
                }
                if (r3eArg == "PCT")
                {
                    _plugin.UseR3EPctChannel();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "R3E force back to the SteeringForcePercentage channel (the default).";
                    return;
                }
                if (r3eArg == "AUTO")
                {
                    bool autoOn = _plugin.ToggleR3EAutoStrength();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = autoOn
                            ? "R3E auto-strength ON: drive a couple of clean laps, then apply this car's max with 'R3EFFB APPLY' (or the Auto force binding), iRacing style. Nothing changes under you until you apply. Persists per car."
                            : "R3E auto-strength OFF: the raw RaceRoom percentage goes to the wheel unscaled (weaker, no per-car max). Persists.";
                    return;
                }
                if (r3eArg == "APPLY")
                {
                    float applied = _plugin.ApplyR3EAutoStrength();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = applied > 0.01f
                            ? $"R3E max applied for this car (peak {applied:0.00}). Nudge with the Peak force up/down bindings; press APPLY again after a clean lap to re-capture."
                            : "Nothing to apply yet: drive a couple of clean laps first so the peak settles (watch the R3E strength confidence), then press APPLY.";
                    return;
                }
                if (r3eArg == "DAMP")
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    double? strength = null, fade = null;
                    if (r3eParts.Length >= 3 && double.TryParse(r3eParts[2],
                            System.Globalization.NumberStyles.Float, ci, out double dS))
                        strength = dS;
                    if (r3eParts.Length >= 4 && double.TryParse(r3eParts[3],
                            System.Globalization.NumberStyles.Float, ci, out double dF))
                        fade = dF;
                    string msg = _plugin.SetR3EStationaryDamper(strength, fade);
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = msg;
                    return;
                }
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "R3EFFB sub-commands: INV (flip sign), NM <n> (raw channel + full scale), PCT (percentage channel), AUTO (auto-strength on/off), APPLY (commit this car's max), DAMP [strength] [fadeKmh] (stationary friction: firm parked, gone at speed).";
                return;
            }

            // RaceRoom signal probe: no force, just the numbers needed to trust
            // R3EFFB (does the force survive in-game FFB intensity 0, does its
            // sign match the steering direction, how fast does it tick).
            if (code.Equals("R3EPROBE", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.ToggleR3EProbe();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "R3E probe ON: '[TF4ALL] R3EPROBE' lines land in SimHub.txt every ~2 s while RaceRoom runs. Drive a steady corner each way (sign check), then set in-game FFB intensity to 0 and drive again (pre-gain check). Type R3EPROBE again to stop."
                        : "R3E probe OFF.";
                return;
            }

            // Le Mans Ultimate handover: the sim's steering shaft torque read
            // straight from its official "LMU_Data" shared memory and reshaped
            // onto the wheel the way the iRacing path does. "LMUFFB" toggles
            // the route (the FFB tab's take-over checkbox is the same switch);
            // INV flips the sign (session only), NM <n> sets the shaft torque
            // that is full wheel force (persists); AUTO, APPLY and DAMP are the
            // RaceRoom ones, which are keyed per game and car already.
            if (code.Equals("LMUFFB", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("LMUFFB ", StringComparison.OrdinalIgnoreCase))
            {
                var lmuParts = code.Split(new[] { ' ', '=', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                AccessCodeBox.Text = string.Empty;
                if (lmuParts.Length == 1)
                {
                    bool on = _plugin.ToggleLmuSharedMemoryFfb();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = on
                            ? "Le Mans Ultimate handover ON: the wheel is driven from the sim's own steering shaft torque, read straight from its shared memory, instead of the USB capture. In the game, set Vendor Specific Force Feedback to Off first; its force feedback strength can stay. Type LMUFFB again for the tap route."
                            : "Le Mans Ultimate handover OFF: back on the USB capture.";
                    RefreshFromPlugin();
                    return;
                }
                string lmuArg = lmuParts[1].ToUpperInvariant();
                if (lmuArg == "INV")
                {
                    bool inv = _plugin.ToggleLmuForceInvert();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = inv
                            ? "Le Mans Ultimate force sign INVERTED (session only). If the wheel now pulls into corners instead of centering, type LMUFFB INV again."
                            : "Le Mans Ultimate force sign back to normal.";
                    return;
                }
                if (lmuArg == "NM")
                {
                    if (lmuParts.Length >= 3 && double.TryParse(lmuParts[2],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double lmuNm))
                    {
                        double set = _plugin.SetLmuFullScaleNm(lmuNm);
                        if (AccessCodeStatus != null)
                            AccessCodeStatus.Text = $"Le Mans Ultimate full scale set to {set:F1} Nm of shaft torque: that much is full wheel force before a car's own max is applied. Persists.";
                    }
                    else if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "Usage: LMUFFB NM <number>, e.g. LMUFFB NM 15.";
                    return;
                }
                if (lmuArg == "APPLY")
                {
                    double applied = _plugin.ApplyIRacingAutoMaxForce();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = applied > 0.5
                            ? $"This car's peak force set to {applied:F1} Nm, as the Auto button does. Nudge with IRacingMaxForceUp/Down; APPLY again after a clean lap to re-learn."
                            : "Nothing learned yet: drive a couple of clean laps first (the Auto button on the FFB tab shows the number once it settles), then APPLY.";
                    return;
                }
                if (lmuArg == "DAMP")
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    double? strength = null, fade = null;
                    if (lmuParts.Length >= 3 && double.TryParse(lmuParts[2],
                            System.Globalization.NumberStyles.Float, ci, out double dS))
                        strength = dS;
                    if (lmuParts.Length >= 4 && double.TryParse(lmuParts[3],
                            System.Globalization.NumberStyles.Float, ci, out double dF))
                        fade = dF;
                    string msg = _plugin.SetR3EStationaryDamper(strength, fade);
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = msg;
                    return;
                }
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "LMUFFB sub-commands: INV (flip sign), NM <n> (shaft torque in Nm that is full force until a car has its own number), APPLY (take this car's learned peak force, as the Auto button), DAMP [strength] [fadeKmh] (stationary friction: firm parked, gone at speed).";
                return;
            }

            // Le Mans Ultimate signal probe: no force, just the numbers needed
            // to trust LMUFFB (does the shaft torque survive in-game strength
            // 0, does its sign match the steering direction, how fast do the
            // telemetry and the sim's own FFB value update).
            if (code.Equals("LMUPROBE", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.ToggleLmuProbe();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Le Mans Ultimate probe ON: '[TF4ALL] LMUPROBE' lines land in SimHub.txt every ~2 s while the game runs. Drive a steady corner each way (sign check) and a hard one (what the car pushes). Type LMUPROBE again to stop."
                        : "Le Mans Ultimate probe OFF.";
                return;
            }

            // Driver testing mode: route FFB through the TFFA kernel filter
            // driver (sole wheel ownership) instead of the USBPcap tap. Needs
            // the TFFA filter driver installed. The code both REVEALS the
            // hidden "Driver testing mode" checkbox (persisted via
            // DriverTestingUnlocked, mirrors MANUALPIN) AND toggles the feature
            // on/off (ExperimentalDriverIntercept). Once revealed the checkbox
            // stays visible across restarts and the user drives it from there.
            // Applied on the next plugin init (re-detect / restart SimHub).
            // DAMPCAL [OFF]: the damper calibration wizard. No game needed;
            // three conditions of three flicks each, measured on the wheel's
            // own DirectInput position; result lands in the synthesized gain.
            // ARCADEFX [OFF]: name the live arcade effects in the log, so which
            // effect carries which feel is read off a run instead of inferred.
            // ID8SCAN [text]: search the running cabinet for a known string and log what
            // surrounds every hit. Defaults to SEGA, the placeholder name filling the online
            // leaderboard while the cabinet has no network, so the hits map the row layout.
            // Read-only, one shot, and it takes a few seconds.
            if (code.Equals("ID8SCAN", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("ID8SCAN ", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string arg = code.Length > 8 ? code.Substring(8).Trim() : null;
                string msg = _plugin.StartArcadeMemorySearch(arg);
                if (AccessCodeStatus != null) AccessCodeStatus.Text = msg;
                return;
            }
            if (code.Equals("ARCADEFX", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("ARCADEFX ", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                bool on = code.IndexOf("OFF", StringComparison.OrdinalIgnoreCase) < 0;
                string msg = _plugin.SetArcadeEffectTrace(on);
                if (AccessCodeStatus != null) AccessCodeStatus.Text = msg;
                return;
            }
            // MENUFX [OFF]: knock the wheel as an arcade cabinet's menus are used. A cabinet has
            // no keyboard and its menus are driven from the wheel, so the wheel is where the
            // feedback belongs. Persisted, unlike the developer codes, because it is a preference
            // rather than a tool.
            if (code.Equals("MENUFX", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("MENUFX ", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                bool on = code.IndexOf("OFF", StringComparison.OrdinalIgnoreCase) < 0;
                if (_plugin?.Settings?.Arcade != null)
                {
                    _plugin.Settings.Arcade.MenuHaptics = on;
                    try { _plugin.PersistSettings(); } catch { }
                }
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Menu knocks on. A thud when you confirm, a tick as you move through the options."
                        : "Menu knocks off.";
                return;
            }
            if (code.Equals("DAMPCAL", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("DAMPCAL ", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (code.IndexOf("OFF", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    _plugin.CancelDamperCalibration();
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = "DAMPCAL cancelled; everything back to normal.";
                    return;
                }
                string calErr = _plugin.StartDamperCalibration(msg =>
                    Dispatcher.BeginInvoke((Action)(() => { if (AccessCodeStatus != null) AccessCodeStatus.Text = msg; })));
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = calErr == null
                        ? "DAMPCAL started, no game needed. Follow this line (or the wheel screen): three conditions, three flicks each, about a minute. DAMPCAL OFF cancels."
                        : "DAMPCAL could not start: " + calErr + ".";
                return;
            }
            // DIDAMP [pct|OFF]: the DAMPCAL feasibility spike. Drives the
            // wheel's native DirectInput damper from the plugin with the
            // Trueforce stream in keepalive, and logs the position read rate.
            if (code.Equals("DIDAMP", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("DIDAMP ", StringComparison.OrdinalIgnoreCase))
            {
                var diParts = code.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                AccessCodeBox.Text = string.Empty;
                if (diParts.Length >= 2 && diParts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                {
                    _plugin.StopDiDamperSpike();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "DirectInput damper released; the Trueforce stream is back to normal.";
                    return;
                }
                int diPct = 75;
                if (diParts.Length >= 2) int.TryParse(diParts[1], out diPct);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;
                string diErr = _plugin.StartDiDamperSpike(diPct, hwnd);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = diErr == null
                        ? $"DirectInput damper ON at {diPct}% with the Trueforce stream fully stopped. Flick the wheel: that is the wheel's native damper, exactly as without the plugin. DIDAMP OFF ends it (auto-off after 60 s); the log shows the position read rate."
                        : "DirectInput damper failed: " + diErr + ".";
                return;
            }
            // DICOND: A/B the decoded DirectInput effect rendering (the
            // game's damper/spring/friction/inertia and periodics, read off
            // the wire and played into the stream). ON by default.
            if (code.Equals("DICOND", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                bool dicondOff = _plugin.ToggleDicondRendering();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = dicondOff
                        ? "DirectInput effect rendering OFF (A/B: the game's damper and spring are dropped again while Trueforce streams). Session only."
                        : "DirectInput effect rendering ON: the game's condition effects are decoded from the wire and played into the stream. CSPFFB DAMPSIGN flips the direction, CSPFFB DAMPK scales the damper, DAMPCAL measures it.";
                return;
            }
            // FXDUMP: what the wheel is actually ASKED for. One line per
            // effect download, decoded off the wire, type and parameters.
            if (code.Equals("FXDUMP", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string msg = _plugin.ToggleEffectDownloadTrace();
                if (AccessCodeStatus != null) AccessCodeStatus.Text = msg;
                return;
            }
            // CLASSICCOND: G923 PS/PC experiment. The game's classic-protocol
            // damper and friction slots (types 0x0c, 0x02, 0x0e), which the
            // firmware ignores while Trueforce streams, rendered through the
            // DirectInput condition engine. OFF by default, unvalidated on
            // hardware; persisted so a tester can restart into it.
            if (code.Equals("CLASSICCOND", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                bool on = _plugin.ToggleClassicConditionEmulation();
                if (AccessCodeStatus != null)
                {
                    if (!on)
                        AccessCodeStatus.Text =
                            "Classic effect rendering OFF: the game's classic-protocol damper, friction "
                            + "and spring slots are tracked but not played (the shipping default).";
                    else if (_plugin.WheelDetected && !_plugin.WheelIsLegacyF8)
                        AccessCodeStatus.Text =
                            "Set, but this wheel does not use the classic slot protocol for its force "
                            + "feedback, so nothing changes here. It applies to the G923 PS/PC only; every "
                            + "other wheel's condition effects already arrive over HID++ and are rendered.";
                    else
                        AccessCodeStatus.Text =
                            "Classic effect rendering ON: the game's classic-protocol damper, friction and "
                            + "spring slots are decoded and played into the stream. None of this has ever run "
                            + "on a wheel, so treat the first drive as a test and keep a hand ready. "
                            + "Watch SimHub.txt for the "
                            + "first 'classic' effect line, then drive: the wheel should resist turning where "
                            + "the game asks for damping, and it should also pull back toward center where the "
                            + "game commands a spring, on top of the force you already feel. CSPFFB DAMPSIGN "
                            + "flips the direction if it feels like an anti-damper, DICOND turns the rendering "
                            + "off for an A/B, DAMPCAL sets the strength. Type CLASSICCOND again to turn it off.";
                }
                return;
            }
            // ACLEDS: the rev-light contention diagnostic. Reports what the
            // GAME is writing to the wheel's rev-light feature, measured off
            // the wire, beside what our own LED and screen surfaces were
            // allowed to do. For "the lights stick, go dark, then catch up".
            if (code.Equals("SOFTLOCK", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                bool slOn = _plugin.ToggleSoftLockDiagnostic();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = slOn
                        ? "Soft-lock diagnostic ON: turn past the steering limit in Assetto Corsa, iRacing or RaceRoom and "
                          + "the log gets a [TF4ALL] SOFTLOCK line twice a second with the steering "
                          + "position, how far the lock has engaged, its target and the stationary "
                          + "spring's contribution. Fires from 0.9 whether or not a lock results, so a "
                          + "lock that never engages is visible too. Session only."
                        : "Soft-lock diagnostic OFF.";
                return;
            }
            if (code.Equals("ACLEDS", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                bool on = _plugin.ToggleRevLightDiagnostic();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Rev-light diagnostic ON: every 2 s the log gets a [REVLIGHT] line with the "
                          + "game's own light writes, the longest gap between them, and what our LEDs "
                          + "and screen were doing. Drive, then send the log. Session only."
                        : "Rev-light diagnostic OFF.";
                return;
            }
            // FXTEST <NATIVE|ENGINE> <effect> [strength%] [periodMs] and
            // FXTEST OFF: the native-vs-engine effect A/B, no game needed.
            if (code.Equals("FXTEST", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("FXTEST ", StringComparison.OrdinalIgnoreCase))
            {
                var fxParts = code.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                AccessCodeBox.Text = string.Empty;
                // Bare FXTEST shows or hides the bench on the FFB tab. The
                // argument forms still drive it from here for anyone who
                // prefers typing to clicking.
                if (fxParts.Length < 2)
                {
                    _plugin.Settings.FxBenchUnlocked = !_plugin.Settings.FxBenchUnlocked;
                    _plugin.PersistSettings();
                    ApplyFxBenchVisibility();
                    if (_plugin.Settings.FxBenchUnlocked)
                    {
                        if (MainTabs != null && TelemetryFfbTab != null)
                            MainTabs.SelectedItem = TelemetryFfbTab;
                        if (AccessCodeStatus != null)
                            AccessCodeStatus.Text = "Effect test bench ON, at the bottom of the FFB tab. "
                                + "Type FXTEST again to hide it.";
                    }
                    else
                    {
                        _plugin.StopFxTest();
                        if (AccessCodeStatus != null)
                            AccessCodeStatus.Text = "Effect test bench hidden.";
                    }
                    return;
                }
                if (fxParts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                {
                    _plugin.StopFxTest();
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "FXTEST off; the stream is back to normal.";
                    return;
                }
                if (fxParts.Length < 3)
                {
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "Usage: FXTEST NATIVE|ENGINE <DAMPER|SPRING|FRICTION|INERTIA|SINE|SQUARE|TRIANGLE|SAWUP|SAWDOWN|RAMP> [strength%, default 50] [periodMs, default 250]. FXTEST OFF ends it.";
                    return;
                }
                int fxPct = 50, fxPeriod = 250;
                if (fxParts.Length >= 4) int.TryParse(fxParts[3], out fxPct);
                if (fxParts.Length >= 5) int.TryParse(fxParts[4], out fxPeriod);
                string fxErr = _plugin.StartFxTest(fxParts[1], fxParts[2], fxPct, fxPeriod);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = fxErr == null
                        ? $"FXTEST {fxParts[1].ToUpperInvariant()} {fxParts[2].ToUpperInvariant()} at {fxPct}%. Feel the wheel, then run the other mode on the same effect and compare; tune with CSPFFB DAMPK / DAMPSIGN. FXTEST OFF ends it (auto-off after 30 s)."
                        : "FXTEST could not start: " + fxErr + ".";
                return;
            }
            if (code.Equals("DRIVER", StringComparison.OrdinalIgnoreCase))
            {
                // DRIVER is a full on/off for driver testing mode. First entry:
                // unlock + reveal the checkbox and turn the feature on. Type
                // DRIVER again to turn it off AND hide the checkbox again (back
                // to the default hidden state). While unlocked, the revealed
                // checkbox toggles the feature without hiding it.
                bool unlocked = false;
                if (_plugin?.Settings != null)
                {
                    unlocked = !_plugin.Settings.DriverTestingUnlocked;
                    _plugin.Settings.DriverTestingUnlocked       = unlocked;
                    _plugin.Settings.ExperimentalDriverIntercept = unlocked;
                    _plugin.PersistSettings();
                }
                AccessCodeBox.Text = string.Empty;
                // Reflect the new state without re-firing the checkbox's Changed
                // handler (the setters already ran above).
                var prevSuppress = _suppressEvents;
                _suppressEvents = true;
                try
                {
                    var vis = unlocked ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    if (DriverInterceptCheck != null)
                    {
                        DriverInterceptCheck.Visibility = vis;
                        DriverInterceptCheck.IsChecked  = unlocked;
                    }
                    if (DriverInterceptHelp != null)
                        DriverInterceptHelp.Visibility = vis;
                }
                finally { _suppressEvents = prevSuppress; }
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = unlocked
                        ? "Driver testing mode ON (restart SimHub / re-detect to apply). Needs the TFFA filter driver installed. A checkbox is now shown in the Settings tab; type DRIVER again to turn it off and hide it."
                        : "Driver testing mode OFF and hidden. Type DRIVER again to turn it back on.";
                return;
            }

            if (code.Equals("OLEDTEST", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin == null) return;
                _plugin.TestOled();
                PollOledStatus();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Showing sample screens on the wheel. Watch the Screen status "
                        + "line beside the wheel-screen settings for each step. Run it with no game driving "
                        + "the wheel: writing the screen while a game sends its own force feedback cuts that "
                        + "force feedback out.";
                return;
            }

            if (code.StartsWith("OLEDMS", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin?.Settings == null) return;
                string arg = code.Substring(6).Trim();
                if (int.TryParse(arg, System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out int ms))
                {
                    if (ms < 20) ms = 20; else if (ms > 1000) ms = 1000;
                    _plugin.Settings.OledWriteIntervalMs = ms;
                    _plugin.PersistSettings();
                }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                {
                    int cur = _plugin.Settings.OledWriteIntervalMs;
                    AccessCodeStatus.Text = $"OLED write interval: {cur} ms (about {1000 / Math.Max(1, cur)} "
                        + "updates a second). Lower is smoother and uses more of the wheel's command channel. "
                        + "Drive with it and watch the force feedback: if it starts to feel soft or cuts, "
                        + "you have found the limit. OLEDMS with no number just reports the current value.";
                }
                return;
            }

            if (code.Equals("F8ANY", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin?.Settings == null) return;
                bool on = !_plugin.Settings.F8IgnoreQuietGate;
                _plugin.Settings.F8IgnoreQuietGate = on;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                {
                    // Says what it is for, because the person typing it is
                    // running an experiment for us rather than using a feature,
                    // and both outcomes are a result worth reporting.
                    if (!on)
                        AccessCodeStatus.Text =
                            "Rev-light gate back on: the legacy F8 lights run only when the game's "
                            + "own force feedback is quiet.";
                    else if (_plugin.WheelDetected && !_plugin.WheelIsLegacyF8)
                        AccessCodeStatus.Text =
                            "Set, but this wheel does not use the legacy F8 rev lights, so nothing "
                            + "changes here. It applies to the G923 PS/PC only; every other wheel "
                            + "drives its lights over HID++, where the answer is already known.";
                    else
                        AccessCodeStatus.Text =
                            "Rev-light gate OFF: the legacy F8 lights now run in any game, including "
                            + "ones driving their own force feedback. Go and drive. Two things to "
                            + "watch: do the five lights follow your revs, and does the game's force "
                            + "stay solid while they move. If the force cuts out, that IS the answer "
                            + "we need, not a fault. Type F8ANY again to put the gate back.";
                }
                return;
            }

            // Reveal (or hide) the Diagnostics "Pick device manually..."
            // control. Off by default since auto-discovery + identity-based
            // self-heal cover the realistic failure modes and a forgotten
            // pin silently breaks FFB after the wheel changes USB address
            // (issue #17). Power users with a real need (multi-wheel
            // disambiguation, or a USBPcap interface mismatch they want to
            // override) flip it on here; persists across restarts.
            // Live rev-light cadence, so the real limit can be found on the
            // wheel instead of guessed at. The 160 ms default is only "what
            // G HUB does": the FFB-limp bug it was long credited with avoiding
            // turned out to be FFB on the LED endpoint, not write rate.
            if (code.StartsWith("LEDRATE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                int ms;
                if (!int.TryParse(code.Substring("LEDRATE".Length).Trim(), out ms))
                {
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "LEDRATE<ms>: 10 to 1000. Currently "
                            + WheelLedChannel.ChangeMinMsValue + " ms.";
                    return;
                }
                WheelLedChannel.ChangeMinMsValue = ms;
                int now = WheelLedChannel.ChangeMinMsValue;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Rev lights now update at most every " + now
                        + " ms (about " + (1000 / Math.Max(now, 1)) + " Hz). Live only, back to 40 on restart. "
                        + "Drive it and watch the FORCE, not just the lights.";
                return;
            }

            // Dev-only: make a slot read as NEVER PROGRAMMED, so the first-run
            // path for a factory wheel can be tested at all. Adoption skips a
            // slot whose bytes are all zero, and there is otherwise no way to
            // produce one without going back to G HUB.
            //
            // Two writes on purpose. The first is a normal borrow, which is what
            // captures the backup; the second settles the loan so the zeros
            // survive a restart, which they must, since the test is what happens
            // on the NEXT launch. SLOTRESTORE<n> puts the slot back.
            // Blank or give back EVERY custom slot in one go, which is the shape
            // the factory-wheel rehearsal actually needs: a wheel with one slot
            // left full is not the case being tested.
            //
            // Checked BEFORE the single-slot codes below, and deliberately so:
            // StartsWith("SLOTBLANK") also matches "SLOTBLANKALL", which would
            // then try to read "ALL" as a slot number and report a range error.
            if (code.Equals("SLOTBLANKALL", StringComparison.OrdinalIgnoreCase)
             || code.Equals("SLOTRESTOREALL", StringComparison.OrdinalIgnoreCase))
            {
                bool restoring = code.Equals("SLOTRESTOREALL", StringComparison.OrdinalIgnoreCase);
                AccessCodeBox.Text = string.Empty;

                var lines = new System.Collections.Generic.List<string>();
                int okCount = 0;
                for (int slot = 0; slot < WheelLedChannel.CustomSlotCount; slot++)
                {
                    string m; bool ok;
                    if (restoring) ok = _plugin.RestoreSlot(slot, out m, undoBlank: true);
                    else
                    {
                        // Same two steps as SLOTBLANK<n>: BorrowSlot takes the
                        // backup and refuses if it cannot, then the blank is
                        // recorded WITHOUT settling the loan, so the colors stay
                        // protected while the slot reads empty.
                        var zeros = new byte[WheelLedChannel.LedCount * 3];
                        ok = _plugin.BorrowSlot(new WheelLedChannel.WheelLedSlot
                        { Slot = (byte)slot, DirectionWire = 3, Rgb = zeros }, out m,
                          displayLevel: 0);
                        if (ok) ok = _plugin.MarkSlotBlanked(slot, out m);
                    }
                    if (ok) okCount++;
                    lines.Add((ok ? "OK   " : "SKIP ") + "CUSTOM " + (slot + 1)
                              + ": " + (m ?? "(no detail)"));
                }

                // Per-slot lines rather than a count. A slot that refused because
                // it could not be read is a different thing from one that had no
                // backup to give back, and whoever is running the rehearsal needs
                // to tell them apart.
                TrueforceDialog.Show(Window.GetWindow(this),
                    restoring ? "Give every light slot back" : "Blank every light slot",
                    string.Join(Environment.NewLine, lines)
                    + Environment.NewLine + Environment.NewLine
                    + (restoring
                       ? okCount + " of " + WheelLedChannel.CustomSlotCount + " restored. "
                         + "SKIP on a slot that was never blanked is expected: there is nothing held for it."
                       : okCount + " of " + WheelLedChannel.CustomSlotCount + " blanked, and every one of "
                         + "those was backed up first. Your colors stay held until you type SLOTRESTOREALL."
                         + Environment.NewLine + Environment.NewLine
                         + "Delete light-patterns.json and relaunch to test the factory-wheel first run."),
                    okCount > 0 ? DialogKind.Info : DialogKind.Warning);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = (restoring ? "Restored " : "Blanked ")
                        + okCount + " of " + WheelLedChannel.CustomSlotCount + " slots.";
                return;
            }

            if (code.StartsWith("SLOTBLANK", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                int n;
                if (!int.TryParse(code.Substring("SLOTBLANK".Length).Trim(), out n) || n < 1 || n > 5)
                {
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "SLOTBLANK<n>: 1-5.";
                    return;
                }
                int slot = n - 1;
                var zeros = new byte[WheelLedChannel.LedCount * 3];

                string msg;
                // ONE write, and the loan stays OPEN. Settling it with
                // permanent:true was the obvious way to make the blank survive a
                // restart and it was quietly destructive: a settled loan makes the
                // NEXT borrow of this slot treat it as un-backed-up, re-read it,
                // and record the blank we just wrote as "the original", discarding
                // the last copy of the user's real colors. Marking it blanked
                // instead keeps the debt open, so the backup stays protected and
                // SLOTRESTORE still works.
                bool ok = _plugin.BorrowSlot(new WheelLedChannel.WheelLedSlot
                { Slot = (byte)slot, DirectionWire = 3, Rgb = zeros }, out msg,
                  displayLevel: 0);
                if (ok) ok = _plugin.MarkSlotBlanked(slot, out msg);

                TrueforceDialog.Show(Window.GetWindow(this), "Blank a light slot",
                    (msg ?? "(no detail)") + "\n\n"
                    + (ok
                       ? "CUSTOM " + n + " now reads as never programmed, and stays that way "
                         + "across a restart." + "\n\n"
                         + "Delete light-patterns.json and relaunch to test the factory-wheel "
                         + "first run. Type SLOTRESTORE" + n + " to put your own colors back."
                       : "Nothing was written, so the slot is untouched."),
                    ok ? DialogKind.Info : DialogKind.Warning);
                if (AccessCodeStatus != null) AccessCodeStatus.Text = msg;
                return;
            }

            // Designate which custom slot the plugin may borrow, or 0 for none.
            if (code.StartsWith("SLOTPICK", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                int n;
                if (!int.TryParse(code.Substring("SLOTPICK".Length).Trim(), out n) || n < 0 || n > 5)
                {
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "SLOTPICK<n>: 1-5 to designate CUSTOM n, or 0 for none.";
                    return;
                }
                _plugin.ReleaseBorrowedSlot();
                _plugin.Settings.LightsyncDynamicSlot = n == 0 ? -1 : n - 1;
                _plugin.ResetStageSlot();
                _plugin.PersistSettings();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = n == 0
                        ? "Back to automatic. The plugin picks the slot itself: the first one "
                          + "never programmed, or CUSTOM 5 when all five are in use. Its contents are "
                          + "backed up before the first write."
                        : "CUSTOM " + n + " designated. Its contents are backed up before the first write, "
                          + "and SLOTRESTORE" + n + " puts them back.";
                return;
            }

            // Put the user's own colors back into a slot from the backup taken
            // before the plugin first wrote it.
            if (code.StartsWith("SLOTRESTORE", StringComparison.OrdinalIgnoreCase))
            {
                int n;
                if (!int.TryParse(code.Substring("SLOTRESTORE".Length).Trim(), out n)
                    || n < 1 || n > 5) n = 5;
                int slot = n - 1;
                AccessCodeBox.Text = string.Empty;

                string msg;
                // undoBlank: typing SLOTRESTORE is a person asking for their
                // colors back, which is exactly the case the automatic paths must
                // not do on their own.
                bool ok = _plugin.RestoreSlot(slot, out msg, undoBlank: true);

                string detail = "Check the wheel's own LIGHTSYNC menu: CUSTOM " + n
                    + " should look as it did before the plugin first wrote the slot.";

                TrueforceDialog.Show(Window.GetWindow(this),
                    "Restore light slot",
                    (msg ?? "(no detail)") + "\n\n" + detail,
                    ok ? DialogKind.Info : DialogKind.Warning);
                if (AccessCodeStatus != null) AccessCodeStatus.Text = msg;
                return;
            }

            // Read-only: ask the wheel whether it implements per-slot LIGHTSYNC
            // colors and dump what each custom slot holds. Writes nothing.
            if (code.Equals("SLOTPROBE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string body = _plugin.ProbeLedSlotFeature();
                TrueforceDialog.Show(Window.GetWindow(this), "Wheel slot-feature probe (read-only)", body);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Slot probe done. Full detail is in the dialog and in SimHub.txt.";
                return;
            }

            // Move the wheel lights + screen controls onto their own tab and
            // reveal it. Nothing is duplicated: the blocks are reparented.
            if (code.Equals("LIGHTSYNC", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.LightsyncTabUnlocked = !_plugin.Settings.LightsyncTabUnlocked;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                ApplyLightsyncTabVisibility();
                // The FFB tab's own name depends on whether it still owns the
                // lights, so re-run the pass that sets it rather than leaving a
                // stale header until the next game-context refresh.
                try { RefreshFromPlugin(); } catch { }
                if (_plugin.Settings.LightsyncTabUnlocked && MainTabs != null && LightsyncTab != null)
                    MainTabs.SelectedItem = LightsyncTab;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _plugin.Settings.LightsyncTabUnlocked
                        ? "LIGHTSYNC & OLED tab ON. Type LIGHTSYNC again to put the wheel lights and screen controls back on the Telemetry FFB tab."
                        : "LIGHTSYNC & OLED tab OFF: the wheel lights and screen controls are back on the Telemetry FFB tab.";
                return;
            }

            // Dev-only: re-arm the LIGHTSYNC intro modal and force the inline
            // cycle-binding hint on screen even though the action is bound. Both
            // normally stay quiet once bound, which is right for users and useless
            // for testing. Session-only; lapses on restart.
            if (code.Equals("CYCLEHINT", StringComparison.OrdinalIgnoreCase))
            {
                _forceCycleHint = !_forceCycleHint;
                if (_forceCycleHint)
                {
                    _plugin.Settings.LightsyncCycleHintDismissed = false;
                    _plugin.Settings.HasSeenLightsyncIntro = false;
                }
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                RefreshLightsyncCycleHint();
                if (_forceCycleHint && MainTabs != null && LightsyncTab != null
                    && _plugin.Settings.LightsyncTabUnlocked)
                    MainTabs.SelectedItem = LightsyncTab;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _forceCycleHint
                        ? "Cycle hint and intro FORCED ON for this session. Open LIGHTSYNC to see them. Type CYCLEHINT again for normal behaviour."
                        : "Cycle hint back to normal: it shows only when the pattern cycle action is unbound.";
                return;
            }

            if (code.Equals("MANUALPIN", StringComparison.OrdinalIgnoreCase))
            {
                bool on = !(_plugin.Settings.ShowManualOverrideUi);
                _plugin.Settings.ShowManualOverrideUi = on;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                var pickerVis = on
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
                if (UsbPcapPickDeviceButton    != null) UsbPcapPickDeviceButton.Visibility    = pickerVis;
                if (FfbTapPickerBannerButton   != null) FfbTapPickerBannerButton.Visibility   = pickerVis;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Manual device picker revealed (Diagnostics + the contextual banner). Persists. Type MANUALPIN again to hide it."
                        : "Manual device picker hidden (persists).";
                return;
            }

            // The stationary spring outside Assetto Corsa, for testing it game by
            // game. Assetto Corsa is unaffected either way: there it always runs.
            if (code.Equals("SPRING", StringComparison.OrdinalIgnoreCase))
            {
                var sp = _plugin.Settings;
                sp.StationarySpringUnlocked = !sp.StationarySpringUnlocked;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = sp.StationarySpringUnlocked
                        ? "Stationary spring unlocked outside Assetto Corsa, RaceRoom, Le Mans Ultimate and Forza. Tick it while each game is running to test it there; every game keeps its own strength and cutoff. Type SPRING again to lock it back to those four, which leaves your per-game tuning saved for next time."
                        : "Stationary spring locked to Assetto Corsa, RaceRoom, Le Mans Ultimate and Forza (the shipping default). Your per-game settings are kept, not deleted.";
                // The checkbox and its badge read the effective value, so re-read
                // rather than leaving a tick on a spring that no longer runs.
                RefreshFromPlugin();
                return;
            }

            if (code.Equals("ARCADE", StringComparison.OrdinalIgnoreCase))
            {
                var arcade = _plugin.Settings.Arcade;
                if (arcade == null)
                {
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "No arcade settings on this install to unlock.";
                    return;
                }

                bool on = !arcade.Enabled;

                // Put the game's own rows back BEFORE the switch goes off, while the boards are
                // still ours to restore. Once ActiveGameIsArcade reads false the leaderboard
                // service is detached and the pre-write snapshot goes with it, so a cabinet
                // running through a lock would otherwise keep our rows until it was closed.
                if (!on)
                {
                    try { _plugin.RestoreArcadeBoards(); }
                    catch (Exception ex) { TrueforceDialog.LogError("Arcade lock: restore boards", ex); }
                }

                arcade.Enabled = on;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                RefreshArcadePanel(_plugin.ActiveGameIsArcade);
                RefreshArcadeLeaderboardControls();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Arcade cabinet path unlocked: force sources, the Initial D 8 map and its leaderboards. Restart SimHub so the Arcade.* dash properties come back, then start the cabinet. Persists. Type ARCADE again to shelve it."
                        : "Arcade cabinet path shelved and the game's own leaderboard rows put back (persists).";
                return;
            }

            // Language runtime (docs/localization-plan.md, Phase 1): what is
            // active, what a language lacks, and whether the layout survives
            // longer text. Session-only views; nothing here persists.
            if (code.Equals("LOCSTATUS", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null) AccessCodeStatus.Text = LocStatusLine();
                return;
            }

            if (code.StartsWith("LOCLANG", StringComparison.OrdinalIgnoreCase))
            {
                string tag = code.Substring("LOCLANG".Length).Trim();
                AccessCodeBox.Text = string.Empty;
                var locStore = Loc.Instance;
                string locMsg;
                if (locStore == null)
                    locMsg = "Language runtime not initialized; see the Init lines in SimHub.txt.";
                else if (tag.Length == 0)
                    locMsg = "Usage: LOCLANG <tag>, e.g. LOCLANG es or LOCLANG de-DE. Session only.";
                else
                {
                    try
                    {
                        locStore.Load(tag);
                        locMsg = "Loaded " + tag + " for this session. " + LocStatusLine();
                    }
                    catch (Exception ex)
                    {
                        TrueforceDialog.LogError("LOCLANG", ex);
                        locMsg = "Could not load " + tag + ": " + ex.Message;
                    }
                }
                if (AccessCodeStatus != null) AccessCodeStatus.Text = locMsg;
                return;
            }

            if (code.Equals("LOCREPORT", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                var locStore = Loc.Instance;
                string locMsg;
                if (locStore == null)
                    locMsg = "Language runtime not initialized; see the Init lines in SimHub.txt.";
                else
                {
                    try
                    {
                        int missing = locStore.MissingKeys(locStore.ActiveTag).Count;
                        string path = LocDiagnostics.WriteMissingReport(locStore.ActiveTag);
                        locMsg = "Wrote " + path + ": " + missing + " of " + locStore.EnglishKeyCount
                            + " English keys have no " + locStore.ActiveTag + " text.";
                    }
                    catch (Exception ex)
                    {
                        TrueforceDialog.LogError("LOCREPORT", ex);
                        locMsg = "Could not write the missing-keys report: " + ex.Message;
                    }
                }
                if (AccessCodeStatus != null) AccessCodeStatus.Text = locMsg;
                return;
            }

            if (code.Equals("LOCMARK", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                var locStore = Loc.Instance;
                string locMsg;
                if (locStore == null)
                    locMsg = "Language runtime not initialized; see the Init lines in SimHub.txt.";
                else
                {
                    locStore.MarkFallbacks = !locStore.MarkFallbacks;
                    locMsg = locStore.MarkFallbacks
                        ? "Fallback marking ON: text that falls back to English because " + locStore.ActiveTag
                          + " lacks it is shown in ⟦ ⟧ brackets. Type LOCMARK again to turn it off."
                        : "Fallback marking OFF.";
                }
                if (AccessCodeStatus != null) AccessCodeStatus.Text = locMsg;
                return;
            }

            if (code.Equals("PSEUDO", StringComparison.OrdinalIgnoreCase)
                || code.Equals("PSEUDOCJK", StringComparison.OrdinalIgnoreCase))
            {
                bool cjk = code.Equals("PSEUDOCJK", StringComparison.OrdinalIgnoreCase);
                AccessCodeBox.Text = string.Empty;
                string locMsg;
                try
                {
                    int rewritten = LocDiagnostics.PseudoWalk(this, cjk,
                        msg => SimHub.Logging.Current.Info(msg),
                        (clipped, measured) =>
                        {
                            if (AccessCodeStatus != null)
                                AccessCodeStatus.Text = "Pseudo-localized the panel: " + clipped + " of " + measured
                                    + " measured texts no longer fit; each is a '[TF4ALL] pseudo-clip' line in SimHub.txt. "
                                    + "Reopen the plugin's panel to get the real text back.";
                        });
                    locMsg = "Pseudo-localized " + rewritten + " strings ("
                        + (cjk ? "Hangul and Han fill, same length" : "accented, 30 percent longer")
                        + "); measuring after layout...";
                }
                catch (Exception ex)
                {
                    TrueforceDialog.LogError(cjk ? "PSEUDOCJK" : "PSEUDO", ex);
                    locMsg = "Pseudo-localization failed: " + ex.Message;
                }
                if (AccessCodeStatus != null) AccessCodeStatus.Text = locMsg;
                return;
            }

            // Give a visible result for a typed-but-unrecognized code instead
            // of swallowing it silently (blank input stays silent).
            if (!string.IsNullOrWhiteSpace(code) && AccessCodeStatus != null)
                AccessCodeStatus.Text = "Code not recognized. Type HELP to list valid codes.";
        }

        // One line for LOCSTATUS and after LOCLANG: what the language runtime
        // resolved and how complete it is.
        private static string LocStatusLine()
        {
            var locStore = Loc.Instance;
            if (locStore == null) return "Language runtime not initialized; see the Init lines in SimHub.txt.";
            int missing;
            try { missing = locStore.MissingKeys(locStore.ActiveTag).Count; }
            catch { missing = -1; }
            return "Language " + locStore.ActiveTag + " (source: " + locStore.ActiveSource
                + ", requested " + locStore.RequestedTag + "), " + locStore.EnglishKeyCount + " English keys, "
                + (missing < 0 ? "missing count unavailable" : missing + " missing")
                + (locStore.MarkFallbacks ? ", fallback marking ON" : "") + ".";
        }

        // The OLED sample sequence used to be a button in the settings section.
        // It is a diagnostic, not a setting, and the section had grown too long
        // to spend a control on it, so it moved to the OLEDTEST access code. The
        // layout report that sat beside it has been retired: it was how this
        // wheel's layout table was read in the first place, and that job is done.

        /// <summary>Live-poll the controller's status while a sequence runs, so
        /// a wheel that never answers says so in the panel and not only in the
        /// log, and so each step names itself while it is on screen.</summary>
        private void PollOledStatus()
        {
            var t = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(250) };
            int idleTicks = 0;
            t.Tick += (s2, e2) =>
            {
                if (OledStatusText != null) OledStatusText.Text = _plugin.OledStatus;
                if (_plugin.OledIsTesting) idleTicks = 0;
                else if (++idleTicks > 4) t.Stop();   // ~1s after it ends
            };
            t.Start();
        }

        // Dev/test: render arbitrary release-notes markdown through the exact
        // production path (MarkdownView.Render) so the GitHub notes can be
        // previewed before publishing (the plugin can't fetch an unpublished
        // draft). Driven by the PREVIEW access code from clipboard text.
        private void ShowNotesPreview(string markdown)
        {
            var win = new Window
            {
                Title = "What's new (notes preview)",
                Width = Math.Min(780, SystemParameters.WorkArea.Width * 0.9),
                Height = Math.Min(620, SystemParameters.WorkArea.Height * 0.85),
                MinWidth = 480,
                MinHeight = 320,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var root = new DockPanel { Margin = new Thickness(16) };
            var header = new TextBlock
            {
                Text = "What's new (preview of the GitHub notes render)",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A)),
                Margin = new Thickness(0, 0, 0, 12),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            var close = new System.Windows.Controls.Button { Content = "Close", Width = 100, Height = 28, IsDefault = true, IsCancel = true };
            close.Click += (_, __) => win.Close();
            footer.Children.Add(close);
            root.Children.Add(footer);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Padding = new Thickness(12),
                Content = MarkdownView.Render(markdown),
            };
            root.Children.Add(scroll);

            win.Content = root;
            win.ShowDialog();
        }
    }
}
