# Copy trim proposal (2026-08-16)

A re-audit of the 2026-08-13 sweep after roughly 60 commits of copy churn (the
iRacing rework, the Feel ladder, the OLED "Nothing" screen, the new tooltips on
the force rows). Every old item was re-checked against the current tree and
every surface was re-read for copy the churn itself introduced.

Where it landed: 55 of the 59 old items survive, 4 were already resolved by the
churn, and 39 new candidates joined them, for 95 items. One old item (the wheel
lights gating note) now needs two proposals because it has two live variants, so
the numbering below is fresh; the old numbers appear only in the "Resolved by
the churn" list.

As proposed, visible copy across the surfaces touched drops from 3,661 words to
1,706. The always-visible subset (the header, the tab intro and badges, the
Forza and wheel-lights blocks, the everyday feel rows) drops from 627 words to
377; the intro and badge strings are per-game variants of the same two
elements, so a driver only ever sees one of each at a time.

Two things changed shape since the last pass. First, the churn is now a source
of trims as well as a solvent for them: 13 items below are marked **(new copy)**
because the text they trim was added or rewritten in the last three weeks,
usually because a fact was written into a new tooltip and left in the visible
paragraph as well. Second, several old items turned out to rest on tooltips that
do not exist; those now say "add" or "extend" instead of claiming coverage.

Untouched on purpose: safety and diagnostic banners (G HUB, wheel-quiet, slip
starved, clip warnings, the Forza fallback banner), consent and privacy copy,
the Forza numbered setup steps, troubleshooting content, and empty-state text.

How to read: **Now** is the exact current text. **Trim** is the proposed visible
text ("delete" = the line goes away). **Tip** is what moves into a ToolTip, on
the control named. Braces in code-built strings are runtime values. Reply with
group letters and numbers, e.g. "A all, B except 4, C 2 and 5".

---

## A. Clean trims (drops only what a label or an existing tooltip already says)

**A1. Offline-edit banner, car-edit variant** (SettingsControl.xaml.cs:1598, and the dead default at SettingsControl.xaml:803)
Now: "The game defaults are shown (and locked) for context; only the per-car effects are editable. Save the usual way, or Revert a section to undo it. Done finishes and returns to your live setup."
Trim: "Game defaults are locked for context; only the per-car effects are editable."
Note: the Done button's tooltip already explains Done. The XAML default is overwritten on every refresh, so trim it for tidiness only; the .cs string is the one users read.

**A2. Offline-edit banner, preset-edit variant** (SettingsControl.xaml.cs:1611)
Now: "Save the usual way, or Revert a section to undo it. Done finishes editing."
Trim: "Save the usual way, or Revert a section to undo it."
Note: the third variant of this hint, which A1 does not reach; the Done button sits inches away in the same banner with a tooltip that says the same thing.

**A3. Car facts no-car placeholder** (SettingsControl.xaml:1145)
Now: "Once you're driving, the car's facts (name, engine, redline) show here. If the redline buzz or the rev lights fire at the wrong RPM, this is where to fix it."
Trim: "Once you're driving, the car's facts show here. Wrong redline buzz or rev lights? This is where to fix it."

**A4. Engine auto-detect readout, your-pick variant** (SettingsControl.xaml.cs:2109)
Now: "Your pick: {your engine}. Auto would be {detected engine}{source}. Pick Auto to use detection."
Trim: "Your pick: {your engine}. Auto would be {detected engine}{source}."
Note: the closing sentence tells the reader to select the dropdown entry literally named Auto, in the combo directly above and named in the sentence before it.

**A5. Engine pulse intro line** (SettingsControl.xaml:1544)
Now: "For the most accurate engine pulse, make sure the engine type is correct in Car facts on the active car card."
Trim: "Set the engine type in Car facts (active car card) for the most accurate pulse."

**A6. Per-gear redlines help** (SettingsControl.xaml:1224)
Now: "Optional. Set a different redline start (where you upshift) for specific gears, e.g. a lower 1st-gear shift point. Gears you don't list use the redline above."
Trim: "Set a different redline for specific gears, e.g. a lower 1st-gear shift point. Unlisted gears use the redline above."
Note: lowest-value item in the batch. The expander is now hidden unless the Settings-tab toggle is on or the car already has per-gear values, so this is doubly buried copy.

**A7. Low-end body intro line** (SettingsControl.xaml:1597)
Now: "Helps the engine still feel present at high RPM, where the wheel's motor can't keep up with the firing rate."
Trim: delete.
Note: both sub-checkboxes beneath carry the mechanism on hover already; the separator above keeps the two grouped.

**A8. Auto peak force confirmation** (SettingsControl.xaml.cs:6215, and the identical build at SettingsControl.xaml.cs:1946)
Now: "Set to {n} Nm. Watching again from now, so a clean lap plus another press redoes it."
Trim: "Set to {n} Nm. Watching again from now."
Note: the Auto button's own tooltip carries the redo procedure. Both build sites take the same edit or the line will flicker between two wordings.

**A9. Auto peak force button tooltip** (SettingsControl.xaml:2856) **(new copy)**
Now: "The number on this button is what the plugin has watched this car actually produce, plus a margin. Drive a clean lap, then press it to put that number in the box and start watching again from scratch. Had a spin or hit a wall? Drive another clean lap and press it again. It stays greyed until it has seen enough to be worth taking. Same idea as iRacing's own auto button."
Trim: no visible line changes; this item rewrites the tooltip itself.
Tip (replacing the tooltip on IRacingAutoMaxForceBtn): "The number on this button is what the plugin has watched this car produce, plus a margin. Drive a clean lap, then press it to take that number and start watching again from scratch. It stays greyed until the reading has held steady. Same idea as iRacing's own auto button."
Note: 72 words of hover, two sentences of which the status line under the button already says at the moment they matter. "Clean" stays: pressing Auto after a spin bakes a bogus number into the car. Excluded from the word totals above, which count visible copy only.

**A10. Car's peak force help** (SettingsControl.xaml:2859) **(new copy)**
Now: "The peak steering force this car itself produces, in Nm, scaled to fit within the wheel power you chose with Strength. Nudge it down to make this car feel heavier, up to make it lighter. Each car keeps its own number."
Trim: "Each car keeps its own number. Nudge it down to make this car feel heavier, up to make it lighter."
Note: the definition is already the tooltip on both the label and the box. Keeping "Each car keeps its own number" first is deliberate: it supplies the antecedent for "it".

**A11. Strength help** (SettingsControl.xaml:2875) **(new copy)**
Now: "How strong the WHEEL should be: your overall preference, roughly a percentage of the wheel's total power, and the only force control you should need day to day. One setting for every car: turn it up and everything gets stronger by the same amount, so there is nothing to re-tune when you switch cars. 1.00 delivers what the sim asked for. Raise it if the wheel feels light, lower it if it clips."
Trim: "The only force control you should need day to day. 1.00 delivers what the sim asked for. Raise it if the wheel feels light, lower it if it clips."
Note: the new tooltip opens with the exact words this paragraph opens with, and also carries the every-car point. Leave the tooltip alone; it exists in two places (label and slider) and any edit has to be made twice.

