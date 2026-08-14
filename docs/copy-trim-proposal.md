# Copy trim proposal (2026-08-13)

A five-reader sweep of every user-facing surface (SettingsControl.xaml, the
Preset Manager, the custom engine editor, the variants window, and code-built
strings in SettingsControl.xaml.cs). 62 raw candidates, merged below to 57
items. As proposed, visible copy drops from ~2,750 words to ~1,150; the
always-on-screen subset drops from ~1,070 to ~460.

The dominant pattern: a control carries a good ToolTip AND a visible HelpText
paragraph restating it, so the same fact is on screen twice. The worst
clusters: the iRacing tuning panel (three stacked 60-110 word paragraphs
explaining the same per-car trade-off three times), the TF4ALL Dash expander
(nearly every toggle trailed by a paragraph duplicating its tooltip), the
Wheel lights / OLED block (three always-visible paragraphs under one
checkbox), and the Presets tab (every Library view opens with a paragraph
re-enumerating the button row beneath it).

Untouched on purpose: safety and diagnostic banners (G HUB, wheel-quiet, slip
starved, clip warnings), consent and privacy copy, the Forza numbered setup
steps, troubleshooting content, and empty-state text.

How to read: **Now** is the exact current text. **Trim** is the proposed
visible text ("delete" = the line goes away). **Tip** is what moves into a
ToolTip (on the control named). Reply with group letters and numbers, e.g.
"A all, B except 4, C 2 and 5".

---

## A. Clean trims (recommended; drops only what a label, tooltip, or adjacent line already says)

**A1. Offline-edit banner** (SettingsControl.xaml:803 + the car-edit variant in SettingsControl.xaml.cs ~1594)
Now: "The game defaults are shown (and locked) for context; only the per-car effects are editable. Save the usual way, or Revert a section to undo it. Done finishes and returns to your live setup."
Trim: "Game defaults are locked for context; only the per-car effects are editable."
Note: the Done button's tooltip already explains Done; both the XAML default and the code-built variant get the same trim.

**A2. Car facts no-car placeholder** (SettingsControl.xaml:1145)
Now: "Once you're driving, the car's facts (name, engine, redline) show here. If the redline buzz or the rev lights fire at the wrong RPM, this is where to fix it."
Trim: "Once you're driving, the car's facts show here. Wrong redline buzz or rev lights? This is where to fix it."

**A3. Engine pulse intro line** (SettingsControl.xaml:1544)
Now: "For the most accurate engine pulse, make sure the engine type is correct in Car facts on the active car card."
Trim: "Set the engine type in Car facts (active car card) for the most accurate pulse."

**A4. Per-gear redlines help** (SettingsControl.xaml:1224; low priority, the expander is now hidden by default)
Now: "Optional. Set a different redline start (where you upshift) for specific gears, e.g. a lower 1st-gear shift point. Gears you don't list use the redline above."
Trim: "Set a different redline for specific gears, e.g. a lower 1st-gear shift point. Unlisted gears use the redline above."

**A5. "Slides and drifting" expander intro** (SettingsControl.xaml:3045)
Now: "How the wheel behaves when the car slides or drifts."
Trim: delete (restates the expander header verbatim).

**A6. Mode B game note, first sentence** (SettingsControl.xaml:2741; check for code-built variants when implementing)
Now: "Enable this per game, for whichever game is running. Set that game's own force feedback and vibration to 0."
Trim: "Set that game's own force feedback and vibration to 0."
Tip (on the Enable checkbox): "Remembered per game, for whichever game is running."

**A7. Rev light pattern help, Wheel lights section** (SettingsControl.xaml:3376; twin of the Car facts line already trimmed)
Now: "Picking a pattern sets the wheelbase's selection, exactly like using the wheel's own menu: it applies now and stays. Remember for this car is optional automation on top, re-applying the pattern whenever that car loads. Colors and direction come from the pattern itself (set in G HUB)."
Trim: "Remember for this car re-applies the pattern whenever that car loads. Colors and direction come from the pattern itself (set in G HUB)."
Tip (on the pattern combo): "Picking a pattern sets the wheelbase's selection immediately, exactly like the wheel's own menu, and it stays until you change it."

