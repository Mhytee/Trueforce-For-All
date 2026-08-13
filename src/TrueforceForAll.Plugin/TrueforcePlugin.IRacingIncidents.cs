// iRacing incident points: the running count, the limit that disqualifies you
// from the session, and a brief announcement each time the count moves.
//
// WHY THIS IS ITS OWN FILE. Everything here rides the raw iRacing object that
// IRacingTelemetryTick already resolved, so the read costs one extra dictionary
// lookup per SimHub tick. Keeping it out of TrueforcePlugin.cs is deliberate:
// that file is already ~18k lines and this is a self-contained piece with its
// own state, its own parser and no callers outside the one tick.
//
// TWO SOURCES, BOTH ON THE SAME OBJECT:
//
//  1. THE COUNT is per-tick telemetry, dictionary-only. The shipped
//     iRacingSDK.dll generates 217 typed properties out of iRacing's 333
//     channels and PlayerCarMyIncidentCount is not among them, so a typed
//     property lookup finds nothing and the dictionary is the route. Verified
//     against the assembly, not assumed.
//
//  2. THE LIMIT is in the session info YAML, not the telemetry. It changes only
//     when the session does, and iRacing hands us a version counter
//     (SessionData.InfoUpdate) that says so, so the YAML is scanned on a change
//     and never per tick. Raw and InfoUpdate are public FIELDS on that type,
//     not properties: GetProperty finds neither.
//
// The SDK's parsed session model is no use here. Its _WeekendOptions type
// predates the incident limit and has no such member, which is exactly why this
// scans the YAML text instead of walking the object graph. A flat key scan also
// survives iRacing moving the key, which a hard-coded path would not.
//
// Everything is name-based reflection with no compile-time reference on
// iRacingSDK.dll or ICarsReader.dll, matching the rule the force reshape set:
// SimHub can rename or drop either assembly and this must degrade to "no
// incident data" rather than take the plugin down.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using SimHub.Plugins;

namespace TrueforceForAll.Plugin
{
    public partial class TrueforcePlugin
    {
        // -1 means "not reporting": no iRacing, or a session that has not
        // published a count yet. Zero is a REAL reading (a clean session) and
        // the dash prints it, so the two cannot share a sentinel.
        private volatile int _irIncidents = -1;
        // 0 means unlimited, which is what most non-official sessions run and
        // what iRacing writes into the YAML as the word "unlimited". -1 is
        // "we have not read a session yet".
        private volatile int _irIncidentLimit = -1;
        // The last count we announced from. -1 arms the FIRST reading as a
        // baseline rather than an event: joining a session mid-race, or
        // starting the plugin with incidents already on the board, must not
        // fire a flash for points that were taken before we were looking.
        private int _irIncidentPrev = -1;
        // What the flash says ("+4x"), and when it stops saying it.
        private volatile string _irIncidentFlash = "";
        private long _irIncidentFlashUntil;
        // The magnitude alone ("4x"), for the wheel base's OLED, where the row
        // is ten characters and the label above it already says INCIDENT.
        private volatile string _irIncidentFlashShort = "";
        // Session-info version, so the YAML is parsed on a change and not on
        // every one of the 60 ticks a second that carry the same string.
        private int _irSessionInfoUpdate = int.MinValue;
        // One line per session, the first time a count reads. Without it a
        // readout showing nothing is ambiguous between "clean so far" and
        // "the channel is not there", which are opposite problems.
        private bool _irIncidentLogged;

        // How long an incident stays on screen. Long enough to read at racing
        // speed, short enough that a second incident in the same corner gets
        // its own announcement instead of being swallowed by the first.
        private const double IncidentFlashSeconds = 2.5;

        /// <summary>The driver's incident count, or -1 when nothing is
        /// reporting one.</summary>
        public int IRacingIncidents => _irIncidents;

        /// <summary>Incidents allowed before disqualification: 0 for an
        /// unlimited session, -1 when unknown.</summary>
        public int IRacingIncidentLimit => _irIncidentLimit;

