# Trueforce For All

**Logitech Trueforce-compatible haptics for any SimHub-supported game.**

While official support for Trueforce has been steadily growing, there are still many major titles which are yet to receive support or will never get support. This
plugin fills those gaps by allowing it to work everywhere SimHub does. Built on top of the wire
protocol reverse-engineered by the [mescon Linux driver project][mescon].

> **Status:** Actively in development. The plugin is functional today; the
> default presets are still being tuned. Feedback welcome.
>
> Note: My Reddit account was immediately banned after sharing this and all of my posts have been removed across several subreddits. If you find this useful, sharing it outside Reddit (Discord servers, sim-racing forums, YouTube) helps other drivers find it. I've appealed the ban and have yet to hear back.

For the record on what this project is: Original Windows code built on top of a wire protocol reverse-engineered by the [mescon Linux driver project][mescon] from USB traffic. No Logitech source, firmware, or proprietary assets are used or redistributed. GPL-2.0, same as mescon's work. Logitech trademarks are acknowledged in the section below; this project is unaffiliated.

## Supported wheels

| Wheel | USB ID | Status |
|---|---|---|
| Logitech G PRO Racing Wheel (Xbox/PC) | `046D:C272` | Full: Trueforce haptics + game FFB pass-through |
| Logitech G PRO Racing Wheel (PS/PC) | `046D:C268` | Full: Trueforce haptics + game FFB pass-through |
| Logitech RS50 | `046D:C276` | Full: Trueforce haptics + game FFB pass-through |
| Logitech G923 (PS/PC) | `046D:C266` | Full: Trueforce haptics + game FFB pass-through |
| Logitech G923 (Xbox/PC) | `046D:C26D`, `046D:C26E` | Full: Trueforce haptics + game FFB pass-through |

The G PRO and RS50 use byte-identical Trueforce packets, so the haptic
layer works on both. The plugin keeps a game's normal force feedback alive
by tapping it off the USB bus and mirroring it into the haptic stream. The
tap resolves the wheel's HID++ force-feedback feature index automatically,
so both the G PRO and the RS50 get Trueforce haptics and their native game
force feedback at the same time.

The G923 was decoded from USB captures (Assetto Corsa Competizione and
Forza Horizon 5). Its Trueforce haptic motor uses the same protocol as
the G PRO. For non-Trueforce games the G923 exposes its force feedback
on a different path than the G PRO and RS50 (a DirectInput-style report
on a separate USB endpoint), which the plugin taps and mirrors into the
haptic stream.

Both G923 variants are confirmed working by owners: Trueforce effects
and in-game force feedback together. The PlayStation and Xbox variants
deliver force feedback over different USB paths (the Xbox path was
decoded from a community-submitted capture and added in 0.1.17); the
plugin handles both. The G923 is a quieter gear-driven wheel than the
G PRO and RS50, so if it feels light, raise master or Trueforce gain.

## What it does

The plugin runs inside SimHub and drives the wheel's Trueforce haptic motor
in real time. Everything rides on top of your real force feedback, which it
preserves via FFB pass-through. It mixes:

- **FFB pass-through (the foundation).** Driving the Trueforce motor would
  otherwise silence the game's own force feedback, so the plugin taps that
  signal off the USB bus and folds it back into the Trueforce stream. Your
  real cornering load, weight transfer and kerb forces keep coming through
  underneath every effect below, in any game whose force feedback uses
  standard HID++ (effectively all of them on these wheels).

