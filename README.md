# Trueforce For All

**Unlock everything your Logitech wheel can do, and more.**
Trueforce, rev lights, the OLED screen.

> **You are on the beta branch.** This branch carries the next release
> while it gets tested in the open. Builds from here ship as pre-releases on the
> [releases page][releases]; anyone can install them and receive
> future betas through the in-app updater. The stable channel lives on the
> [`main` branch](https://github.com/Mhytee/Trueforce-For-All/tree/main).
> Something broken in a beta build? Please [open an issue][issues] or
> say so in the [Discord][discord].

Official Trueforce support keeps growing, but many major titles are still
waiting and some will never get it. This plugin fills those gaps, building
the haptics from telemetry or from the game's own audio.

Original Windows code, built on a wire protocol reverse-engineered by the
[mescon Linux driver project][mescon] and, for the wheel's OLED screen, on
protocol work by [PeposCJ][logidynamicdash]. No Logitech source, firmware or
assets are used or redistributed.

## Supported wheels

| Wheel | Haptics + FFB | Rev lights | OLED screen |
|---|---|---|---|
| Logitech G PRO Racing Wheel (Xbox/PC and PS/PC) | Yes | Yes | Yes |
| Logitech RS50 | Yes | Yes | Yes |
| Logitech G923 (Xbox/PC and PS/PC) | Yes | Yes | No screen |

> The plugin can add rev light support and control the Dynamic OLED screen
> in games with [Telemetry Based FFB](#telemetry-based-ffb), in
> [iRacing](#iracing), and in [Assetto Corsa](#assetto-corsa) with the CSP
> Bridge. LIGHTSYNC car pattern matching works in every game the pattern
> data covers.

## What it does

The plugin runs inside SimHub and drives the wheel's Trueforce haptic motor
in real time. The steering force underneath the effects comes from one of
three places:

- **FFB pass-through (most games).** Driving the Trueforce motor would
  otherwise silence the game's own force feedback, so the plugin taps that
  signal off the USB bus and folds it back into the Trueforce stream. Your
  real cornering load, weight transfer and curb forces keep coming through
  underneath every effect, in any game whose force feedback uses standard
  HID++ (effectively all of them on these wheels).
- **Handed over directly.** [iRacing](#iracing), and
  [Assetto Corsa](#assetto-corsa) with the TF4ALL CSP Bridge, give the
  plugin their force feedback with no capture involved.
- **Built from telemetry.** In some games the plugin can fully replace the
  game's force feedback using telemetry
  ([Telemetry Based FFB](#telemetry-based-ffb)). Currently supported:
  Forza Horizon 4, 5 and 6, Forza Motorsport, and Farming Simulator 22
  and 25.

On top of that force it mixes:

- **Telemetry-derived haptic effects**, synthesized from live game data and
  played over the Trueforce protocol.

  - **Engine pulse**: rumble at the engine's firing pattern, derived
    from RPM and cylinder count (auto-detected per car when possible).
    Idle gives a gentle hum; higher RPM lifts both pitch and intensity.
  - **Gear shift**: a short low-frequency thud whenever the gear changes.
  - **ABS click**: configurable haptic when ABS engages.
  - **Pit limiter**: configurable pulsing buzz while the limiter is
    engaged.
  - **Redline buzz**: a hard buzz when you enter the redline. On by
    default.
  - **DRS**: short chirp on the rising edge when the wing opens, plus an
    optional sustained flutter while DRS stays active. Silent on games
    that don't expose the flag.
  - **Road bumps**: rough terrain rumbles through the wheel. On Forza,
    the per-tire surface rumble is read directly for a richer, more
    accurate road feel.
  - **Implement thud**: lower, raise or extend an implement, or work a
    loader or crane arm yourself, and you feel the hydraulic hum while it
    moves and the thump as it lands. (Farming Simulator.)
  - **Traction loss**: tire-screech haptics when grip breaks (wheelspin,
    lockup, drift), sharpest in games that report per-wheel slip (AC and
    the Forza titles). In Farming Simulator, Axle slip covers this
    instead.
  - **Axle slip**: understeer and oversteer as two distinct feelings
    instead of one blur: a high scrub texture as the front washes wide,
    a deeper pulse as the rear steps out. (Per-tire telemetry: the Forza
    titles, Assetto Corsa and Farming Simulator.)
  - **Lockup judder**: when a wheel locks under braking, a coarse
    pulsing judder kicks in, the feel of a flat-spotted tire skidding
    rather than rolling, fading as the car slows. A locked wheel becomes
    something you feel and can correct instead of a silent loss of grip.
    (Per-tire telemetry: the Forza titles and Assetto Corsa.)
  - **Collision**: a thud on impact, scaled to the hit.
  - **Airborne ducking**: when the car leaves the ground, the chosen
    effects cut out so jumps feel weightless, then return on landing.
    Detected from wheel load / suspension (AC, the Forza titles and
    Farming Simulator). On by default.
  - **Stationary spring**: centering force so a parked or crawling car
    has some weight at the wheel instead of going limp, fading out as
    speed builds (AC).
  - **And more**, with the set still growing. Which effects a game can
    drive depends on the telemetry it publishes, so the plugin shows you
    the ones your current game supports and hides the rest.

- **Audio-derived effects**: WASAPI loopback captures the game's
  audio output (engine, tire, impact sounds) and feeds it into the
  wheel as low-latency haptics. Lets you feel things the telemetry
  doesn't expose, and works even for games which do not output telemetry data
  since capture targets the game process directly.

All of it is configurable per-game, per-car, from the plugin's tabbed
panel inside SimHub: master gain, individual effect tuning, precise typed
values on every slider, sidechain ducking between continuous and transient
effects, and a preset library with community sharing built in.

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

## Install

The easiest path is the bundled installer:

1. Download `TrueforceForAll-Setup.exe` from the [latest release][releases].
2. Close SimHub if it's running.
3. Run the installer. It detects SimHub, copies the plugin files into the
   SimHub install folder, and (if USBPcap isn't already installed) runs
   the bundled USBPcap setup automatically.
4. Close Logitech G HUB (it claims the wheel's HID interface).
5. Launch SimHub. The plugin auto-enables on first run.

The **?** in the panel's header opens the guides: setup for the games that
need it (iRacing, Assetto Corsa, Forza, Farming Simulator), what to do when
something is wrong, and the questions people ask most. Search reads the
guides themselves, so typing G HUB, app.ini or 5300 lands on the one that
explains it.

The installer is conservative on uninstall: it removes our files but leaves
SimHub, USBPcap, and shared dependencies (HidSharp, NAudio) alone, so other
plugins that share those keep working.

## Requirements

- Windows 10 / 11
- [SimHub](https://www.simhubdash.com/)
- A supported Logitech wheel (table above)
- [USBPcap](https://github.com/desowin/usbpcap), bundled with our installer
  if you don't already have it. Used to mirror the game's existing FFB
  signal into the Trueforce stream so the two coexist.
- Logitech G HUB **closed** while playing (it claims the HID interface and
  blocks us from talking to the wheel)
- **SimHub running as administrator.** Reading the wheel's USB traffic needs
  it, so without it the force feedback pass-through never starts. Turn on
  Run as administrator in SimHub's own settings, then restart SimHub.

## Telemetry Based FFB

In supported games the plugin builds the entire steering force itself,
instead of passing the game's own force feedback through. Today that means
the Forza titles (Forza Motorsport and Forza Horizon 4, 5, and 6) and
Farming Simulator 22 and 25.

In Forza you get a real sense of the grip limit: the wheel goes light as
the front washes wide, loads up through a corner, and pulls into a
countersteer as the rear steps out. Farming Simulator gets a model built
for heavy machinery instead: see [Farming Simulator](#farming-simulator).

It tunes itself as you drive, and the optional **Auto strength** levels
cars out so you stop retuning at every swap. (Forza only.)

In the Forza titles it can replace the game's force feedback wholesale, so
it is **off by default**. Farming Simulator is the other way round: there it
is the only thing making real force feedback, so it **arms itself** as
soon as the game is running.

Like [iRacing](#iracing) and [Assetto Corsa](#assetto-corsa) with the
bridge, it also unlocks the wheel's rev lights and screen: the rev lights
fill and flash with the engine, honoring the car's real redline where the
community has confirmed one.

## iRacing

iRacing keeps publishing the steering torque it wants the wheel to hold
even with its own force feedback switched off. The plugin reads that torque
straight from the sim and carries it to the wheel at 1 kHz, so the force you
feel is iRacing's own, with the plugin's effects layered on top, and the
rev lights and the wheel's screen come with it.

- **Each car keeps its own max force.** Cars follow the max-force number
  iRacing already holds for them. The plugin also watches what a car
  really pushes as you drive: drive a clean lap, and once the reading holds
  steady, one press takes it. Nudge any car heavier or lighter from there.
- **Incident points, announced.** The dash shows your count against the
  session's limit, and the wheel's screen flashes what each new one cost.
- **Engines sound like themselves.** iRacing states each car's cylinder
  count, and a shipped table adds the crank layout, so a cross-plane V8
  pulses like one.

Four steps, once, and a first-launch notice walks you through them: set
`loadTrueForceAPI=0` in `app.ini` and turn iRacing's own force feedback off
(off, not strength zero), then set the mode to Normal and tick "Take over
force feedback for iRacing". Until then the plugin sits on Lightsync only
in iRacing and leaves the sim alone. Running MAIRA alongside the plugin is
not supported.

## Assetto Corsa

Assetto Corsa can hand the plugin its force feedback directly through a
small CSP script, the TF4ALL CSP Bridge. The plugin offers to install it
the first time it sees the game, and the Game Mods section in Settings
installs or removes it any time after. It needs Custom Shaders Patch, one
restart of the game, and Content Manager closed while it installs.

- **The wheel feels exactly as you have tuned it.** The bridge hands over
  the game's finished force, after your in-game gain and every CSP FFB
  tweak, and the plugin carries it to the wheel with the effects on top.
- **The screen and pattern changes stop costing force.** With the bridge
  carrying the force, the wheel's screen runs in Assetto Corsa, and
  switching LIGHTSYNC patterns no longer cuts the force feedback.
- **No USB capture needed.** With the bridge installed, Assetto Corsa no
  longer depends on USBPcap. Without it, everything keeps working through
  the capture as before; there is no setting to manage.

## Farming Simulator

Farming Simulator 22 and 25 normally drive the wheel with one basic
centering spring. Every machine feels the same, and none of the ground
you are driving over comes through the wheel. Telemetry Based FFB builds
the force from the game's own telemetry instead: the ground under the
tires, the weight of the machine as it turns, and an implement dragging
harder as it fills.

**The TF4ALL Enhanced Telemetry mod comes with it.** The game does not
publish enough telemetry on its own, so the plugin installs a mod that
reports implement state, fill and mass, hydraulic motion and wheel
speed. It finds your mods folder even if you have moved it. Leave
SimHub's own Telemetry Interface mod in place alongside it; this one
adds a channel rather than replacing it.

**Baked-in engine data for all 153 Farming Simulator 25 base-game
vehicles**, so the engine effects know what they are driving without
being told. Vehicle names come from the game itself, through the mod.

## FFB spike reduction

Some games (Assetto Corsa being the worst offender we've seen) deliver
curb and collision FFB spikes wildly out of proportion to what's safe or
comfortable. On a strong wheelbase they can ruin a racing line or cause
real wrist strain over a session. iRacing has a built-in softener; most
other games don't. The plugin attenuates spikes only, so curbs land as
confident pushes instead of yanks while sustained cornering load and
weight transfer pass through untouched. Useful on its own, even with all
our other effects turned off.

## TF4ALL Dash

The plugin ships its own SimHub dashboard, made for a phone or tablet
kept next to you or mounted on the rig: something to read while you
drive, and a way to change things mid-session without alt-tabbing out of
the game.

- Drive tab: a race-ready view. Gear, speed and revs in the middle with
  pedals and steering around them, and the rest of the screen is boxes
  you arrange yourself. Keep one layout for everything, or a different
  one per game.
- Set a car's name, redline start or engine type.
- Turn individual effects on and off and set their gain.
- Adjust master and audio capture gain.
- Switch presets.
- On-screen rev lights across the top of every screen, the dash's own
  strip rather than the wheel's. Fill left to right or outside in, your
  pick in Settings.
- Visualizer: scrolling waveforms of the game's steering force and the
  haptic layer, as sent to the wheel. Clipping turns the trace red, and
  a yellow SPIKE badge marks where spike reduction stepped in.
- Telemetry Based FFB: turn it on or off for the game you are in, and
  tune its main knobs from the rig. Tap any value to type an exact
  number instead of stepping to it.
- Idle mode: go idle or close the game and the dash becomes an ambient
  card, your name and number over moving artwork, with the plugin
  version and any waiting update along the foot. Ten backgrounds, and
  you set how long it waits before dropping into idle.
- Themes: eight palettes, applied to the running dash without a reload.
  Colors that carry meaning, like a red warning or a green best lap, are
  left alone.
- Make it yours: hide the tabs you don't use and reorder the rest, in
  Settings > TF4ALL Dash.

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
  downloads, and download one to try it.
- **Share your own.** Game presets, car presets, custom engines, and
  multi-preset packs can all be shared, with a description attached.
- **Car facts flow back automatically.** Correct a redline, fix a car's
  name, or pick an engine layout, and the fact is shared anonymously.
  The next driver loading the same car gets the correction applied
  without ever opening a panel. Sharing follows your community
  settings and can be turned off.
- **Downloaded presets stay current.** When a curator updates a preset
  you downloaded, an "updates available" chip surfaces it; apply updates
  manually or automatically.
- **No account needed; sign-in adds extras.** Car facts work
  anonymously; an optional sign-in (an emailed one-time code, no
  password) unlocks preset sharing and the Account tab, where you can
  set a display name, see how your shared presets are doing, export
  your data, or delete your account.

No single bad submission wins out: presets are surfaced by votes, and
car facts converge as more drivers submit agreeing values.

There is also a **[Discord server][discord]**: a place to hang out, swap
tunes, ask for help, and get involved. Link your Discord account in the
plugin and the achievements you earn for contributing (sharing presets,
getting downloads, submitting car facts other drivers end up using)
grant matching roles in the server.

Feedback is welcome there, or on [GitHub issues][issues].

## Per-game enhancements

A few titles are read directly rather than through SimHub, at a much
higher rate than SimHub's 60 Hz cap. That makes their effects sharper and
more responsive, and it needs no SimHub license:

**Assetto Corsa** has a dedicated path: shared memory is read directly at
AC's native 333 Hz physics rate, which makes curb collisions, road bumps,
traction loss and the other haptic effects noticeably sharper than
SimHub's 60 Hz feed can deliver. With the
[TF4ALL CSP Bridge](#assetto-corsa) installed, the game's force feedback
arrives the same way.

**iRacing** is read through the sim's own SDK at 1 kHz: the steering torque
it wants the wheel to hold, plus the session data behind incident points,
per-car max force and engine layout. See [iRacing](#iracing).

**Forza Motorsport and Forza Horizon 4, 5, and 6** also have a direct UDP
Data Out reader that picks up per-tire fields for the surface-texture,
curb-strike and collision effects, and feeds
[Telemetry Based FFB](#telemetry-based-ffb). The Horizon games send this
telemetry once per rendered frame, so it tracks your frame rate, often
well above 60 Hz. All four are auto-detected from SimHub's game profile.

**Farming Simulator 22 and 25** are read through the TF4ALL Enhanced
Telemetry mod the plugin installs for you, at up to 100 Hz. The game
publishes almost nothing on its own, so the mod is what makes
[Farming Simulator](#farming-simulator) force feedback possible at all.

Every other SimHub-supported game runs through SimHub's universal telemetry
feed instead. The plugin works there without a SimHub license, but
unlicensed that feed is capped at 10 Hz, which makes the effects feel coarse.
A licensed copy of SimHub (a small one-time payment) lifts it to 60 Hz, a
big step up in feel.

### Forza setup

Forza needs its telemetry pointed at the plugin (Data Out to `127.0.0.1`,
port `5300` by default), and the plugin can pass a copy on to SimHub so
dashboards and bass shakers keep working. Both are walked through in the
in-app guides (the **?** in the panel's header): Forza setup, and
Forwarding to SimHub.

## Games with native Trueforce

Some titles already ship Trueforce on PC, so the plugin starts on
**Lightsync only** for them: their force feedback and Trueforce are left
alone, and only the wheel's light pattern is set. Switch off the game's
native Trueforce and pick Normal to run the plugin instead, tuning the feel
yourself rather than taking whatever the game hardcodes (and on
Automobilista 2, adding Trueforce that was never really there).

The catch: **a slider at 0 is not off.** Many games keep the Trueforce API
live even at 0, so the plugin fights a channel the game is still driving and
the wheel whines. Only a real on/off switch or a config-file setting fully
releases the wheel.

The plugin catches this itself. The USB capture sees every Trueforce
packet on the wheel, so when a game streams beside the plugin, the plugin
drops to Lightsync only for that game session within a couple of seconds
(the log and the status panel say why) instead of whining beside it. Pick
Normal to try again once the game's Trueforce is off. Without USBPcap there
is nothing to watch, and nothing changes.

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
| Le Mans Ultimate | Not tested | Not tested |

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

## Auto-discovery

On startup the plugin finds the wheel and its USB capture interface by
itself and starts the FFB tap and the Trueforce stream. If the wheel isn't
detected (G HUB still running, USBPcap not installed, wheel unplugged) it
says so in its status and stands down.

## Known limitations

- **Logitech G HUB must stay closed** the entire time the plugin is in
  use, not just at launch. G HUB claims the wheel's HID interface and
  blocks us from talking to it. If G HUB is opened mid-session, close
  it and reload the SimHub plugin to reattach.
- **The Trueforce level dial on the wheel doesn't apply** while the
  plugin is driving Trueforce: the wheel's own intensity scaling stops
  responding to it. Use the plugin's Master Gain and per-effect Gain
  controls to set intensity instead.
- **Rev lights and the screen depend on who carries the force.** The
  wheel's lights and screen share a control channel with the game's force
  feedback, and writing to them while the game's force passes through the
  USB capture cuts that force. So the plugin only adds rev lights and
  drives the screen where it carries the force itself: Telemetry Based
  FFB, iRacing, and Assetto Corsa with the CSP Bridge. For the same
  reason, changing a LIGHTSYNC pattern in a pass-through game interrupts
  the force for a moment, so it is best done parked. A custom driver that
  lifts this is in development.
- **The plugin cannot run alongside a game's own Trueforce.** The wheel's
  Trueforce stream has room for one sender, so in a native-Trueforce game
  the plugin stays on Lightsync only unless the game's Trueforce is
  switched off (see the table above). Running MAIRA at the same time is
  not supported for the same reason.

## FAQ

**Which games does it work with?**
The audio-derived effects work in any game at all, since the plugin captures
the game's audio directly with no SimHub support needed. Games that SimHub
supports additionally get the telemetry-derived effects (engine pulse, gear
shifts, ABS, and so on). Assetto Corsa, iRacing, Forza Motorsport, the
Forza Horizon games and Farming Simulator go further with a higher-fidelity
direct path (see Per-game enhancements).

**Do I need to pay for SimHub?**
SimHub itself is free, and the plugin works without a SimHub license. The
difference is the telemetry rate: unlicensed, games the plugin doesn't read
directly run at only 10 Hz, which makes the effects feel coarse. A licensed
copy lifts that to 60 Hz, which is a big step up in feel. SimHub is cheap and
well worth it. (Assetto Corsa, iRacing, the Forza titles and Farming
Simulator are read directly, so they run at their full rate regardless of
license.)

**Is this anti-cheat safe?**
Yes. The plugin operates entirely outside the game. It never injects code,
reads or modifies game memory, or hooks the game in any way. It only talks
to the wheel over USB (via USBPcap), reads telemetry the game already
broadcasts (SimHub, shared memory, or UDP), and captures game audio through
Windows' own loopback. Switching off a game's native Trueforce is done by
editing a config file or flipping an in-game setting before launch, never by
touching the running game.

**Will it change or replace my normal force feedback?**
Not unless you ask it to. By default the plugin preserves your existing
force feedback and layers haptic effects on top of it; your wheelbase's own
FFB still comes through, with all your usual settings intact. The exception
is [Telemetry Based FFB](#telemetry-based-ffb), which deliberately builds
the steering force from telemetry instead. In the Forza titles that is
opt-in and stays off until you turn it on. In Farming Simulator it runs
automatically, because the centering spring it replaces is all the game
offers. In iRacing, and in Assetto Corsa with the bridge, the force is the
sim's own, carried by the plugin, so it feels exactly as you tuned it.

**Why does it need USBPcap, and is that safe?**
USBPcap is an open-source USB capture driver. The plugin uses it to read the
wheel's own force-feedback traffic off the USB bus so it can mirror that into
the Trueforce stream (this is the FFB pass-through that keeps your normal
force feedback alive). It only looks at the wheel's traffic, it's widely used
and bundled with our installer, and you can uninstall it separately at any
time.

**Do I need Logitech G HUB?**
Some wheels need G HUB launched once to switch into PC mode and expose their
full HID interfaces. If the wheel isn't detected, open G HUB once, let it
recognize the wheel, then close it completely before launching SimHub. G HUB
claims the wheel's HID interface, so it must stay closed while you play. The
wheel can drop out of PC mode after a PC restart or when you unplug it, so
you may need to repeat the open-once-then-close step after each reboot.

**My normal force feedback disappeared, or the plugin says pass-through
is not running.**
Check that SimHub is running as administrator. Reading the wheel's USB
traffic needs it, and without it the pass-through cannot start. Use
SimHub's own Run as administrator setting rather than right-clicking the
exe, then restart SimHub.

**The effects feel weak or light.**
Raise Master Gain and the per-effect Gain in the plugin settings. The
Trueforce dial on the wheel itself does nothing while the plugin is running,
so all intensity is set in the plugin. The G923 is a quieter gear-driven
wheel and usually needs more gain than the G PRO or RS50.

**Can I use this in games that already support Trueforce?**
By default the plugin starts on Lightsync only for native-Trueforce titles,
since the game already provides it, and if both end up streaming at once it
steps aside by itself. But you can switch off the game's native Trueforce
and run the plugin instead, which lets you tune the feel yourself. See
[Games with native Trueforce](#games-with-native-trueforce) for which titles
allow this and how.

## Supporting the project

The plugin is free and stays that way. For anyone who wants to support
it, there is a **[Patreon][patreon]**. It covers the real costs behind
the project: hosting for the community backend, the code-signing
certificate for the upcoming driver, and the time that goes into
building all this. As a thank-you, supporters get cross-device backup
and sync of their full setup (sign in on another PC and your tuning
rides with you) and a spot on the supporters wall in the plugin.
Manual export/import stays available to everyone.

## Community coverage

- **Overtake.gg**, [Logitech's Trueforce Arrives Early in Forza Horizon 6 Thanks to Community-Made SimHub Plugin](https://www.overtake.gg/news/logitechs-trueforce-arrives-early-in-forza-horizon-6-thanks-to-community-made-simhub-plugin.4520/): Detailed news writeup of the plugin bringing Trueforce to Forza Horizon 6 at launch, ahead of any native support from the game.
- **Armando Ramirez**, [Does Logitech TRUEFORCE Actually Matter in Forza Horizon 6?](https://www.youtube.com/watch?v=p5P_Ww14CNg): The first video walkthrough of the plugin in Forza Horizon 6, including custom presets the creator tuned.
- **Revasio**, [French installation tutorial on TikTok](https://www.tiktok.com/@revasio/video/7641185174306180384): A walkthrough of installing and setting up the plugin, narrated in French.

## How it works

The wire protocol (init sequence and ep3 streaming format) was
reverse-engineered by the [mescon Linux driver project][mescon]. This
repo is the Windows-side glue on top of that: a SimHub plugin that opens
the wheel, synthesizes the telemetry/audio-derived effects, handles
per-game tuning, and runs the USBPcap-based FFB tap that mirrors the
game's HID++ output into bytes 6-9 of the Trueforce ep3 stream. That
mechanism (bytes 6-9 as the motor torque target, the rolling window as
an additive overlay on top) has since been independently confirmed by
the mescon driver's own implementation on RS50 hardware.

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
This project is not affiliated with, endorsed by, or sponsored by Logitech.

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