**A12. Smoothing help** (SettingsControl.xaml:2912) **(new copy)**
Now: "Takes the edge off a force that arrives in steps, at the cost of a little lag. Leave it at 0 unless the wheel feels notchy."
Trim: "Leave it at 0 unless the wheel feels notchy."
Note: the tooltip on the label and the slider already carries the lag trade and "0 = none, the default". Leave it unchanged.

**A13. "Steering feel" sub-header help** (SettingsControl.xaml:2795)
Now: "The wheel's everyday manner: cornering weight, resistance, and the pull back to center."
Trim: delete.
Note: after the regrouping only Cornering weight still sits under this header, so the line now promises two controls that moved elsewhere.

**A14. Terrain feel help** (SettingsControl.xaml:2974)
Now: "Bumps and ground texture from the game's physics, added to the steering force. Needs the TF4ALL Enhanced Telemetry mod in the game (the plugin offers to install it)."
Trim: "Bumps and ground texture reach the wheel as steering force. Needs the TF4ALL Enhanced Telemetry mod in the game (the plugin offers to install it)."
Note: small saving, but it stops the checkbox tooltip and the line beneath describing the same sensation twice. A shorter version that opens with the requirement was rejected: it leaves no feel on screen.

**A15. "Slides and drifting" expander intro** (SettingsControl.xaml:3086)
Now: "How the wheel behaves when the car slides or drifts."
Trim: delete (restates the expander header).

**A16. Braking expander intro** (SettingsControl.xaml:3128)
Now: "How steering weight behaves under braking and lockup."
Trim: delete (restates the expander header).

**A17. Grip limit slider help** (SettingsControl.xaml:3154)
Now: "Where the game's combined-slip metric counts as the grip limit (1.0 = Forza's own limit). Lower = the wheel peaks and washes out earlier. Only used when adaptive grip is off."
Trim: "Where the game's combined-slip metric counts as the grip limit (1.0 = Forza's own limit). Lower = the wheel peaks and washes out earlier."
Note: the whole row is collapsed whenever adaptive grip is on, so the caveat can only ever be read in the one state where it does not apply.

**A18. Reset help** (SettingsControl.xaml:3214)
Now: "Restores your wheel's defaults for the game you are in, and leaves the other one alone: resetting in Farming Simulator does not touch your Forza setup. The G PRO, RS50, and G923 each get their own strength; the G923 also gets its own damping, and its own floor and strength in Farming Simulator. Your per-game on/off choices, each car's learned grip calibration, and your rev lights and screen settings are kept."
Trim: "Restores your wheel's defaults for the game you are in and leaves the other one alone."
Note: the confirm dialog states the full scope (per-wheel defaults, what survives) at the moment of the destructive click, so nothing needs a tooltip here. Do not append any of it to the reset button's tooltip; that would make three copies.

**A19. Rev lights checkbox help** (SettingsControl.xaml:3385)
Now: "Your wheel's rev lights follow the engine: the strip fills as revs climb and flashes at the shift point. Turn this off if a game drives the wheel's lights itself."
Trim: "The strip fills as revs climb and flashes at the shift point. Turn this off if a game drives the wheel's lights itself."
Note: the deleted clause is the checkbox label and the section header said again. Both surviving sentences stay visible on purpose; the sensation must not end up on hover with only the exception on screen.

**A20. OLED notifications paragraph** (SettingsControl.xaml:3410) **(new copy)**
Now: "It also reports changes as they happen: a preset loading, a gain nudged from a bound button, Auto strength settling on a car, or a warning that the game is still sending its own force feedback. Each shows for a moment, then your driving screen comes back. Set Show to Nothing and those moments are all you get, with the wheel keeping its own display the rest of the time."
Trim: "It also reports changes as they happen: a preset loading, a gain nudged from a bound button, Auto strength settling on a car, or a warning that the game is still sending its own force feedback. Each shows for a moment, then your driving screen comes back."
Note: drops only the appended Nothing sentence, which the conditional line under the Show picker already carries at the moment it is relevant. Paired with A21: this paragraph stays visible, which is what makes A21's shortening safe.

**A21. Nothing-screen line** (SettingsControl.xaml:3429) **(new copy)**
Now: "The wheel keeps its own display while you drive. A shift, a finished lap, an incident, a value you nudge from a bound button, or a force feedback warning still takes the screen for its moment and then hands it straight back."
Trim: "The wheel keeps its own display while you drive. Shifts, finished laps, incidents and warnings still take the screen for a moment and hand it straight back."
Note: apply with A20 and only in this direction. The full enumeration stays visible three lines up, so naming the categories is enough here.

**A22. Update-interval help** (SettingsControl.xaml:3655)
Now: "The plugin already checks once each time SimHub starts. This adds background re-checks so a fix released mid-session still shows the update banner without a restart. When one's available, an 'Update to vX.Y.Z' button appears in the header up top."
Trim: "When an update is available, an 'Update to vX.Y.Z' button appears in the header up top."
Note: the combo's tooltip carries the startup-check and the on-demand link.

**A23. Beta channel on-state note** (SettingsControl.xaml.cs:8801)
Now: "You're on the beta channel: the in-app updater will offer pre-release builds."
Trim: delete (restates the checked box above it; the off-state message on a prerelease build stays).

**A24. Dash phone control help** (SettingsControl.xaml:3699)
Now: "Control effects, gains, and presets from your phone or tablet while driving. No SimHub setup needed: scan the QR code and the dash opens in your phone's browser."
Trim: "Control effects, gains, and presets from your phone or tablet while driving."
Note: the button's own tooltip carries the QR code and the no-setup fact, and the QR dialog says it a third time one click later.

**A25. Dash tabs help** (SettingsControl.xaml:3728)
Now: "Untick a tab to hide it on the dash; use the arrows to change the order. Changes apply instantly, no dashboard reload needed. At least one tab always stays on."
Trim: "Untick a tab to hide it on the dash; use the arrows to change the order. At least one tab always stays on."
Note: instant application is visible the moment you click.

**A26. Dash Drive-tab help** (SettingsControl.xaml:3764)
Now: "The Drive tab shows the gear and speed in the middle with these boxes around it. Lap times, tire and fuel readouts come from the game, so what they show depends on what that game reports."
Trim: "Lap times, tire and fuel readouts come from the game, so what they show depends on what that game reports."
Note: the first sentence describes the layout the four slot labels and the two radios already draw.

**A27. Dash flags help** (SettingsControl.xaml:3805)
Now: "Shows yellow, blue, black, white and chequered flags across whichever tab is open. Only games that report flags can drive it, which the Forza titles do not, so it simply never appears there."
Trim: "Shows yellow, blue, black, white and chequered flags across whichever tab is open. Forza does not report flags, so the band never appears there."
Note: the qualifier stays, because a Forza owner needs it. Naming the band keeps the pronoun honest.

**A28. Forza forward intro** (SettingsControl.xaml:3957)
Now: "Also passes Forza's telemetry to SimHub so its dashboards, bass shakers, and Buttkicker keep working. Forza already points at this plugin; this just relays a copy on to SimHub. Setup:"
Trim: "Forza already points at this plugin; this just relays a copy on to SimHub. Setup:"
Note: keep the trailing "Setup:" token; the three numbered rows hang off it.

