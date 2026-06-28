# TrueforceForAll wheel-filter driver: context and handoff

This package contains a Windows kernel filter driver (two variants), a user-mode
bridge, and the scripts to build, sign, install, and test it. The goal is to
let TrueforceForAll (TF4ALL) become
the single writer to a Logitech wheel, which fixes a LED-versus-force-feedback
contention problem that cannot be solved from user mode alone.

Sections 1 and 2 are the point of all this: what we are building and why. Read
those first. The Status section right after them is the honest state of the code:
this work was stopped mid-test, so several conclusions are working theories, not
proven facts, and are labeled as such. Sections 3 and 4 cover the two drivers and
where each stands; sections 5 onward are the practical setup: build, sign,
install, run the tests, uninstall.

> Naming note: the project is **TF4ALL**; the code keeps a legacy `TFFA` prefix,
> so `TFFAUsbFilter`, `\\?\TFFAControl`, `IOCTL_TFFA_*`, and the rest all refer to
> this same project.

> Plain-language note on kernel terms: a "filter driver" is a small piece of
> code Windows inserts into the chain that carries data to a device, so every
> message to the device passes through it first. It can read, change, or drop
> those messages. Nothing here injects into a game or touches game memory; it
> sits below the game, at the operating-system-to-device boundary.

---

## 1. The problem this solves

TF4ALL adds Logitech "Trueforce" style audio-haptic effects (road texture, engine
pulse, etc.) plus RPM shift LEDs to wheels, in games that have no native
Trueforce support. The wheel hardware has two relevant input paths:

- The **non-Trueforce endpoint**: the normal force-feedback (FFB) path. The
  game writes its FFB here. The wheel's rev/shift **LEDs are also driven here**,
  over the same Logitech HID++ control channel, into the same on-wheel command
  processor.
- The **Trueforce endpoint**: a separate audio-haptic stream (a 1 kHz packet
  stream on a dedicated HID interface) that can also carry a motor-force target.

How TF4ALL works today (the shipping, USBPcap-based path):

1. It **reads** the game's FFB by passively sniffing the non-Trueforce endpoint
   (USBPcap).
2. It **re-applies** that force through the Trueforce endpoint (the `cur` field
   of the audio-haptic packet), with its own effects overlaid.

That part works. The unsolved problem is contention:

- The game keeps writing FFB to the non-Trueforce endpoint (TF4ALL never told it
  to stop, and cannot from user mode).
- TF4ALL's LED writes go to that **same** endpoint and command processor.
- The game's high-rate FFB traffic and the LED writes collide on that single
  on-wheel command processor. The symptom is the LEDs causing the force to cut
  out or go limp, or the LEDs refusing to update smoothly.

Key insight: the bytes we need to read (the game's FFB) and the traffic we need
to quiet down (to free the LED path) are the **same bytes**. A passive sniffer
can read but cannot remove them. To stop the game's writes from reaching the
wheel, you must sit **in** the path, not beside it. That is what this driver is
for.

Evidence captures of the problem (USBPcap traces of the wheel going limp when
LEDs write, plus a `gpro_leds.py` analyzer) were taken during diagnosis but are
not part of this package. The symptom is the current shipping behavior anyway:
LED writes on the shared HID++ pipe cut the force out, so it is directly
observable without them.

---

## 2. The architecture the driver enables

Make TF4ALL the **only** process that ever writes to the wheel:

1. The driver attaches to the wheel and exposes a small control device,
   `\\?\TFFAControl`. Whatever process opens that control device becomes the
   wheel's **owner**. TF4ALL opens it on startup.
2. While an owner is set:
   - The **owner's** own writes pass straight through to the wheel.
   - **Every other process's** writes to the wheel (the game's FFB, anything
     else) are **intercepted**: the bytes are handed up to TF4ALL through an
     inverted-call channel (`IOCTL_TFFA_RECV`), and the original write is
     completed successfully **without ever reaching the wheel**.
3. TF4ALL decides, per message, what to actually send to the wheel:
   - Game FFB writes: extract the motor-force target and feed it into the
     Trueforce-endpoint stream. Do **not** echo the raw FFB to the wheel (the
     force now comes from the Trueforce path).
   - LED writes: TF4ALL owns the LEDs, so it sends its own LED stream.
   - Setup / query / handshake messages (root queries, get-info,
     notifications): TF4ALL **echoes** these back to the wheel via its own HID
     handle so the wheel still answers and the game's HID++ session stays
     healthy.

