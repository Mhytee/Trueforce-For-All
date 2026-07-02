# TFFAUsbFilter test kit

Everything needed to load and validate the content-based USB FFB filter on a
spare Windows 11 x64 machine. **Nothing gets installed** — the rig only runs
finished files (driver via built-in `pnputil`, portable `DebugView`, a
self-contained harness). No Visual Studio, no WDK, no G HUB, no SimHub, no game.

## What's in the kit

| File | Purpose |
|------|---------|
| `TFFAUsbFilter.sys/.inf/.cat` | the driver package |
| `TFFAUsbFilter.cer` | its test-signing certificate |
| `1-check-rig.ps1` | verify the rig is ready (read-only) |
| `2-install.ps1` | trust cert + install filter |
| `2b-arm-safety.ps1` | **dead-man's switch** - auto-removes the driver if USB dies |
| `2c-disarm-safety.ps1` | undo the safety guard when done |
| `3-uninstall.ps1` | remove filter, return USB to normal |
| `DebugView.exe` | **you add this** — grab from Sysinternals (portable, no install) |
| `tffa-fakegame.exe` | **added when built** — sends game-shaped FFB so no real sim is needed |

Copy this whole folder to a USB stick, then to the spare rig. Run the `.ps1`
scripts from an **elevated** PowerShell (right-click PowerShell -> Run as admin).

---

## One-time rig prep (unlocks test-signed drivers)

Run `1-check-rig.ps1`. It checks four things and tells you how to fix any that
are wrong. In short:

1. **BitLocker** should be Off (if On, back up the recovery key first — changing
   Secure Boot can otherwise lock the drive).
2. **Secure Boot** -> Off (reboot into UEFI/BIOS).
3. **Memory Integrity** -> Off (Windows Security > Device security > Core
   isolation > Memory integrity), reboot.
4. **Test-signing** -> On: `bcdedit /set testsigning on`, reboot.

Re-run `1-check-rig.ps1` until it prints **READY**. You'll see a "Test Mode"
watermark on the desktop — that's the green light. All reversible later.

---

## Stage A — smoke test (no harness, no game)

Goal: prove the filter loads and **doesn't break USB or the wheel.** It's a USB
*class* filter, so the thing to rule out is broad USB breakage.

1. Elevated PowerShell: `.\2-install.ps1`  (add `-Password X` if the account
   has a login password). This one script now **self-protects**:
   - arms the reboot-time guard first (covers a hard-power-off if install hangs),
   - installs the filter,
   - then runs a **live keypress guard for the install moment** - press any key
     to KEEP; if USB just died you can't, so it auto-removes and reboots.
2. If you kept it, **reboot.** The reboot-time guard asks for a keypress again
   (your USB check for the boot path) - press any key to keep.
3. Run `DebugView.exe` as admin -> **Capture** menu -> tick **Capture Kernel**
   and **Enable Verbose Kernel Output**.
4. Plug in the G923.

> The install can no longer lock you out: the moment USB dies (on install *or*
> reboot), doing nothing triggers the self-heal.

**Pass looks like:**
- Keyboard/mouse/USB all still work after reboot (filter didn't jam USB).
- Device Manager shows the wheel normally.
- DebugView shows `TFFAUsbFilter:` lines — an `EvtDeviceAdd ... attached` on the
  wheel, and throttled `PASS` lines as traffic flows.

If the wheel enumerates and USB is healthy, Stage A passes — the driver is safe.

---

## Stage B — interception + G923 index discovery (needs `tffa-fakegame.exe`)

Your G923's FFB *feature index* is unknown (only the G PRO's `0x0E` is
confirmed), and the filter must know it to intercept correctly. The harness
discovers it for us.

1. With DebugView capturing, run `tffa-fakegame.exe` (sends HID++ FFB-shaped
   writes across candidate feature indices).
2. Watch for **`FFB_LEAK_PASS featIdx=0xNN`** — that `0xNN` is your G923's real
   FFB feature index (an FFB-shaped write that the filter passed because it
   didn't match the configured index).
3. Set that index (harness flag / driver parameter — see harness `--help`),
   rebuild-free, and re-run. Now the same writes should log as
   **`INTERCEPTED`** instead of `FFB_LEAK_PASS`.

`INTERCEPTED` on the game-shaped writes = the filter is dropping the game's FFB
= sole-writer achieved. That's the whole thesis.

---

## Safety / recovery

Worst realistic case is a BSOD (reboot) — the filter is demand-start and only
attaches to the listed wheels. If USB or boot ever misbehaves:

1. Boot to **Safe Mode** (hold Shift + Restart -> Troubleshoot > Advanced >
   Startup Settings > Safe Mode).
2. Run `.\3-uninstall.ps1` (or manually delete `TFFAUsbFilter` from
   `HKLM\SYSTEM\CurrentControlSet\Control\Class\{36FC9E60-C465-11CF-8056-444553540000}\UpperFilters`).
3. Reboot normally.

## When you're done

```
.\3-uninstall.ps1        # remove filter, reboot
# then restore security:
bcdedit /set testsigning off      # reboot
# re-enable Secure Boot in UEFI, re-enable Memory Integrity
```

## Supported wheels (runtime-gated in the driver)

G PRO (C272/C268), **G923 (C266/C26D/C26E)**, RS50 (C276). Anything else is
ignored — the filter returns `STATUS_NOT_SUPPORTED` and never enters its stack.