**A8. Performance expander intro** (SettingsControl.xaml:4016)
Now: "Smaller ring buffers = less latency between game and wheel, but require Windows to wake our threads on time. Auto starts at the smallest sizes and ratchets up if dropouts occur (one-way; survived size persists across sessions). Manual locks specific sizes (useful for streamers who want guaranteed-stable behavior, or to force-test the lowest values)."
Trim: "Smaller ring buffers = less latency between game and wheel, but require Windows to wake our threads on time."
Note: sentences two and three restate the Auto/Manual radio tooltips nearly verbatim.

**A9. Dash default-tab help** (SettingsControl.xaml:3666)
Now: "With remember on, the dash reopens where you left it, even across SimHub restarts. Turn it off to always start on the default tab picked here (applies at the next SimHub start)."
Trim: delete.
Tip (on the Default tab combo): "The tab the dash opens on when 'Remember last used tab' is off. Applies at the next SimHub start."

**A10. Dash idle-card intro** (SettingsControl.xaml:3774)
Now: "With no game running the dash shows an ambient card with your name and number instead of an empty dashboard. It clears the moment a game starts, and there is an Exit button on the card itself."
Trim: delete.
Tip (on the idle-card checkbox): "Shows an ambient card with your name and number instead of an empty dashboard. It clears the moment a game starts, and the card has its own Exit button."

**A11. Community car facts cache line** (SettingsControl.xaml:4252)
Now: "Cached locally and refreshed at most weekly per car, so it works offline and keeps server traffic low."
Trim: delete (the checkbox tooltip already says all of it).

**A12. Update-interval help** (SettingsControl.xaml:3602)
Now: "The plugin already checks once each time SimHub starts. This adds background re-checks so a fix released mid-session still shows the update banner without a restart. When one's available, an 'Update to vX.Y.Z' button appears in the header up top."
Trim: "When an update is available, an 'Update to vX.Y.Z' button appears in the header up top."

**A13. Beta updates help** (SettingsControl.xaml:3614)
Now: "Beta builds get fixes and new effects first, but are less tested. Pre-releases are always available by hand on GitHub; this just brings them to the in-app updater. Turn it off to return to the stable release next time one ships."
Trim: "Beta builds get fixes and new effects first, but are less tested. Turn it off to return to the stable release next time one ships."
Tip (on the checkbox): "Pre-releases are always public on GitHub; this just saves downloading each one by hand."

**A14. Beta channel on-state note** (SettingsControl.xaml.cs ~8632)
Now: "You're on the beta channel: the in-app updater will offer pre-release builds."
Trim: delete (restates the checked box beneath it; the off-state message on a prerelease build stays).

**A15. Experimental FFB capture help** (SettingsControl.xaml:3979)
Now: "Try this if you are not getting force feedback in a game that should have it. Load the game and drive for a few seconds after turning it on. Leave it off if your force feedback already works."
Trim: "Try this if you are not getting force feedback in a game that should have it."
Note: the rest duplicates the checkbox tooltip.

**A16. Forza forward intro** (SettingsControl.xaml:3898)
Now: "Also passes Forza's telemetry to SimHub so its dashboards, bass shakers, and Buttkicker keep working. Forza already points at this plugin; this just relays a copy on to SimHub. Setup:"
Trim: "Forza already points at this plugin; this just relays a copy on to SimHub. Setup:"

**A17. Backup-to-file help** (SettingsControl.xaml:4407)
Now: "Bundle your full Trueforce For All state (settings, every preset, every default, every car tuning) into one archive for moving to a new machine. To share individual presets or car tunings with other users, use Import / Export in the Preset Manager (Presets tab) instead."
Trim: "One archive of your full setup for moving to a new machine. To share individual presets or car tunings, use Import / Export in the Preset Manager instead."

**A18. Spike-method descriptions** (SettingsControl.xaml.cs ~5126, both ternary branches)
Now: each variant opens by repeating the selected radio's own label ("Slew-rate limiter (iRacing-style): ..." / "Transient detector: ...").
Trim: drop the leading label repetition; keep the behavior sentences.

**A19. Ratchet banner tail** (SettingsControl.xaml.cs ~15988)
Now: ". Persisted across sessions. Revert to restore the size(s) at the start of this run, or dismiss to keep the current size(s)."
Trim: ". Persisted across sessions. Revert restores this run's starting sizes."