**A29. Performance expander intro** (SettingsControl.xaml:4088)
Now: "Smaller ring buffers = less latency between game and wheel, but require Windows to wake our threads on time. Auto starts at the smallest sizes and ratchets up if dropouts occur (one-way; survived size persists across sessions). Manual locks specific sizes (useful for streamers who want guaranteed-stable behavior, or to force-test the lowest values)."
Trim: "Smaller ring buffers = less latency between game and wheel, but require Windows to wake our threads on time."
Note: sentences two and three restate the Auto and Manual radio tooltips nearly verbatim.

**A30. Perf counters help** (SettingsControl.xaml:4136)
Now: "Counters tally events in a rolling 60-second window. If they're going up, your machine is glitching at the current ring size; Auto mode will bump it up."
Trim: "Counters tally events in a rolling 60-second window. If they are going up, your machine is glitching at the current ring size."
Note: the ratcheting clause is the Auto radio's tooltip restated, and A29 already leaves that fact to the radios.

**A31. Gain tile help** (SettingsControl.xaml:4271)
Now: "Quick master + audio gain control from SimHub's home screen. Turn off to remove the tile."
Trim: delete (the label and the checkbox tooltip say both halves).

**A32. Community car facts cache line** (SettingsControl.xaml:4324)
Now: "Cached locally and refreshed at most weekly per car, so it works offline and keeps server traffic low."
Trim: delete (the checkbox tooltip already says all of it).

**A33. Share buttons help** (SettingsControl.xaml:4350)
Now: "Off hides the card's Share buttons. You can still share anytime from the Preset Manager."
Trim: delete (both sentences are in the existing tooltip word for word).

**A34. Auto-update downloaded presets help** (SettingsControl.xaml:4360)
Now: "Only runs on downloads you haven't edited locally. If you've changed your local copy, those changes are kept; the curator's update is offered through the updates-available chip instead."
Trim: "If you've changed your local copy, those changes are kept; the curator's update is offered through the updates-available chip instead."
Note: the first sentence is the tooltip's last sentence restated; where the update then shows up is the part the tooltip does not carry.

**A35. Auto sync help** (SettingsControl.xaml:4422)
Now: "Real-time sync across your devices: changes you make on one device flow to the others automatically. Off = back up only when you click 'Back up now'."
Trim: "Off = back up only when you click 'Back up now'."
Note: the first sentence is the label and the tooltip said a third time.

**A36. Cross-wheel FFB help** (SettingsControl.xaml:4434)
Now: "Your FFB tuning always backs up. This only controls whether it is applied on a device with a different wheel model. 'Ask me' prompts once per change; you can tick 'remember my choice' on that prompt to stop being asked."
Trim: "'Ask me' prompts once per change; you can tick 'remember my choice' on that prompt to stop being asked."
Note: the first two sentences are the combo's tooltip almost verbatim.

**A37. Backup-to-file help** (SettingsControl.xaml:4479)
Now: "Bundle your full Trueforce For All state (settings, every preset, every default, every car tuning) into one archive for moving to a new machine. To share individual presets or car tunings with other users, use Import / Export in the Preset Manager (Presets tab) instead."
Trim: "One archive of your full setup for moving to a new machine. To share individual presets or car tunings, use Import / Export in the Preset Manager instead."

**A38. Spike-method descriptions** (SettingsControl.xaml.cs:5206, both branches)
Now: "Slew-rate limiter (iRacing-style): caps how fast the force is allowed to change. No amplitude reduction, sustained forces always reach full strength; a sharp spike just gets spread across a few extra milliseconds." and "Transient detector: soft-caps only the part of a sudden jump that exceeds your threshold. Sustained heavy cornering passes through at full strength; crashes and big curb hits get rounded off."
Trim: "Caps how fast the force is allowed to change. No amplitude reduction, sustained forces always reach full strength; a sharp spike just gets spread across a few extra milliseconds." and "Soft-caps only the part of a sudden jump that exceeds your threshold. Sustained heavy cornering passes through at full strength; crashes and big curb hits get rounded off."
Note: each branch opens by repeating the selected radio's own Content.

**A39. Ratchet banner tail** (SettingsControl.xaml.cs:16182)
Now: ". Persisted across sessions. Revert to restore the size(s) at the start of this run, or dismiss to keep the current size(s)."
Trim: ". Persisted across sessions. Revert restores this run's starting sizes."
Note: the Revert button's tooltip says the same thing and the dismiss control is a bare X.

**A40. Presets: Game presets intro** (PresetManagerControl.xaml:327)
Now: "Select a preset to see what it contains; or Edit to open it on the Effects tab. You can rename, duplicate, delete, and choose which games auto-load each preset. Built-ins can't be renamed or deleted; saving over one forks a copy."
Trim: "Select a preset to see what it contains. Built-ins can't be renamed or deleted; saving over one forks a copy."

**A41. Presets: Car presets intro** (PresetManagerControl.xaml:417)
Now: "Every car preset, grouped by game then car. A car can have several presets; the ★ in Default marks the one that loads for that car. Rename, duplicate, delete, or set a preset as the car's default."
Trim: "A car can have several presets; the ★ in Default marks the one that loads for that car."
Note: the grouping is visible in the Game and Car columns, and the last sentence re-enumerates the button row.

**A42. Presets: Custom engines intro** (PresetManagerControl.xaml:510)
Now: "Your saved custom engines. Edit or delete them here; if you delete one a preset uses, that preset falls back to the Auto engine mode until you pick a new one. Built-in layouts (V8, Rotary, etc.) aren't listed."
Trim: "Engines you've created. Built-in layouts (V8, Rotary, etc.) aren't listed."
Note: no tooltip needed. The delete confirmation already tells the user, with real counts, that those presets switch to Auto engine detection.

**A43. Presets: Packs intro** (PresetManagerControl.xaml:577)
Now: "A pack bundles several presets at once. Pick an installed pack to set every preset it includes as a default in one go, or to remove the whole bundle (preserving any entries you've edited). Browse the Community view to find packs other drivers have shared."
Trim: "A pack bundles several presets at once. Find shared packs in the Community view."
Note: the middle sentence restates the two button tooltips, including the preserved-edits caveat.

**A44. Presets: Community view help, all five strings** (PresetManagerControl.xaml:616 default; live variants at PresetManagerControl.xaml.cs:1521, 1527, 1532, 1537)
Now (XAML default): "Browse + download community presets for the car you're driving. Pick a row and Download to import; you'll get a section picker so you can take just the parts you want."
Trim: "Browse and download community presets for the car you're driving."
Now (game kind, .cs:1521): "Browse + download community game presets for the game you're playing, or switch to your own uploads to manage them. Pick a row and Download to import; you'll get a section picker so you can take just the parts you want."
Trim: "Browse and download community game presets for the game you're playing."
Now (engine kind, .cs:1527): "Browse + download community custom engines (cylinder patterns + layout), or switch to your own uploads to manage them. Pick a row and Download to add it to your library."
Trim: "Browse and download community custom engines (cylinder patterns + layout)."
Now (pack kind, .cs:1532): "Browse + download community packs (bundles of game presets, car presets, and custom engines), or switch to your own uploads to manage them. Pick a row and Download to import every entry into the matching part of your library."
Trim: "Browse and download community packs (bundles of game presets, car presets, and custom engines)."
Now (car kind, .cs:1537): "Browse + download community presets for the car you're driving, or switch to your own uploads to manage them. Pick a row and Download to import; you'll get a section picker so you can take just the parts you want."
Trim: "Browse and download community presets for the car you're driving."
Note: the XAML default alone changes nothing on screen. The four live variants overwrite it every time a Community view opens, so they are the item. In each, the uploads clause restates the My uploads segment button and its tooltip, and the picker sentence restates the Download button's tooltip plus the picker that opens before anything imports.

