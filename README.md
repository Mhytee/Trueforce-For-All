# Trueforce For All

**Logitech Trueforce-compatible haptics for any SimHub-supported game.**

Logitech ships Trueforce for only a handful of officially-supported titles. This
plugin makes it work everywhere SimHub does. Built on top of the wire
protocol reverse-engineered by the [mescon Linux driver project][mescon] --
no Logitech SDK, no G HUB integration, no whitelist.

Tested on a GPRO wheel with Assetto Corsa and Wreckfest 2.

The telemetry-driven effects work in any game SimHub can read.
Assetto Corsa gets an enhanced path that reads physics directly from
the game's shared memory at 1 kHz, bypassing SimHub for sharper curb
edges and direct wheel-slip readings; adding more games to that path
is on the roadmap. Games SimHub can't read still work for audio-based
haptics and the FFB spike-reduction filter, just without the
telemetry-derived effect set.

**Bonus: optional FFB spike reduction.**  Some games deliver curb and
collision FFB spikes wildly out of proportion to what's safe or
comfortable. On a strong wheelbase they can be sharp enough to ruin a racing line, or cause real wrist
strain over a session. Assetto Corsa is the worst offender we've seen
and was the original motivation, but the feature works in any game
whose FFB goes through standard HID++ force feedback. iRacing has a
built-in option to soften this; most other games don't. The plugin
taps the game's outgoing FFB on the USB bus and attenuates spikes only,
so curbs land as confident pushes instead of yanks; sustained cornering
load and weight transfer pass through untouched. This still requires
one of the supported Logitech Trueforce wheels in the table below,
since the modified FFB reaches the wheel through the Trueforce
endpoint; support for non-Trueforce wheels would need a different
attenuation point and isn't implemented yet. Useful on its own, even with
all Trueforce effects turned off.

> **Status:** v0.x, actively developed. The plugin is functional today; the
> default presets are still being tuned. Feedback welcome.

## Supported wheels

| Wheel | USB ID |
|---|---|
| Logitech G PRO Racing Wheel (Xbox/PC) | `046D:C272` |
| Logitech G PRO Racing Wheel (PS/PC) | `046D:C268` |
| Logitech RS50 | `046D:C276` |

The G PRO and RS50 use byte-identical Trueforce packets (verified by the
[mescon Linux driver project][mescon]). 
G923 support may come in the future.

## What it does

The plugin runs inside SimHub and drives the wheel's Trueforce haptic motor
in real time, mixing several signal sources:

- **Telemetry-derived effects** synthesized from live game data. AC
  reads the physics shared memory directly at 1 kHz; other games come
  through SimHub at its native data tick.
  
  - **Engine pulse** -- a rumble at the engine's firing frequency, scaled
    by RPM. The signature Trueforce sensation; idle gives a gentle hum,
    pulling toward redline gives meaningful kick.
  - **Gear shift** -- a short low-frequency thud whenever the gear changes.
  - **ABS click** -- configurable haptic when ABS engages.
  - **Road bumps** -- noise gated by vertical acceleration, so curbs and
    rough terrain rumble through the wheel.
  - **Traction loss** -- buzz when grip breaks (wheelspin, lockup, drift)
    derived from the difference between wheel speed and ground speed plus
    a yaw-rate / lateral-G discrepancy check.
    
- **Audio-derived effects** -- WASAPI loopback captures the game's
  audio output (engine, tire, impact sounds) and feeds it into the
  wheel as a low-latency buzz. Lets you feel things the telemetry
  doesn't expose, and works even for games SimHub can't read since
  capture targets the game process directly.
  
- **FFB pass-through with spike reduction.** When a game already drives
  the wheel via standard HID++ force feedback (Assetto Corsa does), the
  plugin transparently taps that signal off the USB bus and mirrors it
  into the Trueforce stream so cornering load coexists with the haptic
  effects above. An optional spike-reduction filter brings AC's
  notoriously over-the-top curb and collision FFB down to comfortable
  levels (see the bonus note above).

All of it is configurable per-game, per-car, via SimHub's settings UI:
master gain, individual effect tuning, sidechain ducking between
continuous and transient effects, and savable preset library.

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

## Install

The easiest path is the bundled installer:

