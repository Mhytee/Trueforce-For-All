# TFFAUsbFilter implementation notes

Companion to HANDOFF.md. This is just me writing down what I actually changed in
the driver and why, since I ended up going a different direction than the
PID/echo approach you sketched, and hit a couple of things along the way worth
knowing about.

## why it's content based instead of PID based

short version: at the USB layer you can't really tell who's writing. the game's
FFB writes and our own writes both come down the stack on a system worker
thread, so `PsGetCurrentProcessId()` comes back as PID 4 (System) for both of
them. that's the exact issue you called out in the handoff (§10/§11) — the
`requestor != owner` check can't tell the two apart, so it either drops both
(dead wheel) or neither. `g_OwnerPid` itself is fine, it's set in
`EvtControlFileCreate` which runs on the caller's real thread. it's only the
per-URB comparison that can't work at that layer.

so instead of dropping writes by who sent them, I drop them by what they are. a
write gets intercepted only if (a) the plugin has actually claimed the wheel
(`g_OwnerPid != 0`, which is reliable) and (b) the bytes look like the game's
HID++ FFB: report `0x11`/`0x12`, device index `0xFF`, feature index ==
`g_FfbFeatureIndex`, function nibble `0x2_`/`0x3_`. that's
`TFFAUsbIsGameFfbWrite()`. everything else just passes through untouched.

what that gets us:

- the game's FFB (the high-rate stuff that jams the shared command processor and
  makes LEDs cut force) never reaches the wheel.
- our Trueforce ep3 stream isn't HID++, so it passes and force is preserved.
- our LED writes (feature `0x09`) pass, so LEDs work, now uncontended.
- the game's root / get_info / notification queries pass, so the wheel answers
  them and the game's HID++ session stays alive with no handshake echo at all.
  that retires §11 step 4, and with it your main suspect for the earlier dead
  wheel (the unanswered handshake).
- nothing in the write path depends on thread context anymore.

it also fails safe. if `g_FfbFeatureIndex` is ever wrong for a wheel, the game's
FFB just doesn't match the classifier and leaks through, so the worst case is
today's contention behavior, never a bricked wheel. every leak gets logged
loudly as `FFB_LEAK_PASS featIdx=0xNN`.

## why not the two approaches suggested

the payload-marker idea felt fragile. the wheel parses our writes, so a marker
risks corrupting them, and "game write = no marker" means any classification
miss forwards game FFB anyway. it also still needs the echo.

the dedicated re-inject IOCTL is clean in theory, but it means routing all
plugin-to-wheel traffic through an IOCTL, including the 1 kHz ep3 stream, and
rebuilding it into URBs on the right pipes inside the driver. that's a big rework
on the hot path, and not worth it when content classification already separates
the one stream we have to drop from everything we have to keep.

content based is smaller, needs no echo, and fails gracefully. the tradeoff is
it's a deviation from your literal sole-writer design, which is why I ran it by
you before turning it into a PR.

## the USB bus fix (this one bit me hard)

worth flagging a bug I hit during bring-up. the original `EvtDeviceAdd` returned
`STATUS_NOT_SUPPORTED` to "opt out" of non-wheel USB devices. turns out a KMDF
filter that fails `EvtDeviceAdd` makes PnP tear down the whole device stack, not
just skip the filter. and since this registers as a USB *class* filter, it loads
on hubs and host controllers too, so failing on a hub killed every device below
it. the first install literally took out all keyboard and mouse input.

the fix is to never fail `EvtDeviceAdd`. always attach as a transparent filter,
and only build the interception queue when the hardware id matches one of our
wheels. non-wheel devices get a filter with no queue, so the framework
auto-forwards all their I/O and the bus stays healthy.

## per-wheel feature index

`g_FfbFeatureIndex` is per wheel. confirmed on hardware: G PRO = `0x0E`, G923
Xbox (C26D/C26E) = `0x0B`. from the capture notes: G923 PS (C266) = `0x08`, RS50
= `0x10`. those last two aren't hardware-confirmed yet, so treat them as best
guesses.

it isn't hardcoded. two things set it: the driver seeds it per wheel in
`EvtDeviceAdd` from the matched hardware id, and the owner can also push the
right index down at runtime with `IOCTL_TFFA_SET_FFB_INDEX`. the plugin resolves
the index from the wheel PID (`WheelDiscovery.FfbFeatureIndexFor`) and pushes it
after it claims the wheel, so even if a default is wrong the resolve corrects it.

## what's actually been tested

this is built, loaded, and working.

I validated it with a little standalone harness (`tffa-fakegame`) instead of a
real game, for a reason I'll get to. the harness resolves the FFB index over
HID++, claims the wheel the same way the plugin does, and injects game-shaped FFB
writes. confirmed on my actual G923:

- inject at `0x0B` (the real index) with the wheel claimed → `INTERCEPTED`, the
  write gets dropped and never reaches the wheel.
- inject at a wrong index → `FFB_LEAK_PASS`, it passes through. so it's dropping
  exactly the one stream and nothing else.
- the real proof: set our re-apply to zero force, and the wheel goes limp even
  while the "game" is commanding force. that confirms we're actually the sole
  writer and the data reaching the wheel is ours.

so the interception and sole-writer are proven on real hardware. I also
confirmed the G923's rev LEDs drive fine over HID++ (page `0x807A`), which was an
open question before.

what's NOT proven is the full thing in a real anti-cheat game. I tried Forza and
it wouldn't launch. EAC, like most kernel anti-cheat, needs Secure Boot on and
refuses to run in test-signing mode, which is exactly the state you need to load
a dev-signed driver. so you can't dev-test this against an EAC title on the same
boot. that's a signing problem, not a driver one. testing it in Forza would need
a Microsoft attestation-signed driver (Secure Boot stays on), or just a sim
without kernel anti-cheat.

Confirmed working offline with Assetto Corsa base game, launched via Content Manager

## building it

there's a `build-driver.ps1` in the driver folder with the whole recipe (WDK
26100, the version pin, Spectre-off for dev, and the 64-bit msbuild needed for
the inf verifier). note the dev build turns Spectre mitigation off, so flip that
back on for a shipping build. the driver is self-test-signed right now; shipping
needs attestation.

## files changed on the driver branch

- `driver/TFFAUsbFilter/driver.c` — content-based intercept, the `EvtDeviceAdd`
  USB-bus fix, per-wheel index default, `IOCTL_TFFA_SET_FFB_INDEX`, and the
  `0x30` function-nibble decode.
- `src/TrueforceForAll.Core/TFFADriverChannel.cs` — decode `0x30` as well as
  `0x20`, and push the resolved index down to the driver.
- `src/TrueforceForAll.Core/WheelDiscovery.cs` — per-wheel FFB index map.
- `src/TrueforceForAll.Plugin/TrueforcePlugin.cs` — resolve the index from the
  wheel PID and push it after claiming.
- `tf4all-driver/` — the build script, the test kit, and the
  `tffa-fakegame` harness for reproducing all of this without a sim.

if any of this becomes a problem or steps on something else it's easy to revert,
but otherwise this is what's going into the PR.
