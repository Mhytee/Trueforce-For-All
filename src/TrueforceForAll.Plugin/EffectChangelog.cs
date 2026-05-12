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
