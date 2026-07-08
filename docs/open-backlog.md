# Open backlog (distilled from completed audits)

## Release prep for 0.2.0 (ui-tabs-layout)

- [x] **Port DRIVER testing mode to ui-tabs.** DONE 2026-07-08 (commit 8f16057): manual
  hunk-for-hunk port of d6aace5 (TFFADriverChannel.cs verbatim; settings, checkbox, access
  code, plugin lifecycle) with [TF4ALL] log prefix + BackupProjection classification.
  Build clean; runtime smoke test on the wheel still outstanding.
- [x] v0.1.25 spike-taming fix (5ee2ce4): already ported (tombstone at TrueforcePlugin.cs
  ~L11823). Issue-13 pause-release + telemetry-stall fixes: present as this branch's reworked
  default-off NOLOCK variant.
- [x] csproj Version bumped 0.1.24 -> 0.2.0 (2026-07-08) so dev builds stop trailing the
  published v0.1.25 and the branch's scale is reflected at release.

The full audit catalogs live in `docs/archive/` (ux-audit.md, codebase-audit-2026-06.md,
community-browser-review.md). Everything fixed or closed stays there; this file carries
only what is still genuinely open, so nothing here reads as done when it isn't.
Code-health / refactoring work is tracked separately in `docs/agent-audit.md`.

## From codebase-audit-2026-06 (archive has full details + line refs)

Open items:

- [ ] **H3** Forza Motorsport 311-byte Dash packets parse to zero speed/throttle/gear/steer
  (FM7, non-native, in scope). Fix shape known (`else if (len >= 311)` with Horizon-minus-12
  offsets) but needs a real FM7 capture to validate before shipping.
- [ ] **M1** TrueforceDevice anti-burst catch-up clamp effectively unreachable; a ~50ms stall
  bursts back-to-back 1kHz writes. Correctness tidy-up, not a wheel-safety issue.
- [ ] **M8 residual** CarDefaults concurrent-write window shrunk (enumerations snapshotted) but
  a complete fix needs a lock around every write + enumeration. Deadlock-sensitive; deferred.
- [ ] **M11 residual** Narrow lineage-loss path remains: resetting ALL sections then a
  whole-override save (override becomes null in the dict) still deletes the store file. Rare.
- [ ] **M14** Pervasive `Task.Run(...).Wait(timeoutMs)` sync-over-async. Latent only on net48;
  cheap hardening = wrap lambdas like FireAndForgetRpc.
- [ ] **M15** CommunityAuth refresh token stored plaintext if DPAPI Protect throws. Confidentiality
  leak at rest, near-zero reachability. Optionally log the fallback.
- [ ] **M21 polish** FeedbackBoxInjector heuristic hardening (anchor to FeedbackWidget/SHSection).
  Failure is cosmetic and caught.
- [ ] **L2** EffectChangelog Versions array out of documented oldest-to-newest order. Masked today.
  Will be resolved by the changelog-to-JSON extraction (agent-audit D2); fix ordering then.
- [ ] **L3** GameSettingsSnapshot hand-duplicates 12 scalar defaults with no test pinning them in
  sync; effect sub-objects null for old presets. Overlaps agent-audit S12.
- [ ] **L4** UpdateChecker: 4-part assembly version vs 3-part tag; `0.1.24 > 0.1.24.0` is false.
  Benign for "newer?", breaks future equality use.
- [ ] **L5** DrsEffect defaults are dead (Apply always overwrites) and diverge from DrsSettings.
  Same trap in other effects; tracked as agent-audit U17.
- [ ] **L6** TractionLossEffect DecayAndEmit never snaps EMA to 0; effect stays "active" with
  denormal-tiny amplitude.
- [ ] **L7** CarCylinderResolver.TryResolve returns the same mutable cached Result to every caller;
  a mutating consumer corrupts the cache entry.
- [ ] **L8** CarPresetStore uses StringComparer.Ordinal while bake/resolver use OrdinalIgnoreCase;
  AC case-variant carIds can split rows / miss defaults.
- [ ] **L9** PresetUpdatesAvailableWindow ctor derefs `updates.Count` with no null guard.
- [ ] **L10** AtomicWriteAllText uses a fixed `.tmp` name; two concurrent saves of the same file collide.

Closed by design (do not reopen): M3, M17 (EV suppression), M19 residual (Rotary reopens as
Even-fire; needs a data-model change for a cosmetic dropdown), L1 residual (CON.json zip-entry gap
inert at its only call site).

## From community-browser-review (archive has the full checklist)

- [ ] **U1** Init fires ApplyView twice on first paint when the last view was Community. Benign,
  owner-deferred; safe fix shape documented in the Init comment.
- [ ] **B4 (owner decision)** Voting rollback on failure vs owner's floated queue-and-retry.
  Under discussion; no code change until decided.
- [ ] **F4 (owner decision)** Pack "Items" (community, includes custom engines) vs "Entries"
  (local, excludes them) show different counts for the same pack. Parked.
- [ ] **Runtime checklist remainder**: C1-C7 (search) blocked on seeding server test data
  (no bmw_m3 / accented-name presets exist on the server yet); D2, D3, D6, E1-E4, F1-F3,
  G1-G2 untested; A6/B1 accepted as untestable-by-hand (code-trace verified).
  Full checklist: docs/archive/community-browser-review.md.

## From ux-audit (2026-06-16)

Closed 2026-07-07: 100 fixed, 4 won't-fix by design (MANUALPIN gate, mixed dialog construction
internals, "Set as default" phrasing trio, preview vote glyphs). Nothing open.
