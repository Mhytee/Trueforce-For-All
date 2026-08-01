# Audit 2026-07-19: dev range eb8f6a0..ebc933b (wall + naming + engine centralization)

133-agent workflow, 63 raw findings, 2 adversarial verifiers each: 52 confirmed, 10 plausible, 1 refuted.
FIXED so far: confirmed #1 (delete heal + pin-aware usage counts, commit da86da2); confirmed #13 + #14 + #15 (deliberate-pick commit discipline, save-flow engine submits removed, dangling-pin option in the variants modal, commit 24ba10b).
Also FIXED: confirmed #2 + #8 + #40 (per-tick combo re-sync to the pin, RevLimiter.IsElectric set post-pin at resolve end, dangling-pin option + custom-name/friendly-source wording on the main panel, commit 23074d6).
Also FIXED (commit 3adee16): #27 variants window width, #36 locked dict lookups, #37 no-car pin reset, #38 wall generation guard, #10's latch-on-success rider, #29 editor intro accuracy, #31 Engine pulse pointer line.
Also FIXED (commit dfb0bb0): #5 signed-out community-fact reads un-gated (anon RLS verified), #16/#11 cloud spiral clamped to canvas width, #44/#50 patron-without-tier tooltip, #29-adjacent editor SizeToContent (plausible #8), EV tooltip wording (plausible #9), variants empty-hint centered (plausible #6), #30 electric-convert warning note in the editor.
Post-audit user-reported preset-save bug fixed separately (2ba8303/e9fe28d): stale default bindings heal the store + banner + browser notify.
Also FIXED (commit ae5efe2): #12 engine readout throttled to 4 Hz; the stale-comment/dead-code sweep (#19/#20/#21/#22/#23/#24/#25/#39/#41/#43/#47/#48/#49/#51/#52): dead ActionNew/ActionManage machinery deleted (incl. OpenCustomEngineEditorForNew/OpenManageCustomEnginesDialog + orphaned doc fragments), banners/comments repointed from the removed Engine pulse dropdown to Car facts, EnginePulseEffect + TrueforceSettings docs corrected, preset-sharing-design.md Tier-1 note (gitignored, uncommitted), migration 0001 comment updated.
Also FIXED: #17 tier ranking by price floor (mig 0103 applied to prod, commit c50c0ad); #32/#33 legacy engine fields stop serializing + stop rendering in previews (commit 8f0033b).
Also FIXED (commit 0b55524): #3/#34 rename tooltip points at Car facts, plausible #3 engine-only overrides pruned (EngineOnlyOverridesPrunedV1 latch), plausible #5 cloud re-renders on panel width change, plausible #7 creator exemption warns + reports creator_exempt_active (edge fn v8 deployed, verified true on prod).
AUDIT CLOSED. Remaining by design: #42/#45/#46 (accepted polish), plausible #4 (covered by the #40 display fixes; the summary provenance field is the only untouched trace), plausible #10 EffectChangelog.Versions fold (belongs to the v0.2.4 release cut).
WONT-FIX (owner decision 2026-07-19): #7 builtin car preset engine picks (community car facts will gradually supply correct engine data; Wreckfest 2 car11 regression accepted) and #6 import-time legacy pick adoption (community content is still test data, will be wiped; file-shared old presets self-heal via detection + community facts). #10's lost-bucket rider dies with them.

===== CONFIRMED (52) =====