**A20. Presets: Game presets intro** (PresetManagerControl.xaml:327)
Now: "Select a preset to see what it contains; or Edit to open it on the Effects tab. You can rename, duplicate, delete, and choose which games auto-load each preset. Built-ins can't be renamed or deleted; saving over one forks a copy."
Trim: "Select a preset to see what it contains. Built-ins can't be renamed or deleted; saving over one forks a copy."

**A21. Presets: Car presets intro** (PresetManagerControl.xaml:417)
Now: "Every car preset, grouped by game then car. A car can have several presets; the ★ in Default marks the one that loads for that car. Rename, duplicate, delete, or set a preset as the car's default."
Trim: "A car can have several presets; the ★ in Default marks the one that loads for that car."

**A22. Presets: Packs intro** (PresetManagerControl.xaml:577)
Now: "A pack bundles several presets at once. Pick an installed pack to set every preset it includes as a default in one go, or to remove the whole bundle (preserving any entries you've edited). Browse the Community view to find packs other drivers have shared."
Trim: "A pack bundles several presets at once. Find shared packs in the Community view."
Note: the middle sentence restates the two buttons' tooltips, including the preserved-edits caveat.

**A23. Presets: Custom engines intro** (PresetManagerControl.xaml:510)
Now: "Your saved custom engines. Edit or delete them here; if you delete one a preset uses, that preset falls back to the Auto engine mode until you pick a new one. Built-in layouts (V8, Rotary, etc.) aren't listed."
Trim: "Engines you've created. Built-in layouts (V8, Rotary, etc.) aren't listed."
Tip (on the Delete button, currently none): "If a preset uses this engine, it falls back to the Auto engine mode until you pick a new one."

**A24. Presets: Community view help** (PresetManagerControl.xaml:616)
Now: "Browse + download community presets for the car you're driving. Pick a row and Download to import; you'll get a section picker so you can take just the parts you want."
Trim: "Browse and download community presets for the car you're driving."
Note: the Download tooltip and the picker itself cover the rest before anything imports.

**A25. Dev mode banner** (PresetManagerControl.xaml:253; owner-only)
Now: "Developer mode is active. Per-row Set as built-in buttons + the Developer toolbar at the bottom are unlocked. Type DEV again in Settings to turn it off."
Trim: "Developer mode is active. Type DEV again in Settings to turn it off."

**A26. Mode B badge, no game** (SettingsControl.xaml.cs ~1069)
Now: "No supported game is running. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25. Start one of those to turn it on. You can still see and pre-tune the controls below."
Trim: "No supported game is running. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25."
Tip: "You can pre-tune the controls below without a game; the per-game Enable box unlocks when a supported game starts."

**A27. Mode B badge, unsupported game** (SettingsControl.xaml.cs ~1070)
Now: "Not available in {game}. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25. It also enables your wheel's rev lights. Start one of those to turn it on."
Trim: "Not available in {game}. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25."
Tip: "Telemetry Based FFB also enables your wheel's rev lights and screen in these games."

**A28. Mode B intro, Farming Simulator variant** (SettingsControl.xaml.cs ~1006)
Now: ends with "Also works in Forza Motorsport (2023) and Forza Horizon 4, 5, and 6."
Trim: move that cross-sell sentence to a tooltip; the FS-specific two sentences stay visible unchanged.

## B. Trims that move real detail to hover (recommended; the detail survives in a tooltip)

**B1. Airborne ducking intro** (SettingsControl.xaml:2543; 87 words, the largest effect help block)
Now: "Cuts vibrations while the car is in the air, so a jump feels weightless instead of buzzing as if the tires were still on the track. Everything comes back the moment you land. Choose how far to turn things down and which effects to cut below (the engine keeps pulsing by default, since it's still revving). Works in Assetto Corsa, Forza, and Farming Simulator (with the TF4ALL Enhanced Telemetry mod), the games that report enough to know the car has left the ground; it does nothing elsewhere."
Trim: "Cuts vibrations while the car is in the air, so a jump feels weightless. Works in Assetto Corsa, Forza, and Farming Simulator (with the TF4ALL Enhanced Telemetry mod); it does nothing elsewhere."
Tip (on the enable checkbox): "Everything comes back the moment you land. The checkboxes below choose which effects are cut; the engine keeps pulsing by default, since it's still revving."

