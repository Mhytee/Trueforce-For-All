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

The G PRO and RS50 use byte-identical Trueforce packets, so the haptic
layer works on both. The plugin keeps a game's normal force feedback alive
by tapping it off the USB bus and mirroring it into the haptic stream. The
tap resolves the wheel's HID++ force-feedback feature index automatically,
so both the G PRO and the RS50 get Trueforce haptics and their native game
force feedback at the same time.

G923 support may come in the future.

## What it does

The plugin runs inside SimHub and drives the wheel's Trueforce haptic motor
in real time, mixing several signal sources:

- **Telemetry-derived effects** synthesized from live game data.

  - **Engine pulse**: rumble at the engine's firing pattern, derived
    from RPM and cylinder count (auto-detected per car when possible).
    Idle gives a gentle hum; higher RPM lifts both pitch and intensity.
  - **Gear shift**: a short low-frequency thud whenever the gear changes.
  - **ABS click**: configurable haptic when ABS engages.
  - **Pit limiter**: configurable pulsing buzz while the limiter is
    engaged.
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

- **Audio-derived effects**: WASAPI loopback captures the game's
  audio output (engine, tire, impact sounds) and feeds it into the
  wheel as low-latency haptics. Lets you feel things the telemetry
  doesn't expose, and works even for games which do not output telemetry data
  since capture targets the game process directly.

- **FFB pass-through.** When a game already drives the wheel via standard
  HID++ force feedback (Assetto Corsa does), the plugin transparently taps
  that signal off the USB bus and mirrors it into the Trueforce stream so
  cornering load coexists with the haptic effects above.

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
pass through untouched. Works in any game whose FFB goes through
standard HID++. Requires one of the supported Trueforce wheels above
(the attenuated signal reaches the wheel through the Trueforce
endpoint). Useful on its own, even with all our other effects turned
off.

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

By default the plugin runs on SimHub's universal 60 Hz telemetry feed, which carries the standard fields all the core effects need.
SimHub is free but the 60 Hz feed requires a licensed copy of SimHub, which carries a small one-time payment. 

Some titles read directly from the game's telemetry, bypassing SimHub's limitations and the need for a licensed copy (Assetto Corsa, Forza Horizon 4, 5, and 6).

**Assetto Corsa** has a dedicated path: shared memory is read directly at AC's native 333 Hz physics rate (polled at 1 kHz so events are seen within 1 ms of being written). The higher rate makes curb collisions, road-bumps, traction-loss and other haptic effects noticeably sharper and more responsive than SimHub's 60 Hz feed can deliver.

**Forza Horizon 4, 5 and 6** also have a direct UDP Data Out reader that picks up per-tire fields for the surface-texture, rumble strips, and curb collision effects. This additional surface information is updated at 60 Hz but allows for more depth in surface detail effects than some other titles may offer. All three are auto-detected from SimHub's game profile, so the only setup is pointing Forza's DATA OUT at the listener's IP and port.

Additional per-title enhancements/bypasses will be added over time.

## iRacing + MAIRA

iRacing ships native Trueforce, so this plugin normally stays out of its
way. The exception is **Marvin's Awesome iRacing App (MAIRA)**: running
MAIRA on a Logitech wheel requires setting `loadTrueForceAPI=0` in
iRacing's `app.ini`, which turns iRacing's Trueforce fully off. This
plugin can restore the Trueforce textural haptics for MAIRA users,
running alongside MAIRA's force feedback without conflict. Step-by-step
setup is in [docs/iracing-maira-trueforce.md](docs/iracing-maira-trueforce.md).

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
  iRacing with MAIRA** so far. Other SimHub-supported games should work
  but haven't been tested by us yet. Feedback welcome.

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
- **Caleb Pearson**: reported that the plugin was not working on the
  RS50, exported the bus-capture logs that helped pinpoint the cause, and
  validated the fix on his hardware. Without his report the RS50 issue
  would have gone unnoticed. He also discovered and confirmed that the
  plugin brings Trueforce back to iRacing when running MAIRA.

Logitech, Trueforce, G PRO, and RS50 are trademarks of Logitech. This
project is not affiliated with, endorsed by, or sponsored by Logitech.

[mescon]: https://github.com/mescon/logitech-rs50-linux-driver
[usbpcap]: https://github.com/desowin/usbpcap
[acshmem]: https://github.com/mdjarv/assettocorsasharedmemory
[hidsharp]: https://github.com/treehopper-electronics/HIDSharp
[naudio]: https://github.com/naudio/NAudio
[manteomax]: https://www.manteomax.com/
[simhub]: https://www.simhubdash.com/
[releases]: https://github.com/Mhytee/Trueforce-For-All/releases
