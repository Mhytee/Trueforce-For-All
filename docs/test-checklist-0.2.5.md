# v0.2.5 pre-release test checklist

Build under test: dev, 54 commits past `v0.2.4`, version bumped in `src/Directory.Build.props` (5c03a94).

Install with the **installer**, not a manual DLL copy, for anything in section A: this build ships TF4ALL Dash into SimHub's dashboard library and drops the built-in car presets, and only the installer puts that layout in place.

Watch `<SimHub>\Logs\SimHub.txt` for `[TF4ALL]` lines throughout. `WARN` lines in section A are expected in some cases and are called out where so.

Your pre-test settings backup is `TrueforcePlugin.GeneralSettings.json.pre-test-backup-20260725-223535`. Restore it with SimHub **closed**.

## A. Upgrade and migration (highest data-loss risk)

The built-in AC / FH6 / Wreckfest car presets and their default bindings were deleted (1f97bff, 13 files). Anything a user had bound to one of them now points at nothing.

- [ ] Upgrade from **v0.2.4** with car defaults bound to a shipped built-in: plugin loads, no freeze, the binding drops cleanly with a `Car default for 'X' dropped` WARN rather than a crash or a silent wrong preset.
- [ ] Upgrade from **v0.1.27 stable** (the older preset folder layout): presets and car overrides survive, built-in counts look right in the Presets tab.
- [ ] A user car preset **named like a factory preset** loads without freezing (b5c3f37, audit batch D).
- [ ] Built-in car presets are detected without the display suffix (656ab7a): no duplicate or orphan rows in the picker.
- [ ] An explicit **None** default survives a restart, and re-binding survives a library rebuild (d95f379, audit batch C).
- [ ] Fresh install on a machine with no prior Trueforce data: first run is clean, no migration warnings.
- [ ] Downgrade path: install v0.2.4 or stable over this build and confirm the switch-back restore still puts things back.

## B. Preset save matrix (audit batches A to E)

Ten commits here, all data-loss class. Worth being systematic.

- [ ] **SAVE BOTH** on a car fork writes a real override and it is still there after a SimHub restart (5a6c518: it used to save empty and vanish).
- [ ] Per-effect save popover offers the three choices and the highlighted one matches the current setup (53951d1, 0d74dc7).
- [ ] Saving a game preset that duplicates an existing one **reuses** it instead of dead-ending, and reuse keeps personal FFB settings (b2feb51).
- [ ] Forking a car preset onto a taken name auto-dedupes instead of prompting (12abecc).
- [ ] Forking a game preset that is content-identical reuses the existing one (e7af577).
- [ ] A car preset store I/O failure surfaces an error rather than being swallowed (616b86c). Force it if you can (make the folder read-only).
- [ ] Select-is-default: picking a game preset binds it as default; **Clear default** unbinds (ffcfe40).
- [ ] Round-trip: save, restart SimHub, confirm every binding and value survived.

## C. TF4ALL Dash (largest new surface)

- [ ] Dash appears in SimHub's dashboard library after install (6705b03), no manual import needed.
- [ ] Pair a phone, confirm live telemetry, and leave it connected through a full session without dropping.
- [ ] Rev strip: fills with revs, flashes at the redline, and the flash is in sync with the wheel rev lights (e163ea4, 8efd2d5).
- [ ] Rev strip direction setting: left-to-right and outside-in both render correctly (f315060).
- [ ] Scope screen: waveforms scroll and stay smooth while driving (7cb4017, 6490b00).
- [ ] Save / Revert from the dash with the desktop-scope chooser lands in the right scope (befddb6).
- [ ] Dirty indicators cross-track: edit on the desktop, dash shows dirty, and the other way round (43ccb19). Edit then undo clears the bar (ebcf896).
- [ ] REVERT button hides when there is nothing to revert (a3b65e7).
- [ ] Dash save paths match the desktop (874b3a0): fork, reuse, dedupe all behave the same from either surface.
- [ ] A car with no friendly name shows its car id rather than blank (b504c50).
- [ ] Overlay taps pass through where they should; tap zones are strict (b6be3c0).
- [ ] Keypad rejects out-of-range values with the range shown (aff3516).
- [ ] Toast appears for actions that need a game or car running (58cc149).

## D. Telemetry FFB, feel, and the refreshed 0.2.5 defaults

732b2bd changed shipped defaults. This is the section where your own settings are most at risk, hence the backup above.

- [ ] Fresh profile with the new defaults: drive AC and Forza, confirm the out-of-box feel is sane and not overpowering.
- [ ] Braking feel and drift feel behave as intended, and the drift-feel settings UI moves them (61b6399).
- [ ] Existing user settings are **not** silently overwritten by the refreshed defaults on upgrade.
- [ ] Axle slip lockup gate: on-wheel check in both AC and Forza (still pending from the earlier fix).
`BrakingGripLearner.cs` and its test stay untracked and do **not** ship in 0.2.5. Nothing to test here; just confirm they were not swept into a commit before the build is cut.

## E. Rev lights on hardware

- [ ] **G923 Xbox rev-lights over HID++ page 0x807A (060c181).** Protocol-correct by the documented spec but not confirmed on hardware. You own a G PRO, not a G923 Xbox, so this needs the reporter or another G923 Xbox owner. Treat as unvalidated until someone confirms.
- [ ] G PRO rev lights still work (no regression from the 0x807A path change).
- [ ] LED writes stay off the wire while game FFB is live (the contention rule).

## F. Community and headless paths

- [ ] Community car facts apply with the settings panel **closed** (cc63a37).
- [ ] Headless community refresh no longer dead-ends during signature warm-up (caced76).
- [ ] Silent car-name submit goes out once and is not repeated (61b6399).
- [ ] Downloaded-preset auto-update runs headlessly; dash taps count as activity (9e2ef97).
- [ ] No runaway request volume over an hour of idle (the polling-cost gating still holds).

## G. Engine variant identification (new this session, uncommitted)

Set up once: turn **"Also forward to SimHub" off** (its shipped default) and pick a car with **no community redline**. A community redline resolves even with a zero rev ceiling, so it masks every symptom in this section.

- [ ] Drive a car you have not driven before, then set a redline. It saves instead of showing "Couldn't identify this car's engine variant yet".
- [ ] Same car: pin an engine type and confirm it sticks.
- [ ] Wheel rev lights fill and flash (needs Telemetry Based FFB on, which is the gate on that path). These have never worked in this configuration since v0.2.0, so this is the headline check.
- [ ] Dash rev strip fills and flashes in step with the wheel.
- [ ] Rev limiter buzz fires (inferred from the code, not yet observed on a wheel).
- [ ] Repeat with a car that **does** have a community redline: no regression there either.
- [ ] Forwarding **on** (your normal setup): no regression, variants still carry MaxRpm.
- [ ] AC still resolves MaxRpm correctly (it relies on the overlay by design).

## Notes / failures

(Record anything that did not behave as expected, with the SimHub log snippet and which section it came from.)
