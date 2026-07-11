# Testing Trueforce For All v0.2.0 (pre-release)

Thanks for helping test the biggest release yet. This build brings a new telemetry-driven force feedback mode, Forza rev lights, three new effects, a rebuilt interface, community preset browser, cloud backup, and much more. It is a pre-release, so a few things are still being dialed in and your feedback helps shape what ships.

If you hit anything odd, jump to [Reporting feedback](#reporting-feedback) at the bottom.

## Getting the build

Download the pre-release installer here: `[pre-release download link]`, then run it while SimHub is closed. Use the installer, not a manual DLL copy: this build reworks the plugin's file layout to make presets file-based, and only the installer puts the new structure and built-in presets in place.

Because the installer is not code-signed yet, Windows SmartScreen may warn about an unknown publisher. Click **More info**, then **Run anyway**. (Code signing is on the roadmap.)

**Staying updated (Patreon supporters).**
Once you are on a pre-release build, future betas can come to you through the in-app updater instead of downloading each one by hand. In the plugin, open **Settings > Updates** and turn on **Get beta (pre-release) updates in-app**. It is a Patreon supporter perk; the pre-releases stay public on GitHub for everyone either way.

## Wheels

This build is for Trueforce-enabled Logitech wheels (for example the G PRO, G923, and RS50). The rev lights are confirmed on a G PRO, and the telemetry-based force feedback is confirmed on a G PRO and a G923. The RS50 should work too but is not hardware-verified yet, so RS50 reports are especially useful.

## What to try

### Telemetry Based FFB (Forza)
In the Forza titles (Forza Motorsport 2023 and Forza Horizon 4, 5, and 6), the plugin can build the entire steering force from the game's telemetry instead of passing the game's own force feedback through. It is an optional alternative to the built-in force feedback, and it also gives us full control of the wheel, unlocking the rev lights.

To try it:
1. Open the **Telemetry FFB** tab and enable it for the game you are running.
2. Set the game's own force feedback scale to 0 (this mode replaces the game's force feedback, so you do not want both at once).

It ships off by default. Tell us how it feels on your wheel, the sense of the grip limit, weight through corners, and catching the rear when it steps out.

### Framerate and Telemetry Based FFB
Forza sends its telemetry once per rendered frame, so Telemetry Based FFB is only as smooth as your framerate. Help us find where it holds up and where it falls apart so we can recommend a minimum FPS.

Try it across a few framerates and tell us how each feels:
- **Lower vs higher FPS.** Sample a low framerate (cap the game or run a heavier scene) and a high one. Note where the force feedback turns steppy, notchy, or vague, and where it feels smooth and connected.
- **Locked vs unlocked FPS.** Compare a locked rate against an uncapped one; a steady locked rate can feel smoother than a higher but fluctuating one. Tell us which feels better on your rig.

Include your FPS (and whether it was locked) with any force-feedback report so we can line the two up.

### Forza rev lights
With Telemetry Based FFB enabled, your wheel's rev lights fill and flash with the engine and honor the community redline start where one is confirmed. Check that they track the revs and light up around the right point.

### New effects
Three new texture effects, all off by default, built for the extra per-tire detail Forza exposes: **Axle slip**, **Kerb thump**, and **Lockup judder**. Enable them in the Effects tab and see how they feel.

### Airborne ducking and tire-load effects
Turn the traction loss and road bumps effects on, then check these two related fixes:

- **Airborne ducking (Assetto Corsa).** Turn on airborne ducking and go airborne over a kerb or crest. With it enabled, the wheel should fall quiet in the air instead of buzzing or rumbling, and pick back up when the tires land. The quieting only happens with airborne ducking on, and it essentially never fired in AC before. (Forza is already confirmed.)
- **Traction loss and road bumps.** These now scale with tire load, so they should feel tied to how hard each tire is working rather than buzzing at a flat level. Traction loss should swell as a loaded tire starts to slide and fade when the tire goes light; road bumps should hit harder when a tire is pressed into the road and soften as it unloads (over crests, through dips, on the inside wheel mid-corner). Drive normally and tell us how the load update feels, and whether it comes across as too strong, too subtle, or twitchy.

Real wheelspin, lockups, and slides on the ground should still come through as normal.

### The new interface
The single long-scrolling panel is gone, replaced by tabs (Effects, Telemetry FFB, Presets, Controls, Settings, Account, Support us). Poke around and flag anything confusing, mislabeled, or awkward.

### Adjusting gain on the fly
Two new ways to change the Trueforce master gain without opening the plugin panel: a gain widget on SimHub's home screen (in the Feedback section, next to the Motors and Wind tiles), and bindable **Master gain up** and **Master gain down** actions you can map to a wheel button or input from the Controls tab. Try both and confirm they move the gain as expected.

### Community and backup
Browse and share presets, packs, and car facts from the Presets tab, and sign in from the Account tab. Patreon supporters also get cloud backup and sync; manual export and import are available to everyone.

## Setup tips

- **Telemetry Based FFB feels doubled or fights itself.** You still have the game's own force feedback on. Set the game's force feedback scale to 0.

## Known limitations

- A custom driver that would enable rev lights in every game, and let the plugin run alongside a game's own force feedback instead of replacing it, is in development. It is not in this build.

## Heads-up: this is a pre-release

- **The community backend is test data.** Shared presets and packs here may be reset before the public launch. Your account, achievements, and any car facts you contribute are kept.
- **Telemetry Based FFB replaces your wheel's force feedback**, so it always ships off by default. It is a major change to how the wheel behaves; turn it on deliberately, per game.

## Reporting feedback

Two good places:

- **GitHub issues:** https://github.com/Mhytee/Trueforce-For-All/issues
- **Discord:** https://discord.gg/sfwsDqTsdn

When you report a problem, please include:

- Your **wheel model** and the **game**.
- What you did and what happened.
- The **SimHub log**: `<SimHub folder>\Logs\SimHub.txt`. The plugin's lines are prefixed `[TF4ALL]`.

## Your existing setup is safe

When you update, your existing presets are backed up first and then migrated automatically; you should not have to touch anything. The backup lives next to your normal settings file in SimHub's `PluginsData\Common\` folder, with a timestamped `.bak-...` filename. Keep it or delete it once you have confirmed everything still feels right.
