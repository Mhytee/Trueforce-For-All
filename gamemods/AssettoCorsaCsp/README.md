# TF4ALL FFB Bridge (Assetto Corsa, CSP)

Status: groundwork. The plugin does not consume this bridge yet; the
tap-free AC force feedback shipped first via vanilla shared memory
(`finalFF`, the ACFFB access code) and needs no CSP at all. This script is
the validated Phase 2 path for data vanilla AC does not publish.

## What it does

A Custom Shaders Patch "ffb-postprocess" Lua script. CSP calls it once per
physics frame (333 Hz). It is a pure pass-through (the game's force
feedback is returned unchanged); it only publishes to a named shared
memory section, `TF4All.ACBridge.v1`, readable from any local process via
`MemoryMappedFile.OpenExisting`:

- `ffbValue` (the post-chain FFB scalar CSP hands the hook), physics rate
- `ffbPure`, `ffbFinal`, `ffbMultiplier`, `steerTorque` (pre-gain
  material vanilla AC lacks), refreshed at graphics rate
- per-wheel `slipAngle` (radians), `slipRatio`, `ndSlip`, `load`, `mz`,
  `fx`, `fy`, physics rate on CSP builds with `StateCarPhysicsRate.wheels`
  (0.2.11+), graphics rate otherwise

- `acLeds` (v3): who owns the wheel's rev lights, packed into one word.
  Byte 0 is whether CSP's `g27_lights` module is active, byte 1 is its
  `MODE` (1 DI_BASED, 2 PERCENTAGE, 3 AI_BASED, 4 DISABLED, 0 unknown).
  AC drives the rev lights through that module, and it shares the wheel's
  HID++ pipe with anything else writing to the wheel, so a reader needs to
  know whether the game intends to drive the bar before touching it.
  CSP offers no way to suppress the module at runtime: this reports, it
  does not control.

- `acLedCount`, `acLedRpm[12]`, `acLedBlinkRpm`, `acLedBlinkHz` (v4): the
  CAR's own shift lights, read from its `digital_instruments.ini` `[LED_n]`
  sections via `ac.INIConfig.carData`, which reads `data.acd` too (nearly
  every car is packed, and doing this from outside the game would mean
  implementing AC's container format). Each `RPM_SWITCH` becomes one entry in
  `acLedRpm`, so the wheel's bar can light where the car's own dash lights
  instead of at a percentage of the rev range. Optional data: cars that model
  no shift lights report a count of 0, which readers must treat as "no
  opinion" rather than "no lights".

- `acLedRgb[36]` (v5): the same LEDs' `EMISSIVE` colours, three floats each,
  UNSCALED. AC treats these as emissive intensities rather than 0-255 colours
  (values above 255 are ordinary), so readers normalise per LED: scale the
  brightest channel to full and keep the hue. An all-zero triple means the car
  gave no colour.

Readers must honor the seqlock: `seq` is odd while the writer is mid-frame;
re-read until it is even and unchanged across the read. `magic` is
0x54463441, `version` is 5. Each version only APPENDS fields, so every
earlier offset holds and a reader takes the tail only from a writer new
enough to have written it.

## Install (manual, for testing)

1. Copy the `tf4all` folder to
   `<assettocorsa>\extension\lua\ffb-postprocess\tf4all`
   (so the script sits at `...\ffb-postprocess\tf4all\ffb.lua`).
2. In Content Manager: Settings > Custom Shaders Patch > FFB Tweaks,
   enable "Additional post-processing script" and pick `tf4all`.
   (In-game: the CSP settings app has the same option.)
3. Restart the session.

Caution: CSP allows exactly ONE active post-processing script. Selecting
this one displaces any other FFB script the user runs (for example custom
soft-lock or FFB-shaping scripts). Any future auto-install in the plugin
must detect an existing enabled script and ask, never silently replace it.

## Notes

- `steerTorque` units are undocumented in the CSP SDK (presumed Nm at the
  shaft). Calibrate against a known wheel before trusting absolute values.
- The four `slipAngle` values from the graphics-rate fallback path are
  converted from degrees to radians in the script, so the consumer always
  sees radians either way.
