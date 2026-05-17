// Catalog of effects + per-version changelog entries that drive the
// "what's new" banner and the per-effect NEW badges.
//
// Two ideas live here:
//   - KnownEffectIds is the source of truth for what's shippable today.
//     On fresh install (or first run on a pre-feature settings file) the
//     plugin pre-marks every one as already seen, so existing users don't
//     get a wall of badges for effects they've already been tuning.
//   - Versions is an append-only, chronologically-ordered list. The banner
//     shows everything strictly newer than the user's LastSeenVersion;
//     dismissing stamps LastSeenVersion to the running build so the same
//     entries don't pop up twice.
//
// To ship a new effect:
//   1. Add its stable ID string to KnownEffectIds.
//   2. Add a ChangelogVersion to the tail of Versions with one ChangelogEntry
//      per user-visible thing (EffectId = the same string for items that
//      should also trigger a per-section "NEW" badge).

using System;
using System.Collections.Generic;

namespace TrueforceForAll.Plugin
{
    public sealed class ChangelogEntry
    {
        // Optional. When set, drives the per-effect "NEW" badge on this
        // section's expander header. When null, the entry is just a note
        // surfaced in the banner modal (e.g. behavior change, default
        // tweak) with no badge.
        public string EffectId { get; set; }
        public string Headline { get; set; }
        public string Description { get; set; }
    }

    public sealed class ChangelogVersion
    {
        public Version Version { get; set; }
        public string Title { get; set; }
        public List<ChangelogEntry> Entries { get; set; } = new List<ChangelogEntry>();
    }

    public static class EffectChangelog
    {
        // Stable IDs for every effect that gets a per-effect "NEW" badge.
        // Strings match TrueforcePlugin.SectionKind names so the existing
        // dirty-tracking + revert plumbing can share the same identifiers.
        // Append-only: removing or renaming an entry invalidates any user's
        // persisted SeenEffects entry for it.
        public static readonly IReadOnlyList<string> KnownEffectIds = new[]
        {
            "Audio", "Engine", "Bumps", "Traction", "Shift",
            "Abs", "PitLimiter", "Drs", "Collision",
        };