- **Telemetry-derived effects** synthesized from live game data.

  - **Engine pulse**: rumble at the engine's firing pattern, derived
    from RPM and cylinder count (auto-detected per car when possible).
    Idle gives a gentle hum; higher RPM lifts both pitch and intensity.
  - **Gear shift**: a short low-frequency thud whenever the gear changes.
  - **ABS click**: configurable haptic when ABS engages.
  - **Pit limiter**: configurable pulsing buzz while the limiter is
    engaged.
  - **Rev limiter**: a hard buzz at the shift point and on the limiter,
    independent of the engine pulse. Fires at the car's real redline
    where the game reports one (with an optional early/late offset),
    otherwise at a percentage of the rev limit you set. On by default.
  - **DRS**: short chirp on the rising edge when the wing opens, plus an
    optional sustained flutter while DRS stays active. Silent on games
    that don't expose the flag.
  - **Road bumps**: triggered by vertical acceleration so curbs and
    rough terrain rumble through the wheel. On Forza, the per-tire
    surface-rumble and rumble-strip fields are read directly for a
    richer, more accurate continuous road feel on top of the heave
    channel.
  - **Traction loss**: tire-screech haptics when grip breaks (wheelspin,
    lockup, drift). Read directly from per-wheel slip in games that
    expose it (AC); inferred on the SimHub universal path from
    wheel-vs-ground speed plus a yaw-rate / lateral-G discrepancy check.
  - **Collision**: amplitude-scaled thud on impact, with a soft-knee
    curve so harder hits feel stronger without becoming unsafe, plus a
    refractory window so multi-frame crashes don't stutter.
  - **Airborne ducking**: when the car leaves the ground, the chosen
    effects cut out so jumps feel weightless, then return on landing.
    Detected from wheel load / suspension (AC and the Forza Horizon
    games). On by default.
  - **Stationary spring**: optional centering force so a parked or
    crawling car has some weight at the wheel instead of going limp,
    fading out as speed builds (AC and Forza Horizon; off by default).

- **Audio-derived effects**: WASAPI loopback captures the game's
  audio output (engine, tire, impact sounds) and feeds it into the
  wheel as low-latency haptics. Lets you feel things the telemetry
  doesn't expose, and works even for games which do not output telemetry data
  since capture targets the game process directly.

All of it is configurable per-game, per-car, via SimHub's settings UI:
master gain, individual effect tuning, sidechain ducking between
continuous and transient effects, and savable preset library.

## FFB spike reduction