**B2. iRacing Strength help** (SettingsControl.xaml:2862)
Now: "Your overall preference, and the only force control you should need day to day. One setting for every car: turn it up and everything gets stronger by the same amount, so there is nothing to re-tune when you switch cars. 1.00 delivers what the sim asked for. Raise it if the wheel feels light, lower it if it clips."
Trim: "The only force control you should need day to day. 1.00 delivers what the sim asked for; raise it if the wheel feels light, lower it if it clips."
Tip (on the Strength label): "One setting for every car: everything gets stronger by the same amount, so there is nothing to re-tune when you switch cars."

**B3. iRacing per-car checkbox help** (SettingsControl.xaml:2878)
Now: "This is the switch that decides whether cars feel different. Off, one number scales everything, so set it from your HEAVIEST car and nothing will clip while lighter cars sit honestly below it. On, each car reaches full force at its own limit: nothing clips and nothing feels weak, but a Formula Vee and a GT3 end up feeling much alike."
Trim: "Off, set it from your heaviest car and nothing will clip."
Tip: extend the existing checkbox tooltip with "On, each car reaches full force at its own limit, so a Formula Vee and a GT3 end up feeling much alike."

**B4. iRacing Max force help** (SettingsControl.xaml:2880; 111 words, the largest in the app)
Now: "Max force is the car torque at which your wheel is giving everything it has, and it decides whether cars keep their relative weight. A DIFFERENT number per car makes every car reach full force at its own limit, so they all feel about the same effort. ONE number for every car keeps the difference: a heavy GT3 pushes harder than a light car, as it does in reality. Follow iRacing inherits whatever you set in its black box, which flattens them if you use iRacing's auto force and keeps them apart if you set one value there. Strength is your preference on top and is never changed for you."
Trim: "Max force is the car torque at which your wheel is giving everything it has."
Tip (on the Max force label): "Follow iRacing inherits whatever you set in iRacing's black box. Strength is your preference on top and is never changed for you."
Note: the per-car trade-off currently appears three times within a few rows; B3 keeps it once.

**B5. iRacing Detail method help** (SettingsControl.xaml:2891)
Now: "Two ways to turn iRacing's 360 Hz force into a smooth stream. Both use all six samples per frame. Lead sends the part that pushes your hands immediately and lets fine detail trail slightly. Replay plays each event out whole and uses your wheel's own movement to stay current. Drive both and keep the one you prefer; they are different trades, not better and worse."
Trim: "Drive both and keep the one you prefer; they are different trades, not better and worse."
Tip (on the combo): "Both use all six of iRacing's 360 Hz samples per frame. Lead sends the part that pushes your hands immediately and lets fine detail trail slightly. Replay plays each event out whole and uses your wheel's own movement to stay current."

**B6. iRacing Prediction help** (SettingsControl.xaml:2900)
Now: "Replay only, and NOT something you need to tune. The plugin works out for itself how far ahead this car's force is heading, learning it fresh for every car you drive, so 1.00 means simply using what it found. Turn it down only if prediction feels nervous to you, or to 0 to switch it off and accept a slightly later force."
Trim: "Replay only; nothing you need to tune. Turn it down only if prediction feels nervous, or to 0 to switch it off."
Tip: "The plugin learns how far ahead each car's force is heading, fresh for every car you drive; 1.00 means using what it found. 0 accepts a slightly later force."

**B7. iRacing 360 Hz help** (SettingsControl.xaml:2905)
Now: "Sharper, and it does NOT add delay: the extra samples are used to track where the force is heading, not to replay where it has been. Turn it off if the wheel ever feels nervous or busy at a standstill."
Trim: "Turn it off if the wheel ever feels nervous or busy at a standstill."
Tip: extend the existing checkbox tooltip with "Sharper without added delay; the extra samples track where the force is heading, not where it has been."

**B8. Forza Auto strength help** (SettingsControl.xaml:2767)
Now: "Levels cars out: learns how hard each car pushes the wheel, then scales it so every car lands at the heaviness your Strength slider sets. No more retuning per car; the trade is that cars stop feeling lighter or heavier than each other. Takes a few laps per car to settle."
Trim: "Levels cars out so every car lands at the heaviness your Strength slider sets. The trade: cars stop feeling lighter or heavier than each other. Takes a few laps per car to settle."
Tip: "Learns how hard each car pushes the wheel, then scales it to match your Strength setting."