Net effect: the wheel only ever receives writes from one process (TF4ALL), so the
LED-versus-FFB contention on the on-wheel command processor disappears, while
force still works through the Trueforce endpoint. The game's view is unchanged:
its writes return success and its queries get answered.

**Safe by default:** if no process owns the control device, the driver is a pure
pass-through and every write reaches the wheel exactly as before. If TF4ALL
crashes or exits, Windows closes its handle, ownership is released
automatically, and the driver reverts to pass-through. There is no state in
which a dead plugin leaves the wheel hijacked.

---

## Status: where the code actually stands

(Read this after sections 1 and 2.) The kernel-driver work is **unfinished work
that was stopped mid-test.** It proves the idea is buildable and gets most of the
way there, but it does **not** yet prove the idea works end to end. Treat the
layer/driver choice and the "why the wheel went dead" explanations as **working
theories, not settled facts.**

**What is actually true (verified in code or confirmed by the author):**

- The goal and architecture are sound and most of the plumbing exists (sections
  1, 2, 10).
- Both driver variants build. Ownership claim/release works. Interception
  (intercept the write, hand the bytes to user mode, drop the original) works.
  `PING` round-trips.
- The user-mode side already **decodes** the intercepted force target and feeds
  it into the plugin's existing force path (the Trueforce-endpoint `cur` field).
  An older comment in `TFFADriverChannel.cs` says "logged only, no re-emit"; that
  comment is **stale**. The decode-and-feed is wired. So "the force re-apply was
  never written" is **not** the problem.
- There is a real, known bug: the user-mode decode in `TFFADriverChannel.cs` only
  handles FFB function nibble `0x20`, not `0x30`. With the recommended
  `TFFAUsbFilter` the kernel hands up every intercepted write, so a `0x30` FFB
  write arrives but produces no force target (section 10).

**What is NOT settled (open; do not treat as answered):**

- **Which driver/layer is right.** A code-level read of both drivers now points
  to the USB-layer filter (TFFAUsbFilter): capturing every write is a property of
  its layer (the same one USBPcap already proves complete), while its one
  weakness, owner detection, is a fixable bug. The HIDClass filter is the
  opposite: working owner detection but permanent blindness to some writes. This
  is reasoned from the source, not yet hardware-proven, and neither was confirmed
  on hardware before the work stopped. See section 3.
- **Why an earlier test left the wheel with no force.** Two candidate causes:
  (a) the setup/handshake echo is not wired, so the wheel's HID++ session may
  die; (b) the decode never produced a fresh force target at runtime (wrong
  feature index, the `0x20`/`0x30` bug, or staleness). Both are unproven, and the
  logs from that night were inconsistent.

**The shortest path to facts** (not yet done): set up (sections 5 to 7), run the
smoke test (section 8), then the staged validation (section 11) with DebugView
capturing, so the next conclusion comes from observation instead of inference.

---

## 3. Two drivers: which one and why

There are two driver variants in `driver/`. They share the same control-device
and ownership design; they differ in **where in the stack** they sit.

| Driver | Layer | Class GUID | Status |
|---|---|---|---|
| `TFFAFilter` | HID class upper filter (above HIDClass) | `{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}` | HID-class layer. Owner detection works here (the write arrives on the caller's own thread, so the process id is the game's). But it is structurally blind to writes that bypass this HIDClass stack, and it hardcodes the FFB feature index to `0x0E` (wrong for e.g. the RS50, which is `0x10`), so on those wheels the game's force is never recognized and leaks to the wheel. Most-tested, but not the one to build on. |
| `TFFAUsbFilter` | USB class upper filter (above usbccgp) | `{36FC9E60-C465-11CF-8056-444553540000}` | USB/URB layer. Sees every write regardless of API, the same vantage USBPcap uses, so capture is structurally complete. Recommended. One fixable catch: it identifies the owner by current process id, which is unreliable at this layer (see below and section 10). Build on this one. |

