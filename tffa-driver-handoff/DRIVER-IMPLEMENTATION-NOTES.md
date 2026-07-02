# TFFAUsbFilter — implementation notes (owner-detection fix)

Companion to `HANDOFF.md`. Records the change made to `TFFAUsbFilter/driver.c`
to resolve the URB-layer owner-detection problem the handoff flagged (§10,
§11), the reasoning for the approach chosen over the two the handoff suggested,
and the exact staged validation to run once the driver is built and loaded.

> Status: **written, NOT yet compiled or loaded.** The build toolchain (WDK +
> its VS extension) is not installed on the dev machine yet, and bring-up will
> happen in a VM with the wheel passed through. Treat everything below as a
> proposal to validate, not a proven result.

## The bug (confirmed by reading `driver.c`)

`TFFAUsbEvtIoInternalDeviceControl` decided whether to intercept a write with:

```c
ULONG requestor = HandleToULong(PsGetCurrentProcessId());
if (writeBuf && writeLen > 0 && owner != 0 && requestor != owner) intercept = TRUE;
```

At the USB-submit layer (`IOCTL_INTERNAL_USB_SUBMIT_URB`) the URB is carried
down by a **system worker thread**, not the thread of the process that issued
the write. So `PsGetCurrentProcessId()` is typically **PID 4 (System)** for
*both* the game's FFB writes and the plugin's own re-issued writes. The
`requestor != owner` test therefore cannot tell them apart: it either
intercepts both (the plugin's own writes get dropped → dead wheel) or neither.
`g_OwnerPid` itself is fine — it is set in `EvtControlFileCreate`, which *does*
run on the caller's own thread — the problem is only the per-URB comparison.

## The fix: intercept by content, not by PID

Drop the PID comparison. Intercept a write iff **(a)** a plugin has claimed the
wheel (`g_OwnerPid != 0`, reliable) **and (b)** the bytes are the game's HID++
FFB stream: report `0x11`/`0x12`, device index `0xFF`, feature index ==
`g_FfbFeatureIndex`, function nibble `0x2_`/`0x3_`. Everything else passes
through untouched. (`TFFAUsbIsGameFfbWrite()`.)

Consequences:

- The game's FFB — the high-rate traffic that starves the shared HID++ command
  processor and makes LED writes cut force — never reaches the wheel.
- Our **Trueforce ep3** stream (not HID++) passes → force is preserved.
- Our **LED** writes (feature `0x09`) pass → LEDs work, now uncontended.
- The game's **root/GET_INFO/notification** queries pass → the wheel answers
  them → the game's HID++ session stays alive **with no handshake echo**. This
  retires handoff §11 step 4 entirely, and with it the leading suspect for the
  earlier dead-wheel result (unanswered handshake).
- No reliance on thread context anywhere in the write path.

### Why not the two approaches the handoff suggested

- **Payload marker in the plugin's own writes.** Fragile: the wheel parses our
  writes, so a marker risks corrupting them, and "game write == absence of
  marker" means any classification miss forwards game FFB anyway. It also still
  needs the echo.
- **Dedicated re-inject IOCTL** (plugin sends all wheel-bound bytes via an
  IOCTL; driver forwards them; intercept everything on the normal path). Clean
  in principle but requires *all* plugin→wheel traffic — including the 1 kHz
  ep3 Trueforce stream — to be re-routed through the IOCTL and rebuilt into
  URBs on the correct pipes in the driver. Large rework, and the ep3 pipe is
  the hot path. Not worth it when content classification already separates the
  one stream we must drop from everything we must keep.

Content-based interception is smaller, needs no echo, and its failure mode is
graceful (see below). The trade-off: it is a **deviation from the handoff's
"literal sole writer" design** — worth raising with the maintainer before it
goes in as a PR, since he may prefer the sole-writer model for reasons beyond
the LED-contention fix.

## The per-wheel feature-index gap (matters for the G923)

`g_FfbFeatureIndex` defaults to `0x0E`, which is confirmed only on the **G PRO**
(RS50 is `0x10`). The **G923's index is unknown.** If it is wrong for the wheel
under test, the game's FFB simply will not match the classifier and will
**leak** to the wheel — i.e. it degrades to today's contention behaviour, never
a dead wheel. That leak is logged loudly as `FFB_LEAK_PASS featIdx=0xNN`, which
is exactly how you discover the right index: read `featIdx` from that line and
set `g_FfbFeatureIndex` to it (a future `IOCTL_TFFA_SET_FFB_INDEX` can let the
plugin push its `WheelDiscovery`-resolved index down instead of hardcoding).

## Staged validation (run in the VM, DebugView capturing)

DebugView: run as admin, Capture ▸ **Capture Kernel** + **Enable Verbose Kernel
Output**. Filter on `TFFAUsbFilter`.

0. **Discover the wheel's FFB index first.** Load the driver, do NOT run the
   plugin (no owner). Drive a game that outputs FFB. Watch `PASS`/`FFB_LEAK_PASS`
   lines and note the feature index (`pad[2]`) on the game's fn `0x2_`/`0x3_`
   writes. Set `g_FfbFeatureIndex` to it, rebuild. (This replaces the handoff
   §11 "first check" PID experiment — the fix no longer depends on the PID
   answer, so confirming PID 4 is now just informational.)
1. **Pass-through (no owner).** Confirm FFB + LEDs behave exactly as with no
   driver. Proves the filter breaks nothing.
2. **Claimed, correct index.** Start the plugin in driver testing mode (`DRIVER`
   access code) so it claims the wheel. Confirm in DebugView: `INTERCEPTED`
   lines for the game's FFB, **no `FFB_LEAK_PASS`**, and steady `PASS` lines for
   our ep3/LED traffic. On the wheel: force still present (via TF stream), LEDs
   drive from RPM, and the LED-vs-FFB cutout is **gone**. This is the
   definition-of-done from handoff §11.
3. **Soak.** Sustained session, no bugcheck, no `FFB_LEAK_PASS`, force + LEDs
   stable.

## Files changed on the `driver` branch

- `driver/TFFAUsbFilter/driver.c` — `g_FfbFeatureIndex`,
  `TFFAUsbIsGameFfbWrite()`, content-based intercept criterion, buffer snapshot
  moved ahead of the decision, content-aware pass-through logging, header.
- `src/TrueforceForAll.Core/TFFADriverChannel.cs` — decode `0x30` as well as
  `0x20` FFB function nibble (handoff §11 step 1). *(separate commit)*