1. Download `TrueforceForAll-Setup.exe` from the [latest release][releases].
2. Close SimHub if it's running.
3. Run the installer. It detects SimHub, copies the plugin files into the
   SimHub install folder, and -- if USBPcap isn't already installed -- runs
   the bundled USBPcap setup automatically.
4. Close Logitech G HUB (it claims the wheel's HID interface).
5. Launch SimHub. Enable "Trueforce For All" in the Plugins list.

The installer is conservative on uninstall: it removes our files but leaves
SimHub, USBPcap, and shared dependencies (HidSharp, NAudio) alone, so other
plugins that share those keep working.

## Requirements

- Windows 10 / 11
- [SimHub](https://www.simhubdash.com/)
- A supported Logitech wheel (table above)
- [USBPcap](https://github.com/desowin/usbpcap) -- bundled with our installer
  if you don't already have it. Used to mirror the game's existing FFB
  signal into the Trueforce stream so the two coexist.
- Logitech G HUB **closed** while playing (it claims the HID interface and
  blocks us from talking to the wheel)

## Known limitations

- **Logitech G HUB must stay closed** the entire time the plugin is in
  use, not just at launch. G HUB claims the wheel's HID interface and
  blocks us from talking to it. If G HUB is opened mid-session, close
  it and reload the SimHub plugin to reattach.
- **The Trueforce level dial on the wheel doesn't apply** while this
  plugin is driving Trueforce. Once we take over the ep3 stream, the
  wheel's own Trueforce intensity scaling stops responding to the dial.
  Use the in-plugin Master Gain and per-effect Gain controls to set
  intensity instead; the [mescon Linux driver project][mescon] hit the
  same limitation and works around it the same way.
- **Validated only on G PRO + AC + Wreckfest 2** so far. Other supported
  wheels (RS50) and other SimHub-supported games should work but
  haven't been tested by us yet. Feedback welcome.

## Build from source

You'd need .NET 8 SDK and (optionally) [Inno Setup 6][inno] to build the
installer.

```powershell
# Plugin and helper
dotnet build src\TrueforceForAll.Plugin\TrueforceForAll.Plugin.csproj -c Release
dotnet publish src\TrueforceForAll.LoopbackHelper\TrueforceForAll.LoopbackHelper.csproj -c Release -r win-x64

# Installer
& "C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\TrueforceForAll.iss
```

Continuous integration is configured at [.github/workflows/release.yml][ci]:
push a `v*` tag and a draft GitHub release with the installer is created
automatically.

## How it works

The wire protocol -- init sequence and ep3 streaming format -- was
reverse-engineered by the [mescon Linux driver project][mescon]. This
repo is the Windows-side glue on top of that: a SimHub plugin that opens
the wheel, the telemetry- and audio-derived effect synthesis, the per-game
tuning, and a USBPcap-based tap that reads the game's outgoing HID++ FFB
target off the USB bus and mirrors it into bytes 6-9 of the Trueforce ep3
stream so cornering load coexists with the synthesized effects.

## License

GPL-2.0-only. See [LICENSE](LICENSE).

The wire protocol and init sequence are derived from the
[mescon Linux driver project][mescon], also GPL-2.0.

## Acknowledgments

- **[mescon/logitech-rs50-linux-driver][mescon]** -- reverse-engineered
  the wheel's driver and wire protocol. This project would not exist
  without their work.
- **[USBPcap][usbpcap]** by Tomasz Mon -- the kernel-mode USB filter that
  lets us tap the wheel's bus traffic for FFB pass-through.
- **[HidSharp][hidsharp]** -- cross-platform HID library used for the
  control-side of wheel communication.
- **[NAudio][naudio]** -- audio I/O library used for the per-process
  loopback capture pipeline.
- **[SimHub][simhub]** -- the host application. This plugin is unofficial
  and not affiliated with the SimHub project.

Logitech, Trueforce, G PRO, and RS50 are trademarks of Logitech. This
project is not affiliated with, endorsed by, or sponsored by Logitech.

[mescon]: https://github.com/mescon/logitech-rs50-linux-driver
[usbpcap]: https://github.com/desowin/usbpcap
[hidsharp]: https://github.com/treehopper-electronics/HIDSharp
[naudio]: https://github.com/naudio/NAudio
[simhub]: https://www.simhubdash.com/
[inno]: https://jrsoftware.org/isinfo.php
[releases]: https://github.com/Mhytee/Trueforce-For-All/releases
[ci]: .github/workflows/release.yml