        // Ordered oldest -> newest. Append-only.
        public static readonly IReadOnlyList<ChangelogVersion> Versions = new ChangelogVersion[]
        {
            new ChangelogVersion {
                Version = new Version(0, 1, 0),
                Title = "Per-effect 'NEW' badges + this changelog",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Effects added after you upgrade will be flagged with a NEW badge",
                        Description = "The badge stays on each effect's section header until you expand it or change a value, so you'll always know which voices are new without having to read release notes.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 18),
                Title = "Logitech G923 fully supported",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "G923 (PlayStation and Xbox) fully supported",
                        Description = "Both G923 variants are now confirmed working by owners: Trueforce effects and in-game force feedback together. The Xbox/PC G923's force-feedback fix from 0.1.17 (it delivers FFB as the same Logitech HID++ message the G PRO uses, but over a different USB transport and feature index) is now verified on Xbox G923 hardware. If the G923 feels light, raise master or Trueforce gain: it is a quieter gear-driven wheel than the G PRO and RS50, so the conservative shipped defaults can read weak on it. Other wheels are unaffected.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 17),
                Title = "G923 PS confirmed; Xbox G923 FFB fix (needs testing)",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "G923 (PS/PC) confirmed working by an owner",
                        Description = "A PlayStation/PC G923 owner has confirmed the wheel working end to end: Trueforce effects and in-game force feedback together. If it feels light, raise master or Trueforce gain. The G923 is a quieter gear-driven wheel than the G PRO and RS50, so the conservative shipped defaults can read weak on it.",
                    },
                    new ChangelogEntry {
                        Headline = "Xbox G923: force-feedback fix included, not yet confirmed",
                        Description = "The Xbox/PC G923 delivers its force feedback as the same Logitech HID++ message the G PRO uses, but over a different USB transport and at a different feature index, so the plugin's tap missed it entirely and the Trueforce stream silenced the wheel's own force feedback instead of carrying it. The plugin now also reads that interrupt-endpoint HID++ path and auto-resolves its feature index the same way it does for the RS50. This was decoded from a community-submitted USB capture and is not yet confirmed on Xbox G923 hardware. Xbox G923 owners: please report whether in-game force feedback now works alongside the Trueforce effects. Other wheels are unaffected; the new path only fires on traffic shaped this way.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 15),
                Title = "Logitech G923 support (community testing)",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "G923 wheels recognized, ready for testing",
                        Description = "The Logitech G923 is now detected. Its protocol was decoded from USB captures: the G923's Trueforce motor uses the same protocol as the G PRO, and the plugin now taps the force-feedback path the G923 uses in non-Trueforce games so in-game FFB and Trueforce effects can coexist. This is built from confirmed captures but has not been tested on a physical G923 yet. G923 (PS/PC): please try it and report how it feels (effects playing, in-game FFB still present, direction and strength sensible). G923 (Xbox/PC): experimental, supported by inference only. If Trueforce effects work but your game's force feedback is silent, report it via Feedback then Report an issue with Export logs attached.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 14),
                Title = "Clearer 'restart after install' guidance",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Reboot prompt after a fresh USBPcap install",
                        Description = "USBPcap's capture driver (the part that lets the plugin read your game's force feedback) only attaches to your USB ports at boot, so right after a first-time install it does nothing until you restart. The installer now offers a 'restart now' option when it just installed USBPcap, and if the wheel is found but the FFB tap can't see it on the USB bus, the plugin and the in-panel banner now tell you to restart your computer first. This is the most common cause of 'Trueforce works but I get no in-game force feedback' on a new install.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 13),
                Title = "Forza UDP settings only show up in Forza",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "The Forza telemetry (UDP) section no longer appears in non-Forza games",
                        Description = "It now shows only while a Forza title (Horizon 4/5/6 or Motorsport) is the active game, instead of staying visible in Assetto Corsa and everything else. The old internal 'always listen' setting that kept it on for every game has been retired (it was a bridge for Forza Horizon 6 before SimHub auto-detected it, which it now does), so the panel stays uncluttered for the non-Forza majority.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 12),
                Title = "RS50 native force feedback now works",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Logitech RS50: game force feedback alongside Trueforce",
                        Description = "The FFB tap now resolves the HID++ force-feedback feature index per wheel instead of assuming the G PRO's. On the RS50 the game's native constant force is mirrored into the Trueforce stream, so you feel the game's normal force feedback and Trueforce haptics at the same time. G PRO is unchanged.",
                    },
                    new ChangelogEntry {
                        Headline = "Engine pulse auto-detects your car's engine in Forza Horizon 5",
                        Description = "The engine-pulse effect matches each car's real cylinder count and engine layout from a baked car catalog, so the firing-pattern rumble is correct with no manual setup. Forza Horizon 5 is covered now (this catalog actually shipped in v0.1.11 but was never called out); more Forza titles are being added.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 11),
                Title = "FFB protocol diagnostics for unsupported wheels",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Parser counters in the log every 5 seconds",
                        Description = "The FFB tap now logs a per-endpoint and per-transfer-type breakdown of OUT traffic to your wheel: how many control / interrupt / bulk / iso transfers, on which endpoint number, how many were ep0 Set_Reports, how many matched our pattern, and which (reportId, featIdx, funcByte) triplets the game sent. Surfaces protocol mismatches for wheels where the HID++ FFB format diverges from what we expect.",
                    },
                    new ChangelogEntry {
                        Headline = "Opt-in raw USB packet logging as pcap",
                        Description = "Diagnostics has a new 'Log raw USB FFB packets to usb-trace.pcap' checkbox. Off by default. When on, captures every OUT transfer (any endpoint, any transfer type) to your wheel as a real pcap file alongside SimHub's logs, openable in Wireshark with the USBPcap dissector. Export Logs bundles the pcap into the zip. Capped at 50 MB; toggle off + on to reset. Use only when support asks: the file grows several KB/sec during active FFB and exposes your wheel's USB bus traffic.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 10),
                Title = "Manual USB device picker + log export for easier support",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Manual USB device picker for FFB pass-through",
                        Description = "If auto-discovery can't find your wheel on the USB bus (USBPcap's descriptor cache can go stale on hot-plugged wheels), a new 'Pick device manually' dialog lists every USB device it sees and lets you map your wheel by hand. Selection persists across restarts. When your wheel is detected on HID but FFB pass-through can't find it, a banner surfaces the picker right at the top of the panel.",
                    },
                    new ChangelogEntry {
                        Headline = "Export logs button next to Report Issue",
                        Description = "Zips your SimHub logs and Trueforce settings to your Desktop and opens Explorer to the zip. Attach the zip to bug reports so I can debug what's actually happening on your machine. Report Issue also asks first whether you want to bundle logs.",
                    },
                    new ChangelogEntry {
                        Headline = "Periodic FFB-tap rediscovery",
                        Description = "When the FFB tap can't find your wheel, it now retries every 15 seconds without needing a plugin reload. Replugging the wheel mid-session is enough to recover.",
                    },
                    new ChangelogEntry {
                        Headline = "Smoking-gun diagnostics for FFB discovery failures",
                        Description = "Discovery now logs per-interface packet counts, descriptor matches, access-denied flags, and an explicit 'HID found wheel X but USBPcap did NOT see it' line when the two enumerations diverge. Makes triaging FFB pass-through issues from a log dump straightforward.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 9),
                Title = "Engine: load layer + high-RPM boost, Forza Horizon polish",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Engine: load layer for high-RPM perceptibility",
                        Description = "Adds a low-frequency oscillation at the engine's cycle rate (RPM/120 Hz) alongside the firing pulse. Sits in the band the wheel motor can actually render, so the engine keeps feeling present as the firing pulse rolls off at top end. On by default at strength 0.80; tune in the Engine section.",
                    },
                    new ChangelogEntry {
                        Headline = "Engine: high-RPM boost on the firing pulse",
                        Description = "Pre-emphasis on the firing-rate pulse, ramping in above 50% RPM up to the set amount at redline. Partially compensates for the wheel's natural high-frequency rolloff. On by default at amount 0.70; tune in the Engine section.",
                    },
                    new ChangelogEntry {
                        Headline = "Forza Horizon: audio capture works out of the box",
                        Description = "ForzaHorizon4/5/6 are now in the curated exe-name list, so per-process audio capture latches onto Forza without needing a manual exe override in Advanced settings. Existing per-game overrides still take precedence if you set one.",
                    },
                    new ChangelogEntry {
                        Headline = "Forza Horizon: ABS section flagged as not exposed",
                        Description = "Forza's Data Out telemetry doesn't include ABS pump activity (the brake pedal is there, but no anti-lock intervention flag), so the ABS effect can't fire in FH4/FH5/FH6 regardless of how you tune it. A grey 'not exposed by Forza UDP' badge now sits in the ABS header when an FH title is active, with a tooltip explaining why. The section stays interactive so the values still save for other games.",
                    },
                    new ChangelogEntry {
                        Headline = "Forza Horizon: built-in preset road bumps + traction loss tuned down",
                        Description = "The 'Forza Horizon (default)' built-in preset shipped with conservative-on-the-loud-side defaults for road bumps + traction loss. Both are now anchored to a current GPRO tuning: lower bump and surface gains, milder traction loss sensitivity. Forks of the built-in (any preset you saved yourself) keep your values.",
                    },
                    new ChangelogEntry {
                        Headline = "Forza / F1 UDP: stale 'restart plugin' note removed",
                        Description = "Port and bind-address changes have rebound the listener live since 0.1.4, but the helper text next to the port field still asked users to toggle the plugin off+on. Replaced with 'Takes effect immediately.' so the UI matches the actual behavior.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 7),
                Title = "Preset workflow + Manage Presets polish",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Preset dropdown applies on selection",
                        Description = "The Apply button is gone. Picking a preset from the dropdown loads it immediately. If you've made unsaved tuning, the same confirmation prompt appears; clicking No restores the previously active preset.",
                    },
                    new ChangelogEntry {
                        Headline = "Built-in presets read '(built-in)' instead of '(default)'",
                        Description = "The trailing ' (default)' marker overloaded with the per-game auto-load binding ('Set as default for this game'), making it ambiguous whether a preset was a factory default or the bound default. Built-ins now show as '(built-in)' in the dropdown, the active-preset header, and car-preset dropdowns. Underlying file names and saved settings are unchanged.",
                    },
                    new ChangelogEntry {
                        Headline = "Sortable columns in Manage Presets",
                        Description = "Every column header in the Manage Presets tabs is clickable now. First click sorts ascending, second click on the same column flips to descending. Sort choice persists across modal close/reopen and SimHub restarts, per tab.",
                    },
                    new ChangelogEntry {
                        Headline = "Pack picker groups car presets by game",
                        Description = "The Export pack picker now groups car presets under bold game headers (Assetto Corsa, Wreckfest 2, etc). Checking a game preset on the left filters the car list to the games that preset is bound to as a default, so an Export sweep can pull one game's set without scrolling through every car.",
                    },
                    new ChangelogEntry {
                        Headline = "F1 22-25 recognized as Trueforce-native",
                        Description = "The plugin now skips itself for F1 22-25 on PC the same way it does for Forza Motorsport, since those titles ship Logitech Trueforce natively. The bundled 'F1 25 (default)' preset is removed, and the F1 telemetry section auto-collapses for these games.",
                    },
                    new ChangelogEntry {
                        Headline = "Manage Presets: Export and Import live at the dialog footer",
                        Description = "Per-row 'Export…' buttons in the Game and Car tabs are gone, replaced by a single Export… / Import… pair at the bottom of the dialog. Same flow as the buttons on the main panel, so there's one place to manage file-level actions.",
                    },
                    new ChangelogEntry {
                        Headline = "Export metadata dialog matches the dark theme",
                        Description = "The 'Author / Version / Description' dialog that surfaces during Export used to fall through to WPF defaults (white background, black text). It now paints with the same dark chrome as the rest of the plugin.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 6),
                Title = "Bug fix",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "What's new banner now shows the correct version after an upgrade",
                        Description = "The header used to read 'What's new in v0.1.4' right after upgrading to v0.1.5 because the banner picked the last entry in the changelog array rather than the true max version. The modal also rendered older entries above newer ones for the same reason. Both now read version order regardless of file order.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 5),
                Title = "Built-in preset refresh + Pit limiter / DRS / Collision dirty fix",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Built-in presets now refresh on plugin load",
                        Description = "Earlier releases shipped built-in presets without sections that were added later (Pit limiter, DRS, Collision). If you installed before 0.1.3, those sections were missing in your saved 'Assetto Corsa (default)' / 'Wreckfest 2 (default)' presets, so the comparison against the current effect settings always reported them as unsaved and the Save button stayed lit on first launch. Built-in presets are now refreshed from the shipped JSON on every plugin load. User-saved presets (any preset not ending in ' (default)') are untouched.",
                    },
                    new ChangelogEntry {
                        Headline = "Forza Horizon and F1 25 default presets gained missing sections",
                        Description = "Forza Horizon (default) now ships with Pit limiter, DRS, and Collision sections; F1 25 (default) gained Collision. Same content as the Assetto Corsa baseline. The effects don't trigger from Forza Horizon's telemetry (no pit-limiter / DRS signal on the wire), but having the sections in the preset stops the Save button from spuriously flagging them as unsaved.",
                    },
                    new ChangelogEntry {
                        EffectId = "Drs",
                        Headline = "DRS sustained waveform default is now Sine",
                        Description = "The 0.1.3 release split the DRS waveform into separate activation and sustained pickers and defaulted both to Square. Square on the sustained tone reads harsh against the activation chirp; the AC and Wreckfest 2 baselines now use Sine for the trail. Existing presets you saved yourself keep whatever you set.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 4),
                Title = "Bug fix",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Forza/F1 UDP settings take effect without restarting SimHub",
                        Description = "Changing the listen port, bind address, or AlwaysListen toggle while a listener was running could leave a stale socket bound to the old port (or keep listening after AlwaysListen was turned off) until SimHub was restarted. Settings changes now rebind the socket within about a second in every combination of game-running and AlwaysListen state.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 3),
                Title = "Advanced settings + polish",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Performance, Sidechain ducking, and Diagnostics moved out of the main panel",
                        Description = "These three sections are rarely touched once tuning is dialed in. They now live behind an 'Advanced settings…' button near the bottom of the panel, so day-to-day tuning has less to scroll past. The controls themselves are unchanged.",
                    },
                    new ChangelogEntry {
                        EffectId = "Drs",
                        Headline = "DRS: separate waveforms for the blip vs the trail",
                        Description = "The activation chirp (rising edge) and the sustained tone (held while DRS stays open) used to share one waveform setting. Each gets its own selector now, so a sharp Square blip can be paired with a softer Sine trail. Old presets keep the single waveform you'd set, then default to Sine for the trail; tune the trail picker if you want them matched.",
                    },
                    new ChangelogEntry {
                        EffectId = "Collision",
                        Headline = "Collision: waveform selector",
                        Description = "The Collision section now exposes its waveform picker like every other effect (Sine / Square / Saw / Triangle / Noise). Default Square matches the previous bake.",
                    },
                    new ChangelogEntry {
                        Headline = "Save button clears when a slider returns to its prior value",
                        Description = "Dirty comparison was using a tolerance slightly smaller than the slider's display precision, so a slider that visibly returned to the same value could still flag the section as unsaved. The comparison now rounds both sides to the displayed precision before comparing.",
                    },
                    new ChangelogEntry {
                        Headline = "Built-in Assetto Corsa + Wreckfest 2 presets refreshed",
                        Description = "The factory 'Assetto Corsa (default)' and 'Wreckfest 2 (default)' presets were carrying tuning from before PitLimiter / DRS / Collision and the RoadBumps surface channel existed. Both presets are refreshed from a current AC tuning so a fresh install lands on sensible numbers across every effect. Existing custom presets are untouched.",
                    },
                    new ChangelogEntry {
                        Headline = "Out-of-the-box class defaults aligned with AC tuning",
                        Description = "Fresh installs (with no preset loaded yet) used to start with maximum-gain effect defaults, which were uncomfortably aggressive on most wheelbases. The C# defaults for every effect's gain / frequency / waveform are now anchored to the same conservative starting point as the refreshed AC preset.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 2),
                Title = "Settings panel + modal polish",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Manage presets and Set-as-default are now real buttons",
                        Description = "Promoted from understated text links to standard buttons so the primary preset actions stand out from the secondary 'Save as new' / 'Delete' / 'Clear default' links.",
                    },
                    new ChangelogEntry {
                        Headline = "Update dialog renders Markdown release notes",
                        Description = "GitHub release bodies use Markdown for headings and bullet lists; the update dialog now renders those instead of dumping the raw '##' and '-' characters as plain text.",
                    },
                    new ChangelogEntry {
                        Headline = "Update now button is green",
                        Description = "Confirm-action coloring on the primary button in the update dialog. Dismiss stays neutral.",
                    },
                    new ChangelogEntry {
                        Headline = "All plugin dialogs paint in the SimHub dark theme",
                        Description = "The Custom Engine editor, Manage Presets dialog, and every code-behind prompt now match the dark chrome of the host instead of falling back to system defaults (white background, black text).",
                    },
                    new ChangelogEntry {
                        Headline = "Tidied spacing in the settings panel",
                        Description = "ABS / Pit limiter / DRS / Collision sat further apart than the earlier effects; standardized. Performance and Diagnostics get the same treatment, with a real gap before the Presets section so it doesn't read as part of Diagnostics.",
                    },
                },
            },
            new ChangelogVersion {
                Version = new Version(0, 1, 1),
                Title = "Polish pass on the updater path",
                Entries = new List<ChangelogEntry>
                {
                    new ChangelogEntry {
                        Headline = "Inline 'Update to vX.Y.Z' button replaces the old banner",
                        Description = "When a newer release exists, the 'Check for updates' link in the header now turns into a prominent inline button. The separate orange banner row is gone, so the panel reads cleaner when no update is pending.",
                    },
                    new ChangelogEntry {
                        Headline = "Update and What's new dialogs match the SimHub dark theme",
                        Description = "Both pop-ups now render with light text on a dark background instead of the system default, so release notes are legible against SimHub's chrome.",
                    },
                    new ChangelogEntry {
                        Headline = "Manual 'Check for updates' link in the header",
                        Description = "A small link next to the version readout re-polls GitHub on demand, so users who leave SimHub open for hours don't have to restart it to discover a new release.",
                    },
                    new ChangelogEntry {
                        Headline = "Auto-updater no longer requires a connected wheel",
                        Description = "If a wheel can't be detected (unplugged, G HUB holding the HID), the plugin used to skip the update check entirely. It now polls regardless of wheel state, so users with a detection problem can still discover the fix when it ships.",
                    },
                    new ChangelogEntry {
                        Headline = "Installer 'Launch SimHub now' surfaces the window",
                        Description = "The post-install checkbox launches SimHub through the same path as a desktop double-click, so the window comes to the foreground instead of being stranded behind a taskbar button.",
                    },
                },
            },
        };

        /// <summary>Returns every changelog version strictly newer than the
        /// given baseline. Pass null to get the full history.</summary>
        public static IReadOnlyList<ChangelogVersion> EntriesNewerThan(Version since)
        {
            var list = new List<ChangelogVersion>();
            foreach (var v in Versions)
            {
                if (since == null || v.Version > since) list.Add(v);
            }
            return list;
        }

        /// <summary>Best-effort System.Version parse. Returns false (and a
        /// null out) for null / empty / malformed strings so callers can
        /// treat any of those uniformly as "no last-seen version".</summary>
        public static bool TryParseVersion(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            try { v = new Version(s); return true; }
            catch { return false; }
        }
    }
}