**A45. Dev mode banner** (PresetManagerControl.xaml:253; owner-only)
Now: "Developer mode is active. Per-row Set as built-in buttons + the Developer toolbar at the bottom are unlocked. Type DEV again in Settings to turn it off."
Trim: "Developer mode is active. Type DEV again in Settings to turn it off."

**A46. Custom engine editor intro** (CustomEngineEditor.xaml:37)
Now: "Build the pulse rhythm your wheel plays for an engine nothing built-in matches. Pick the firing pattern closest to the real engine and set the cylinder or rotor count; the pattern below fills in from those until you hand-edit it (Regenerate refills it on demand). Save it, then choose it in a variant's Engine dropdown to hear it on that car."
Trim: "Pick the firing pattern closest to the real engine and set the cylinder or rotor count. Save, then choose it in a variant's Engine dropdown to hear it on that car."
Note: the auto-fill clause is covered twice over by the Pattern label and Regenerate button tooltips; the purpose sentence is carried by the window title and the link that opens it.

## B. Trims that move real detail to hover (the detail survives in a tooltip)

**B1. Capture exe override help** (SettingsControl.xaml:1503)
Now: "Type the game's exe name here (with or without &quot;.exe&quot;). Saved per-game and applied within 1 second."
Trim: "Type the game's exe name here (with or without &quot;.exe&quot;)."
Tip (on CaptureExeOverrideBox, which has none today): "Saved per-game and applied within 1 second."
Note: this is troubleshooting content two expanders deep, and the timing sentence is what stops someone restarting SimHub to test, so it has to survive on hover rather than be cut.

**B2. Airborne ducking intro** (SettingsControl.xaml:2543; 87 words, still the largest effect help block)
Now: "Cuts vibrations while the car is in the air, so a jump feels weightless instead of buzzing as if the tires were still on the track. Everything comes back the moment you land. Choose how far to turn things down and which effects to cut below (the engine keeps pulsing by default, since it's still revving). Works in Assetto Corsa, Forza, and Farming Simulator (with the TF4ALL Enhanced Telemetry mod), the games that report enough to know the car has left the ground; it does nothing elsewhere."
Trim: "Cuts vibrations while the car is in the air, so a jump feels weightless. Works in Assetto Corsa, Forza, and Farming Simulator (with the TF4ALL Enhanced Telemetry mod); it does nothing elsewhere."
Tip (on AirborneEnabledCheck, which has none today): "Everything comes back the moment you land. The checkboxes below choose which effects are cut; the engine keeps pulsing by default, since it's still revving."

**B3. Collision sources clause** (SettingsControl.xaml:2350)
Now: "A thud on impact that hits harder the bigger the crash. Sources: PC2 reads its native impact magnitude directly; other sources derive from sudden lateral/vertical accel spikes above ~5g (well above hard cornering or curbs)."
Trim: "A thud on impact that hits harder the bigger the crash."
Tip (on CollisionEnabledCheck, which has none today): "PC2 supplies its native impact magnitude; other games derive it from sudden accel spikes above about 5g, well above hard cornering or curbs."

**B4. Pit limiter source clause** (SettingsControl.xaml:2194)
Now: "Feels like the engine being cut at low RPM while the pit limiter is engaged. A steady pulse train you can't miss in the pit lane. Source: SimHub StatusDataBase.PitLimiterOn (works for most racing sims that have a pit lane)."
Trim: "Feels like the engine being cut at low RPM while the pit limiter is engaged: a steady pulse train you can't miss. Works in most racing sims that have a pit lane."
Tip (on PitLimiterEnabledCheck, which has none today): "Driven by SimHub's PitLimiterOn property."

**B5. DRS source clause** (SettingsControl.xaml:2264)
Now: "A quick chirp the moment DRS opens, then a faint sustained tone while the wing stays open. Silent on games that don't expose DRS. Source: SimHub StatusDataBase.DRSEnabled."
Trim: "A quick chirp the moment DRS opens, then a faint sustained tone while the wing stays open. Silent on games that don't expose DRS."
Tip (on DrsEnabledCheck, which has none today): "Driven by SimHub's DRSEnabled property."

**B6. Implement thud slider help lines** (SettingsControl.xaml:2052, 2060, 2068, 2076, 2084, 2092, 2100; 132 words)
Now: seven visible per-slider help lines, e.g. "How much slow hydraulic movement quiets the whine: it swells as motion picks up and fades as it eases into the stop. 0 = constant loudness."
Trim: convert all seven to ToolTips on their own slider labels (2047, 2055, 2063, 2071, 2079, 2087, 2095), texts unchanged.
Note: none of the seven labels carries a tooltip today. Axle slip is the house pattern: same-style sliders, label tooltips, no visible help lines.

**B7. Axle slip sub-checkbox help lines** (SettingsControl.xaml:1881 and 1889)
Now: "Predicts the slide from how fast grip is being used up, so the warning arrives a fraction before the limit instead of at it." and "The rear pulse speeds up and slows down with the spinning wheels themselves, so wheelspin feels like wheels turning rather than a generic buzz."
Trim: both become ToolTips on their own checkboxes, texts unchanged (matches the Engine pulse sub-checkboxes).

**B8. Road bumps leading-edge help** (SettingsControl.xaml:1683)
Now: "The extra whack the instant a wheel first strikes a hard bump: ruts, edges, drops. 0 = smooth texture only."
Trim: delete.
Tip (on the Leading edge slider label, which has none today): same text.
Note: do it in the same pass as B6 so the Farming Simulator panels end up consistent.

**B9. Engine auto-detect readout, no-detection variant** (SettingsControl.xaml.cs:2100)
Now: "Could not auto-detect engine type for '{car}'. Pick the closest match in the Engine dropdown above; the Engine pulse Test button can help you A/B."
Trim: "Could not auto-detect engine type for '{car}'. Pick the closest match in the Engine dropdown above."
Tip (append to the CarFactsEngineCombo tooltip): "The Engine pulse Test button can help you A/B two candidates."
Note: diagnostic copy, so the diagnosis and the immediate instruction both stay visible; only the optional technique moves.

**B10. Per-game enable note** (SettingsControl.xaml:2741 default; live text from SettingsControl.xaml.cs:1093)
Now (XAML default): "Enable this per game, for whichever game is running. Set that game's own force feedback and vibration to 0."
Trim: no user-visible change. The live string is already in the shape this item asked for, and the XAML default is never read.
Tip (on ModeBEnabledCheck, which has none today): "Remembered per game, for whichever game is running."
Note: the only real deliverable here is the tooltip; the XAML edit is tidy-up and is not counted in the word totals.