Recommendation (from a code-level analysis of both `driver.c` files, not yet
hardware-proven): **build on TFFAUsbFilter, and test it first.** The deciding
factor is which weakness is fixable. Seeing every write is a property of the
*layer*: the HIDClass filter physically cannot see all the game's writes (the
proof is below it, at the USB layer) and you cannot move it down, so its
blindness is permanent. The USB filter already sees everything; its only real
problem is that it identifies the owner by current process id, which is
unreliable at the URB layer (the write is carried down by a system thread, not
the game's), and that is an ordinary bug you can fix (tag the plugin's own writes
another way, see section 11). One driver has the hard-to-get property plus a
fixable bug; the other has an easy property behind an unfixable blindness. Pick
the fixable one.

One consequence of building on `TFFAUsbFilter`: because it intercepts every
non-owner URB while it owns the wheel, it swallows the game's root queries,
GET_INFO, and handshake traffic too, not just FFB. So the user-mode handshake
echo (section 11, step 4) is not optional polish. Without it the game's HID++
session gets no responses at all, which is the strongest mechanism behind the
earlier dead-wheel result.

**Build and test one driver at a time.** They are both class filters for the same
wheel; install both at once and two filters intercept the same writes and fight.
Install one, test, uninstall, then the other.

Both drivers gate themselves at runtime to the target wheels only (by USB
VID/PID). Every other device on the system is untouched at runtime even though a
class filter is technically loaded for the whole class. Target wheels:

| PID (`VID_046D&PID_...`) | Model |
|---|---|
| `C272` | Logitech G PRO (Xbox/PC) |
| `C268` | Logitech G PRO (PS/PC) |
| `C266` | Logitech G923 (PS/PC) |
| `C26D` / `C26E` | Logitech G923 (Xbox/PC) |
| `C276` | Logitech RS50 |

To add a wheel later, append to `g_TargetWheels[]` in `driver.c`. No INF change
needed (the INF installs us class-wide; the table is the runtime gate).

Validate on the **Logitech G PRO** first. The bring-up and all the wire-level
values in section 10 (FFB feature index `0x0E`, LED feature `0x09`) are confirmed
only on the G PRO, and both drivers and the decoder currently hardcode `0x0e`.
Treat other wheels (e.g. the RS50, index `0x10`) as the multi-wheel follow-up
after stage-3 passes.

---

## 4. Current state, in one line

The hard parts exist: a loadable filter, safe ownership (claim/release including
the rapid-restart race), interception, and the force decode-and-feed
(`TFFADriverChannel.cs` decodes the intercepted FFB target and the plugin's force
provider reads it into the Trueforce ep3 `cur` field). What is unproven is
whether the whole loop yields a live, contention-free wheel. The one
clearly-missing piece of code is the setup/handshake echo, and the earlier
no-force test was never diagnosed (it is the not-yet-echoed behavior, or a
runtime decode miss; both are live candidates).

The full built-vs-open inventory is in the Status section above; the task is in
section 11.

---

## 5. What is in this package

```
tffa-driver-handoff/
  HANDOFF.md                         <- this file
  driver/
    TFFAUsbFilter/                   <- USB-layer filter (recommended start, see section 3)
      driver.c, *.inf, *.vcxproj, *.sln
    TFFAFilter/                      <- HIDClass-layer filter (the one most tested, see section 3)
      driver.c, *.inf, *.vcxproj, *.vcxproj.filters, *.sln
  scripts/
    create-dev-cert.ps1              <- step 1: make the test-signing cert
    enable-test-signing.ps1          <- step 2: trust cert + turn on test-signing (admin)
    test-tffa-control.ps1            <- smoke test: claim ownership, PING, dump intercepts
```

### Getting the source

Everything you need is on the **`driver`** branch of
https://github.com/Mhytee/Trueforce-For-All. The branch is the full plugin repo
plus this `tffa-driver-handoff/` folder, so the driver builds standalone AND the
plugin (which already includes driver testing mode, shipped in TF4ALL 0.1.24)
builds and runs for the full-loop test. Fork the repo, branch from `driver`, and
send your work as pull requests against `driver`. The repo's `CONTRIBUTING.md` describes the flow; open
a GitHub issue (tag it `driver`) for design questions or progress notes so they
stay async and visible, and push an early draft/WIP PR so the maintainer can
course-correct without a back-and-forth.

No prebuilt driver binaries are shipped (they would be signed with the author's
test certificate and not trusted on another machine anyway), so build and sign
locally per sections 6 to 7. The user-mode bridge you extend (section 11) is the
live file `src/TrueforceForAll.Core/TFFADriverChannel.cs` in the repo.

