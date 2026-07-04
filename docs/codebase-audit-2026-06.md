# Codebase audit backlog (ui-tabs-layout)

Source: 9-agent parallel audit of ~107k LOC on 2026-06-23. Severity is bug impact x reach.
Check items off as fixed. HIGH items have a verified assessment in the next section.

## Re-verification pass (2026-06-24, 36 adversarial skeptics)

Every finding (fixed/skipped/open/low) was independently re-checked against the actual code. Results:
- **Fixes confirmed correct:** H1, H4, M2, M4, M5, M6, M7, M10, M12, M16, M18, M20, M9. H5 = harmless no-op (the awaited methods can't throw; crash was unreachable).
- **Fixes incomplete -> need rework:**
  - **H2 — REOPENED.** The "NaN permanently silences" claim was a FALSE POSITIVE (line-282 early-return self-heals NaN in one frame). But +Infinity RPM (unsanitized Forza ReadFloat) slips BOTH new guards, latches `_wtPhase=+Inf`, and `wt[(int)+Inf]`=`wt[int.MinValue]` throws IndexOutOfRangeException in the audio render loop = a CRASH that never resets. Fix properly with `double.IsFinite(rpm)` (and/or `double.IsFinite(phaseStep)`), ideally sanitize at ForzaUdp ReadFloat. The current comment "rejects NaN/Inf" is inaccurate.
  - **M19 — incomplete (cosmetic).** Rotary still reopens as "Even-fire" (`BuildPatternString(Rotary,r)`==Even(r*2), EvenFire matched first). No data loss. Fix: check Rotary before EvenFire.
- **Deferral overturned:**
  - **M11 — REOPENED, defer was wrong.** Broader path: `CloneCarOverride` drops all 6 `Community*` fields, so a single section-edit+Save wipes tracking (no IsEmpty needed). Harm: Share-gate/attribution bypass + re-upload as new row. 2 surgical fixes (CloneCarOverride carries the fields; Save doesn't Delete when Community* set), NOT 13 sites.
- **Confirmed false positives (skip/drop correct):** M1, M3, L9.
- **Open-item corrections:** M13 real (backup merge drops cloud-side list edits, unrecoverable). M21 premise wrong (ShowFeedbackBox defaults TRUE, so it IS reachable; still cosmetic). H3 has a real code fix (FM offsets = Horizon-12, add `else if (len>=311)`; steer dir wants hw verify). M17 by-design but `UserRedlinePinned` is dead code. M8 real but narrow (needs import/restore/migration overlapping a car-change tick); fix = one `_carStoreLock`.
- **Low-item corrections:** L6 "indefinite/denormal" is a false positive (float underflow self-clears in ~1-2s); L8 "miss baked default" mechanism is false (real harm = cross-install pack-import row split); L1 `..` over-reject silently drops legit imports (worth fixing); L4/L7/L10 latent-only.

## Round 2 hunt (2026-06-24, 8 lenses + adversarial verify)

Fresh complementary sweep (concurrency / lifecycle / error-paths / numeric / state-machine / data-integrity / UI / new-fix-code), deduped vs the catalog, each candidate verified. Result: **0 new HIGH**, 4 medium, 15 low; 2 false positives filtered (WriteLong wrong-stream — topology never occurs per 18 capture logs; SetPluginEnabled/StopStreamOnPause desync — code does the opposite of the claim).

Mediums (verified real, all FIXED):
- [x] **N1. Custom-engine sharing dropped community provenance (M11 part 2).** `MergeImportedCustomEngines` (TrueforcePlugin.cs:13323) and `CollectReferencedCustomEngines` (:13399) hand-copied `CustomEngineDef` minus the 6 `Community*` fields, so a downloaded engine lost its `CommunitySourceId`/`AllowInPacks` and `IsRedistributable` (CreatePackWindow.cs:518) short-circuited `true` -> re-bundleable with attribution stripped + author's AllowInPacks=false ignored. FIXED: both copies now carry the 6 fields (the CloneCarOverride pattern).
- [x] **N2. Backup restore could half-apply settings (M12 part 2).** `BackupProjection.ApplySettings` `Populate` (:210) was unguarded, so a malformed/cross-version envelope leaf threw mid-stream after some fields were written onto live `Settings`. FIXED: validate the portable JSON on a throwaway `TrueforceSettings` first; only Populate live if the dry run succeeds. Closes the auto-sync-pull path and the `ApplyProfileToLive` slot-switch surface at once.
- [x] **N3. `_activeGame` stomped during an offline car edit.** The DataUpdate game-change block (:2889) reassigned `_activeGame` + fired `ApplyGamePreset` with no car-edit guard (`IsOfflineEditing` is the PRESET-edit flag, false during a car edit), clobbering the edit baseline and mis-keying the Car-facts panel when editing a car of a non-running game. FIXED: added `&& !IsOfflineEditingCar` to the block guard.

Lows (15, verified): mostly benign/self-healing/near-unreachable. The 8 with cheap mechanical fixes were all done (build clean):
- [x] torn cross-thread longs: `WheelSteeringReader.LastUpdateTicks` (Interlocked-backed), `_lastFrameTicks` (Volatile->Interlocked.Read/Exchange), `_telemetryStalled` (now `volatile`).
- [x] undisposed `Process` handles: USBPcapCMD child disposed on every restart (Stop/elevation/finally), `GetProcessesByName("lghub")` snapshot disposed, `WheelLedChannel.TryGroup` success-path orphan streams disposed.
- [x] `End()` `SaveCommonSettings` wrapped in try/catch (no longer skips the helper-kill / device-cleanup disposes on a throw).
- [x] EnginePulse: added `rpm > 60000` upper bound so a garbage-but-finite RPM can't drive phaseStep past WavetableSize and overflow the index (not reachable under real games; defense in depth on top of the +Inf fix).

Left as-is (verified benign): Biquad struct tear (self-heals on next filter edit), `_lastFrame` struct tear (release path gated out by MeasuredHz<=0), the two UI combo/async-fetch items (no-op on stale), +Inf MaxRpm (transient cosmetic display only), `_stopStreamPauseActive` recovery reset (default-off NOLOCK toggle, timing-race self-heals).

## HIGH

- [x] **H1. Export drops 4-5 tuning sections.** `TrueforcePlugin.cs:13538` `ExportActiveCarPreset`
  fallback `CarOverride` captured only 6 of 11 sections. FIXED: now captures all 11 (added PitLimiter,
  Drs, Collision, RevLimiter, Airborne). Also removed dead+buggy `CloneOverrides` (was dropping Airborne,
  zero callers). Verified `GameSettingsSnapshot` build (13246) and the section switches are complete.
- [x] **H2. Non-finite RPM crashes the audio render loop.** Re-verification found the original "NaN
  permanently silences" claim was a false positive (a `!playPulse && !playLoad` early-return self-heals
  NaN), but **+Inf** (a corrupt Forza UDP float) slipped both first-pass guards, latched `_wtPhase=+Inf`,
  and `wt[(int)+Inf]`=`wt[int.MinValue]` threw IndexOutOfRange = a crash. PROPERLY FIXED:
  `EnginePulseEffect.cs` now bails on `double.IsNaN(rpm) || double.IsInfinity(rpm) || rpm < 100`, and the
  phase-step guard is `!(phaseStep > 0) || double.IsInfinity(phaseStep)`. (net48 has no `double.IsFinite`.)
- [ ] **H3. Forza Motorsport (311-byte) packets parse to zero speed/throttle/gear/steer.** DEFERRED.
  `ForzaUdpTelemetrySource.cs:359` reads dash fields only at `len >= 324` (Horizon). FM7 (non-native,
  in scope) emits 311-byte Dash packets -> those channels stay at defaults. Engine pulse still works
  (RPM is in the 232-byte Sled). FM2023/FM8 is auto-disabled (native Trueforce) so unaffected by default.
  NOTE: Motorsport dash offsets differ from Horizon by ~12 bytes; fix needs a real FM7 capture to validate.
- [x] **H4. stderr-drain thread leak + wrong-process reads.** FIXED: `UsbPcapFfbTap.cs` snapshots `_proc`
  into a local `stderrProc` before the drain thread, so the thread is bound to its own process (exits
  cleanly on kill, can't read a restarted process's stderr).
- [x] **H5. Unguarded async-void can crash SimHub.** FIXED: both `ModerationNoticesWindow` handlers (ack,
  appeal) now wrap the await in try/catch and degrade to the retry path.

## MEDIUM

- [ ] M1. `TrueforceDevice.cs:593` anti-burst catch-up clamp effectively unreachable; a ~50ms stall bursts back-to-back 1kHz writes.
- [x] M2. `UsbPcapFfbTap.cs:810` `PacketsParsed` (plain long) drives the liveness watchdog; non-atomic 64-bit read on 32-bit can misfire the capture-kill. FIXED: backed by `_packetsParsed` with `Interlocked.Increment`/`Interlocked.Read`.
- [~] M3. `AcSharedMemoryTelemetrySource.cs:130` reads at offsets 248/300 not bounds-checked. SKIPPED: not reachable. The MMF view maps a full page (>=4 KB), so `ReadSingle(300)` can't throw for AC's ~800-byte physics struct; original AC is the only consumer (ACC/EVO/Rally are native-TF, auto-disabled). Over-defensive; no change.
- [x] M4. `MairaIpcSource.cs:108` stale MMF/view handles never disposed on reconnect; leaks a handle per reconnect cycle. FIXED: the TryRefresh catch now disposes `_view`/`_mmf` before nulling, so the next EnsureOpen can't leak them.
- [x] M5. `WheelLedChannel.cs:231` `TryGetFeature` can block ~3s synchronously; `RpmLedController.RunTest` called it on the UI thread. FIXED: `RunTest` now sets `_testing` up front and moves the `OpenAndResolve` into the background task, so the click returns immediately and failure surfaces via the polled status.
- [x] M6. `HelperHost.cs:145` stdout pump reads `_helper` field while `Dispose` nulls it -> NRE race; snapshot to local. FIXED: both `StdoutPumpLoopCore` and `StderrLogLoop` snapshot `_helper` to a local with a null guard.
- [x] M7. `TrueforcePlugin.cs:16815` `_recoveryInProgress` non-atomic check-then-set across data-tick + UI-probe threads; both can pass and run device cleanup/bring-up concurrently. FIXED: now an `int` claimed via `Interlocked.CompareExchange` at both trigger sites; reads use `Volatile.Read`, clears `Interlocked.Exchange`.
- [~] M8. `TrueforcePlugin.cs:3072` car-store backfills on the data-tick thread mutate `Settings.CarDefaults` + on-disk files with no lock while UI mutators touch the same. MITIGATED (not fully closed): the one concrete reachable crash (a `foreach (var kv in Settings.CarDefaults)` enumeration during a data-tick structural write -> "Collection was modified") is removed by snapshotting the two raw enumerations (5219, 6604) like the existing 6321 site, shrinking the window from ms×N to the copy itself. A COMPLETE fix needs a lock around every `CarDefaults` write + enumeration (broad, deadlock-sensitive); deferred as the residual concurrent-write corruption is far rarer and rated low-priority.
- [x] M9. `TrueforcePlugin.cs:4518` `ToggleSectionOverride` ignores `PersistActiveCarOverride()`'s false return. RESOLVED as DEAD CODE: the `SetXxxOverride` wrappers + `ToggleSectionOverride` had zero callers; the live "For this car" save path (`SettingsControl.ApplyEffectSaveForCar` -> `SaveActiveCarPresetAs`) already auto-forks built-ins correctly. So there was no live bug. Removed the dead methods (kept `IsXxxOverridden`, used by the override badges). The auto-fork behavior you wanted already ships.
- [x] M10. Car presets weren't self-contained re: custom firing patterns. REFRAMED + FIXED: the store `Save` dropping `CustomEngines` is actually correct (store files reference the global library by Id). The real gap was that single-car **export** (`ExportActiveCarPreset` + `ExportCarPreset`) never bundled the referenced custom engines, so a shared car preset arrived with a dangling `CustomEngineId`. Both exports now bundle via `CollectReferencedCustomEngines` (matching pack export).
- [x] M11. Community lineage dropped on car-preset save. Re-verification overturned the defer: the broad
  reachable path is `CloneCarOverride` dropping all 6 `Community*` fields, so a single section-edit + Save
  wiped lineage on disk (Share-gate/attribution bypass, re-upload as new row). FIXED with the 2 surgical
  changes (not 13): `CloneCarOverride` now carries the 6 fields, and `CarPresetStore.Save` deletes only
  when `IsEmpty && !HasCommunityTracking` (new `CarOverride.HasCommunityTracking`). Residual narrow path:
  resetting ALL sections then a whole-override save (ovr becomes null in the dict) still deletes — rare.
- [x] M12. `BackupProjection.cs:217` Forza restore casts `Enabled`/int with no type guard and `ApplySettings` has no try/catch -> one malformed field aborts an already half-applied restore. FIXED: Forza block wrapped in try/catch, leaves live values on failure.
- [x] M13. `BackupService.cs:157` 3-way merge took `JArray` conflicts wholesale from local; list fields like `CustomEngines` edited on two PCs lost the cloud side (unrecoverable once stamped baseline). FIXED: added a `MergeArray` union (objects keyed by Id, others by value; local wins ties).
- [ ] M14. Pervasive `Task.Run(...).Wait(timeoutMs)` sync-over-async. Re-verified real but LATENT only — net48 swallows the unobserved exception, static HttpClient so no socket leak. Cheap hardening (wrap lambdas like `FireAndForgetRpc`); not blocking.
- [ ] M15. `CommunityAuth.cs:962` refresh-token plaintext if DPAPI Protect fails. Re-verified real but the consequence is a confidentiality leak (token-at-rest), NOT the claimed sign-out (kept token is valid). Near-zero reachability. Leave as-is, optionally log.
- [x] M16. TLS: `EnableTls12()` existed but only as an update-check side effect. FIXED: now set explicitly at the top of `Init` before any network client is used. (Low real risk on Win10/11, which default to TLS1.2.)
- [~] M17. `RevLimiterEffect.cs:300` EV suppression silences a game-reported redline. Re-verified BY-DESIGN: the Forza path can't even reach the telemetry-redline tier (plugin refuses to overlay Forza redline), and a SimHub-fed EV with `EngineLayout.Electric` is a deliberate user choice, so silencing is consistent. Genuine dead code found + REMOVED: `RevLimiter.UserRedlinePinned` (written, never read).
- [x] M18. `AirborneEffect` has no `OnTelemetryStall`; an airborne duck can latch on after a game closes while airborne. FIXED: added `OnTelemetryStall => _airborne = false`.
- [x] M19. `CustomEngineEditor.xaml.cs:73` `ShapeCombo.SelectedIndex = 0` hardcoded; a saved non-even-fire engine reopened as "Even-fire". FIXED: added `InferShapeIndex` so the dropdown reflects the real shape (10 of 11 shapes). Re-verification: the residual is Rotary — `BuildPatternString(Rotary, r)` is byte-identical to `EvenFire` at `2r`, so the pattern string can't distinguish them; checking Rotary first would mislabel the common EvenFire case, so EvenFire-wins is intentionally kept. A true fix needs persisting the shape (data-model change), not worth it for a cosmetic dropdown. (The data-loss protection — `_lastGeneratedPattern = null` — was added in this same change, not pre-existing.)
- [x] M20. `CarNameInputWindow.cs:115` invalid-length name returns silently with no feedback; Save looks dead. FIXED: the length hint recolors to a warning and re-focuses the input on a rejected submit.
- [~] M21. `FeedbackBoxInjector.cs:189` visual-tree heuristic can mis-target. Re-verification corrected the premise: `ShowFeedbackBox` defaults TRUE (HOMEBOX is just a dev toggle), so it IS reachable for normal users — but failure is cosmetic (try/catch, never throws). FIXED the stale "default-off" comments (`FeedbackBoxInjector.cs:20`, `TrueforcePlugin.cs:2431`). Heuristic hardening (anchor to FeedbackWidget/SHSection) left as optional polish.

## LOW / dead code / latent (sampled)

- [x] L1. `SafePath.cs:29` `IndexOf("..")` over-rejected legit filenames (`my..preset.json`), silently dropping them on import/restore. FIXED: now a segment-aware check (rejects only a `..` path segment; the GetFullPath+containment guard remains authoritative). The reserved-name `CON.json` gap is real-as-code but inert at its only (zip-entry-name) call site — left as-is.
- [ ] L2. `EffectChangelog.cs:62` array documented "oldest->newest, append-only" but is out of order (`0.1.24` before `0.1.20`); masked because consumers compare on `.Version`.
- [ ] L3. `TrueforceSettings.cs:1199` `GameSettingsSnapshot` hand-duplicates 12 scalar defaults (no test pins them in sync) and leaves effect sub-objects uninitialized (null for old presets).
- [ ] L4. `UpdateChecker.cs:73` 4-part assembly version vs 3-part parsed tag: `0.1.24 > 0.1.24.0` is false; benign for "newer?" but breaks any future `>=`/equality use.
- [ ] L5. `DrsEffect.cs` defaults are dead (always overwritten by Apply) and diverge from `DrsSettings` defaults; misleading for future tuning.
- [ ] L6. `TractionLossEffect.cs:223` `DecayAndEmit` never snaps EMA to 0, leaving the effect perpetually "active" with denormal-tiny amplitude.
- [ ] L7. `CarCylinderResolver.cs:157` `TryResolve` returns the same mutable cached `Result` to every caller; a consumer mutating it corrupts the cache entry.
- [ ] L8. `CarPresetStore.cs:113` user car store uses `StringComparer.Ordinal` while bake/resolver use `OrdinalIgnoreCase`; AC case-variant carIds can split rows / miss defaults.
- [ ] L9. `PresetUpdatesAvailableWindow.cs:64` ctor derefs `updates.Count` with no null guard.
- [ ] L10. `CarPresetStore.cs:275` `AtomicWriteAllText` uses fixed `.tmp` name; two concurrent saves of the same file collide.

## Notably clean

SettingsControl.xaml.cs (13k): symmetric event subs, async-void uniformly try/catch-wrapped, background
work marshalled via Dispatcher with stale-fetch guards. Zero High/Critical. The `Forza_<n>` -> `Car_<n>`
orphaning hazard IS handled (one-shot `DevNormalizeForzaCarIds` + per-frame runtime alias). No Clone/Equals
field-coverage omissions in the effect override clones.