1. [HIGH] Engine usage analysis ignores Car facts variant pins, so the delete/remove-pack warnings claim a pinned engine is unused
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:16996 [engine-centralization/stale-analysis]
   AnalyzeEngineUsage / GetEngineUsage counts references only via legacy EnginePulseSettings.Layout==Custom + CustomEngineId (global default, game presets, car presets, active config). Post-centralization nothing writes those fields anymore; the only live reference to a custom engine is an EngineVariant.UserEngineLayout pin, and the sweep never looks at Settings.CarFacts. Result: the Customs-tab delete confirm (PresetManagerControl.xaml.cs ~3620, us

2. [HIGH] CarFactsEngineCombo shows a stale pin for multi-variant cars: selection is only synced on rebuild events, and the car-change rebuild usually runs before the variant signature exists
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:9172 [misleading-ui]
   RebuildEngineLayoutDropdown sets the combo's SelectedIndex from GetActiveVariantUserEngine, but it is only invoked on RefreshFromPlugin events (car/game change at line 1682, library reload, clicks) or when ItemsSource is null (RefreshCarFactsPanel line 9172). The pin is per-variant and FindActiveStoredVariant (TrueforcePlugin.cs:10941) returns null in the empty-signature window for a multi-variant bundle with no CarFactsSelection. On a car change

3. [HIGH] Preset-library tooltip directs users to the removed header Rename button
   src/TrueforceForAll.Plugin/PresetManagerControl.xaml:447 [stale-ui-copy]
   The Car presets Rename button tooltip still says: 'To rename the CAR instead, click Edit and use the Rename button on the header card.' Commit 8fde5f0 removed HeaderCarRenameBtn from the header card; the only car-name editor is now the Car facts Name box. Users following this user-visible instruction will find no such button. Should point at the Car facts panel's Name field.

4. [HIGH] Changelog custom-engines bullet describes UI that no longer exists and an auto-pin that no longer happens
   CHANGELOG_UNRELEASED.md:40 [changelog-accuracy]
   The v0.2.4 draft claims 'the Create and Manage links now live under the Car facts engine dropdown, and a newly created custom engine pins itself to the active car.' Both halves are false as of commits a454107/b219402: the main-panel Create/Manage-customs links were REMOVED (the Car facts engine row only carries 'Refresh' and 'Manage variants�'), authoring moved to the Manage variants modal footer, and that path deliberately does NOT auto-pin (Car

5. [HIGH] Community car-facts reads still client-gated on sign-in, so account-free users (the new default) never fetch engine/name/redline consensus
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:5468 [account-free-contradiction]
   MaybeRefreshEngineCommunityContext is the ONLY fetch path for all three fact consensuses (engine layout, car name, redline ride the same Task at lines 5486-5490; the only other caller, RefreshActiveCommunityRedlineFromServer:9304, is a post-save refresh). Its network gate is 'CommunityEnabled == true && _plugin.AuthIsSignedIn', with a comment claiming 'consensus reads are authenticated-only, so a signed-out fetch is doomed'. That comment is now f

6. [HIGH] Importing old car presets / packs silently drops the author's engine pick; bundled custom engines land unreferenced
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:15907 [import-compat]
   ImportCarPreset still merges bundled CustomEngineDefs into the library (MergeImportedCustomEngines at 15920) but nothing folds the imported Override.EnginePulse engine pick (Layout / CustomEngineId / legacy Cylinders+EngineConfig) into a CarFacts variant pin. The one-time EngineChoiceMovedToCarFactsV1 migration is latched at Init long before any import runs, and ApplyEngineSettings no longer reads those fields, so an old shared .tfcar.json or pac

7. [HIGH] Builtin CAR presets still carry legacy engine picks that are now dead; Wreckfest 2 car11 loses its V8 with no detection fallback
   builtins/cars/Wreckfest2/car11_default/car11_default (default).json:1 [builtin-cleanup]
   The range removed Layout/CustomEngineId keys from the 4 builtins/games/*.json but left the 6 builtin car presets untouched; all still carry a pre-flat-enum engine pick as '"Cylinders": N' (bdc_streetspec_ae86_v4=4, gravygarage_street_e36_touring=8, ks_nissan_skyline_r34=6, ks_toyota_ae86_tuned=4, nohesi_realistic_nissan_gtr_r35_vlct=6, Wreckfest2 car11_default=8). Runtime never reads these fields anymore, and on a fresh install the EngineChoiceMo

8. [MEDIUM] RevLimiter.IsElectric is computed before the engine-pin block writes the electric state, so EV gating lags one resolve behind a pin change
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:10595 [engine-centralization/ordering]
   In ResolveAndApplyCarFactsForActiveCar, RevLimiter.IsElectric = EnginePulse.IsElectricEffective runs at line 10595, but the new pin block that writes EnginePulse.Layout, ActiveCustomIsElectric and ElectricMode runs after it (10605-10634), and AutoLayout was already nulled at 10481. IsElectricEffective (EnginePulseEffect.cs 121-123) reads exactly those fields, so on the resolve triggered by SaveActiveVariantUserEngine the limiter is gated on the P

9. [MEDIUM] Preset/pack sharing pipeline still keyed to the legacy preset engine fields: custom engines and engine picks no longer travel, inbound legacy picks are dead forever
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:16707 [engine-centralization/stale-leftover]
   Pack export collects bundled custom engines by walking EnginePulse.CustomEngineId in game/car preset snapshots (16707-16770, 15754-15777) and nothing writes those fields anymore (the pick lives in the CarFacts pin, which is not part of a shareable preset). So after this range: (a) exporting/sharing a car preset for a car whose variant is pinned to a custom engine no longer bundles the engine def, and the recipient gets no engine pick at all; (b) 

10. [MEDIUM] Engine-choice migration silently drops picks for cars without a matching CarFacts variant, and latches complete even when it throws
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:11177 [engine-centralization/migration]
   MigrateEngineChoicesToCarFacts only relocates a per-car engine pick if a CarFacts bundle whose key ends with "/"+carId already has at least one EngineVariant row. Users upgrading from builds before variant auto-create (or whose car never produced a discriminator) have CarOverride picks but empty/absent bundles: their explicit pick is permanently lost, and these are disproportionately the cars where auto-detect fails, i.e. exactly why the pick exi

11. [MEDIUM] Supporters cloud never sleeps and edge chips jitter forever when the packed cloud is wider than the canvas (anchors past the wall)
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:6794 [perf-ui]
   RenderSupportersCloud places spiral anchors without clamping them to canvas.Width: padX = Max(8, (width - cloudW)/2), so when the packed cloud is wider than the canvas (roster growth, long names, or the 660px fallback in a narrower panel) rightmost anchors land beyond canvas.Width - chipW. In CloudTick the spring (VX += (AX-X)*0.015) then fights the wall bounce (X clamped, VX = -|VX|*0.5) every frame. The steady state is a 2-cycle with |v| = 0.6k

12. [MEDIUM] 60 Hz MeterTimer engine block now does per-tick GetActiveVariantUserEngine + a redundant GetActiveCarFactsSummary, taking _carFactsLock ~5 times every 16 ms on the UI thread
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:1366 [efficiency]
   The EngineLayoutAutoText block lives in MeterTimer_Tick (16 ms timer started on Loaded, runs regardless of active tab or Car facts expander state). It previously read the cheap _plugin.ActiveEngine property; it now calls GetActiveVariantUserEngine() (one _carFactsLock + variant scan via FindActiveStoredVariant) and GetActiveCarFactsSummary() (which itself calls GetActiveResolvedVariant + FindActiveStoredVariant twice + GetActiveVariantCommunityRe

13. [MEDIUM] Engine pin commits and community submissions fire directly from ComboBox SelectionChanged with no confirm step: keyboard browsing produces a pin write, a modal, or a silent community submission per keystroke
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:5765 [ux-data-quality]
   CarFactsEngine_Changed -> ApplyEngineDropdownSelection -> CommitEnginePin runs on every SelectionChanged. A focused, closed WPF ComboBox changes selection on every arrow-key press or mouse-wheel notch, so each keystroke persists a pin (ScheduleCarFactsFlush + full re-resolve) and, once the one-time car-data consent has been granted, MaybePromptToSubmitEngineData submits a community engine-layout correction SILENTLY per distinct layout browsed (th

14. [MEDIUM] Save flows still call MaybePromptToSubmitEngineData: a feel-only Engine save or car-preset save silently re-submits the old engine pin every session
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:11666 [stale-hook]
   The comment at 1468-1471 states engine-data submission now fires from CommitEnginePin with 'no save-flow hook needed', but three save paths still call MaybePromptToSubmitEngineData (11666 EffectSave for EffectKind.Engine, 11895 unsaved-car-preset flow, 12011). The engine choice can no longer be dirtied via the Engine section, so these prompts are now decoupled from what the user just saved: with a pin that diverges from auto-detect and consent al

15. [MEDIUM] Opening Manage variants silently erases dangling custom-engine pins
   src/TrueforceForAll.Plugin/CarFactsVariantsWindow.cs:434 [data-loss]
   When a variant's pin is (Custom, id) but that id is no longer in Settings.CustomEngines, FindEngineOptionIndex (line 337) maps it to index 0 (Auto). The ComboBox's initial SelectedIndex binding (-1 to 0) fires EngineCell_Changed, whose no-op guard compares combo=Auto(null) against stored pin=Custom and, on mismatch, calls SaveActiveCarVariantUserEngineById(row.Id, null, null). Merely opening (or Reload-ing) the window permanently deletes the pin 

16. [MEDIUM] Cloud spiral layout ignores canvas width; overflow pins chips to the right wall and the 16ms timer never sleeps
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:6777 [supporters-wall-physics]
   The spiral anchor placement loop (t up to 400, radius 4+5.5t) resolves overlap only; it never constrains anchors to the canvas width (capped at 700, or ActualWidth as low as 320). Vertical size is fitted afterward (canvas.Height from the anchor bounding box) but horizontal is not: padX = Math.Max(8, ...) only re-centers, so once the cloud's natural width exceeds the canvas (roughly 60-70 chips at 700px, far fewer at 320-400px panels), anchors lan

17. [MEDIUM] tier_level ranks tiers by MAX member pledge, so one over-pledging member in a low tier steals gold from the real top tier
   supabase/migrations/0102_supporters_tier_level.sql:14 [backend-ranking]
   tier_rank per supporter row is the member's currently_entitled_amount_cents (patreon-supporters-sync fetchMembers sets tier_rank: amount), which is what the member actually pays, not the tier's price. Patreon allows pledging above a tier's price. The 0102 tiers CTE ranks tiers by max(s.tier_rank) desc, so a single member of a lower tier custom-pledging more than every currently-active member of a higher tier promotes the whole lower tier to tier_

18. [MEDIUM] Release-notes draft describes the superseded custom-engine link location and an auto-pin that no longer happens
   CHANGELOG_UNRELEASED.md:40 [stale-doc]
   Bullet says 'the Create and Manage links now live under the Car facts engine dropdown, and a newly created custom engine pins itself to the active car.' Both claims describe the intermediate state (commits 0b59f5e/137c653/a454107) that later commits in the same range retired: b219402 moved authoring to the Manage variants modal footer (explicitly with NO auto-pin), and SettingsControl.xaml.cs:5786-5788 records that the main-panel links were retir

19. [MEDIUM] Orphaned engine-dropdown action machinery: unreachable ActionNew/ActionManage kinds, dead OpenCustomEngineEditorForNew/OpenManageCustomEnginesDialog, banner still claims '+ actions'
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:5628 [stale-code]
   RebuildEngineLayoutDropdown no longer adds action entries (its own comment at 5691-5693 says the dropdown 'carries no action links here'), so EngineDropdownKind.ActionNew/ActionManage (5628), the switch cases at 5750-5756, OpenCustomEngineEditorForNew (5794-5829) and OpenManageCustomEnginesDialog (5831-5834) are unreachable dead code left behind by the range. The section banner at 5622 still reads 'Engine layout dropdown (dynamic: built-ins + cus

20. [MEDIUM] Truncated orphaned doc-comment fragment above RebuildEngineLayoutDropdown, fused with the summary of a deleted method
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:5639 [stale-comment]
   Lines 5639-5641 are the OLD summary of RebuildEngineLayoutDropdown, cut off mid-sentence ('...plus the "Custom..." and') and immediately followed at 5642-5647 by a second '<summary>' belonging to the deleted inline variant-picker refresh method ('Refresh the inline variant picker for the active car... The "Manage variants..." link is collapsed in lockstep'). Neither block attaches to any code; the real, current summary starts at 5653. The fragmen

21. [MEDIUM] XAML comment says the Car facts engine picker is 'the same picker as the Engine Pulse section's Layout dropdown' and promises a meta line, both removed
   src/TrueforceForAll.Plugin/SettingsControl.xaml:1115 [stale-comment]
   Comment above CarFactsEngineCombo reads: 'Engine type (inline editable, same picker as the Engine Pulse section's Layout dropdown) + a meta line for the rev ceiling and the fact's source.' The Engine Pulse Layout dropdown (EngineLayoutCombo) was deleted in this range (the Car facts combo is now the ONLY picker), and the meta line (CarFactsEngineMetaText) was removed too, with the rev ceiling folded into EngineLayoutAutoText. The very next comment

22. [MEDIUM] Section banner 'Community context row on the Engine pulse panel' now points at the wrong panel
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:5426 [stale-comment]
   The banner over MaybeRefreshEngineCommunityContext still says the community context row lives 'on the Engine pulse panel'. This range moved EngineCommunityText into the Car facts panel on the active card (SettingsControl.xaml:1136, inside the CarFactsRowsPanel region); the Engine pulse expander now hosts feel knobs only. CLAUDE.md already warns that these banners have drifted, but this one was made wrong by the audited range itself.

23. [MEDIUM] Auto-detect readout comments still reference 'Layout=Auto' and picking a value 'in the Layout dropdown (which then triggers the existing save -> share flow)'
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:1406 [stale-comment]
   In the EngineLayoutAutoText refresh block, the comment at 1402-1409 tells the reader the user can 'pick that value in the Layout dropdown (which then triggers the existing save -> share flow)'. The Layout dropdown is gone; the pick now goes through CarFactsEngineCombo -> ApplyEngineDropdownSelection -> CommitEnginePin -> SaveActiveVariantUserEngine + MaybePromptToSubmitEngineData, and there is no preset save involved. The block header at 1356-135

24. [MEDIUM] XAML button-order comment still claims car renaming 'lives on the header card during Edit'
   src/TrueforceForAll.Plugin/PresetManagerControl.xaml:439 [stale-comment]
   The comment above the Car presets button row ends: 'Renaming the CAR (vs the preset) lives on the header card during Edit, so there's no second "name" button here.' The header-card Rename button was removed in this range; car naming lives in the Car facts panel Name box. The conclusion (no second name button) is still right, but the stated reason points to a control that no longer exists. Same missed-update family as the line 447 tooltip.

25. [MEDIUM] preset-sharing design doc still classifies EnginePulse.Layout/CustomEngineId/CustomFiringPattern(Name) as live Tier 1 preset fields
   docs/preset-sharing-design.md:203 [stale-doc]
   The 'Field classification' section, which claims to be 'grounded in the actual settings classes in TrueforceSettings.cs', lists Tier 1 (car identity, consensus-shared) as EnginePulse.Layout, CustomEngineId, CustomFiringPattern, CustomFiringPatternName. After this range those four fields are LEGACY (deserialization + one-time migration only, never read at runtime); the car-identity truth is the per-variant Car facts pin EngineVariant.UserEngineLay

26. [MEDIUM] Changelog omits several user-visible changes the range introduced
   CHANGELOG_UNRELEASED.md:36 [changelog-accuracy]
   The draft says nothing about: (1) the new per-row Engine dropdown in the Manage variants window (the only way to pin an engine on a variant you are not currently driving, a headline part of the centralization); (2) the header 'Rename�' button removal / car naming consolidating into the Car facts Name box (returning users will look for the button); (3) the removal of the read-only firing-pattern readout from Engine pulse; (4) electric authoring be

27. [MEDIUM] Manage variants grid does not fit at the default 720px window width; Delete column is clipped behind a horizontal scrollbar
   src/TrueforceForAll.Plugin/CarFactsVariantsWindow.cs:79 [layout]
   Column minimums sum to 710px (Label star MinWidth 160 + Source 100 + Cyl 60 + Engine 170 + Redline 130 + Delete 90), but the window is Width=720 with 18+18 root margins, leaving roughly 665-670px of client width for the DataGrid (less another ~17px when the vertical scrollbar shows). Every open therefore shows a horizontal scrollbar with the Delete column (and part of Redline) cut off until the user scrolls or resizes. Width ~770-780 or trimming 

28. [MEDIUM] 'Your pick' readout leaks raw internal source tokens and shows 'Custom (advanced)' instead of the pinned custom engine's name
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:1444 [copy-accuracy]
   The new pin-disagrees-with-auto branch builds the suffix from ep.AutoLayoutSource verbatim: users see 'Auto would be Inline-4 (baked).' or '(cache)' where the Auto-detected branch directly above translates the same tokens to friendly phrases ('from built-in car list', 'cached from earlier session'). Additionally, when the pin is a custom engine, LayoutDisplayName(Custom) renders 'Your pick: Custom (advanced)' rather than the custom's actual name 

29. [MEDIUM] Custom engine editor intro promises 'the pattern below fills in by itself' but the hand-edit guard stops it filling in for edited/existing engines
   src/TrueforceForAll.Plugin/CustomEngineEditor.xaml:36 [copy-accuracy]
   The intro text states the pattern auto-fills from shape+count. That is only true for a fresh entry that has never been hand-edited: Init sets _lastGeneratedPattern = null for any existing engine ('saved pattern is treated as user-owned'), and RegeneratePattern refuses to overwrite unless the box exactly matches the last generated string. So when EDITING any saved engine, or after any manual keystroke, changing the shape dropdown or count slider v

30. [MEDIUM] Saving a legacy electric custom silently converts it to a combustion engine with no warning in the dialog
   src/TrueforceForAll.Plugin/CustomEngineEditor.xaml.cs:288 [ux-trap]
   With the Electric checkbox gone, a user who opens their existing electric custom just to rename it sees a pre-seeded even-fire 4-cyl pattern (electric defs have empty Pattern, so Init seeds the default) with nothing indicating the def used to be electric. Clicking Save writes IsElectric=false + that 4-cyl pattern, so every variant pinned to it flips from muted-hum/silent to inline-4 pulses. The conversion is a deliberate design decision but the U

31. [MEDIUM] Engine pulse panel gives users no visible pointer that the Engine type control moved to Car facts
   src/TrueforceForAll.Plugin/SettingsControl.xaml:1648 [discoverability]
   The centralization removed the Engine type dropdown, variant row, pattern readout and custom links from the Engine pulse panel, replacing them only with a XAML comment users cannot see. A returning 0.2.3 user opening Engine pulse to change a car's engine type finds the section silently gone with no 'Engine type now lives in Car facts on the active car card' hint. Every comparable removal in this codebase (e.g. per-gear redline editor note in the 

32. [MEDIUM] Stale legacy engine fields keep serializing into every new save, export, and community upload; old-plugin recipients will apply them
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:6673 [legacy-field-hygiene]
   The migration relocates picks out of CarOverrides but never clears the legacy fields anywhere: Settings.EnginePulse (global), preset snapshots in the library, and CarOverride.EnginePulse all keep their pre-migration Layout/CustomEngineId/CustomFiringPattern(Name)/Cylinders values, the Clone(EnginePulseSettings) used for every new override/preset/snapshot save still copies all of them (6669-6680), applying a preset copies them back into Settings.E

33. [MEDIUM] PresetPreviewWindow still renders legacy engine keys (Layout, Custom engine id, Cylinders) under Engine pulse
   src/TrueforceForAll.Plugin/PresetPreviewWindow.cs:581 [misleading-ui]
   BuildFieldsBlock renders every non-null primitive field of the EnginePulse section (AddSection at 479 -> 544-553 -> 562-588 dumps all properties alphabetically). Old community preset bodies, and per the stale-serialization finding also newly uploaded ones, carry Layout/CustomEngineId/CustomFiringPattern/CustomFiringPatternName/Cylinders/EngineConfig/FiringOrderEnabled, so the preview shows 'Layout: V8' etc. as if the engine type were part of the 

34. [MEDIUM] Car preset Rename tooltip still directs users to the header-card Rename button this range deleted
   src/TrueforceForAll.Plugin/PresetManagerControl.xaml:447 [stale-copy]
   CarRenameBtn's tooltip ends with 'To rename the CAR instead, click Edit and use the Rename button on the header card.' Commit 8fde5f0 removed HeaderCarRenameBtn (car naming now lives solely in the Car facts Name box), and the .cs even documents the removal at PresetManagerControl.xaml.cs:3349-3352, but the user-facing tooltip was not updated. A user following it finds no such button. Should say the Car facts panel's Name box on the active card.

35. [MEDIUM] CHANGELOG_UNRELEASED overclaims that per-car engine picks 'carry over automatically'
   CHANGELOG_UNRELEASED.md:39 [release-notes-accuracy]
   Line 39 says 'Per-car engine picks you already made carry over automatically; picks stored inside game presets are dropped and auto-detection takes over.' The migration itself documents a third bucket the note omits: cars with a per-car pick but no CarFacts bundle yet (a car not driven since the variants model started recording bundles) lose the pick entirely ('Cars with a pick but no facts bundle yet lose the pick', TrueforcePlugin.cs:11175-1117

36. [LOW] GetActiveCarVariantUserEngineById / SaveActiveCarVariantUserEngineById read Settings.CarFacts outside _carFactsLock
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:11114 [engine-centralization/locking]
   Both new ById helpers do Settings.CarFacts.TryGetValue(key, out var bundle) BEFORE taking _carFactsLock and only lock around the EngineVariants iteration (11114-11132, 11134-11160). The codebase's own rule (comment at 10518-10520 and the locked lookup in FindActiveStoredVariant 10947-10950) is that unlocked TryGetValue can misread mid-rehash because the telemetry thread's variant auto-create adds CarFacts keys under the lock. The variants modal c

37. [LOW] Resolve early-return on empty carId leaves the previous car's pin state live (EnginePulse.Layout, CustomPattern, _activePinnedCustomEngine)
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:10495 [engine-centralization/staleness]
   On a car change to no-car (back to menu), the handler calls ResolveAndApplyCarFactsForActiveCar("") (3928) which returns at `if (string.IsNullOrEmpty(carId)) return;` after clearing only the Auto* fields. EnginePulse.Layout, CustomPattern, ActiveCustomIsElectric and _activePinnedCustomEngine keep the previous car's pin. The fallback ApplyActiveCarOverride at 3939 then applies the GLOBAL engine settings for feel, but ApplyEngineSettings uses the s

38. [LOW] Supporters wall: community-disabled path does not bump _supportersWallGen, so an in-flight fetch can render the cloud over the 'turn on community features' message
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:6685 [race]
   RefreshSupportersWallAsync guards stale continuations with _supportersWallGen, but the CommunityEnabled != true early path (6685-6694) clears the panel without incrementing the generation. Sequence: refresh A starts with community on (gen=N, fetch in flight); user disables community; tab reselect runs refresh B which takes the disabled path (clear + notice, gen still N); fetch A completes, gen check passes (N==N), Children.Clear + RenderSupporter

39. [LOW] Dead code: ActionNew/ActionManage dropdown sentinels, OpenCustomEngineEditorForNew and OpenManageCustomEnginesDialog are unreachable, including the freshly added click-path diagnostics
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:5750 [dead-code]
   RebuildEngineLayoutDropdown now only adds BuiltIn and Custom items (5666-5690), so ApplyEngineDropdownSelection's ActionNew/ActionManage cases (5750-5756) can never match, making OpenCustomEngineEditorForNew (5794) and OpenManageCustomEnginesDialog (5831) unreachable: their only remaining callers are those dead cases. This strands the '[TF4ALL] Custom engine editor opening/closed' diagnostics added in a454107/e10ec9e for the create-click bug (the

40. [LOW] Dangling custom pin renders contradictory state: combo falls back to Auto while the auto-detect line says 'Your pick: Custom' (never naming the custom)
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:5718 [misleading-ui]
   When a pinned custom engine id is missing from the library (e.g. synced settings from another machine, or a delete that raced the pin-clear pass), FindEngineDropdownIndex returns 0 (Auto) so the combo shows Auto, but GetActiveCarFactsSummary.EngineTypeIsUserOverride and GetActiveVariantUserEngine still report the pin, so EngineLayoutAutoText shows 'Your pick: Custom'. Separately, even for a healthy custom pin the 'Your pick' branches print Layout

41. [LOW] Stale copy after the move: 'use Test to A/B' now renders in the Car facts panel far from the Engine pulse Test button; XAML comment still describes the removed Layout dropdown mirror and meta line
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:1437 [copy-staleness]
   The could-not-detect message ('Pick the closest match from the list, or use Test to A/B') was written when it sat next to the Engine pulse section's Test button; it now appears under the Car facts engine combo where there is no Test control in sight (Test lives in the Engine pulse effect card, SettingsControl.xaml:1601). Also SettingsControl.xaml:1115-1117 still comments the combo as 'same picker as the Engine Pulse section's Layout dropdown + a 

42. [LOW] Per-row 'Auto (...)' label can misstate what detection actually resolves to
   src/TrueforceForAll.Plugin/CarFactsVariantsWindow.cs:289 [misleading-ui]
   BuildEngineOptions labels the Auto entry from variant-stored fields only, but the resolver has extra branches: (1) EngineConfig.Custom rows are labelled 'Auto (community custom)' unconditionally, while ResolveAndApplyCarFactsForActiveCar only rides the community custom when v.Source==Community AND the cached consensus custom is present (TrueforcePlugin.cs 10643-10649); otherwise it falls through to catalog/telemetry, so the label claims a custom 

43. [LOW] Dead engine-dropdown action plumbing left behind after the Create/Manage links were removed
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:5750 [stale-code]
   RebuildEngineLayoutDropdown no longer adds ActionNew/ActionManage items (comment at 5691-5693), so ApplyEngineDropdownSelection's ActionNew/ActionManage cases, OpenCustomEngineEditorForNew (5794) and OpenManageCustomEnginesDialog (5831) are unreachable from the dropdown. OpenCustomEngineEditorForNew also still auto-pins the new custom via CommitEnginePin, contradicting the new no-auto-pin authoring flow in the variants modal, so if it is ever rew

44. [LOW] Active Patreon patron with no entitled tier is rendered and tooltipped as a one-time supporter
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:6959 [chip-labeling]
   fetchMembers can produce an active patron with tier=null (currently_entitled_tiers empty, which Patreon returns for legacy/custom pledges not attached to a tier). Such a row is source='patreon' with tier null: the 0102 tiers CTE excludes it (tier is not null filter) so tier_level is null, and BuildSupporterChip's badge branch requires a non-blank Tier, so the chip falls into the else branch and gets ToolTip 'One-time supporter' plus the neutral b

45. [LOW] Already-applied prod RPC reorders tier sections on shipped old clients (cosmetic, grouping survives)
   supabase/migrations/0101_supporters_flat_ranked_wall.sql:17 [old-client-compat]
   Shipped clients (v0.1.26 stable, v0.2.1 beta) parse display_name+tier and group rows into tier sections in encounter order (RenderSupportersGrouped byTier dictionary). The old ORDER BY (0050) kept tiers contiguous and ordered sections by the tier's top pledge; the new flat `order by lifetime_cents desc` interleaves tiers, so on old clients section ORDER now follows each tier's top-lifetime member: e.g. the 'One-time supporters' section jumps abov

46. [LOW] Mouse repulsion shoves the hovered chip out from under the cursor, so the hover swell can never settle
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:6854 [ux-polish]
   CloudTick applies repulsion to every body within 120px of the cursor whenever the mouse is over the canvas, force up to ~2.6 px/frame^2 at zero distance. Hovering a chip therefore accelerates that exact chip away from the pointer; MouseLeave fires almost immediately and AnimateChipHover reverses the swell, so the 1.12x grow + brighten is only ever a flicker while the pill flees. The two features (scatter and hover-swell) fight by construction. If

47. [LOW] EnginePulseEffect doc says ActiveCustomIsElectric is 'set by ApplyEngineSettings from the looked-up CustomEngineDef'
   src/TrueforceForAll.Engine/Effects/EnginePulseEffect.cs:126 [stale-comment]
   The XML doc for ActiveCustomIsElectric (lines 125-129) names ApplyEngineSettings as its writer. After the centralization, ApplyEngineSettings applies feel only; ActiveCustomIsElectric is written by the car-facts resolution path in TrueforcePlugin (ResolveAndApplyCarFactsForActiveCar, lines 10626 and 10656), and TrueforcePlugin.cs:6476 explicitly states Layout/CustomPattern/ActiveCustomIsElectric 'are owned by the car-facts resolution'. A maintain

48. [LOW] Migration 0001 comment references the removed EngineLayoutCombo control name
   supabase/migrations/0001_carfacts_init.sql:83 [stale-comment]
   The car_fact_submissions schema comment justifies the single engine_layout fact type with 'axes users can't independently assert from the EngineLayoutCombo.' That control was deleted in this range; the picker is CarFactsEngineCombo on the Car facts panel. The rationale still holds, but the named anchor no longer exists anywhere in the repo, so a grep from this comment dead-ends. Editing an applied migration's comment text is safe (idempotent, no 

49. [LOW] Could-not-detect line still says 'use Test to A/B' after moving into the Car facts panel
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:1435 [copy-staleness]
   'Pick the closest match from the list, or use Test to A/B.' was written when this text sat inside the Engine pulse section next to its Test button. It now renders under the Car facts engine combo on the active card, where there is no Test control in sight ('the list' is also unlabeled). Consider 'the Engine dropdown above' and 'the Test button on the Engine pulse effect'.

50. [LOW] Patreon supporter with a missing tier string is labeled 'One-time supporter'
   src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:6937 [copy-accuracy]
   The badge/tooltip branch requires patreon AND a non-blank Tier; a Patreon row whose tier is null (allowed by migration 0102: tier_level CTE filters 's.tier is not null', and the row itself can have null tier) falls into the else branch, getting the neutral blue chip and the 'One-time supporter' tooltip even though they are a recurring patron. A safer else-tooltip would be 'Supporter' or branch on Source alone.

51. [LOW] Stale comments left by the range still reference the removed Engine pulse Layout dropdown
   src/TrueforceForAll.Plugin/SettingsControl.xaml:1115 [staleness]
   Non-user-facing but created/left stale by this range and misleading for the next editor: (1) SettingsControl.xaml:1115-1116 comment says the Car facts combo is the 'same picker as the Engine Pulse section's Layout dropdown', which no longer exists; (2) SettingsControl.xaml.cs:1406 comment still says 'pick that value in the Layout dropdown'; (3) the section banner at SettingsControl.xaml.cs:5426 reads 'Community context row on the Engine pulse pan

52. [LOW] Stale comments left by the centralization: XAML claims the Engine Pulse Layout dropdown still exists; settings model claims ApplyEngineSettings still folds legacy fields
   src/TrueforceForAll.Plugin/SettingsControl.xaml:1115 [stale-comments]
   Three comments now contradict the code this range shipped: (1) SettingsControl.xaml:1115-1116 '<!-- Engine type (inline editable, same picker as the Engine Pulse section's Layout dropdown)...' - that dropdown was removed; the Car facts combo IS the only picker. (2) TrueforceSettings.cs:1638-1640 says of the pre-flat-enum fields 'one-time migration in ApplyEngineSettings folds them into Layout on first load' - ApplyEngineSettings no longer folds a
===== PLAUSIBLE (10) =====

1. [HIGH] Deleting a custom engine no longer heals the live effect: the deleted engine keeps playing until the next resolve
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:17257 [engine-centralization/live-apply]
   DeleteCustomEngines (17244-17258) and RemovePack (17362) end with ApplyActiveCarOverride() as their live re-apply step, but since this range ApplyEngineSettings applies FEEL only and never writes EnginePulse.Layout / CustomPattern / ActiveCustomIsElectric. RewriteEnginePulseToAuto clears the variant pin in Settings.CarFacts, yet nothing calls ResolveAndApplyCarFactsForActiveCar/ReresolveActiveCarFacts, so the runtime effect keeps Layout=Custom, t

2. [MEDIUM] Editing a legacy electric custom silently converts it to a 4-cyl combustion pattern with zero warning
   src/TrueforceForAll.Plugin/CustomEngineEditor.xaml.cs:288 [misleading-ui]
   With the Electric checkbox removed, an electric def opened for edit is visually indistinguishable from a combustion one: Init seeds the empty Pattern with even-fire 4 (line 61-65), and Save unconditionally writes IsElectric=false + that seeded pattern. A user who opens their old electric custom just to rename it and hits Save flips every variant pinned to it from EV hum to a generic even-fire 4-cylinder pulse, an unexplained haptic change unrelat

3. [MEDIUM] Migration leaves engine-only per-car overrides behind: Engine section keeps its 'overridden' badge and frozen feel with no remaining reason
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:6438 [override-badge-staleness]
   IsEngineOverridden is still 'GetActiveCarOverride()?.EnginePulse != null'. Correct for feel overrides, but the most common historical reason a per-car EnginePulse override existed was the engine pick, and MigrateEngineChoicesToCarFacts pins the pick without removing or pruning the override it came from. Every such car keeps showing the Engine 'overridden' badge and keeps its feel values frozen against future preset edits, even when the snapshot-e

4. [LOW] Dangling Custom pin reports provenance "user" while the wheel plays Auto, and SaveActiveVariantUserEngine accepts Custom with an empty engine id
   src/TrueforceForAll.Plugin/TrueforcePlugin.cs:11442 [engine-centralization/provenance]
   GetActiveCarFactsSummary sets EngineTypeIsUserOverride/provenance="user" from UserEngineLayout != null (11442-11443) without checking resolvability, but ResolveAndApplyCarFactsForActiveCar demotes a dangling Custom pin (engine missing from the library, e.g. after a cloud restore onto a machine without the def) to Auto (10616-10622). The UI then claims a user pick is active while auto-detection is actually driving, and the resolve path logs the "n

5. [LOW] RenderSupportersCloud width fallback forces a 660 px canvas when ActualWidth is 0 (first render) or under 320, overflowing narrower panels
   C:/Users/mhyte/Documents/SimHubTrueforce/src/TrueforceForAll.Plugin/SettingsControl.xaml.cs:6755 [layout]
   width = SupportersWallPanel.ActualWidth; if NaN or < 320 it is forced to 660. On the first Support-tab render ActualWidth can still be 0 (layout not yet passed), and on a genuinely narrow SimHub window (< 320-660 px available) the fixed 660 px canvas is wider than its parent StackPanel, so chips draw past the panel edge (Canvas does not clip) or get cut by an ancestor. There is no MaxWidth binding or SizeChanged re-render, so the cloud never adap

6. [LOW] Empty-state hint is docked to the window bottom instead of centered
   src/TrueforceForAll.Plugin/CarFactsVariantsWindow.cs:162 [polish]
   _emptyHint is added with DockPanel.SetDock(Dock.Bottom) while the collapsed DataGrid remains the fill child, so with zero variants the hint renders as a strip just above the footer with a large blank region between it and the header (window MinHeight 320). Its VerticalAlignment=Center has no effect in a bottom-docked auto-height slot. Cosmetic only; making the hint the fill child (or swapping it into the grid's slot when empty) would center it as

7. [LOW] Creator exemption fails silent-open: null creatorId reverts to the revoke-the-creator behavior with no signal
   supabase/functions/patreon-supporters-sync/index.ts:90 [edge-function]
   The exemption depends entirely on resolveCampaign extracting relationships.creator.data.id from /v2/campaigns?include=creator (a valid include per the Patreon v2 API; the added include is unlikely to break the previously-working campaigns call, and a hard failure still aborts safely via the existing camp.id null 502 path). But if the relationship comes back absent or unparsable while data[0].id resolves, creatorId is null and reconcileLinkedEntit

8. [LOW] Custom engine editor keeps fixed Height=460 with NoResize after two rows were removed, leaving a large dead band above the buttons
   src/TrueforceForAll.Plugin/CustomEngineEditor.xaml:5 [layout]
   Content now measures roughly 340-360px (intro ~4 wrapped lines, four input rows, help row, buttons) against a ~425px client area, and the star row (Grid.Row 6) pushes the buttons to the bottom, so the dialog shows ~70-100px of empty space between the pattern help and Cancel/Save. Since ResizeMode=NoResize the user cannot tidy it. Either shrink Height (~390-400) or use SizeToContent=Height.

9. [LOW] Electric-cars tooltip says 'detected as pure electric' but the setting now also governs pinned-Electric cars
   src/TrueforceForAll.Plugin/SettingsControl.xaml:1647 [copy-accuracy]
   With engine type centralized, a user can PIN Electric (or an electric community/imported custom) in Car facts, and the EV behavior combo applies to that too via the ElectricMode cascade; the tooltip's 'cars detected as pure electric' undersells that the user's own Car facts pick also routes here. Minor wording update: 'cars detected or set as electric in Car facts'.

10. [LOW] Engine-type relocation has no EffectChangelog entry, and Versions already lags two shipped releases
   src/TrueforceForAll.Plugin/EffectChangelog.cs:611 [in-app-changelog]
   EffectChangelog.Versions ends at 0.2.1 while the current pre-release is 0.2.3 (per CHANGELOG_UNRELEASED baseline), so the fold-at-cut step has already been skipped at least once. The engine-type move is exactly the kind of change the in-app What's-new banner exists for: the Layout dropdown vanished from the Engine pulse panel and reappeared in Car facts, which users will otherwise experience as a missing control. It belongs as a ChangelogVersion 