### License

The repo is **GPL-2.0-only**. It derives from mescon's GPL-2.0 Logitech RS50
Linux driver, so the copyleft is load-bearing, not optional, and any contribution
is accepted under the same license. A kernel driver under copyleft also carries
the production-signing implications in section 9. Full text is in the repo
`LICENSE`.

---

## 6. Build and sign

**Environment:** Windows 10/11 x64, Visual Studio 2022 with the Windows Driver
Kit (WDK) and a matching SDK (the WDK and SDK versions must match or the driver
project will not load; the `.vcxproj` is the source of truth for the target
platform version), or the standalone EWDK. Build `Debug | x64`. Install is
amd64-only: the INF only has `.NTamd64` sections. (The `.vcxproj` also carries
ARM64 build configs, but there is no ARM64 INF section, so an ARM64 binary cannot
be installed without adding one. There is no 32-bit build.)

**Build:**

1. Open `driver/TFFAUsbFilter/TFFAUsbFilter.sln` (the `.sln`, `.vcxproj`, and
   `driver.c` are co-located in that folder).
2. Configuration `Debug`, platform `x64`. Build. Output is `TFFAUsbFilter.sys`
   plus a `TFFAUsbFilter.cat` catalog (the project runs inf2cat as part of the
   build).

**Test-sign (an unsigned kernel driver will not load):**

1. `scripts/create-dev-cert.ps1` (normal PowerShell) creates a code-signing
   cert `CN=TrueforceForAll Dev` in your user store.
2. `scripts/enable-test-signing.ps1` (admin PowerShell) trusts that cert
   machine-wide (Root + TrustedPublisher), runs `bcdedit /set testsigning on`,
   and reports Secure Boot state. **Reboot** after it finishes.
   - **Secure Boot must be OFF**, or test-signing is silently ignored. After
     reboot you should see a "Test Mode" watermark in the lower-right of the
     desktop. No watermark means Secure Boot is still blocking it (disable it in
     BIOS).
   - **Heads-up:** disabling Secure Boot can trigger a BitLocker recovery prompt
     on the next boot. Have the BitLocker recovery key ready (Microsoft account,
     Devices, BitLocker keys) before you start.
3. If Visual Studio is configured for test-signing it will sign the `.sys`/`.cat`
   on build using your cert. If not, sign manually in the build output folder:

   ```
   signtool sign /v /fd SHA256 /s My /n "TrueforceForAll Dev" TFFAUsbFilter.sys
   signtool sign /v /fd SHA256 /s My /n "TrueforceForAll Dev" TFFAUsbFilter.cat
   ```

   (`signtool` and `inf2cat` ship with the WDK. If the `.cat` is missing,
   regenerate it first with `inf2cat /driver:. /os:10_X64` from the folder that
   holds the `.inf` and `.sys`.)

---

## 7. Install and uninstall

Run from an **admin** command prompt in the folder that holds the signed
`TFFAUsbFilter.sys`, `.cat`, and `.inf`. These match the INF's own documented
install/uninstall sections (which mark the `rundll32` line as optional).

**Install:**

```
pnputil /add-driver TFFAUsbFilter.inf /install
```

`pnputil /add-driver /install` stages and installs the class filter by itself.
The legacy `rundll32 ... DefaultInstall.NTamd64` line below is an optional
fallback (the INF marks it Optional); with `PnpLockdown=1` set in the INF,
Microsoft deprecates that path and current Windows may refuse it, so prefer
`pnputil`.

```
rundll32.exe SetupApi.dll,InstallHinfSection DefaultInstall.NTamd64 132 <full path>\TFFAUsbFilter.inf
```

Then **reboot** (or re-enumerate USB) so the new class filter takes effect. The
filter appends itself to the USB class `UpperFilters` list (it does not replace
anything, so other filters such as HidHide keep working).

**Uninstall:**

```
rundll32.exe SetupApi.dll,InstallHinfSection DefaultUninstall.NTamd64 132 <full path>\TFFAUsbFilter.inf
pnputil /delete-driver oem<N>.inf /uninstall
```

Then **reboot**. To find the `oem<N>.inf` name, run `pnputil /enum-drivers` and
look for the entry whose Original Name is `TFFAUsbFilter.inf`.