**B11. Forza Auto strength help** (SettingsControl.xaml:2767)
Now: "Levels cars out: learns how hard each car pushes the wheel, then scales it so every car lands at the heaviness your Strength slider sets. No more retuning per car; the trade is that cars stop feeling lighter or heavier than each other. Takes a few laps per car to settle."
Trim: "Levels cars out so every car lands at the heaviness your Strength slider sets. The trade: cars stop feeling lighter or heavier than each other. Takes a few laps per car to settle."
Tip (on ModeBAutoStrengthCheck, which has none today): "Learns how hard each car pushes the wheel, then scales it to match your Strength setting."

**B12. Forza Min force help** (SettingsControl.xaml:2791)
Now: "The smallest force your wheel will play. Raising it lifts faint cues above the wheel's own friction and brings the average force up, so a belt-driven wheel like the G923 feels stronger overall."
Trim: "The smallest force your wheel will play."
Tip (on ModeBMinForceSlider, which has none today): "Raising it lifts faint cues above the wheel's own friction and brings the average force up, so a belt-driven wheel like the G923 feels stronger overall."
Note: the Forza twin of B14. Trimming only one of the pair leaves the longer paragraph on the panel most people see, so take them together.

**B13. Spring Strength help** (SettingsControl.xaml:2820)
Now: "Overall steering force (spring, terrain, drag, cornering weight together). Damping is unaffected. The bound strength buttons adjust this while in Farming Simulator."
Trim: "Overall steering force (spring, terrain, drag, cornering weight together)."
Tip (on SpringStrengthSlider, which has none today): "Damping is unaffected. The bound strength buttons adjust this while in Farming Simulator."

**B14. Spring minimum force help** (SettingsControl.xaml:2828)
Now: "The smallest force your wheel actually plays here: lifts faint terrain hits, and light centering once the wheel is off dead-ahead, above the wheel's own friction. Raise it on a belt-driven wheel (G923) until faint bumps just move the wheel. A parked wheel stays limp, and damping is unaffected."
Trim: "The smallest force your wheel actually plays. Raise it on a belt-driven wheel (G923) until faint bumps just move the wheel."
Tip (on SpringMinForceSlider, which has none today): "Lifts faint terrain hits and light centering above the wheel's own friction. A parked wheel stays limp, and damping is unaffected."

**B15. Sidechain frequency-aware help** (SettingsControl.xaml:2618)
Now: "Only ducks effects that overlap in frequency. Grip textures always win their band, so a slide stays crisp through the engine pulse instead of blending into it."
Trim: "Only ducks effects that overlap in frequency."
Tip (on DuckFrequencyAwareCheck, which has none today): "Grip textures always win their band, so a slide stays crisp through the engine pulse instead of blending into it."

**B16. Forza Centering help** (SettingsControl.xaml:3053)
Now: "Caster-style pull toward straight, scaled up with speed (a parked wheel stays free). Follows the wheel's own position, so a released wheel settles into center instead of swinging past."
Trim: "Caster-style pull toward straight, scaled up with speed (a parked wheel stays free)."
Tip (on ModeBCenterSlider, which has none today): "Follows the wheel's own position, so a released wheel settles into center instead of swinging past."

**B17. Anticipation help** (SettingsControl.xaml:3111)
Now: "Leads the force slightly ahead of the wheel to cancel telemetry lag, so a released wheel settles instead of oscillating. Raise the lead only until a released wheel stops ringing; too much adds nervousness on fast transitions."
Trim: "A released wheel settles instead of oscillating, because the force is led slightly ahead of the wheel to cancel telemetry lag."
Tip (on the "Lead (ms):" label at SettingsControl.xaml:3114, not on the checkbox): "Raise the lead only until a released wheel stops ringing; too much adds nervousness on fast transitions."
Note: the tuning advice is about the slider, not the checkbox it currently sits under, and "raise it" has no valid antecedent on a checkbox.

**B18. Spring Centering help** (SettingsControl.xaml:2991)
Now: "The pull back to center. 0 turns the spring off entirely, so you can feel what remains (terrain, damping, effects)."
Trim: "The pull back to center."
Tip (on SpringCenterGainSlider, which has none today): "0 turns the spring off entirely, so you can feel what remains (terrain, damping, effects)."
Note: the 0 trick is the fastest way for a Farming Simulator user to hear what the other spring settings do, so this costs a little discoverability.

**B19. Lateral-demand force help** (SettingsControl.xaml:3191)
Now: "Bases the steering force on cornering (lateral) grip instead of total grip, so braking in a straight line makes almost no force and the wheel cannot start swinging left and right on its own. Cornering feel is unchanged."
Trim: "Braking in a straight line makes almost no force, so the wheel cannot start swinging left and right on its own. Cornering feel is unchanged."
Tip (on ModeBLateralDemandCheck, which has none today): "Bases the steering force on cornering (lateral) grip instead of total grip."

**B20. Grip auto-cal help** (SettingsControl.xaml:3132)
Now: "Learns where each car's grip actually tops out as you drive (about a lap of pushing) and shapes the limit and braking feel to it: the wheel lightens at each car's real grip limit as you brake or slide, instead of at one fixed point. Off = a fixed manual grip limit (the slider below) and the classic lockup fade."
Trim: "The wheel lightens at each car's real grip limit, learned as you drive (about a lap of pushing). Off = a fixed manual grip limit (the slider below)."
Tip (on ModeBGripCalCheck, which has none today): "Shapes both the grip limit and the braking fade to each car's measured grip as you brake or slide, instead of one fixed point. Off also restores the classic lockup fade."

**B21. Wheel lights gating note, default variant** (SettingsControl.xaml:3381 default and SettingsControl.xaml.cs:1067; identical strings)
Now: "These need Telemetry Based FFB switched on. Writing to them while a game runs its own force feedback makes that force feedback cut out, because they share one channel on the wheel; replacing the game's force feedback frees them. A custom driver that would enable them in every game is in testing, but it needs to be signed by Microsoft first."
Trim: "These need Telemetry Based FFB switched on. Writing to them while a game runs its own force feedback makes that force feedback cut out, because they share one channel on the wheel; replacing the game's force feedback frees them."
Tip (on WheelLightsNeedNote, which has none today): "A custom driver that would enable them in every game is in testing, but it needs to be signed by Microsoft first."
Note: the contention consequence stays visible. The separate contention warning elsewhere on the tab does not cover this: different section, and it only fires once contention has already been detected. Only the driver teaser moves.

**B22. Wheel lights gating note, iRacing variant** (SettingsControl.xaml.cs:1066)
Now: "These need the force feedback above switched on. Writing to them while a game runs its own force feedback makes that force feedback cut out, because they share one channel on the wheel; taking the force over ourselves frees them. That is why iRacing needs its own force feedback turned off, and why the lights and screen come back once it is."
Trim: no change. Leave this variant exactly as written.
Note: listed only so the B21 edit is not mirrored onto it by reflex. Its whole body is the contention explanation plus the reason iRacing's own force feedback has to be off, and there is no driver teaser in it to move.