Some games (Assetto Corsa being the worst offender we've seen) deliver
curb and collision FFB spikes wildly out of proportion to what's safe or
comfortable. On a strong wheelbase they can ruin a racing line or cause
real wrist strain over a session. iRacing has a built-in softener; most
other games don't. The plugin taps the game's outgoing FFB on the USB
bus and attenuates spikes only, so curbs land as confident pushes
instead of yanks while sustained cornering load and weight transfer
pass through untouched. Useful on its own, even with all our other
effects turned off.

## Install

The easiest path is the bundled installer:

1. Download `TrueforceForAll-Setup.exe` from the [latest release][releases].
2. Close SimHub if it's running.
3. Run the installer. It detects SimHub, copies the plugin files into the
   SimHub install folder, and (if USBPcap isn't already installed) runs
   the bundled USBPcap setup automatically.
4. Close Logitech G HUB (it claims the wheel's HID interface).
5. Launch SimHub. The plugin auto-enables on first run.

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

## Per-game enhancements

A few titles are read directly from the game's own telemetry, at a much
higher rate than SimHub's 60 Hz cap. That makes their effects sharper and
more responsive, and it needs no SimHub license:

**Assetto Corsa** has a dedicated path: shared memory is read directly at
AC's native 333 Hz physics rate (polled at 1 kHz so events are seen within
1 ms of being written). The higher rate makes curb collisions, road-bumps,
traction-loss and other haptic effects noticeably sharper and more
responsive than SimHub's 60 Hz feed can deliver.

**Forza Horizon 4, 5 and 6** also have a direct UDP Data Out reader that
picks up per-tire fields for the surface-texture, rumble strips, and curb
collision effects. These games send this telemetry once per rendered frame,
so it tracks your frame rate (often well above 60 Hz), giving more depth in
surface detail effects than some other titles offer. All three are
auto-detected from SimHub's game profile, so the only setup is pointing
Forza's DATA OUT at the listener's IP and port.

Additional direct-read titles will be added over time.

Every other SimHub-supported game runs through SimHub's universal telemetry
feed instead. The plugin works there without a SimHub license, but
unlicensed that feed is capped at 10 Hz, which makes the effects feel coarse.
A licensed copy of SimHub (a small one-time payment) lifts it to 60 Hz, a
big step up in feel.

### Using a UDP game alongside SimHub (dashboards, bass shakers, Buttkicker)

This plugin sits between the game and SimHub: the game sends telemetry to
the plugin, and the plugin passes a copy to SimHub, so both work. Forza
and F1 only send to one place, which is why the game must point at this
plugin, not at SimHub. Anything SimHub drives (dashboards, ShakeIt bass
shakers, a Buttkicker, arduino devices) keeps working through the relay.
In the Forza (or F1) section of the plugin settings:

1. In SimHub, open Games then the game and note the UDP port it uses. If
   SimHub has an "automatically configure" option for that game's data
   output, turn it off, or it will keep overwriting the setting you make
   in step 2.
2. In the game's telemetry settings, set the data-out IP to `127.0.0.1`
   and the data-out port to this plugin's listen port (the Port field in
   that section, default 5300). This must be a different number than
   SimHub's port from step 1.
3. Enable "Also forward to SimHub", set the forward host to `127.0.0.1`
   and the forward port to SimHub's port from step 1.
4. Drive for a moment and check the "Forwarded:" line in that section.
   Once it shows packets, SimHub's dashboards and bass shakers are back.

The result is `game → this plugin → SimHub`, so haptics from this plugin
and everything SimHub drives both work at the same time.

## iRacing + MAIRA

iRacing ships native Trueforce, so this plugin normally stays out of its
way. The exception is **Marvin's Awesome iRacing App (MAIRA)**: running
MAIRA on a Logitech wheel requires setting `loadTrueForceAPI=0` in
iRacing's `app.ini`, which turns iRacing's Trueforce fully off. This
plugin restores the Trueforce textural haptics for MAIRA users, running
alongside MAIRA's force feedback without conflict. No MAIRA changes are
needed. Step-by-step setup is in
[docs/iracing-maira-trueforce.md](docs/iracing-maira-trueforce.md).

## Games with native Trueforce

Some titles already ship Trueforce on PC, so the plugin defaults itself
**off** for them. Switch off the game's native Trueforce and you can run
the plugin instead, tuning the feel yourself rather than taking whatever
the game hardcodes (and on Automobilista 2, adding Trueforce that was never
really there).

The catch: **a slider at 0 is not off.** Many games keep the Trueforce API
live even at 0, so the plugin fights a channel the game is still driving and
the wheel whines. Only a real on/off switch or a config-file setting fully
releases the wheel.

| Game | How to disable native Trueforce | Plugin takes over? |
|---|---|---|
| iRacing | `app.ini` `loadTrueForceAPI=0` (see iRacing + MAIRA above) | Yes |
| Dirt Rally 2.0 | In-game Trueforce on/off switch | Yes |
| GRID (2019) | In-game Trueforce on/off switch | Yes |
| Automobilista 2 | Steam launch option `disableTF` (try `-disableTF` if that fails) | Likely, untested |
| Assetto Corsa Competizione | Slider only, no off switch found | No, stays live |
| Assetto Corsa EVO | Slider only, no off switch found | No, stays live |
| Assetto Corsa Rally | Slider only, no off switch found | No, stays live |

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

 On startup the plugin:

1. Enumerates connected HID devices, finds the wheel's Trueforce interface
   (`MI_02`, vendor usage page `0xFFFD`).
2. Enumerates USBPcap interfaces and parses injected device descriptors to
   find which root hub the wheel is on and what USB address the OS assigned
   it this boot.
3. Starts the FFB tap and Trueforce stream automatically.

If the wheel isn't detected (G HUB still running, USBPcap not installed,
wheel unplugged) the plugin logs a clear status message and disables itself
gracefully

## Known limitations

- **Logitech G HUB must stay closed** the entire time the plugin is in
  use, not just at launch. G HUB claims the wheel's HID interface and
  blocks us from talking to it. If G HUB is opened mid-session, close
  it and reload the SimHub plugin to reattach.
- **The Trueforce level dial on the wheel doesn't apply** while this
  plugin is driving Trueforce. Once we take over the ep3 stream, the
  wheel's own Trueforce intensity scaling stops responding to the dial.
  Use the in-plugin Master Gain and per-effect Gain controls to set
  intensity instead.
- **Validated on G PRO and RS50 + AC + Wreckfest 2 + FH5 + FH6 +
  iRacing with MAIRA** so far. Other games should work
  but haven't been tested by us yet. Feedback welcome.

## FAQ

**Which games does it work with?**
The audio-derived effects work in any game at all, since the plugin captures
the game's audio directly with no SimHub support needed. Games that SimHub
supports additionally get the telemetry-derived effects (engine pulse, gear
shifts, ABS, and so on). Assetto Corsa and Forza Horizon 4/5/6 go further
with a higher-fidelity direct path (see Per-game enhancements).