**To fully revert the machine afterward** (optional): turn test-signing back off
with `bcdedit /set testsigning off` (admin) and reboot, and remove the
`CN=TrueforceForAll Dev` cert from `LocalMachine\Root`,
`LocalMachine\TrustedPublisher`, and `CurrentUser\My`.

(The `TFFAFilter` HIDClass variant installs/uninstalls the same way but with its
own INF and the HIDClass GUID; see comments at the top of
`driver/TFFAFilter/TFFAFilter.inf`.)

---

## 8. Use and verify

**Watch the driver's log:** it prints to the kernel debug stream. Use
Sysinternals **DebugView**, run as admin, with "Capture Kernel" and "Enable
Verbose Kernel Output" checked under the Capture menu. You will see lines like
`TFFAUsbFilter: ... INTERCEPT_FFB ...` and `... PASS ...`.

**Smoke-test the control channel without the plugin:**

```
powershell -ExecutionPolicy Bypass -File scripts\test-tffa-control.ps1 -Recv -Seconds 20
```

While that script runs, **PowerShell is the wheel owner**. It opens
`\\.\TFFAControl`, sends a `PING` (expects magic `0x54464641`, ASCII "TFFA"),
then loops on `RECV` and dumps the first bytes of any intercepted write from
other processes. Start a game or move the wheel in something that writes FFB and
you should see intercepts. **Note:** while this script holds the handle, those
intercepted writes are being dropped, so the wheel will feel dead in-game. That
is the same not-yet-echoed behavior described in section 4, and it is expected
for the smoke test.

**With the plugin (the full loop):** the driver itself is fully exercisable with
the smoke test above (no plugin required), so the section-11 bring-up can run
against PowerShell as the owner. For the full loop, run the TF4ALL plugin and
enable **driver testing mode**: type `DRIVER` in the plugin's access-code box (a
hidden experimental toggle, not shown to regular users; default off), then
restart SimHub / re-detect. The plugin then automatically becomes the wheel owner
(it opens the control device on startup) and routes the game's FFB through the
Trueforce stream. The user-mode
bridge `src/TrueforceForAll.Core/TFFADriverChannel.cs` decodes the force target
but does not yet echo handshake traffic; it is the file to extend (section 11).

Ownership model recap: first opener wins; last opener takes over on a rapid
restart; closing the handle (including on a crash) releases ownership; no owner
means full pass-through.

---

## 9. Safety, risk, and posture

- **Anti-cheat / fairness:** this is a filter driver below the game. It does not
  inject into the game, read or write game memory, or hook game code. That is a
  deliberate line for this project.
- **Default-safe:** no owner means pure pass-through. A plugin crash releases
  ownership automatically. The wheel cannot get stuck owned by a dead process.
- **Kernel risk is real:** a bug in a kernel driver can blue-screen or destabilize
  the machine. Do bring-up on a **dedicated test machine or a VM with the wheel
  passed through**, or at least set a system restore point and have your
  BitLocker recovery key handy before enabling test-signing.
- **Test-signing weakens the machine's security posture** (it trusts a
  self-signed cert and lets unsigned-by-Microsoft drivers load). Turn it back
  off when done (section 7).
- **Not shippable as-is to end users.** Test-signing is a developer-only path.
  Shipping to real users needs a proper kernel-driver signature (attestation
  signing through the Microsoft Partner Center, or full WHQL). Treat that as a
  separate, later milestone with its own cost.

---

## 10. Reference: the wire-level details

**Control device:** `\\?\TFFAControl` (kernel name), opened from user mode as
`\\.\TFFAControl`. Its security descriptor allows Builtin Users to open it
(SimHub usually runs non-elevated).

**IOCTLs** (FILE_DEVICE_UNKNOWN, METHOD_BUFFERED):

| Name | Code | Purpose |
|---|---|---|
| `IOCTL_TFFA_PING` | `0x222000` | Returns magic `0x54464641` ("TFFA"). Wiring sanity check. |
| `IOCTL_TFFA_RECV` | `0x222004` | Inverted call: user mode posts an output buffer; the driver completes it with the bytes of the next intercepted write. Post a fresh one immediately after each completion. |

