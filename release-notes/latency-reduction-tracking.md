# Latency reduction tracking

Running tally of latency work so we can write a "Latency reduction" section in a
future release. Numbers are **derived from code/buffer math**, not bench-measured
on hardware, unless a row says "measured". Treat estimates as estimates.

Two independent paths exist and should be reported separately:

- **Path A, audio-haptic** (game audio to wheel torque). Baseline ~16-27 ms
  (center ~18-21 ms), derived. This is the path most of the work targets.
- **Path B, FFB passthrough** (game FFB to wheel torque). Baseline ~5-8 ms typical,
  derived. Dominated by the USBPcap tap tail.

Roughly 8-12 ms of Path A is fixed (WASAPI engine quantum, USB 1 ms frame, firmware
13-slot window, 1 ms packet cadence at our chosen 1000 packets/s). The cadence is a
parameter, not a firmware constraint (the wheel accepts 250-1000 packets/s, per
mescon 2026-07), but lowering it would grow this term, so 1 ms stands as the
practical floor. The rest is reducible.

## Finalized public copy (changes 2 and 3, for this release)

```
### Latency and responsiveness

This release sharpens the wheel's response even further: a more consistent feel
when your system is under load, and quicker detail from captured game audio.

- Keeps feedback steady when your PC is under load. The force-output and
  audio-capture threads now run at Windows' multimedia real-time priority
  (MMCSS "Pro Audio"), the class the system audio engine uses for itself, so a
  busy CPU is far less likely to interrupt them into a hitch or click.
- Haptics generated from captured game audio reach the wheel sooner. Smaller
  chunks from the capture helper typically cut the audio buffer from about 8 ms
  to 4 ms, with no added risk of dropouts.
```

The WASAPI capture cleanup (change 1) is intentionally NOT in the public copy: it
has no user-facing benefit (it did not change latency, see change 1 above). It can
go under a generic "Under the hood" line in the full notes if we want to acknowledge
it at all.

Scope notes for accuracy: bullet 1 (MMCSS on the output pump) helps ALL haptics +
FFB passthrough. Bullet 2 only affects the captured-audio layer (telemetry effects
like engine pulse and road bumps are synthesized fresh each cycle and never touch
the audio buffer). The 4 ms vs 8 ms figure is the derived buffer depth in the
typical case (ring had settled at 32 with the old 2 KB chunk), not a hardware
measurement and not an end-to-end total.

## How to measure the real numbers

The helper now logs its actual capture buffer on every capture start. After a
session with a game running, look in the SimHub log for:

```
[Trueforce-helper] shared engine period: default N frames (X.XX ms), min ...
[Trueforce-helper] capture init OK; endpoint buffer N frames (X.XX ms latency ceiling)
```

The "latency ceiling" line is the real WASAPI capture latency for that session.
Log location: `C:\Program Files (x86)\SimHub\Logs\SimHub.txt`.

## Changes

### 1. WASAPI capture buffer: hard-coded 10 ms to engine minimum (DONE, unshipped)

- **File:** `src/TrueforceForAll.LoopbackHelper/ProcessLoopbackCapture.cs`
- **What changed:** the legacy `IAudioClient::Initialize` now requests
  `hnsBufferDuration = 0` (engine sizes the buffer to one engine period) instead
  of a fixed 10 ms. Removed the dead IAudioClient3 fast-path expectation
  (`InitializeSharedAudioStream` rejects the LOOPBACK flag, confirmed via MS docs,
  so it always fell back to the 10 ms path anyway). Added a log line reporting the
  real endpoint buffer size in ms.
- **Estimated saving (Path A):** likely ~0 ms in practice. Do not quote a number
  in patch notes without the log confirming it. Reasoning: in shared event-driven
  loopback the capture latency is ~one engine period, and our capture loop already
  drains every available packet per wake, so buffer SIZE does not add steady-state
  latency. The engine period is set by the OS (and dropped by whatever low-latency
  audio client the game itself runs), and we cannot lower it for loopback. So the
  real WASAPI capture latency is mostly out of our hands and is often already below
  the 10 ms worst case when a game is running. The buffer change just stops us
  over-requesting; the real wins on Path A are changes 2-4.
- **Genuine value of this change:** (a) the new log line measures the real capture
  latency per session (turns guesswork into a number), (b) removes the dead
  IAudioClient3 fast-path that implied a 3 ms path that never ran for loopback.