**B9. Grip auto-cal help** (SettingsControl.xaml:3091)
Now: "Learns where each car's grip actually tops out as you drive (about a lap of pushing) and shapes the limit and braking feel to it: the wheel lightens at each car's real grip limit as you brake or slide, instead of at one fixed point. Off = a fixed manual grip limit (the slider below) and the classic lockup fade."
Trim: "The wheel lightens at each car's real grip limit, learned as you drive (about a lap of pushing). Off = a fixed manual grip limit (the slider below)."
Tip: "Shapes both the grip limit and the braking fade to each car's measured grip as you brake or slide, instead of one fixed point. Off also restores the classic lockup fade."

**B10. Spring minimum force help** (SettingsControl.xaml:2828)
Now: "The smallest force your wheel actually plays here: lifts faint terrain hits, and light centering once the wheel is off dead-ahead, above the wheel's own friction. Raise it on a belt-driven wheel (G923) until faint bumps just move the wheel. A parked wheel stays limp, and damping is unaffected."
Trim: "The smallest force your wheel actually plays. Raise it on a belt-driven wheel (G923) until faint bumps just move the wheel."
Tip: "Lifts faint terrain hits and light centering above the wheel's own friction. A parked wheel stays limp, and damping is unaffected."

**B11. Collision sources clause** (SettingsControl.xaml:2350)
Now: "A thud on impact that hits harder the bigger the crash. Sources: PC2 reads its native impact magnitude directly; other sources derive from sudden lateral/vertical accel spikes above ~5g (well above hard cornering or curbs)."
Trim: "A thud on impact that hits harder the bigger the crash."
Tip: "PC2 supplies its native impact magnitude; other games derive it from sudden accel spikes above about 5g, well above hard cornering or curbs."

**B12. Pit limiter source clause** (SettingsControl.xaml:2194)
Now: "Feels like the engine being cut at low RPM while the pit limiter is engaged. A steady pulse train you can't miss in the pit lane. Source: SimHub StatusDataBase.PitLimiterOn (works for most racing sims that have a pit lane)."
Trim: "Feels like the engine being cut at low RPM while the pit limiter is engaged: a steady pulse train you can't miss. Works in most racing sims that have a pit lane."
Tip: "Driven by SimHub's PitLimiterOn property."

**B13. DRS source clause** (SettingsControl.xaml:2264)
Now: "A quick chirp the moment DRS opens, then a faint sustained tone while the wing stays open. Silent on games that don't expose DRS. Source: SimHub StatusDataBase.DRSEnabled."
Trim: "A quick chirp the moment DRS opens, then a faint sustained tone while the wing stays open. Silent on games that don't expose DRS."
Tip: "Driven by SimHub's DRSEnabled property."

**B14. Implement thud slider help lines** (SettingsControl.xaml:2052, 2060, 2068, 2076, 2084, 2092, 2100; ~130 words)
Now: seven visible per-slider help lines, e.g. "How much slow hydraulic movement quiets the whine: it swells as motion picks up and fades as it eases into the stop. 0 = constant loudness."
Trim: convert all seven to ToolTips on their slider labels, texts unchanged.
Note: Axle slip is the house pattern; its same-style sliders carry label tooltips with no visible help lines.

**B15. Axle slip sub-checkbox help lines** (SettingsControl.xaml:1881 and 1889)
Now: "Predicts the slide from how fast grip is being used up, so the warning arrives a fraction before the limit instead of at it." (and the wheelspin-locked rear pulse sibling)
Trim: both become checkbox ToolTips, texts unchanged (matches the Engine pulse sub-checkboxes).

**B16. Low-end body intro line** (SettingsControl.xaml:1597)
Now: "Helps the engine still feel present at high RPM, where the wheel's motor can't keep up with the firing rate."
Trim: delete (the checkbox tooltip explains the same mechanism in more depth).