**HID++ wire layout used to classify intercepted writes.** WHERE this
classification happens differs by driver: `TFFAFilter` (HIDClass) classifies in
the kernel and only intercepts matching writes; the recommended `TFFAUsbFilter`
intercepts EVERY non-owner URB indiscriminately (its only kernel test is
`requestor != owner`) and does all of the sorting below in user mode, in
`TFFADriverChannel.cs`. Either way, a message is HID++ when `byte[0]` is `0x10`
(short), `0x11` (long), or `0x12` (very long) and `byte[1] == 0xFF` (wired device
index). Then:

- **FFB write:** `byte[2] == 0x0E` (feature page 0x8123 on the bring-up wheel)
  and the high nibble of `byte[3]` is `0x20` or `0x30` (function 2_/3_). The
  signed int16 motor-force target is at **bytes 10 to 11, big-endian**.
- **LED write:** `byte[2] == 0x09` (the rev-light feature index on the G PRO).
- Everything else passes through (or, in the finished design, is echoed).

> Decoder gap to fix during the echo work: the user-mode **decode** in
> `TFFADriverChannel.cs` only matches FFB function nibble `0x20`, not `0x30`.
> Under `TFFAUsbFilter` the kernel hands up every intercepted write, so a `0x30`
> FFB write IS delivered to user mode; the decoder just fails its `0x20` test and
> produces no force target. (The original write is dropped for every intercept by
> design; the only bug is the missing target. `TFFAFilter`'s kernel classifier
> does test both `0x20` and `0x30`, but you are not building on that one.)

> Per-wheel index: the FFB feature index is `0x0e` on the G PRO and `0x10` on the
> RS50. The C# side currently hardcodes `0x0e`. Resolve it per wheel for
> multi-wheel support (the existing `WheelDiscovery` / HID++ root getFeature
> logic in the plugin already does this kind of resolution for LEDs). The
> `TFFAFilter` driver also hardcodes `0x0e` in its FFB classifier, so on a wheel
> whose index is not `0x0e` it never recognizes the game's FFB and lets it
> through. The LED feature index (`0x09`) is likewise confirmed only on the G PRO;
> treat it as per-wheel-unknown until verified on others.

> Owner detection differs by layer. `TFFAFilter` compares `PsGetCurrentProcessId()`
> to the owning PID, which is correct there because the write arrives on the
> caller's own thread. `TFFAUsbFilter` copies that check, but at the URB layer the
> write is carried down by a system/worker thread, so the current PID is not the
> originating process. The USB-layer owner check therefore cannot rely on the
> current PID; tag the plugin's own writes another way (a marker in the HID++
> payload, or a dedicated re-inject IOCTL). Validate and fix this first
> (section 11).

---

## 11. The next task, concretely

**Definition of done.** The deliverable is stage-3 validation (below) passing:
force stays solid through the Trueforce endpoint AND the LED-vs-FFB cutout is
gone, with the owner-detection fix, the handshake echo, and the `0x30` decode
landed as PRs. Prefer objective checks over "feels right": DebugView shows no
intercepted game write leaking to the wheel while owned, and no bugcheck over a
sustained session.

**Out of scope for this task:** production / WHQL signing (section 9),
feature-index resolution for wheels beyond the G PRO bring-up wheel, and end-user
packaging; those are later, separate milestones. The maintainer reviews and
merges the PRs.

**Putting the plugin in driver mode.** Wherever a step below has the plugin own
the wheel, that happens automatically, there is no manual claim step. Once
**driver testing mode** is on, the plugin opens the control device on startup and
becomes the wheel owner by itself. Turn driver testing mode on by typing `DRIVER`
in the plugin's access-code box (default off and hidden; see section 8), then
restart SimHub / re-detect. The driver-only smoke test (section 8) instead makes
PowerShell the owner just by running, and needs no plugin at all.

**Which driver, and the prerequisite fix.** Build on TFFAUsbFilter (section 3).
Before any of the echo/merge work below means anything, fix its owner check: it
uses `PsGetCurrentProcessId()` at the URB layer, which does not return the
originating process, so it can drop the plugin's own writes (dead wheel) and let
the game's through. Replace it with an owner signal that does not depend on
thread context (a marker in the plugin's own HID++ writes, or a dedicated
re-inject IOCTL).

