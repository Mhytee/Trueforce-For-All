# Community browser review (2026-06-28)

Static review of the ui-tabs community browser (PresetManagerControl + CommunityClient + CommunityBrowseCacheStore + pack/edit dialogs). Every finding below was adversarially verified against the code by a second pass; 2 reported bugs were rejected as not-real. Nothing here is runtime-confirmed, so the race-condition items in particular want a real-hardware check (see the test checklist).

## Fix before trusting it

These are reachable by ordinary use and break the browser in ways a user will notice. **All three FIXED 2026-06-28 (PresetManagerControl.xaml.cs, ui-tabs-layout, build clean + deployed; not yet runtime-confirmed):** #1 try/catch/finally wraps CommunityRefreshAsync so the latch always releases; #2 try/catch around the post-await PresetPreviewWindow ctor and the hover-body render; #3 a ReissueCommunityFetchAfterUnwind helper re-fires (via Dispatcher.BeginInvoke, after the finally clears the latch) at the three mid-flight stale bails. The runtime checklist below still applies as confirmation.

1. **Browser can freeze permanently (in-flight latch wedge).** `CommunityRefreshAsync` sets `_communityFetchInFlight = true` but only the network section is in a try/catch. If anything in the post-fetch UI build throws (e.g. a malformed row, a WPF binding/visual exception, OOM on a big list), the flag stays true and every later refresh/segment-switch/search/show-more AND voting silently no-ops, stuck on "Loading...", restart-only recovery. Every other in-flight latch in this file uses `try/finally`; this one doesn't. PresetManagerControl.xaml.cs 3889-4276. Fix: wrap the whole method body in `try { } finally { _communityFetchInFlight = false; }`.

2. **Malformed community upload can crash SimHub (async void, post-await work outside try).** Several `async void` handlers guard only the fetch, then do UI work after the await with no protection. Strongest path: `CommunityPreview_Click` constructs `new PresetPreviewWindow(full.Summary, full.Body)` outside the try; that ctor calls `body[...].ToObject<int?>()/<bool>()` which throw on a wrong-typed server body. No DispatcherUnhandledException handler is installed, so an escaped async-void exception can tear down SimHub. Also CommunityDelete_Click, ToggleVote, MaybeStartCommunityBodyFetch. PresetManagerControl.xaml.cs 4727-4747 / 4531-4541 / 4623-4648 / 1790-1800; PresetPreviewWindow.cs 289-493. Fix: try/catch the post-await UI/window work and/or harden the renderers against wrong-typed JSON.

3. **Fast scope switch strands the list on "Loading..." (in-flight strand).** Switch Community -> My uploads (or toggle game chips) while a fetch is still in flight: rows clear, the new fetch is suppressed by the in-flight guard, and when the old fetch returns the stale guard bails without re-firing or resetting the label. Refresh is also swallowed during the window because `force:true` doesn't bypass the in-flight guard. PresetManagerControl.xaml.cs 845-852, 3891, 4056-4062. Fix: on the stale-guard bail, re-fire the fetch for the current scope (or clear the flag and re-enter), and let `force:true` bypass the in-flight guard.

## Other confirmed bugs

Medium:
- **Deleted own-preset can reappear for up to 72h (cache invalidate/Put race).** Delete invalidates the disk cache on the UI thread, but a browse fetch started just before finishes later and `Put`s its pre-delete list back, stamped fresh. No generation token; delete path lacks the in-flight guard that vote has. Self-heals on a forced Refresh. TrueforcePlugin.cs 9804-9826; PresetManagerControl.xaml.cs 4503-4542.
- **Accented / non-ASCII car names never match in search.** `FindCarIdsByDisplayName` uses OrdinalIgnoreCase (case-folds, not accents), so "Megane" vs "Megane"(accented) miss each other. Worse, the data has mojibake: Car_3508 is stored as "CitroÃ«n BX4TC", a guaranteed hard miss no matter how typed. BuiltinCarCylinders.cs 116, 128, 1760. Fix: diacritic-fold both sides; repair the file encoding.