        /// <summary>What the wheel's OLED should flash right now ("4x"), or
        /// empty. Read from the OLED context build in DataUpdate.</summary>
        internal string IRacingIncidentFlashShort =>
            IncidentFlashLive() ? _irIncidentFlashShort : "";

        /// <summary>True while the flash is still inside its window. Expiry is
        /// served on READ rather than by a timer, the same way Dash.Toast
        /// expires: nothing has to tick for the flash to end.</summary>
        private bool IncidentFlashLive()
        {
            long until = System.Threading.Interlocked.Read(ref _irIncidentFlashUntil);
            return until != 0 && Stopwatch.GetTimestamp() < until;
        }

        /// <summary>Drop everything on a game change. Called from the same
        /// place the rest of the per-game dash state resets, so a Forza session
        /// after an iRacing one does not inherit a count.</summary>
        internal void IRacingIncidentsReset()
        {
            _irIncidents = -1;
            _irIncidentLimit = -1;
            _irIncidentPrev = -1;
            _irIncidentFlash = "";
            _irIncidentFlashShort = "";
            System.Threading.Interlocked.Exchange(ref _irIncidentFlashUntil, 0);
            _irSessionInfoUpdate = int.MinValue;
            _irIncidentLogged = false;
        }

        /// <summary>One pass over the raw iRacing sample the force path already
        /// holds. Called from IRacingTelemetryTick BEFORE its force-mode gate:
        /// the count is dash data and has nothing to do with whether we are
        /// authoring force.</summary>
        private void IRacingIncidentsTick(object raw, object tel, IDictionary<string, object> dict)
        {
            try
            {
                string source;
                int count = ReadIncidentCount(tel, dict, out source);
                if (count < 0)
                {
                    // The session stopped publishing (garage, replay, or a
                    // channel that went away). Keep the last known count on
                    // screen rather than blanking it, but do not let the next
                    // real reading be measured against a stale baseline.
                    return;
                }

                ReadIncidentLimit(raw);

                if (!_irIncidentLogged)
                {
                    _irIncidentLogged = true;
                    int lim = _irIncidentLimit;
                    SimHub.Logging.Current.Info(
                        "[TF4ALL] iRacing incidents: count=" + count.ToString(CultureInfo.InvariantCulture)
                        + " via " + source
                        + ", limit=" + (lim > 0 ? lim.ToString(CultureInfo.InvariantCulture)
                                      : lim == 0 ? "unlimited" : "not in session info")
                        + ".");
                }

                int prev = _irIncidentPrev;
                _irIncidents = count;
                _irIncidentPrev = count;

                // First reading of a session, or a new session resetting the
                // board to zero. Neither is an incident, so neither flashes.
                if (prev < 0 || count < prev) return;
                int delta = count - prev;
                if (delta <= 0) return;

                _irIncidentFlashShort = delta.ToString(CultureInfo.InvariantCulture) + "x";
                _irIncidentFlash = "+" + _irIncidentFlashShort;
                System.Threading.Interlocked.Exchange(
                    ref _irIncidentFlashUntil,
                    Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * IncidentFlashSeconds));
            }
            catch { /* dash garnish: never disturb the data pump */ }
        }

        /// <summary>The driver's own count. Three channels carry one, and which
        /// of them is populated depends on the session: a team event fills the
        /// per-driver and team totals, a solo race fills all three alike. Own
        /// count first, because that is the number the driver is asked about;
        /// the others stand in only when it is absent.</summary>
        private static readonly string[] IncidentChannels =
        {
            "PlayerCarMyIncidentCount",
            "PlayerCarDriverIncidentCount",
            "PlayerCarTeamIncidentCount",
        };

        private static int ReadIncidentCount(object tel, IDictionary<string, object> dict, out string source)
        {
            for (int i = 0; i < IncidentChannels.Length; i++)
            {
                int v = ReadIntChannel(tel, dict, IncidentChannels[i]);
                if (v >= 0) { source = IncidentChannels[i]; return v; }
            }
            source = "none";
            return -1;
        }