**Do I need to pay for SimHub?**
SimHub itself is free, and the plugin works without a SimHub license. The
difference is the telemetry rate: unlicensed, games the plugin doesn't read
directly run at only 10 Hz, which makes the effects feel coarse. A licensed
copy lifts that to 60 Hz, which is a big step up in feel. SimHub is cheap and
well worth it. (Assetto Corsa and Forza Horizon 4/5/6 are read directly from
the game, so they run at their full rate regardless of license.)

**Is this anti-cheat safe?**
Yes. The plugin operates entirely outside the game. It never injects code,
reads or modifies game memory, or hooks the game in any way. It only talks
to the wheel over USB (via USBPcap), reads telemetry the game already
broadcasts (SimHub, shared memory, or UDP), and captures game audio through
Windows' own loopback. Switching off a game's native Trueforce is done by
editing a config file or flipping an in-game setting before launch, never by
touching the running game.

**Will it change or replace my normal force feedback?**
No. The plugin preserves your existing force feedback and layers haptic
effects on top of it. Your wheelbase's own FFB still comes through, with all
your usual settings intact.

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

**The effects feel weak or light.**
Raise Master Gain and the per-effect Gain in the plugin settings. The
Trueforce dial on the wheel itself does nothing while the plugin is running,
so all intensity is set in the plugin. The G923 is a quieter gear-driven
wheel and usually needs more gain than the G PRO or RS50.

**Can I use this in games that already support Trueforce?**
By default the plugin stays off for native-Trueforce titles, since the game
already provides it. But you can switch off the game's native Trueforce and
run the plugin instead, which lets you tune the feel yourself. See
[Games with native Trueforce](#games-with-native-trueforce) for which titles
allow this and how.

## Community coverage

- **Armando Ramirez**, [Does Logitech TRUEFORCE Actually Matter in Forza Horizon 6?](https://www.youtube.com/watch?v=p5P_Ww14CNg): The first video walkthrough of the plugin in Forza Horizon 6, including custom presets the creator tuned.
- **Revasio**, [French installation tutorial on TikTok](https://www.tiktok.com/@revasio/video/7641185174306180384): A walkthrough of installing and setting up the plugin, narrated in French.

## How it works

The wire protocol (init sequence and ep3 streaming format) was
reverse-engineered by the [mescon Linux driver project][mescon]. This
repo is the Windows-side glue on top of that: a SimHub plugin that opens
the wheel, synthesizes the telemetry/audio-derived effects, handles
per-game tuning, and runs the USBPcap-based FFB tap that mirrors the
game's HID++ output into bytes 6-9 of the Trueforce ep3 stream.

## License

GPL-2.0-only. See [LICENSE](LICENSE).

The wire protocol and init sequence are derived from the
[mescon Linux driver project][mescon], also GPL-2.0.

## Acknowledgments

- **[mescon/logitech-rs50-linux-driver][mescon]**: reverse-engineered
  the wheel's driver and wire protocol. This project would not exist
  without their work.
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
- **Caleb Pearson**: reported that the plugin was not working on the
  RS50, exported the TF4ALL logs that helped pinpoint the cause, and
  validated the fix on his hardware. Without his report the RS50 issue
  would have gone unnoticed. He also discovered and confirmed that the
  plugin brings Trueforce back to iRacing when running MAIRA.
- **Svenmoor**: tested the plugin against a range of native-Trueforce
  titles and mapped which ones have a true Trueforce off switch (so the
  plugin can take over cleanly) versus which only expose an intensity
  slider, which populated the "Games with native Trueforce" table above.

Logitech, Trueforce, G PRO, RS50, and G923 are trademarks of Logitech.
This project is not affiliated with, endorsed by, or sponsored by Logitech.

[mescon]: https://github.com/mescon/logitech-rs50-linux-driver
[usbpcap]: https://github.com/desowin/usbpcap
[acshmem]: https://github.com/mdjarv/assettocorsasharedmemory
[hidsharp]: https://github.com/treehopper-electronics/HIDSharp
[naudio]: https://github.com/naudio/NAudio
[manteomax]: https://www.manteomax.com/
[simhub]: https://www.simhubdash.com/
[releases]: https://github.com/Mhytee/Trueforce-For-All/releases
[armando]: https://www.youtube.com/watch?v=p5P_Ww14CNg
[revasio]: https://www.tiktok.com/@revasio/video/7641185174306180384