**B17. Mode B reset help** (SettingsControl.xaml:3173)
Now: "Restores your wheel's defaults for the game you are in, and leaves the other one alone: resetting in Farming Simulator does not touch your Forza setup. The G PRO, RS50, and G923 each get their own strength; the G923 also gets its own damping, and its own floor and strength in Farming Simulator. Your per-game on/off choices, each car's learned grip calibration, and your rev lights and screen settings are kept."
Trim: "Resets only the game you are in: resetting in Farming Simulator does not touch your Forza setup. Your per-game on/off choices, each car's learned grip calibration, and your rev lights and screen settings are kept."
Tip: "Puts every Telemetry Based FFB slider and feel toggle back to your wheel's defaults. The G PRO, RS50, and G923 each have their own: each gets its own strength, and the G923 also gets its own damping plus its own floor and strength in Farming Simulator."

**B18. OLED notifications paragraph** (SettingsControl.xaml:3387; second of three stacked paragraphs)
Now: "It also reports changes as they happen: a preset loading, a gain nudged from a bound button, Auto strength settling on a car, or a warning that the game is still sending its own force feedback. Each shows for a moment, then your driving screen comes back."
Trim: delete.
Tip (on the OLED checkbox): same text.

**B19. Dash incidents help** (SettingsControl.xaml:3764; also fixes the amber/violet copy bug from the 0.2.7 testplan)
Now: "Shows the points you have taken against the limit that ends your session, turning amber and then red as you run out of room. Each new incident announces what it cost across the top of the dash and on the wheel base screen, so you know whether it was a 1x or a 4x without waiting for the replay. iRacing is the only game that publishes a count, so nothing appears anywhere else."
Trim: "Shows the points you have taken against the limit that ends your session, turning violet and then red as you run out of room."
Tip: "Your incident count sits under the speed on the Drive tab, and a band across the top and the wheel base screen announce each new incident with what it cost (1x, 4x). iRacing is the only game that publishes a count, so nothing appears anywhere else."

**B20. Dash spotter help** (SettingsControl.xaml:3758)
Now: "SimHub works this out from the other cars in the session, so like the flags it only lights up in games that report car positions and simply never appears in the ones that do not."
Trim: delete.
Tip: extend the checkbox tooltip with "Only games that report car positions can drive it; it never appears in the others."

**B21. Dash per-game layout help** (SettingsControl.xaml:3717)
Now: "Games report different telemetry, so a layout that fills up in one can be half empty in another. With this on, boxes you pick while a game is running are remembered for that game. A game you have not set up yet uses the layout above, and so do changes you make with no game running."
Trim: "A game you have not set up yet uses the layout above, and so do changes you make with no game running."
Tip: "Each game keeps its own set of four boxes; switching games loads the one you last used there. Games report different telemetry, so a layout that fills up in one can be half empty in another."

**B22. Variants window intro** (CarFactsVariantsWindow.cs ~117)
Now: "Variants are created automatically the first time this car shows a new engine. Rename a row by clicking its label (cosmetic, stays local). The Engine dropdown pins that variant's engine type; Auto lets detection decide. Delete drops the row; if that engine turns up again, a fresh row is created. Built-in rows come from the car list and can't be edited."
Trim: "Variants are created automatically the first time this car shows a new engine. In the Engine dropdown, Auto lets detection decide; built-in rows can't be edited."
Tip: on the Label column header, "Renaming a row is cosmetic and stays on this machine."; extend the Delete tooltip with "If that engine turns up again, a fresh row is created."

**B23. Custom engine editor intro** (CustomEngineEditor.xaml:37)
Now: "Build the pulse rhythm your wheel plays for an engine nothing built-in matches. Pick the firing pattern closest to the real engine and set the cylinder or rotor count; the pattern below fills in from those until you hand-edit it (Regenerate refills it on demand). Save it, then choose it in a variant's Engine dropdown to hear it on that car."
Trim: "Pick the firing pattern closest to the real engine and set the cylinder or rotor count. Save, then choose it in a variant's Engine dropdown to hear it on that car."

**B24. Custom engine pattern help** (CustomEngineEditor.xaml:95)
Now: "Each number is when a cylinder fires within one engine cycle, from 0 to 1. Hand-edit for odd-fire engines; an optional :weight per pulse (0.25:0.85) makes that pulse softer."
Trim: "Each number is when a cylinder fires within one cycle, from 0 to 1."
Tip: merge the :weight syntax and hand-edit note into the existing Pattern label tooltip.

## C. Your call (each trims something with real visible value; my lean noted)

