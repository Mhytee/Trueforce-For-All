# Cloud sync + per-account profiles: 2-PC hardware test checklist

Build under test: `TrueforceForAll-Setup.exe` (commit 2dc1247).
Install on **both** PCs (the installer's factory cleanup runs, so built-in counts match).
Both signed in as the **same** account for sections A and C; section B needs a second account.

Watch the SimHub log while testing for `[Trueforce] Auto-sync ...` lines and any warnings.

## A. Sync works (same account, both PCs)
- [ ] PC1: toggle an effect off and save -> change appears on PC2 within ~5-10s.
- [ ] PC2: change a value -> appears on PC1.
- [ ] PC1: duplicate a preset -> appears on PC2 in ~10s.
- [ ] PC1: delete a preset -> it disappears on PC2 and stays gone (no resurrection).
- [ ] Retry fix: on PC1, toggle+save ~10 times in a row -> every change lands on PC2 (none silently missed).
- [ ] Both PCs idle a few minutes -> no runaway uploads, library stays identical.

## B. Per-account isolation (needs a second account; the new, riskiest part)
- [ ] Note PC1's presets + a distinctive setting (e.g. master gain value) while signed in as A.
- [ ] Sign out on PC1 -> you see the shared/anonymous library (your original presets), not A's account view.
- [ ] Sign back in as A -> A's presets + settings return intact.
- [ ] Sign out, sign in as B -> B does NOT show A's presets or A's feel (B starts from the shared baseline).
- [ ] As B, change a preset/setting. Sign out, sign back in as A -> A is unchanged; B's change is NOT visible in A.
- [ ] Anonymous -> first sign-in: with presets present while signed out, sign in -> your library is NOT emptied (it's copied into the account).

## C. Backup / restore / export / import buttons
- [ ] "Back up now" succeeds (supporter flag on).
- [ ] "Restore from cloud" on the other PC pulls your library.
- [ ] Local "Back up" (zip) produces a zip; local "Restore" restores it AND you stay signed in as yourself (identity not clobbered).
- [ ] Export a preset to a file; import it on the other PC -> lands in your library.
- [ ] Grayed-out upload for a non-supporter (if testable): download stays active, upload disabled with the lapsed message.

## D. No FFB / performance regression
- [ ] Drive in a sim -> FFB feels normal; no stutter or blip from the 2s polling (it defers while a game runs).
- [ ] After an account switch, master gain / FFB scale on the wheel match the signed-in account's profile.

## E. Cross-wheel FFB gate (two different wheel models)

Needs a different wheel chassis on each PC (e.g. a G PRO on PC1, a G923 on PC2), same account.
Wheel-specific tuning backs up from both PCs, so this gate is the only thing keeping one wheel's
numbers off the other, and until this pass a merged backup carried no wheel stamp at all, which
switched the gate off for every device that later restored it.
Set "FFB tuning from a different wheel model" to "Ask me" on both PCs except where an item says
otherwise.

- [ ] PC1: move a Mode B value you can feel (e.g. saturation gain) and "Back up now". PC2: "Restore from cloud" -> presets and settings arrive, PC2's own Mode B values are untouched, and the yellow "Force feedback tuning from your <PC1 wheel> was not applied" notice appears.
- [ ] On that notice, apply it anyway -> PC2's Mode B values become PC1's and the feel changes on the next drive. Dismiss instead -> PC2 keeps its own numbers.
- [ ] Conflict merge: with both PCs holding unpushed changes, "Back up now" on PC2 and take the merge in the conflict dialog. Then PC1: "Restore from cloud" -> the cross-wheel notice STILL appears. (Merged backups used to lose the wheel stamp, so the gate went silent for every device until someone pushed again.)
- [ ] Auto-sync merge: "Keep my devices in sync" on both, change a different non-FFB setting on each PC within the same minute, let them settle -> each PC's Mode B values are still its OWN, not a blend of the two. Check 2-3 sliders per side, not just one.
- [ ] Leave both PCs syncing for several rounds with no FFB change -> you are prompted at most once. A fresh "apply anyway" notice after every sync is the bug that was fixed; the gate compares values now, not just whether the keys arrived.
- [ ] PC2 set to "Always apply": restore from PC1 -> PC1's FFB tuning lands and no notice appears.
- [ ] PC2 set to "Never apply": restore from PC1 -> PC2's tuning is kept and nothing is shown (silent, not a prompt).
- [ ] Tick "Remember my choice" on a notice -> the dropdown moves to "Always apply" (applied) or "Never apply" (dismissed), and the next restore honors it without asking.
- [ ] Repeat the first three items with the FFB tab's condition gains (damper, spring, friction, inertia) instead of Mode B: they count as wheel-specific now, so they must be withheld, prompted and applied exactly like the Mode B values.

## F. Settings file / zip import: what travels and what stays local

- [ ] Export a settings file on PC1 (the other wheel) and import it on PC2 -> the cross-wheel notice appears here too, with the same choice as section E. It never did before: the gate was comparing this PC against itself.
- [ ] PC1: set a custom Forza bind address (second NIC, or a non-default IP) and back up to a zip. Restore that zip on PC2, where that address does not exist -> Forza telemetry still arrives on PC2 (speed and gear move), because PC2 kept its own bind address.
- [ ] PC1: unlock the arcade path with the access code, then export settings and import them on PC2 -> the arcade path is still LOCKED on PC2 and no arcade UI appears.
- [ ] Import an old 0.1.x settings file (one that still carries its presets inside the JSON) -> those presets show up in the library after the import. Regression guard: the legacy rescue reads them off the file itself, so they have to stay in it.
- [ ] Restore a zip on a PC that already has its own Initial D 8 board archive -> the archive in the `TrueforceForAll-Arcade` folder is unchanged afterwards (same size and timestamp); a restore must not overwrite local boards with the other PC's.
- [ ] Take a zip on PC1, restore it on PC2 -> PC2's `TrueforceForAll-Arcade` folder gains nothing, and the log says the arcade archive was skipped because it belongs to another machine. Those files record one machine's own save, so they are stamped with the PC that wrote them and only that PC takes them back.
- [ ] Same machine, the case the zip exists for: back up, delete (or rename) `TrueforceForAll-Arcade`, restore the zip -> the two board files come back. Then restore again -> they are left alone the second time.

## Notes / failures
(Record anything that didn't behave as expected, with the SimHub log snippet.)
