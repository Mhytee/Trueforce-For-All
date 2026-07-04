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
- [ ] Greyed-out upload for a non-supporter (if testable): download stays active, upload disabled with the lapsed message.

## D. No FFB / performance regression
- [ ] Drive in a sim -> FFB feels normal; no stutter or blip from the 2s polling (it defers while a game runs).
- [ ] After an account switch, master gain / FFB scale on the wheel match the signed-in account's profile.

## Notes / failures
(Record anything that didn't behave as expected, with the SimHub log snippet.)