**B23. Rev light pattern help** (SettingsControl.xaml:3399)
Now: "Picking a pattern sets the wheelbase's selection, exactly like using the wheel's own menu: it applies now and stays. Remember for this car is optional automation on top, re-applying the pattern whenever that car loads. Colors and direction come from the pattern itself (set in G HUB)."
Trim: "Picking a pattern sets the wheelbase's selection, like using the wheel's own menu: it applies now and stays. Remember re-applies it whenever this car loads."
Tip (on RevLightEffectCombo, which has none today): "Colors and direction come from the pattern itself, set in G HUB."
Note: do NOT mirror the already-trimmed Car facts twin, which kept only the Remember sentence. That twin is a row under a car card; this is the section's only description. It is also the only place in the app that says the plugin permanently rewrites the wheel's own setting, and the only mention of G HUB as the source of the colors.

**B24. OLED custom screen intro** (SettingsControl.xaml:3464)
Now: "Pick a layout, then what goes in each of its slots. The wheel decides how big each slot is drawn and where it sits, so the layout is the shape and the slots are the contents. Anything set to Custom text shows exactly what you type."
Trim: "Pick a layout, then what goes in each of its slots. Anything set to Custom text shows exactly what you type."
Tip (on the Layout combo, which has none today): "The wheel decides how big each slot is drawn and where it sits, so the layout is the shape and the slots are the contents."

**B25. Dash default-tab help** (SettingsControl.xaml:3719)
Now: "With remember on, the dash reopens where you left it, even across SimHub restarts. Turn it off to always start on the default tab picked here (applies at the next SimHub start)."
Trim: delete.
Tip (append to the RemoteDashDefaultTabCombo tooltip, which stops at "when 'Remember last used tab' is off."): "Applies at the next SimHub start."

**B26. Dash per-game layout help** (SettingsControl.xaml:3770)
Now: "Games report different telemetry, so a layout that fills up in one can be half empty in another. With this on, boxes you pick while a game is running are remembered for that game. A game you have not set up yet uses the layout above, and so do changes you make with no game running."
Trim: "A game you have not set up yet uses the layout above, and so do changes you make with no game running."
Tip (append to the RemoteDashDrivePerGameCheck tooltip, which already carries sentence two): "Games report different telemetry, so a layout that fills up in one can be half empty in another."

**B27. Dash spotter help** (SettingsControl.xaml:3811)
Now: "SimHub works this out from the other cars in the session, so like the flags it only lights up in games that report car positions and simply never appears in the ones that do not."
Trim: delete.
Tip (append to the RemoteDashSpotterCheck tooltip, which describes only the bar itself): "Only games that report car positions can drive it; it never appears in the others."

**B28. Dash incidents help** (SettingsControl.xaml:3817)
Now: "Shows the points you have taken against the limit that ends your session, turning amber and then red as you run out of room. Each new incident announces what it cost across the top of the dash and on the wheel base screen, so you know whether it was a 1x or a 4x without waiting for the replay. iRacing is the only game that publishes a count, so nothing appears anywhere else."
Trim: "Shows the points you have taken against the limit that ends your session, turning violet and then red as you run out of room."
Tip (on the incidents checkbox, which has none today): "Your incident count sits under the speed on the Drive tab, and a band across the top and the wheel base screen announce each new incident with what it cost (1x, 4x). iRacing is the only game that publishes a count, so nothing appears anywhere else."
Note: the colour is a real bug, not a preference. The dash builds the three states as green, violet, red, with an explicit "VIOLET, not amber" comment; whatever survives the trim must say violet. The stale comment at make-tf4all-dash.ps1:2588 still says amber and should be fixed in the same pass.

**B29. Dash idle-card help** (SettingsControl.xaml:3827)
Now: "With no game running the dash shows an ambient card with your name and number instead of an empty dashboard. It clears the moment a game starts, and there is an Exit button on the card itself."
Trim: delete.
Tip (on RemoteDashIdleCheck, which has none today): "Shows an ambient card with your name and number instead of an empty dashboard. It clears the moment a game starts, and the card has its own Exit button."

**B30. Beta updates help** (SettingsControl.xaml:3667)
Now: "Beta builds get fixes and new effects first, but are less tested. Pre-releases are always available by hand on GitHub; this just brings them to the in-app updater. Turn it off to return to the stable release next time one ships."
Trim: "Beta builds get fixes and new effects first, but are less tested."
Tip (append to the BetaUpdatesCheck tooltip, which carries neither fact today): "Pre-releases are always public on GitHub; this just brings them to the in-app updater. Turn it off to return to the stable release next time one ships."
Note: the tooltip must be extended first. The only other carrier for the turn-it-off behavior renders solely when the running build is itself a prerelease.