**C1. Header subtitle** (SettingsControl.xaml:655; the largest always-on block in the app, on every tab)
Now: "Everything your Logitech wheel can do, in games that never supported it: Trueforce haptics, Telemetry Based FFB, rev lights and a customizable wheelbase OLED screen. Unofficial reverse-engineered implementation of Logitech's Trueforce and OLED protocols, not affiliated with Logitech. Supports G PRO, RS50 and G923; the screen is G PRO and RS50 only."
My lean: "Everything your Logitech wheel can do, in games that never supported it: Trueforce haptics, Telemetry Based FFB, rev lights and a customizable wheelbase OLED screen. Unofficial; not affiliated with Logitech."
Tip: "Unofficial reverse-engineered implementation of Logitech's Trueforce and OLED protocols. Supports G PRO, RS50 and G923; the wheelbase screen is G PRO and RS50 only."
Trade-off: the supported-wheel list becomes hover-only; the four-word disclaimer stays visible.

**C2. Redline definition line** (SettingsControl.xaml:1205)
Now: "The redline start: where the tachometer turns red and you upshift. Not the rev limiter cutoff (that sits a little higher)."
Sweep proposed: delete, fold into the redline box tooltip.
My lean: shorten instead of delete: "Where the tach turns red and you upshift, not the rev limiter cutoff." A user typing the limiter cutoff here corrupts what gets shared to the community, so the one-line education earns its slot.

**C3. Mode B intro, iRacing variant** (SettingsControl.xaml.cs ~1013; 100 words)
Now: ends with "Two switches make the handover: turn iRacing's force feedback OFF in its options (do not just set its strength to 0, the plugin reads that number), and set loadTrueForceAPI=0 in app.ini."
My lean: accept the dedupe. That final sentence renders twice on the same screen today (ModeBGameNote shows it whenever iRacing is active), and the first-launch modal covers first-time setup. Trimmed intro: "The plugin takes force feedback over from iRacing rather than inventing its own: it reads the forces iRacing computes and delivers them over Trueforce, so the car still feels like the car and your rev lights and wheel screen work again. The two setup switches are in the note below."

**C4. Mode B intro, Forza variant** (SettingsControl.xaml.cs ~1021)
Sweep proposed: move "Set the game's force feedback and vibration to 0 so this is the only force on the wheel." into a tooltip.
My lean: keep that sentence visible (with no game running, ModeBGameNote is hidden and this intro is the only carrier; an armed Mode B with game FFB still on is the dead-wheel trap). Accept only the game-list merge: "The wheel's steering force is built from telemetry instead of the game's own FFB. Works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25 (through the spring option below). Set the game's force feedback and vibration to 0 so this is the only force on the wheel."

**C5. Wheel lights gating note, both variants** (SettingsControl.xaml:3358 defaults; live text set in SettingsControl.xaml.cs ~1062-1063)
Now (non-iRacing): "These need Telemetry Based FFB switched on. Writing to them while a game runs its own force feedback makes that force feedback cut out, because they share one channel on the wheel; replacing the game's force feedback frees them. A custom driver that would enable them in every game is in testing, but it needs to be signed by Microsoft first."
My lean (middle ground; the toggle gating, not this text, is what actually prevents the FFB cut-out): "These need Telemetry Based FFB switched on: the lights and screen share one channel on the wheel with the game's force feedback." Same shape for the iRacing variant. Tip carries the full mechanism plus the driver-in-testing teaser.

**C6. Spike reduction motivation** (SettingsControl.xaml:3314)
Now: middle sentence "Hits can be sharp enough to wrench the wheel against your grip, ruin a racing line, or strain your wrists on a long session."
Sweep proposed: move it to a tooltip. My lean: accept; the first sentence already motivates the feature. But it is the closest thing to safety persuasion in the sweep, so it is your call.

**C7. OLED reverse-engineering trivia** (SettingsControl.xaml:3389)
Now: "The screen's protocol was reverse engineered; Logitech never documented it for the public."
Sweep proposed: delete. My lean: delete from here; the credibility flex already lives in the header disclaimer and the README. Keep it if you like the flex where the feature is.

---

Implementation note (for whichever items you approve): several elements have
code-built text variants that overwrite the XAML default (Mode B intro,
wheel-lights note, offline-edit hint, badges); each approved item includes a
grep for other .Text writers of that element so no variant is missed. All
proposed tooltip text lands on the control the user is already looking at,
never on a container.