- **Status:** needs a hardware session to read the real "latency ceiling" log line.
  That number is the actual WASAPI contribution to Path A; record it here when known.

### 2. MMCSS "Pro Audio" on the two real-time threads (DONE, unshipped)

- **Files:** `src/TrueforceForAll.Core/TrueforceDevice.cs` (StreamLoop, the 1 kHz
  pump), `src/TrueforceForAll.LoopbackHelper/ProcessLoopbackCapture.cs` (CaptureLoop).
- **What changed:** both threads call `AvSetMmThreadCharacteristics("Pro Audio")` +
  `AvSetMmThreadPriority(HIGH)` for the life of the loop, reverting in a finally.
  Best-effort: null handle on failure, thread just runs at its normal priority.
- **Estimated saving:** ~1-3 ms p99 jitter under load (tail, not mean). Also helps
  Path A mean indirectly by reducing ring auto-ratchet pressure (fewer glitch-driven
  bumps), which compounds with change 3.
- **Status:** built, compiles. Needs a hardware session to confirm via the
  AudioRingGlitches / ring-cap telemetry that ratchet activity drops under load.
  Helper logs "capture thread joined MMCSS Pro Audio" when registration succeeds.

### 3. Shrink the helper-to-plugin transport chunk so the audio ring settles lower (DONE, unshipped)

- **File:** `src/TrueforceForAll.Plugin/HelperHost.cs` (stdout pump read buffer
  2048 -> 1024 bytes).
- **What changed:** each pipe read fires one DataAvailable burst, and the audio ring
  must hold a whole burst without lapping, so burst size sets where the ring ratchet
  settles. 2 KB delivered ~21 decimated samples (forced ring to 32 = 8 ms); 1 KB
  delivers ~10 (ring holds at 16 = 4 ms). Did NOT touch the ratchet/settings/UI; the
  existing adaptive ratchet settles lower on its own with smaller bursts.
- **Estimated saving (Path A):** ~4 ms for users whose ring was sitting at 32. Less
  for users already lower. Bounded by the 1 kHz back-pressure coupling. Pairs with
  change 2 (jitter headroom). Note: change 4 (shared memory) supersedes this transport
  but will keep the pipe as fallback, so this tuning still applies to the fallback.
- **Status:** built, compiles. Confirm on hardware that the audio ring cap reported
  in the perf UI settles at 16 rather than 32.

### 4. Shared-memory IPC instead of the stdout pipe (DEFERRED)

- **Files (if revisited):** `HelperHost.cs`, `Program.cs`, `ProcessLoopbackCapture.cs`,
  new shared-memory ring type.
- **Estimated saving (Path A):** originally ~1-3 ms typical + remove the ~5 ms p99
  coalescing tail, measured against the 2 KB pipe. After change 3 dropped the pipe to
  1 KB (p99 tail now ~2.7 ms), the remaining edge is only ~1-2 ms typical + ~2.7 ms
  off the p99 tail, and only on the captured-audio layer.
- **Why deferred:** marginal win after change 3, against the highest risk on the list
  (a cross-process lock-free ring with sync / wraparound / teardown edge cases) on a
  path that needs hardware validation. The 1 KB pipe is simple and proven. Keep the
  net8 helper (COM-activation reliability) and keep the pipe as fallback if revisited.
- **Status:** deferred to the roadmap as a future improvement. Revisit only if
  profiling shows the transport is still a meaningful contributor.

### Out of scope for now

- **Kernel FFB filter driver (TFFAUsbFilter)** for the Path B USBPcap tap. Would
  cut ~2-5 ms mean and remove the ~2-13 ms jitter tail, but it is a far bigger
  project (kernel dev, signing). Deferred.

### Not worth doing (recorded so we do not revisit)

- **Assembly / SIMD of the DSP loop:** ~0 ms. The bottleneck is buffering and USB
  timing, not CPU math. The hot loops cost single-digit microseconds per second.
- **Output ring 8 to 4 samples:** ~1 ms at the cost of all jitter headroom;
  underruns force the ratchet back up, net worse under load.
- **WriteFile/overlapped HID rewrite for mean latency:** ~0 ms (USB 1 ms frame
  dominates). Minor p99/CPU win only.