Low (polish):
- Search: `SanitizeSearchTerm` strips `_` instead of escaping it, so AC underscore car_ids (`bmw_m3`) never match (PresetSharingClient.cs 1030-1043); no minimum query length so 1-2 char searches build a ~300-id URL (TrueforcePlugin.cs 9866-9926); "Show more" offset uses post-dedup row count and can drift under dataset churn (PresetManagerControl.xaml.cs 3861-3866); search debounce timer isn't stopped on scope switch, firing one stray fetch (913-923, 1011-1019).
- Packs: suppressed/moderated own packs show "Items: 0" (client never sets EntryCount + server get_my_uploads omits it) (4176-4181); the inline installed-packs grid isn't refreshed after removing a pack via the "Installed packs..." dialog (2581-2591); custom engines are excluded from the local "Entries" count but included in the community "Items" count, so the same pack shows two different numbers (TrueforcePlugin.cs 14393-14512).
- Gating: the open community panel doesn't react when CommunityEnabled is turned OFF at runtime (stale, still-clickable rows; Download/Vote silently no-op) (SettingsControl.xaml.cs 5846-5881); OnActiveCarChanged churns the panel on every car/game change without a CommunityEnabled check (PresetManagerControl.xaml.cs 3792-3796).
- Init: fires ApplyView twice on first paint when the last view was Community (benign today, fragile; the gating comment is also factually wrong) (482-510).
- Delete/edit: dev-mode "check only built-in rows then Delete" silently deletes the highlighted row instead; bulk-delete takes precedence over the highlighted row whenever any checkbox is ticked (only cue is the button label); community Edit kind-detection omits the `_communityKind` fallback that delete/report use (latent wrong-table edit on empty Kind); editing a community preset in browse mode doesn't invalidate the browse cache (stale name/body until refresh).
- Cache/threading: per-entry 500-row cap causes repeated re-fetch of the same page past the cap on return visits; UI-thread cache invalidation/Clear can briefly block on synchronous disk I/O held by a background Put.

Rejected (not real): "OnLocalLibraryChanged doesn't refresh the packs grid" (the stated trigger chain doesn't exist); "Items column born Visible in XAML" (it's toggled correctly at runtime).

## Runtime test checklist

### Scope / nav
- [ ] Load a car with community data. Click Community (watch "Loading..."), then within ~1s click My uploads. Expect: My uploads loads. Watch for: stuck empty + "Loading..." forever (bug #3).
- [ ] First switch into each scope after a plugin restart (Library -> Community -> My uploads -> Community). Each first entry should auto-load without a manual Refresh.
- [ ] Rapidly fan Library -> Community -> My uploads -> Community -> Library several times. Final mode shows correct, freshly-loaded list; no scope shows another scope's rows.
- [ ] My uploads across segments (Game/Car/Engine/Pack) without leaving My uploads: each shows only your uploads of that kind.
- [ ] Open Community for car A, switch to Library, change to car B in-game, switch back to Community: reloads for car B (not car A rows under a "car B" label).
- [ ] Leave manager in Community mode, restart SimHub, reopen Presets: restores to Community and auto-loads once.

### Search / filter
- [ ] Type "mx5": MX-5 presets resolve via name tables (not just literal car_id).
- [ ] Search "Citroen" / "Citroen"(accent) / "Megane" / "Megane"(accent): all four should surface the same cars. Watch for: accented variants and even plain "Citroen" for Car_3508 returning no matches (bug).
- [ ] AC selected, search "bmw_m3" then "m3": both should find bmw_m3_e30. Watch for: "bmw_m3" returns nothing (underscore strip) while "m3" works.
- [ ] Multi-game filter: uncheck All games, check two games, empty search: union of both games; unchecking one removes only that game's rows.
- [ ] Type "mx5" (results), change to "zzzznomatch" (no leftover rows), then clear (returns to default).
- [ ] Type "mazda" fast: one Loading then results ~350ms after last keystroke (not per-char). Then type + immediately switch segment: watch for a redundant late fetch.
- [ ] Broad browse >25 rows, click "Show more" to the end: each click appends unique rows; no dupes; button hides on short page.

### Packs
- [ ] Community Items count vs post-install Entries count on a pack that contains custom engines: note if the two numbers differ.
- [ ] In My uploads, view one of your moderated/removed packs: Items count (currently shows 0).
- [ ] Remove a pack via the "Installed packs..." dialog while on the Packs segment: the inline grid should drop it (currently stale until you re-enter the segment).

### Threading / gating / robustness
- [ ] Watch the UI during a fetch on a slow connection: stays responsive (no freeze).
- [ ] Settings: turn OFF Enable community features, then open Community: empty + "off" message, no network call.
- [ ] With the Community panel open and rows loaded, turn community OFF, return and try Download/Vote: panel should reflect disabled state (currently rows stay clickable and silently no-op).
- [ ] Preview + Download across game/car/engine/pack kinds: opens / imports / clear failure message, no crash (bug #2 watch).
- [ ] Voting: optimistic update then confirm; on failure the counter rolls back; "Refresh in progress" if mid-fetch; "download first to rate" when not downloaded.
- [ ] My uploads while signed out: "Sign in to see your uploads", no network call.
- [ ] If the panel ever sticks on "Loading...", confirm whether Refresh/segment-switch recover it or only a restart does (bug #1 watch).