First concrete check, before anything else: load TFFAUsbFilter, turn on driver
testing mode (the plugin auto-owns the wheel), then have the plugin push one
HidSharp write while a separate process pushes one, and log `PsGetCurrentProcessId()`, `KeGetCurrentIrql()`, and the
first bytes for every intercepted URB. The likely result is that on a
system/worker thread the requestor is the System process (PID 4) for BOTH the
plugin's and the game's writes, so the current-PID test cannot tell them apart at
all: it intercepts both, and the plugin's own re-injected write gets swallowed
too, so nothing reaches the wheel. Whether it fails that way or asymmetrically,
any `PID != owner` for the plugin's own write confirms the current-PID check is
dead and the payload-tagging rewrite is task one.

Turn this experiment into the real thing by implementing the user-mode echo/merge so
TF4ALL is the sole writer. The intended behavior is already written down in the
`TFFAUsbFilter/driver.c` header; implement its user-mode half in
`src/TrueforceForAll.Core/TFFADriverChannel.cs`. Note: the FFB-feed in step 1 (decode the target into the
`FfbTargetProvider` path) is already wired, so the genuinely new
work is the handshake echo in step 4 and the `0x30` decode fix in step 1:

For each message delivered by `IOCTL_TFFA_RECV`:

1. **FFB** (feat `0x0E`, function `0x2_` or `0x3_`): extract the int16 target
   (bytes 10 to 11) and feed it into the Trueforce-endpoint stream's force field
   (the existing `FfbTargetProvider` path). Do **not** echo the raw HID++ FFB to
   the wheel. Fix the `0x20`-only decode to also handle `0x30`.
2. **LED** (feat `0x09`): drop it; let TF4ALL's existing LED channel own the LEDs.
   (Pick one LED owner and stick to it.)
3. **SET_EFFECT_STATE** (short report, feat `0x0E`, the play/stop effect command):
   drop it. The wheel never received the matching effect download, so there is no
   real effect slot to start or stop.
4. **Everything else** (root queries, get-info, notifications, the startup
   handshake): **echo** it to the wheel via TF4ALL's own HID handle (which passes
   through the filter because TF4ALL is the owner). This is the missing step that
   should keep the wheel responsive and the game's HID++ session alive. Skipping
   it is a leading theory for the no-force result seen earlier (section 4), though
   that was never confirmed.

**Validate in stages** (so a failure is diagnosable instead of all-or-nothing):

1. **Pass-through:** install the driver, leave driver testing mode off (so there
   is no owner). Confirm FFB and LEDs behave exactly as without the driver.
   Proves the driver itself breaks nothing.
2. **Own + echo everything:** turn driver testing mode on (the plugin auto-owns
   the wheel) and echo **all** intercepted writes straight back to the wheel
   unchanged. Confirm the game still feels normal. Proves the echo path and the
   HID++ session stay healthy.
3. **Start dropping selectively:** now drop FFB (force comes from the Trueforce
   endpoint) and route LEDs through TF4ALL. Confirm two things at once: the wheel
   still feels right, and the LED-versus-FFB cutout is gone.

If the cutout clears at stage 3 while force stays solid, the contention problem
is solved with parts that already exist; everything after that is hardening and
the production-signing milestone (section 9).

---

## Glossary

- **TF4ALL / TFFA** - the same project. TF4ALL is the brand; TFFA is the legacy
  prefix kept in code identifiers (`TFFAUsbFilter`, `\\?\TFFAControl`, `IOCTL_TFFA_*`).
- **Trueforce endpoint** - a separate 1 kHz audio-haptic HID stream that carries
  our force + effects. **Non-Trueforce endpoint** - the normal HID++ path the game
  writes its FFB and LED commands to (one shared on-wheel command processor).
- **`cur`** - the motor-force field in the Trueforce packet, ep3 bytes 6-9; the
  `driver.c` headers refer to it by that byte range.
- **HID++** - Logitech's control protocol. Reports start `0x10`/`0x11`/`0x12`,
  wired device index is `0xFF`, FFB lives on feature page `0x8123`.
- **feature index** - the per-wheel id of a HID++ feature (FFB is `0x0e` on the
  G PRO, `0x10` on the RS50; LED is `0x09` on the G PRO).
- **inverted call** - the `IOCTL_TFFA_RECV` pattern: user mode posts an output
  buffer and the driver completes it later with the next intercepted write's bytes.
- **owner** - the process holding the `\\?\TFFAControl` handle. Its writes pass
  through to the wheel; every other process's writes are intercepted.
- **WheelDiscovery** - the plugin's existing HID++ root getFeature logic that
  already resolves per-wheel feature indices; reuse it for FFB-index resolution.
