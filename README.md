# Trueforce For All

**The ultimate companion for a Trueforce-enabled Logitech wheel.**
Trueforce haptics in any game, rev light enhancements, the Dynamic OLED
screen, a phone dash, and community presets.

[![Latest release](https://img.shields.io/github/v/release/Mhytee/Trueforce-For-All?label=latest%20release&color=2ea043)][releases]
[![Downloads](https://img.shields.io/github/downloads/Mhytee/Trueforce-For-All/total?label=downloads&color=2ea043)][releases]
[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows&logoColor=white)](#install)
[![License](https://img.shields.io/badge/license-GPL--2.0--only-555)](LICENSE)
[![Discord](https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white)][discord]

### [Download the latest release][releases]

Free and open source, for SimHub. Contributors welcome.

Official Trueforce support keeps growing, but many major titles are still
waiting and some will never get it. This plugin fills the gap, and then
some.

> Something not working? Please [open an issue][issues] or say so in the
> [Discord][discord].

## Supported wheels

| Wheel | Haptics + FFB | Rev lights | OLED screen |
|---|---|---|---|
| Logitech G PRO Racing Wheel (Xbox/PC and PS/PC) | Yes | Yes | Yes |
| Logitech RS50 | Yes | Yes | Yes |
| Logitech G923 (Xbox/PC and PS/PC) | Yes | Yes | None |

> The plugin can add rev light support and control the Dynamic OLED screen
> in games with [Telemetry Based FFB](#telemetry-based-ffb), in iRacing,
> RaceRoom and Le Mans Ultimate, and in Assetto Corsa with the CSP Bridge.
> LIGHTSYNC car pattern matching works in every game the pattern data
> covers.

## FFB and Trueforce Effects

The plugin runs inside SimHub and drives the wheel's Trueforce haptic motor
in real time. The steering force underneath the effects comes from one of
three places:

- **FFB pass-through (most games).** The plugin taps the force feedback the
  game sends down the wire and layers the Trueforce haptics on top. Your
  cornering load and curb forces keep coming through underneath.
- **Handed over directly.** iRacing, RaceRoom, Le Mans Ultimate, and
  Assetto Corsa with the TF4ALL CSP Bridge give the plugin their force
  feedback with no capture involved. Each takes a switch or two in the game
  first, and one tick on the FFB tab; the [setup guides](guides/README.md)
  walk through them.
- **Built from telemetry.** In some games the plugin can fully replace the
  game's force feedback using telemetry
  ([Telemetry Based FFB](#telemetry-based-ffb)). Currently supported:
  Forza Horizon 4, 5 and 6, Forza Motorsport, and Farming Simulator 22
  and 25.

All of it is configurable per-game, per-car, from the plugin's tabbed
panel inside SimHub: master gain, individual effect tuning, precise typed
values on every slider, sidechain ducking between continuous and transient
effects, and a preset library with community sharing built in.

### Effects from telemetry

Synthesized from live game data and played over the Trueforce protocol.

- **Engine pulse**: rumble at the engine's firing pattern, derived from
  RPM and cylinder count (auto-detected per car when possible). Idle gives
  a gentle hum; higher RPM lifts both pitch and intensity.
- **Gear shift**: a short low-frequency thud whenever the gear changes.
- **ABS click**: configurable haptic when ABS engages.
- **Pit limiter**: configurable pulsing buzz while the limiter is engaged.
- **Redline buzz**: a hard buzz when you enter the redline. On by default.
- **DRS**: short chirp on the rising edge when the wing opens, plus an
  optional sustained flutter while DRS stays active. Silent on games that
  don't expose the flag.
- **Road bumps**: rough terrain rumbles through the wheel. On Forza, the
  per-tire surface rumble is read directly for a richer, more accurate
  road feel.
- **Implement thud**: lower, raise or extend an implement, or work a
  loader or crane arm yourself, and you feel the hydraulic hum while it
  moves and the thump as it lands. (Farming Simulator.)
- **Traction loss**: tire-screech haptics when grip breaks (wheelspin,
  lockup, drift), sharpest in games that report per-wheel slip (AC and the
  Forza titles). In Farming Simulator, Axle slip covers this instead.
- **Axle slip**: understeer and oversteer as two distinct feelings instead
  of one blur: a high scrub texture as the front washes wide, a deeper
  pulse as the rear steps out. (Per-tire telemetry: the Forza titles,
  Assetto Corsa and Farming Simulator.)
- **Lockup judder**: when a wheel locks under braking, a coarse pulsing
  judder kicks in, the feel of a flat-spotted tire skidding rather than
  rolling, fading as the car slows. A locked wheel becomes something you
  feel and can correct instead of a silent loss of grip. (Per-tire
  telemetry: the Forza titles and Assetto Corsa.)
- **Collision**: a thud on impact, scaled to the hit.
- **Airborne ducking**: when the car leaves the ground, the chosen effects
  cut out so jumps feel weightless, then return on landing. Detected from
  wheel load / suspension (AC, the Forza titles and Farming Simulator). On
  by default.
- **Stationary spring**: centering force so a parked or crawling car has
  some weight at the wheel instead of going limp, fading out as speed
  builds (AC).

The set is still growing, and which effects a game can drive depends on the
telemetry it publishes, so the plugin shows you the ones your current game
supports and hides the rest.

### Effects from game audio

WASAPI loopback captures the game's audio output (engine, tire, impact
sounds) and feeds it into the wheel as low-latency haptics. Lets you feel
things the telemetry doesn't expose, and works even in games that output no
telemetry at all, since the capture targets the game process directly.

## Telemetry Based FFB

In supported games the plugin builds the entire steering force itself,
instead of passing the game's own force feedback through. Today that means
the Forza titles (Forza Motorsport and Forza Horizon 4, 5, and 6) and
Farming Simulator 22 and 25.

It also unlocks the wheel's rev lights and screen, with the lights filling
and flashing with the engine.

## FFB spike reduction

Some games (Assetto Corsa being the worst offender we've seen) deliver
curb and collision FFB spikes wildly out of proportion to what's safe or
comfortable. On a strong wheelbase they can ruin a racing line or cause
real wrist strain over a session. iRacing has a built-in softener; most
other games don't. The plugin attenuates spikes only, so curbs land as
confident pushes instead of yanks while sustained cornering load and
weight transfer pass through untouched. Useful on its own, even with all
our other effects turned off.

## Per-game enhancements

Some games get enhancements of their own, whether that is a faster telemetry
stream, sharper effects, a force feedback overhaul, or the sim handing its
forces straight to the plugin. None of it needs a SimHub license.

<details>
<summary><b>iRacing</b></summary>

Reads the sim's telemetry at 1 kHz. iRacing keeps publishing the
steering torque it wants the wheel to hold even with its own force feedback
off, so the plugin carries that to the wheel with its effects on top, and
the rev lights and the wheel's screen come with it.

- **Each car's max force, learned.** The plugin watches what a car really
  pushes; drive a clean lap, and once the reading holds steady, one press
  takes it. Nudge any car heavier or lighter from there.
- **Incident points, announced.** The dash shows your count against the
  session's limit, and the wheel's screen flashes what each new one cost.
- **Engines sound like themselves.** iRacing states each car's cylinder
  count, and a shipped table adds the crank layout, so a cross-plane V8
  pulses like one.

Setup instructions show in the plugin on first launch, or
[here](guides/iracing-setup.md).

</details>

<details>
<summary><b>Assetto Corsa</b></summary>

Shared memory read at the game's own 333 Hz physics rate, which makes curb
collisions, road bumps and traction loss noticeably sharper than a 60 Hz
feed can deliver.

- **The TF4ALL CSP Bridge hands the force over.** A small CSP script the
  plugin offers to install, after which your rev lights and the wheel's
  screen run, light patterns can be changed mid-session, and Assetto Corsa
  needs no USB capture at all.

Setup instructions show in the plugin when it first sees the game, or
[here](guides/csp-bridge.md).

</details>

<details>
<summary><b>RaceRoom</b></summary>

Telemetry read from the game's shared memory, far above SimHub's 60 Hz
max, carries the steering force RaceRoom computes to the wheel with the
plugin's effects on top, and the rev lights and the wheel's screen come
with it.

- **Each car's strength, learned.** Drive a couple of clean laps, and once
  the reading holds steady one press takes it.
- **Engine data for every car in the game.** A shipped table covers all 356
  with their real cylinder count and crank layout, so a V8 pulses like one.

Setup instructions show in the plugin when it first sees the game, or
[here](guides/raceroom-setup.md).

</details>

<details>
<summary><b>Le Mans Ultimate</b></summary>

The official shared memory its SDK documents, read at 100 Hz, carries the
torque on the steering shaft to the wheel with the effects on top, and the
rev lights and the wheel's screen come with it.

- **Each car's peak force in Nm, learned**, and taken with one press, the
  same gesture iRacing uses.

Setup instructions show in the plugin when it first sees the game, or
[here](guides/lmu-setup.md).

</details>

<details>
<summary><b>Forza</b></summary>

Telemetry read straight from the game over UDP, output at the game's frame
rate, which is often much higher than SimHub's 60 Hz cap.

- **Per-tire data.** SimHub leaves this out, so reading it from the game
  directly is what lets us enhance the surface texture effect, curb strikes
  and collisions.
- **[Telemetry Based FFB](#telemetry-based-ffb)**, which can replace
  Forza's own force feedback outright.
- **SimHub still gets its copy**, so dashboards and bass shakers keep
  working.

Setup instructions show in the plugin when it first sees the game, or
[here](guides/forza-setup.md) and [here](guides/forza-forward.md).

</details>

<details>
<summary><b>Farming Simulator</b></summary>

Force feedback in Farming Simulator is notoriously basic. The game gives
the wheel a centering spring and little else, so
[Telemetry Based FFB](#telemetry-based-ffb) replaces it with a force of its
own, built from the ground under the tires, the weight of the machine as it
turns and an implement dragging harder as it fills.

- **The TF4ALL Enhanced Telemetry mod comes with it**, adding ground
  texture through the wheel, the thud as an implement drops into work, and
  the cut while your wheels are off the ground. The game publishes too
  little on its own, so the mod sends it, at up to 100 Hz.
- **Baked-in engine data for all 153 Farming Simulator 25 base-game
  vehicles**, so the engine effects know what they are driving without
  being told.

Setup instructions show in the plugin when it first sees the game, or
[here](guides/farming-sim.md).

</details>

Every other SimHub-supported game runs through SimHub's universal telemetry
feed instead. The plugin works there without a SimHub license, but
unlicensed that feed is capped at 10 Hz, which makes the effects feel coarse.
A licensed copy of SimHub (a small one-time payment) lifts it to 60 Hz, a
big step up in feel.

## The wheel's lights

A G PRO or RS50 stores lighting patterns for its rev strip, and until now
the only way to choose between them was the wheelbase's own menu or G HUB.
The LIGHTSYNC tab takes that over. The G923's strip has a fixed layout, so
the tab stays hidden there.

- **The pattern can match the car you are driving.** Tick "Match my wheel
  to the car I'm driving" and the strip takes on each car's own colors and
  fill direction as you get in, lighting where the real car lights. The
  data comes from [Lovely Sim Racing][lovely] (CC BY-NC-SA 4.0); a car
  they have not covered keeps whatever pattern you chose.
- **Save as many patterns as you want.** The wheel itself stores five; the
  plugin's library has no limit, and a bound button walks the whole library
  without taking your hands off the wheel.
- **A pattern maker.** Ten LEDs, click one and color it, with the wheel
  showing the whole pattern as you work. Thirteen hand-made patterns come
  built in, yours to copy and edit.
- **Color trim, pre-tuned.** The three colors inside an LED are not equally
  bright, so an even mix of red and green can reach the rim looking lime.
  Three sliders correct the balance, shipped already set from measurements
  on a G PRO.
- **A car can remember its own.** One click on "Remember for this car" and
  the pattern comes back whenever that car loads.
- **Idle modes.** With no game running, or the car sat still, the strip can
  run a slow sweep up and back, or meter your computer's audio so the
  lights move with whatever you are listening to. Colors come from the
  pattern you chose, and your revs take the lights back the moment a game
  reports them. Off by default.
- **The TF4ALL Dash's rev strip can match the wheel's**, colors, fill
  direction and switch-on points included.

Picks apply immediately, in and out of game. In a game whose force feedback
passes through the USB capture, changing the pattern interrupts that force
for a moment, so it is best done parked; where the plugin carries the force
itself, there is nothing to interrupt.

**Three modes, remembered per game.** The switch at the top of the panel
is Normal (everything), Lightsync only (the plugin leaves the game's force
feedback and Trueforce completely alone and only sets the wheel's light
pattern for the car you are in) or Off. Games that bring their own
Trueforce start on Lightsync only.

## The wheel's OLED screen

The G PRO and RS50 have a small display in the middle of the wheel. The
plugin takes it over, and you choose what goes on it.

- **Eleven ready-made screens.** Speed over gear and gear over speed,
  captioned or not; speed alone or gear alone; a big gear with the speed
  beside it, and the reverse; and three that carry your lap delta. Or pick
  Nothing and keep the wheel's own display, with shifts, finished laps and
  warnings still taking the screen for a moment.
- **Or build your own.** Pick a layout, then choose what goes in each
  slot: gear, speed, lap delta, position, lap of total, last lap time, or
  your own text. The editor gives each slot's size and character limit,
  and shows the screen on the wheel as you build it.
- **It reacts as you drive.** Shift and it flashes the gear, unless your
  screen already shows it. Cross the line and it puts up the lap time with
  your delta underneath, and tells you when it was a personal best.
- **It reports changes as they happen**, like a preset loading or a gain
  you just nudged, and warns you if the game is still sending its own
  force feedback.

Bind a button to step through your screens without letting go of the
wheel.

## TF4ALL Dash

The plugin ships its own SimHub dashboard, made for a phone or tablet kept
next to you or mounted on the rig: what you need at a glance while you
drive, and a way to change things mid-session without alt-tabbing out of
the game.

- **The Drive tab:** a big gear and rev strip, plus four info boxes you can
  set up how you like, from tire temps and wear, fuel, lap times, a
  radar, and more. The dash remembers its layout per game.
- **Tune from the seat:** effects on and off with their gains, master and
  audio capture gain, presets, Telemetry Based FFB and its main knobs, and
  a car's name, redline or engine type. Tap any value to type an exact
  number.
- **FFB Visualizer:** scrolling waveforms of the game's steering force and
  the haptic layer, as sent to the wheel. Clipping turns the trace red, and
  a yellow SPIKE badge marks where spike reduction stepped in.
- **An idle card:** close the game and the dash becomes your name and number
  over moving artwork, with the plugin version and any waiting update along
  the foot. Ten backgrounds, and you set the delay.
- **Make it yours:** eight themes, the option to drop to two info boxes if
  four crowd your screen, and the tabs you don't use can be hidden or
  reordered.

![TF4ALL Dash](docs/images/tf4all-dash-tabs.gif)

Installs with the plugin and appears in SimHub's dashboard list.

## Community features

Once one driver figures out a car's redline, fixes its name, or picks
its engine layout, everyone driving that same car gets it automatically.
Community features are on by default and anonymous: car facts need no
account. Turn them off in Settings and the plugin works fully offline
(see [Privacy](#privacy)).

- **Community preset browser**, built into the Presets tab. Browse what
  other drivers have shared for any game or car, sorted by votes and
  downloads.
- **Share your own.** Game presets, car presets, custom engines, and
  multi-preset packs can all be shared, with a description attached.
- **Car facts flow back automatically**, and converge as more drivers
  submit agreeing values. Sharing follows your community settings and can
  be turned off.
- **Downloaded presets stay current.** When a curator updates a preset
  you downloaded, an "updates available" chip surfaces it; apply updates
  manually or automatically.
- **No account needed; sign-in is optional.** It takes an emailed one-time
  code, no password, and unlocks preset sharing and the Account tab, where
  you can set a display name, see how your shared presets are doing, export
  your data, or delete your account.

There is also a **[Discord server][discord]**: a place to hang out, swap
tunes, ask for help, and get involved. Link your Discord account in the
plugin and the achievements you earn grant matching roles there.

## Install

1. Download `TrueforceForAll-Setup.exe` from the [latest release][releases].
2. Close SimHub if it's running.
3. Run the installer. It finds [SimHub](https://www.simhubdash.com/),
   copies the plugin files into its install folder, and installs
   [USBPcap](https://github.com/desowin/usbpcap) if you don't
   already have it. No SimHub yet? It points you at the download and waits,
   so you can install SimHub and press Retry.
4. Close Logitech G HUB, and keep it closed while you play: running it
   alongside can cause issues.
5. Turn on **Run as administrator** in SimHub's settings. Reading the
   wheel's USB traffic needs it. Without it the force feedback pass-through
   never starts.
6. Launch SimHub.

The **?** in the panel's header opens the guides: setup for the games that
need it (iRacing, RaceRoom, Le Mans Ultimate, Assetto Corsa, Forza,
Farming Simulator), what to do when something is wrong, and the questions
people ask most. Search reads the guides themselves, so typing G HUB,
app.ini or 5300 lands on the one that explains it. The same guides are in
[guides/](guides/README.md) here, to read before you install anything.

The installer is conservative on uninstall: it removes our files but leaves
SimHub, USBPcap, and shared dependencies (HidSharp, NAudio) alone, so other
plugins that share those keep working.

## Games with native Trueforce

Some titles already ship Trueforce on PC, so the plugin starts on
**Lightsync only** for them: their force feedback and Trueforce are left
alone, and only the wheel's light pattern is set. Switch off the game's
native Trueforce and set the plugin to Normal mode to take over, tuning the
feel yourself rather than taking whatever the game hardcodes (and on
Automobilista 2, adding Trueforce that was never really there).

<details>
<summary><b>Why a slider at 0 is not off</b></summary>

Many games keep the Trueforce API live even at 0, so the plugin fights a
channel the game is still driving and the wheel whines. Only a real on/off
switch or a config-file setting fully releases the wheel.

The plugin catches this itself. The USB capture sees every Trueforce packet
on the wheel, so when a game streams beside the plugin, the plugin drops to
Lightsync only for that game session within a couple of seconds (the log and
the status panel say why) instead of whining beside it. Set the plugin to
Normal mode to try again once the game's Trueforce is off. Without USBPcap
there is nothing to watch, and nothing changes.

</details>

Running MAIRA and the plugin at the same time is not supported: with MAIRA's
RPM lights on, the plugin steps aside for it. Close MAIRA, then set the mode
to Normal.

| Game | How to disable native Trueforce | Plugin takes over? |
|---|---|---|
| iRacing | Set `loadTrueForceAPI=0` in `app.ini` | Yes |
| Dirt Rally 2.0 | In-game Trueforce on/off switch | Yes |
| GRID (2019) | In-game Trueforce on/off switch | Yes |
| Forza Motorsport (2023) | Not tested | Yes, through [Telemetry Based FFB](#telemetry-based-ffb). Set the mode to Normal first |
| Automobilista 2 | Steam launch option `disableTF` (try `-disableTF` if that fails) | Likely, untested |
| Assetto Corsa Competizione | Slider only, no off switch found | No, stays live |
| Assetto Corsa EVO | Slider only, no off switch found | No, stays live |
| Assetto Corsa Rally | Slider only, no off switch found | No, stays live |
| BeamNG.drive | Not tested | Not tested |
| F1 22, 23, 24 and 25 | Not tested | Not tested |
| EA Sports WRC (2023) | Not tested | Not tested |
| WRC 10 | Not tested | Not tested |
| WRC Generations | Not tested | Not tested |
| Project CARS 3 | Not tested | Not tested |
| Test Drive Unlimited Solar Crown | Not tested | Not tested |
| Le Mans Ultimate | Settings > Controls > Force Feedback: Vendor Specific Force Feedback off | Yes, through the handover: tick "Take over force feedback for Le Mans Ultimate" on the FFB tab |

**AMS2 is a special case:** per Reiza's devs it loads the Logitech SDK but
never actually implements Trueforce, so it behaves like a non-Trueforce
game with the channel left live. The `disableTF`
launch option falls back to legacy mode and should let the plugin take
over, but I haven't confirmed it on hardware. (Steam launch options: right-
click the game, Properties, General, Launch Options.)

>I don't own some of these titles, so this table grows from user reports. If
you find an off switch or config setting for one of the ones still marked
"no", or get the plugin working on a native-Trueforce game that isn't listed
here at all, please open an issue and let me know.

## Known limitations

- **Logitech G HUB must stay closed** when the plugin is in use. Running it
  alongside can cause issues.
- **The Trueforce level dial on the wheel doesn't apply** while the
  plugin is driving Trueforce: the wheel's own intensity scaling stops
  responding to it. Use the plugin's Master Gain and per-effect Gain
  controls to set intensity instead.
- **Rev lights and the screen depend on who carries the force.** They share
  a control channel with the game's force feedback, and writing to them
  while the game's force passes through the USB capture cuts that force, so
  the plugin drives them wherever it can (see
  [Per-game enhancements](#per-game-enhancements)). For the same reason,
  changing a LIGHTSYNC pattern in a pass-through game interrupts the force
  for a moment, so it is best done parked. A custom driver that lifts this
  is in development.
- **The plugin cannot run alongside a game's own Trueforce.** The wheel's
  Trueforce stream has room for one sender, so in a native-Trueforce game
  the plugin stays on Lightsync only unless the game's Trueforce is
  switched off (see the table above). Running MAIRA at the same time is
  not supported for the same reason.

## FAQ

<details>
<summary><b>Which games does it work with?</b></summary>

The audio-derived effects work in any game at all, since the plugin captures
the game's audio directly with no SimHub support needed. Games that SimHub
supports additionally get the telemetry-derived effects (engine pulse, gear
shifts, ABS, and so on). Assetto Corsa, iRacing, RaceRoom, Le Mans
Ultimate, Forza Motorsport, the Forza Horizon games and Farming Simulator
go further with a higher-fidelity direct path (see
[Per-game enhancements](#per-game-enhancements)).

</details>

<details>
<summary><b>Do I need to pay for SimHub?</b></summary>

SimHub itself is free, and the plugin works without a SimHub license. The
difference is the telemetry rate: unlicensed, games the plugin doesn't read
directly run at only 10 Hz, which makes the effects feel coarse. A licensed
copy lifts that to 60 Hz, which is a big step up in feel. SimHub is cheap and
well worth it. (Assetto Corsa, iRacing, RaceRoom, Le Mans Ultimate, the
Forza titles and Farming Simulator are read directly, so they run at their
full rate regardless of license.)

</details>

<details>
<summary><b>Is this anti-cheat safe?</b></summary>

Yes. The plugin operates entirely outside the game. It never injects code,
reads or modifies game memory, or hooks the game in any way. It only talks
to the wheel over USB (via USBPcap), reads telemetry the game already
broadcasts (SimHub, shared memory, or UDP), and captures game audio through
Windows' own loopback. Switching off a game's native Trueforce is done by
editing a config file or flipping an in-game setting before launch, never by
touching the running game.

</details>

<details>
<summary><b>Will it change or replace my normal force feedback?</b></summary>

Not unless you ask it to. By default the plugin preserves your existing
force feedback and layers haptic effects on top of it; your wheelbase's own
FFB still comes through, with all your usual settings intact. The exception
is [Telemetry Based FFB](#telemetry-based-ffb), which deliberately builds
the steering force from telemetry instead. In the Forza titles that is
opt-in and stays off until you turn it on. In Farming Simulator it runs
automatically, because the centering spring it replaces is all the game
offers. In iRacing, RaceRoom and Le Mans Ultimate, and in Assetto Corsa
with the bridge, the force is the sim's own, carried by the plugin, so it
feels exactly as you tuned it.

</details>

<details>
<summary><b>Why does it need USBPcap, and is that safe?</b></summary>

USBPcap is an open-source USB capture driver. The plugin uses it to read the
wheel's own force-feedback traffic off the USB bus so it can mirror that into
the Trueforce stream (this is the FFB pass-through that keeps your normal
force feedback alive). It only looks at the wheel's traffic, it's widely used
and bundled with our installer, and you can uninstall it separately at any
time. Games that hand their force over (iRacing, RaceRoom, Le Mans Ultimate,
and Assetto Corsa with the bridge) and games running Telemetry Based FFB
need no capture at all.

</details>

<details>
<summary><b>Do I need Logitech G HUB?</b></summary>

No, and it has to stay closed while you play: running it alongside can
cause issues.

</details>

<details>
<summary><b>My normal force feedback disappeared, or the plugin says pass-through is not running.</b></summary>

Run the self-test in the Settings tab's Diagnostics section. It checks the
whole chain and names the part that is missing, USBPcap's capture driver
included. The usual cause is SimHub not running as administrator: reading
the wheel's USB traffic needs it, and without it the pass-through cannot
start. Use SimHub's own Run as administrator setting rather than
right-clicking the exe, then restart SimHub.

</details>

<details>
<summary><b>The effects feel weak or light.</b></summary>

Raise Master Gain and the per-effect Gain in the plugin settings. The
Trueforce dial on the wheel itself does nothing while the plugin is running,
so all intensity is set in the plugin.

</details>

<details>
<summary><b>Can I use this in games that already support Trueforce?</b></summary>

By default the plugin starts on Lightsync only for native-Trueforce titles,
since the game already provides it, and if both end up streaming at once it
steps aside by itself. But you can switch off the game's native Trueforce
and run the plugin instead, which lets you tune the feel yourself. See
[Games with native Trueforce](#games-with-native-trueforce) for which titles
allow this and how.

</details>

## Supporting the project

The plugin is free and stays that way. It is primarily built by one person,
and a **[Patreon][patreon]** is there for anyone who wants to keep that
going. Supporters get cross-device backup and sync of their full setup, and
a spot on the supporters wall in the plugin. Manual export and import stay
available to everyone.

## Community coverage

- **Overtake.gg**, [Logitech's Trueforce Arrives Early in Forza Horizon 6 Thanks to Community-Made SimHub Plugin](https://www.overtake.gg/news/logitechs-trueforce-arrives-early-in-forza-horizon-6-thanks-to-community-made-simhub-plugin.4520/)
- **Armando Ramirez**, [Does Logitech TRUEFORCE Actually Matter in Forza Horizon 6?](https://www.youtube.com/watch?v=p5P_Ww14CNg)
- **Revasio**, [a French installation tutorial on TikTok](https://www.tiktok.com/@revasio/video/7641185174306180384)

## How it works

<details>
<summary><b>The detail</b></summary>

The plugin opens the wheel, runs the Trueforce init sequence, and streams
haptics to endpoint 3 at 1 kHz. The effects themselves are synthesized from
telemetry or from the game's own audio, with per-game tuning on top.

A USBPcap-based tap reads the game's own force feedback off the USB
connection. Its plain force is mirrored into bytes 6-9 of that stream, which
the wheel takes as its motor torque target, with the rolling window riding on
top as an additive overlay. The game's DirectInput effects, its spring,
damper, friction, inertia and waveforms, are decoded from the same traffic and
rendered into the stream alongside it.

In some games Telemetry Based FFB is an alternative to capturing. The force
is built from the telemetry itself, from per-axle grip and slip and the load
through the corner, and some prefer that to what their game sends. For the
games that send nothing usable, Farming Simulator among them, it is the only
route. Where a sim publishes its own steering torque, that torque is
reshaped rather than synthesized.

Some games need no tap at all. Assetto Corsa, iRacing, RaceRoom and Le Mans
Ultimate publish their own force values, so the plugin reads them from there
and leaves the wheel's HID++ pipe alone, which is what lets the rev lights
and the screen run alongside the force.

The rev lights and the wheel base's screen take a different path, the wheel's
HID++ control pipe rather than the haptic stream: level and slot writes for
the lights, frames for the screen. That pipe carries one writer at a time, so
the plugin watches what else is writing to it and stands down while a game is
driving the lights itself.

</details>

## Privacy

Community features are on by default and anonymous; turn them off in
Settings and the plugin runs fully offline. What the online features
(community presets, car data, sign-in, cloud backup) store, who
processes it, and how to export or delete it is covered in
[PRIVACY.md](PRIVACY.md).

## License

GPL-2.0-only. See [LICENSE](LICENSE).

The wire protocol and init sequence are derived from the
[mescon Linux driver project][mescon], also GPL-2.0.

## Acknowledgments

- **[mescon/logitech-trueforce-linux-driver][mescon]**: reverse-engineered
  the wheel's driver and wire protocol, and later the light-slot protocol
  (the staging and commit sequence behind the LIGHTSYNC tab). This project
  would not exist without their work.
- **[PeposCJ/LogiDynamicDash][logidynamicdash]**: worked out how the wheel
  base's OLED screen is driven and documented it publicly. The wheel screen
  support here is built on that work, and PeposCJ's captures settled when
  the wheel's own pattern selection has to be left alone.
- **[Lovely Sim Racing's car data][lovely]**: the per-car light patterns,
  per-gear redlines and blink rates behind "Match my wheel to the car",
  shared under CC BY-NC-SA 4.0.
- **Andrew Boersma**: built the telemetry-based force feedback engine
  and the axle slip, curb thump, and lockup judder effects. The headline
  features of the 0.2.0 release are his work.
- **[USBPcap][usbpcap]** by Tomasz Mon: the kernel-mode USB filter that
  lets us tap the wheel's bus traffic for FFB pass-through.
- **[mdjarv/assettocorsasharedmemory][acshmem]**: community reference
  for AC's shared-memory layout, used to validate our SPageFilePhysics
  field offsets.
- **[HidSharp][hidsharp]**: cross-platform HID library used for the
  control-side of wheel communication.
- **[NAudio][naudio]**: audio I/O library used for the per-process
  loopback capture pipeline.
- **[ManteoMax's Forza Horizon 5 spreadsheet][manteomax]**: the
  canonical community catalog mapping Forza CarOrdinal to year/make/model
  and engine specs. Our FH5 lookup (engine cylinder / layout / electric
  detection plus auto-named per-car presets) is built from this data.
- **[SimHub][simhub]**: the host application. This plugin is unofficial
  and not affiliated with the SimHub project.
- **Armando Ramirez**: produced a [video walkthrough][armando] of the
  plugin in Forza Horizon 6 and tuned his own presets for it.
- **Revasio**: produced a [French-language installation tutorial][revasio]
  on TikTok, helping French-speaking drivers get set up.
- **Svenmoor**: tested the plugin against a range of native-Trueforce
  titles and mapped which ones have a true Trueforce off switch (so the
  plugin can take over cleanly) versus which only expose an intensity
  slider, which populated the "Games with native Trueforce" table above.

Logitech, Trueforce, LIGHTSYNC, G PRO, RS50, and G923 are trademarks of Logitech.
This project is not affiliated with, endorsed by, or sponsored by Logitech. It is
original Windows code built on protocols reverse-engineered by the community and
me, and uses or redistributes no Logitech source, firmware or assets.

[mescon]: https://github.com/mescon/logitech-trueforce-linux-driver
[lovely]: https://github.com/Lovely-Sim-Racing/lovely-car-data
[logidynamicdash]: https://github.com/PeposCJ/LogiDynamicDash
[usbpcap]: https://github.com/desowin/usbpcap
[acshmem]: https://github.com/mdjarv/assettocorsasharedmemory
[hidsharp]: https://github.com/treehopper-electronics/HIDSharp
[naudio]: https://github.com/naudio/NAudio
[manteomax]: https://www.manteomax.com/
[simhub]: https://www.simhubdash.com/
[releases]: https://github.com/Mhytee/Trueforce-For-All/releases
[issues]: https://github.com/Mhytee/Trueforce-For-All/issues
[discord]: https://discord.gg/sfwsDqTsdn
[patreon]: https://www.patreon.com/Mhytee
[armando]: https://www.youtube.com/watch?v=p5P_Ww14CNg
[revasio]: https://www.tiktok.com/@revasio/video/7641185174306180384