**B31. Experimental FFB capture help** (SettingsControl.xaml:4038)
Now: "Try this if you are not getting force feedback in a game that should have it. Load the game and drive for a few seconds after turning it on. Leave it off if your force feedback already works."
Trim: "Try this if you are not getting force feedback in a game that should have it."
Tip (rewrite the ExperimentalFfbCheck tooltip's closing advice): "Load the game and drive for a few seconds after turning it on. Leave it off if your force feedback already works, and turn it back off if it causes any trouble."
Note: the tooltip today says "turn it back off if it causes any trouble", which is a rollback instruction, not the do-not-enable-speculatively guard being moved. The trigger sentence stays visible because this is the recovery path for a wheel with no force feedback at all, and someone in that state is scanning, not hovering.

**B32. Per-gear redline editor toggle help** (SettingsControl.xaml:4276) **(new copy)**
Now: "Off by default to keep the Car facts panel small. Cars whose current engine already has per-gear redlines saved keep showing the editor, and saved values keep working either way."
Trim: "Off by default to keep the Car facts panel small."
Tip (append to the existing checkbox tooltip): "Cars that already have per-gear redlines saved keep showing the editor, and saved values keep working either way."
Note: the reassurance matters only to someone who already has per-gear values, and that person sees the editor regardless.

**B33. Badge, no supported game** (SettingsControl.xaml.cs:1073)
Now: "No supported game is running. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25. Start one of those to turn it on. You can still see and pre-tune the controls below."
Trim: "No supported game is running. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25."
Tip (on the badge itself, which has none today): "You can pre-tune the controls below without a game; the per-game Enable box unlocks when a supported game starts."

**B34. Badge, unsupported game** (SettingsControl.xaml.cs:1074)
Now: "Not available in {game}. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25. It also enables your wheel's rev lights. Start one of those to turn it on."
Trim: "Not available in {game}. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25."
Tip (same badge tooltip as B33, which the two variants share): "Telemetry Based FFB also enables your wheel's rev lights and screen in these games."

**B35. Tab intro, Farming Simulator variant** (SettingsControl.xaml.cs:1010)
Now: "In Farming Simulator the plugin replaces the game's basic force feedback with its own steering model built from the game's physics, and engages by itself. The game's force feedback setting doesn't change the feel while SimHub runs; leaving it on just keeps native FFB as a fallback when SimHub is closed. Also works in Forza Motorsport (2023) and Forza Horizon 4, 5, and 6."
Trim: the same two Farming Simulator sentences, unchanged, without the closing sentence.
Tip (on ModeBIntroText, which has none today): "Also works in Forza Motorsport (2023) and Forza Horizon 4, 5, and 6."

**B36. Variants window intro** (CarFactsVariantsWindow.cs:117)
Now: "Variants are created automatically the first time this car shows a new engine. Rename a row by clicking its label (cosmetic, stays local). The Engine dropdown pins that variant's engine type; Auto lets detection decide. Delete drops the row; if that engine turns up again, a fresh row is created. Built-in rows come from the car list and can't be edited."
Trim: "Variants are created automatically the first time this car shows a new engine. In the Engine dropdown, Auto lets detection decide; built-in rows can't be edited."
Tip (on the Label column header, via a HeaderStyle setter): "Rename a row for your own reference. Cosmetic and local; it is not shared."
Tip (append to the code-built Delete tooltip): "If that engine turns up again, a fresh row is created."
Note: the column header already reads "Label (click to rename)", but it carries only the affordance, not the fact that the rename never reaches the community car-name pool. That is the part the header tooltip has to say.

**B37. Custom engine pattern help** (CustomEngineEditor.xaml:95)
Now: "Each number is when a cylinder fires within one engine cycle, from 0 to 1. Hand-edit for odd-fire engines; an optional :weight per pulse (0.25:0.85) makes that pulse softer."
Trim: "Each number is when a cylinder fires within one cycle, from 0 to 1."
Tip (rewriting the existing Pattern label tooltip): "Comma-separated positions from 0 up to (but not including) 1, marking where each pulse fires within one engine cycle. Optional ':amplitude' suffix per pulse (0.25:0.85) makes that pulse softer. Auto-fills when you pick a shape / count above; hand-edit for odd-fire engines."
Note: this is a rewrite of that tooltip, not an append, and it deliberately avoids interval notation and degrees so the hover and the visible line speak the same language.

**B38. Community browser, my uploads** (PresetManagerControl.xaml.cs:1514)
Now: "Your community uploads. Select a row to reveal Edit and Delete; use Edit to update a preset's name, description, or body without resetting its votes and downloads."
Trim: "Your community uploads. Select a row to reveal Edit and Delete."
Tip (extend the CommunityEditBtn tooltip): "Edit one of your own uploads (name, description, body) without resetting its votes and downloads."
Note: the first clause is real discoverability copy (Edit and Delete are hidden until a row is selected) and stays.

## C. Your call (each trims something with real visible value; my lean noted)

**C1. Header subtitle** (SettingsControl.xaml:655; the largest always-on block in the app, on every tab)
Now: "Everything your Logitech wheel can do, in games that never supported it: Trueforce haptics, Telemetry Based FFB, rev lights and a customizable wheelbase OLED screen. Unofficial reverse-engineered implementation of Logitech's Trueforce and OLED protocols, not affiliated with Logitech. Supports G PRO, RS50 and G923; the screen is G PRO and RS50 only."
My lean: "Everything your Logitech wheel can do, in games that never supported it: Trueforce haptics, Telemetry Based FFB, rev lights and a wheelbase OLED screen. An unofficial reverse-engineered implementation, not affiliated with Logitech."
Tip (on that TextBlock, which is anonymous and has no tooltip, so an x:Name or an inline ToolTip has to be added): "Supports G PRO, RS50 and G923; the wheelbase screen is G PRO and RS50 only."
Trade-off: the affiliation disclaimer stays visible in shortened form, and only the wheel list goes to hover. The list is partly enforced by behavior anyway, since the screen section hides itself on a wheel with no screen.

**C2. Redline definition line** (SettingsControl.xaml:1205)
Now: "The redline start: where the tachometer turns red and you upshift. Not the rev limiter cutoff (that sits a little higher)."
My lean: "Where the tachometer turns red and you upshift. The rev limiter cutoff sits a little higher."
Note: the first half is now a near-verbatim duplicate of the tooltip on the box it sits under, so only the limiter caveat is unique. Keep that caveat as a full sentence: a user typing the limiter cutoff here corrupts what gets shared to the community, so the one-line education earns its slot.

**C3. iRacing Advanced intro** (SettingsControl.xaml:2939) **(new copy)**
Now: "iRacing sends telemetry 60 times a second, and each update carries six force samples from the physics steps in between. Two things follow: whether to fill the time between updates instead of holding still, and whether to bring in the detail those six samples carry. These options go from neither to both."
My lean: "iRacing sends telemetry 60 times a second, with six force samples inside each update. The rungs below go from using neither to using both: filling the time between updates, and bringing in the detail those samples carry."
Note: decide this one first. It is the anchor for C4 to C7, whose trims are all justified against whatever stays visible here, and it is the only place that names the two dimensions as a pair, which is what makes the four rungs read as a ladder. Nothing moves to hover; the Feel row keeps no tooltip, so there is exactly one carrier for each fact.

**C4. Feel rung, Plain** (SettingsControl.xaml.cs:6441) **(new copy)**
Now: "Each update is used exactly as the sim sends it, and held until the next one arrives. Nothing is filled in and nothing is added."
My lean: "Each update is used exactly as the sim sends it, and held until the next one arrives."
Note: the second sentence is the first one again. Take it if C3 lands; the ladder framing in C3 already maps this rung onto the two decisions.

**C5. Feel rung, Filled** (SettingsControl.xaml.cs:6444) **(new copy)**
Now: "Fills the time between updates by continuing the force along its own trend, so it keeps moving instead of holding still and the updates stop arriving as small steps."
My lean: "Fills the time between updates by continuing the force along its own trend, so it keeps moving instead of holding still."
Note: one clause per rung is what makes the ladder scannable. If you would rather keep the symptom a driver recognises, cut the earlier clause instead and keep "the updates stop arriving as small steps"; either one alone is enough, but not both.

**C6. Feel rung, Detailed** (SettingsControl.xaml.cs:6447) **(new copy)**
Now: "Fills the gaps as above, and brings in the detail the sim solves between updates, so kerbs and surface texture reach your hands. The texture arrives a fraction later than the steering weight, where the delay cannot be felt."
My lean: "Fills the gaps as above, and brings in the detail the sim solves between updates, so kerbs and surface texture reach your hands."
Note: the cut sentence has nowhere to go. A tooltip that changes with the selection is a second string to keep in sync with this one in the same switch, so the reassurance is dropped rather than moved. It is the sentence that stops the timing fact reading as a drawback, so keep it visible if you would rather not lose it.

**C7. Feel rung, Detailed and predicted** (SettingsControl.xaml.cs:6450) **(new copy)**
Now: "The same detail, kept whole and in its true order rather than split from the steering weight. The force arrives a frame delayed as a result, so the plugin predicts forward to close that gap, learning how far ahead to reach for every car you drive."
My lean: "Kerbs and texture arrive whole and in step with the steering weight, instead of split from it. The plugin predicts forward so that costs nothing."
Note: the longest of the four rungs, and the only one whose visible copy is pure signal-path description. The rewrite leads with what the driver feels and keeps the word "predicted" explained on screen. The per-car learning detail is dropped, for the same reason as C6.

**C8. Tab intro, iRacing variant** (SettingsControl.xaml.cs:1017; 98 words)
Now: "The plugin takes force feedback over from iRacing rather than inventing its own. iRacing works out what the car's steering is doing; you stop it driving the wheel directly, and the plugin reads those same forces and delivers them over Trueforce instead. The car still feels like the car, and your rev lights and wheel screen work again, because nothing is fighting over the wheel any more. Two switches make the handover: turn iRacing's force feedback OFF in its options (do not just set its strength to 0, the plugin reads that number), and set loadTrueForceAPI=0 in app.ini."
My lean: "The plugin takes force feedback over from iRacing rather than inventing its own: it reads the forces iRacing computes and delivers them over Trueforce, so the car still feels like the car and your rev lights and wheel screen work again. The two setup switches are in the note below."
Note: accept the dedupe. The final sentence renders twice on the same screen today, because the note below already spells out both switches whenever iRacing is active, and the first-launch modal covers first-time setup.

**C9. Tab intro, Forza variant** (SettingsControl.xaml.cs:1025 and the XAML default at SettingsControl.xaml:2707; identical strings)
Now: "The wheel's steering force is built from telemetry instead of the game's own FFB. Works in Forza Motorsport (2023) and Forza Horizon 4, 5, and 6. Set the game's force feedback and vibration to 0 so this is the only force on the wheel. Farming Simulator 22 and 25 are supported too, through the spring option below."
My lean: "The wheel's steering force is built from telemetry instead of the game's own FFB. Works in Forza Motorsport (2023) and Forza Horizon 4, 5, and 6; set the game's force feedback and vibration to 0 so this is the only force on the wheel. Farming Simulator 22 and 25 are supported too, through the spring option below."
Note: word-neutral, so take it only for the binding. Merging Farming Simulator into the game list, as first proposed, aims the zero instruction at Farming Simulator, where the game's own force feedback setting is feel-neutral and is meant to stay on as a fallback. This version binds the instruction to the Forza list with a semicolon and leaves Farming Simulator last. Both places take the same edit or the intro will differ before and after the first refresh.

**C10. Spike reduction motivation** (SettingsControl.xaml:3339)
Now: "Some games deliver curb and collision FFB spikes well beyond what's safe or comfortable on stronger wheelbases (Assetto Corsa is the worst offender we've seen). Hits can be sharp enough to wrench the wheel against your grip, ruin a racing line, or strain your wrists on a long session. Pick one of the two methods below; it only applies while the box above is checked."
My lean: "Some games deliver curb and collision FFB spikes well beyond what's safe or comfortable on stronger wheelbases (Assetto Corsa is the worst offender we've seen). Hits can be sharp enough to wrench the wheel against your grip or strain your wrists on a long session."
Tip (on the "Method:" label at SettingsControl.xaml:3340, not on the enabling checkbox): "The method picked below only applies while the box above is checked."
Note: the earlier plan moved the physical-harm sentence to hover. That is the wrong half to hide, so it stays and only the racing-line clause and the third sentence go. The conditional rides on the Method label because a user picking a method with the box unchecked is looking there, not at the checkbox above.

**C11. OLED reverse-engineering trivia** (SettingsControl.xaml:3412)
Now: "The screen's protocol was reverse engineered; Logitech never documented it for the public."
My lean: delete. It is the third of three stacked always-visible paragraphs under one checkbox, and the same credibility flex sits in the header subtitle and the README. Keep it if you like the flex where the feature is.

---

## Conflicts resolved

Six lines came back from more than one reader with incompatible proposals.
Applying two of them would double-trim a line or silently revert the other, so
one winner was picked for each. What was chosen, and why:

1. **SettingsControl.xaml:2859 (car's peak force)**, three competing trims. Winner: the version that opens "Each car keeps its own number." (item A10). The alternative opened with "Nudge it down...", where "it" had lost its antecedent to the deleted sentence.
2. **SettingsControl.xaml:2875 (Strength)**, three competing trims. Winner: the shortest one (item A11), which drops both the opening clause and the every-car sentence, since the new tooltip carries both on the label and on the slider.
3. **SettingsControl.xaml:2912 (Smoothing)**, three competing tooltips. Winner: leave the tooltip untouched and cut the visible line to the actionable sentence (item A12). The rewritten-tooltip variant was discarded because that tooltip exists twice and every edit has to be made in both places.
4. **SettingsControl.xaml:2939 (iRacing Advanced intro)**, three competing trims. Winner: a reconciled wording (item C3) that keeps the sampling fact and the ladder framing visible in one sentence each. One of the alternatives was not a sentence, and both of the others deleted the clause that the rung trims then leaned on.
5. **SettingsControl.xaml:3410 and 3429 (OLED notifications and the Nothing screen)**, two mutually exclusive pairings. Winner: keep the notifications paragraph visible minus its appended Nothing sentence, and shorten the Nothing line to the named categories (items A20 and A21). The alternative moved the whole paragraph to hover, which would have made the Nothing line's own justification false, and a cross-reference wording ("apart from the moments listed above") was rejected as fragile.
6. **SettingsControl.xaml.cs:6441 to 6450 (the four Feel rungs)**, two sets of trims that cut opposite clauses. Winner: the per-rung set at C4 to C7, except at 6444 where the other wording won, so "instead of holding still" stays on screen in at least one place once C3 lands. No per-selection tooltips are added on the Feel combo: a tooltip that changes with the selection is a second string to keep in sync inside the same switch.

Two more adjudications worth recording. The wheel lights gating note is now two
items (B21 and B22) instead of one, and the contention consequence stays
visible in both. And the Forza fallback banner, which one reader wanted to
trim, is left alone: see the list below.

## Resolved by the churn

Four items from the 2026-08-13 sheet are gone because the code resolved them.
Two more entries are dropped on review: half of an old item, and one new
candidate that was proposed this round and then rejected.

- **Old B3** (iRacing per-car max force checkbox help): the checkbox and its paragraph were deleted outright; no successor control exists.
- **Old B5** (iRacing Detail method help): "Detail method" became the four-rung Feel ladder, and the both-modes-at-once paragraph no longer exists. Its replacements are C3 to C7.
- **Old B6** (iRacing Prediction help): the Prediction slider, its box and its help line were removed, since it was never a number to tune.
- **Old B7** (iRacing 360 Hz help): the checkbox was folded into the Feel ladder, so there is no control left to hang a tooltip on.
- **Old A23's tooltip half** (custom engines Delete button): dropped. The delete confirmation already tells the user, with real counts, that affected presets switch to Auto engine detection, so a tooltip would be a third copy. The visible trim survives as A42.
- **The Forza fallback banner** (SettingsControl.xaml:862, new copy): proposed and then rejected. It is the only always-visible place that connects Telemetry Based FFB to the Data Out requirement, and it sits on the failure mode where an armed setup without the direct feed leaves the wheel silent. The setup flow behind the button is not a substitute for someone who never presses it.

---

Implementation note (for whichever items you approve): several elements have
code-built text variants that overwrite the XAML default (the tab intro, the
wheel-lights note, the offline-edit hint, the badges, the community browser
help). Where both are listed above they are listed together; where only one is,
grep for other writers of that element before editing so no variant is missed.
Two defaults are dead copy that never reaches the screen (the tab enable note
and the offline-edit banner), so trim them for tidiness but expect no visible
change.

Line numbers here match the tree as read on 2026-08-16, with SettingsControl.xaml
modified in the working tree. Several anchors have already drifted by a line or
two since the proposals were captured, so match on text, not on line number.

All proposed tooltip text lands on the control the user is already looking at,
never on a container, and several of the controls named have no tooltip at all
today, so the item adds one rather than extending one; each says which.