        /// <summary>One integer channel, typed property first and the
        /// dictionary the telemetry type inherits second. -1 when absent or
        /// unreadable, which is why every caller tests for negative rather than
        /// zero: zero is a clean sheet.</summary>
        private static int ReadIntChannel(object tel, IDictionary<string, object> dict, string name)
        {
            object o;
            if (!IRacingChannel(tel, dict, null, name, out o) || o == null) return -1;
            try
            {
                double d = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d) || d < 0.0) return -1;
                return (int)d;
            }
            catch { return -1; }
        }

        /// <summary>Refresh the session's incident limit, but only when iRacing
        /// says the session info actually changed.</summary>
        private void ReadIncidentLimit(object raw)
        {
            if (raw == null) return;
            const System.Reflection.BindingFlags Pub =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

            var sessProp = raw.GetType().GetProperty("SessionData", Pub);
            if (sessProp == null || !sessProp.CanRead || sessProp.GetIndexParameters().Length != 0) return;
            object sess = sessProp.GetValue(raw, null);
            if (sess == null) return;

            var sessType = sess.GetType();
            // Fields, not properties: the SDK declares Raw and InfoUpdate as
            // public fields and GetProperty returns null for both.
            var updField = sessType.GetField("InfoUpdate", Pub);
            if (updField != null)
            {
                try
                {
                    int upd = Convert.ToInt32(updField.GetValue(sess), CultureInfo.InvariantCulture);
                    if (upd == _irSessionInfoUpdate) return;      // same YAML we already read
                    _irSessionInfoUpdate = upd;
                }
                catch { }
            }

            var rawField = sessType.GetField("Raw", Pub);
            string yaml = rawField != null ? rawField.GetValue(sess) as string : null;
            if (yaml == null) return;
            _irIncidentLimit = ParseIncidentLimit(yaml);
        }

        /// <summary>Pull IncidentLimit out of the session YAML. Returns the
        /// number of incidents allowed, 0 for an unlimited session, and -1 when
        /// the key is not there at all.
        ///
        /// Scanned as a flat key rather than walked as a path
        /// (WeekendInfo -> WeekendOptions -> IncidentLimit) on purpose: the key
        /// is unique in the document, and a scan keeps working if iRacing moves
        /// it, which is the failure a hard-coded path could not survive.</summary>
        internal static int ParseIncidentLimit(string yaml)
        {
            if (string.IsNullOrEmpty(yaml)) return -1;
            int i = yaml.IndexOf("IncidentLimit", StringComparison.Ordinal);
            while (i >= 0)
            {
                // The key has to START its line, or "MaxIncidentLimit" and any
                // other key ending in these characters would match it.
                bool atKeyStart = true;
                for (int b = i - 1; b >= 0; b--)
                {
                    char cb = yaml[b];
                    if (cb == '\n' || cb == '\r') break;
                    if (cb != ' ' && cb != '\t') { atKeyStart = false; break; }
                }
                int c = i + "IncidentLimit".Length;
                if (atKeyStart && c < yaml.Length && yaml[c] == ':')
                {
                    int end = yaml.IndexOf('\n', c);
                    string val = (end < 0 ? yaml.Substring(c + 1) : yaml.Substring(c + 1, end - c - 1))
                        .Trim().Trim('"', '\'');
                    if (val.Length == 0) return -1;
                    // iRacing writes the word, not a sentinel number.
                    if (val.IndexOf("unlimited", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
                    int n;
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0)
                        return n;
                    return -1;
                }
                i = yaml.IndexOf("IncidentLimit", i + 1, StringComparison.Ordinal);
            }
            return -1;
        }

        /// <summary>Dash properties for the incident readout and its flash.
        /// Called from the dash property registration in Init.</summary>
        private void AttachIncidentProperties()
        {
            // -1 keeps the whole readout hidden, which is the state every game
            // that is not iRacing sits in permanently.
            this.AttachDelegate("Dash.Incidents", () =>
                Settings?.DashIncidentsEnabled == false ? -1 : _irIncidents);
            this.AttachDelegate("Dash.IncidentLimit", () => _irIncidentLimit);
            this.AttachDelegate("Dash.IncidentFlash", () =>
                Settings?.DashIncidentsEnabled == false || !IncidentFlashLive()
                    ? "" : _irIncidentFlash);
        }
    }
}
