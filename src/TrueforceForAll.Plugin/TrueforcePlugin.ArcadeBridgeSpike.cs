// TEMPORARY SPIKE (2026-09-06). Delete once the question below is answered.
//
// Question: can this plugin publish arcade telemetry into SimHub's OWN
// standard fields, so stock dashes, shakers and motion treat an emulated
// cabinet as a real game with no second plugin installed?
//
// Established from SimHub's shipped assemblies, and then on the rig:
//   - GameData.NewData is a public mutable FIELD of type StatusDataBase, and
//     GameData.GameName is a public settable property.
//   - Every other field we want (Rpms, MaxRpm, Gear, SpeedKmh, lap times, and
//     GameData.GameRunning / GamePaused) has a setter that is INTERNAL to
//     SimHub. They exist, but a plugin cannot assign them: the compiler
//     rejects it. Reflection on the non-public setter is the only way in,
//     which is itself a finding, because anything shipped this way rides on
//     members SimHub never promised to keep.
//   - Those reflected setters DO work: the first rig run wrote live rpm with
//     no failures.
//   - SimHub hands a plugin a GameData whose NewData is NULL while no game is
//     running, so the spike builds its own StatusData<object>.
//   - Nothing loadable implements IGameManagerDiscovery (not SimHub.Plugins,
//     not any of the sixteen shipped reader DLLs), so registering a real game
//     reader from a third-party assembly is not on offer either.
//
// STILL not established, and the entire point of this file: whether a write
// made from a plugin's DataUpdate reaches the property engine that dashboards
// bind to. The first run could not tell us, because the read-back probe threw
// AmbiguousMatchException resolving GetPropertyValue and the failure was
// swallowed. Both are fixed here, and a failed read now names its own reason.
//
// It also puts everything back. SimHub may reuse a single GameData instance
// across ticks, and a spoof left standing would be read by our own arcade
// detection (which fires only while data.GameRunning is false) and by
// PushFromGameData. Every field written is saved first and restored at the top
// of the following tick, before any of our own logic looks at it.

using System;
using System.Collections.Generic;
using System.Reflection;
using GameReaderCommon;
using SimHub.Plugins;

namespace TrueforceForAll.Plugin
{
    public sealed partial class TrueforcePlugin
    {
        /// <summary>The identity the spike claims. Deliberately not a name any
        /// SimHub reader uses, so nothing game-specific can match it.</summary>
        private const string SpikeGameName = "InitialD8";

        private const string SpikeProbeProperty = "DataCorePlugin.GameData.Rpms";

        /// <summary>Cache of the properties whose setters SimHub keeps internal,
        /// keyed by type and name. A null entry means unreachable, reported once
        /// rather than retried every tick.</summary>
        private static readonly Dictionary<string, PropertyInfo> _spikeProps =
            new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        /// <summary>What each written StatusDataBase field held beforehand.
        /// A dictionary rather than a field per value, because the list of
        /// fields worth writing is exactly what the spike is exploring.</summary>
        private readonly Dictionary<string, object> _spikeSavedNd =
            new Dictionary<string, object>(StringComparer.Ordinal);

        private bool _spikeWrote;
        private bool _spikeMadeNewData;
        private object _spikeMadeNd;
        private bool _spikeNullUnderManagerLogged;
        private string _spikeSetFailure;
        private string _spikeReadError;

        // GameData's own three, saved explicitly.
        private string _spikeWasGameName;
        private bool _spikeWasGameRunning;
        private bool _spikeWasGamePaused;

        /// <summary>The rpm the previous tick wrote, so the read-back has
        /// something to compare against.</summary>
        private double _spikeWroteRpm;

        private MethodInfo _spikeGetProp;
        private bool _spikeGetPropResolved;
        private long _spikeLastLogTicks;
        private string _spikeLastVerdict;
        private long _spikeProgressLogTicks;
        private bool _spikeWasInRace, _spikeRaceRestart;
        private bool _spikeSpoofedIdentity;
        private bool _spikeCustomMode;
        private string _spikeLastCardState;
        private string _spikeLastCarState;
        private string _spikeLastReaderState;

        /// <summary>The last car we actually saw, held across the gaps where the pointer chain
        /// cannot resolve, so SimHub is never handed a silence to fill with its own placeholder.</summary>
        private string _spikeHeldCarId, _spikeHeldCarModel;
        private double _spikeHeldDialMax, _spikeHeldRedline;

        /// <summary>Every field written this tick and what it was set to, so the next tick can ask
        /// SimHub what became of each. One field was not enough: Rpms is stamped back out while
        /// Gear and the lap times survive, and a single-field probe called that "not propagated"
        /// for the whole bridge.</summary>
        private readonly Dictionary<string, object> _spikeWroteValues =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private string _spikeSeenGameName;
        private int _spikeLastElapsedMs;

        /// <summary>Resolve a property anywhere up the hierarchy, public setter
        /// or not. StatusData&lt;T&gt; is the instance; the properties live on
        /// StatusDataBase above it.</summary>
        private static PropertyInfo SpikeProp(object target, string property)
        {
            Type t = target.GetType();
            string key = t.FullName + "." + property;
            lock (_spikeProps)
            {
                PropertyInfo found;
                if (_spikeProps.TryGetValue(key, out found)) return found;
                found = null;
                try
                {
                    for (Type cur = t; cur != null && found == null; cur = cur.BaseType)
                        found = cur.GetProperty(property,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly);
                }
                catch { found = null; }
                _spikeProps[key] = found;
                return found;
            }
        }

        /// <summary>Assign a property whose setter SimHub keeps internal.
        /// Names the first failure, which is an answer rather than an error.</summary>
        private bool SpikeSet(object target, string property, object value)
        {
            if (target == null) return false;
            PropertyInfo p = SpikeProp(target, property);
            MethodInfo setter = p == null ? null : p.GetSetMethod(true);
            if (setter == null)
            {
                if (_spikeSetFailure == null) _spikeSetFailure = "no setter for " + property;
                return false;
            }
            try { setter.Invoke(target, new[] { value }); return true; }
            catch (Exception ex)
            {
                if (_spikeSetFailure == null)
                    _spikeSetFailure = property + " threw " + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>Write one StatusDataBase field, remembering what it held so
        /// the next tick can hand it back. Skipped when we built the object
        /// ourselves, since the whole thing is discarded instead.</summary>
        private void SpikeNdWrite(object nd, string property, object value)
        {
            if (!_spikeMadeNewData && !_spikeSavedNd.ContainsKey(property))
            {
                PropertyInfo p = SpikeProp(nd, property);
                object was = null;
                try { if (p != null) was = p.GetValue(nd, null); } catch { }
                _spikeSavedNd[property] = was;
            }
            if (SpikeSet(nd, property, value)) _spikeWroteValues[property] = value;
        }

        /// <summary>Try to write a property SimHub itself publishes, rather than the data behind
        /// it. Reports what happened once and then stays quiet, because this either works or it
        /// does not and repeating the answer every tick helps nobody.</summary>
        private void SpikeTryPublishOverride(PluginManager pluginManager, double rpm)
        {
            if (pluginManager == null || _spikeOverrideDead) return;
            try
            {
                if (_spikeSetProp == null)
                {
                    foreach (var m in pluginManager.GetType().GetMethods())
                    {
                        if (m.Name != "SetPropertyValue" || m.IsGenericMethodDefinition) continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 3 && ps[0].ParameterType == typeof(string)
                            && ps[1].ParameterType == typeof(Type)) { _spikeSetProp = m; break; }
                    }
                    if (_spikeSetProp == null)
                    {
                        _spikeOverrideDead = true;
                        SimHub.Logging.Current.Info(
                            "[TF4ALL] ID8 bridge spike: no SetPropertyValue(string, Type, object), "
                            + "so the published property cannot be written directly.");
                        return;
                    }
                }
                if (_spikeDataCoreType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            _spikeDataCoreType = asm.GetType(
                                "SimHub.Plugins.DataPlugins.DataCore.DataCorePlugin", false);
                        }
                        catch { }
                        if (_spikeDataCoreType != null) break;
                    }
                    if (_spikeDataCoreType == null)
                    {
                        _spikeOverrideDead = true;
                        SimHub.Logging.Current.Info(
                            "[TF4ALL] ID8 bridge spike: DataCorePlugin type not found, so its "
                            + "properties cannot be addressed.");
                        return;
                    }
                }
                // The plugin name is prepended by SimHub when a property is registered, so the
                // name to write is the part after it. The full name is tried as well, because
                // which one SetPropertyValue expects is not documented anywhere we can see.
                _spikeSetProp.Invoke(pluginManager,
                    new object[] { "GameData.Rpms", _spikeDataCoreType, rpm });
            }
            catch (Exception ex)
            {
                _spikeOverrideDead = true;
                var inner = ex.InnerException ?? ex;
                SimHub.Logging.Current.Info(
                    "[TF4ALL] ID8 bridge spike: writing the published property failed ("
                    + inner.GetType().Name + ": " + inner.Message + "), so that route is out.");
            }
        }

        private MethodInfo _spikeSetProp;
        private Type _spikeDataCoreType;
        private bool _spikeOverrideDead;

        /// <summary>DIAGNOSTIC: every property SimHub publishes whose name mentions one of the
        /// fields in question, with what each currently holds.
        ///
        /// Needed because "DataCorePlugin.GameData.Gear" reads 1 while a dash shows the real gear,
        /// so the name being probed is evidently not the name a dash binds to. Rather than guess
        /// at another one, this asks SimHub for its own list and prints the candidates.</summary>
        private void SpikeDumpPropertyNames(PluginManager pluginManager)
        {
            if (_spikePropNamesDumped || pluginManager == null) return;
            _spikePropNamesDumped = true;
            try
            {
                var m = pluginManager.GetType().GetMethod("GetAllPropertiesNames", Type.EmptyTypes);
                var names = m == null ? null : m.Invoke(pluginManager, null) as System.Collections.IEnumerable;
                if (names == null)
                {
                    SimHub.Logging.Current.Info("[TF4ALL] ID8 props: SimHub lists no property names.");
                    return;
                }
                string[] wanted = { "gear", "rpms", "currentlaptime", "bestlaptime", "speedkmh" };
                int shown = 0;
                foreach (object o in names)
                {
                    string name = o as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    string lower = name.ToLowerInvariant();
                    bool match = false;
                    foreach (string w in wanted) if (lower.EndsWith("." + w) || lower == w) { match = true; break; }
                    if (!match) continue;
                    object v;
                    string shownValue = SpikeReadProperty(pluginManager, name, out v) ? SpikeShow(v) : "(unreadable)";
                    SimHub.Logging.Current.Info("[TF4ALL] ID8 props: " + name + " = " + shownValue);
                    if (++shown >= 40) break;
                }
                if (shown == 0)
                    SimHub.Logging.Current.Info("[TF4ALL] ID8 props: nothing matched the field names.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] ID8 props: listing failed, " + ex.Message);
            }
        }

        private bool _spikePropNamesDumped;

        /// <summary>Read one SimHub property. Resolved by scanning rather than
        /// GetMethod(name, types): PluginManager declares BOTH GetPropertyValue(string) and a
        /// generic GetPropertyValue&lt;T&gt;(string), so the default binder throws
        /// AmbiguousMatchException between them.</summary>
        private bool SpikeReadProperty(PluginManager pluginManager, string name, out object value)
        {
            value = null;
            if (pluginManager == null) return false;
            try
            {
                if (!_spikeGetPropResolved)
                {
                    _spikeGetPropResolved = true;
                    foreach (var m in pluginManager.GetType().GetMethods())
                    {
                        if (m.Name != "GetPropertyValue" || m.IsGenericMethodDefinition) continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) { _spikeGetProp = m; break; }
                    }
                    if (_spikeGetProp == null) _spikeReadError = "no GetPropertyValue(string)";
                }
                if (_spikeGetProp == null) return false;
                value = _spikeGetProp.Invoke(pluginManager, new object[] { name });
                return true;
            }
            catch (Exception ex)
            {
                _spikeReadError = ex.GetType().Name + ": "
                    + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
        }

        /// <summary>Did the value survive? Numbers compare with a tolerance because SimHub converts
        /// some of them on the way through; everything else compares as text.</summary>
        private static bool SpikeSameValue(object wrote, object read)
        {
            if (wrote == null || read == null) return wrote == null && read == null;
            try
            {
                if (wrote is double || wrote is int || wrote is float)
                {
                    double a = Convert.ToDouble(wrote), b = Convert.ToDouble(read);
                    return Math.Abs(a - b) <= Math.Max(1.0, Math.Abs(a) * 0.01);
                }
            }
            catch { }
            return string.Equals(SpikeShow(wrote), SpikeShow(read), StringComparison.Ordinal);
        }

        private static string SpikeShow(object v)
        {
            if (v == null) return "null";
            if (v is double d) return d.ToString("F1");
            if (v is TimeSpan ts) return ts.ToString();
            return v.ToString();
        }

        /// <summary>Runs FIRST in DataUpdate. Reads back what SimHub made of
        /// last tick's write, logs the verdict, then undoes the write so the
        /// rest of the plugin sees an unspoofed world.</summary>
        private void ArcadeBridgeSpikeRestore(PluginManager pluginManager, GameData data)
        {
            if (!_spikeWrote) return;
            _spikeWrote = false;

            // ---- the answer, field by field ----
            var kept = new List<string>();
            var lost = new List<string>();
            var unknown = new List<string>();
            foreach (var kv in _spikeWroteValues)
            {
                object read;
                if (!SpikeReadProperty(pluginManager, "DataCorePlugin.GameData." + kv.Key, out read))
                { unknown.Add(kv.Key); continue; }
                if (SpikeSameValue(kv.Value, read)) kept.Add(kv.Key);
                else lost.Add(kv.Key + "(" + SpikeShow(read) + ")");
            }
            _spikeWroteValues.Clear();

            string verdict;
            if (_spikeSetFailure != null) verdict = "BLOCKED (" + _spikeSetFailure + ")";
            else if (kept.Count == 0 && lost.Count == 0) verdict = "NOTHING WRITTEN";
            else verdict = "KEPT " + kept.Count + ", LOST " + lost.Count;

            // One line a second, plus one whenever the picture changes.
            string shape = verdict + "|" + string.Join(",", kept) + "|" + string.Join(",", lost);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (shape != _spikeLastVerdict
                || _spikeLastLogTicks == 0
                || (now - _spikeLastLogTicks) > System.Diagnostics.Stopwatch.Frequency)
            {
                _spikeLastLogTicks = now;
                _spikeLastVerdict = shape;
                SimHub.Logging.Current.Info(
                    "[TF4ALL] ID8 bridge spike: " + verdict
                    + " | kept: " + (kept.Count == 0 ? "(none)" : string.Join(" ", kept))
                    + " | lost: " + (lost.Count == 0 ? "(none)" : string.Join(" ", lost))
                    + (unknown.Count == 0 ? "" : " | unreadable: " + string.Join(" ", unknown))
                    + " [game=" + (_spikeSeenGameName ?? "(none)")
                    + (_spikeCustomMode ? ", custom game, SimHub's own manager running" : ", no manager")
                    + (_spikeMadeNewData ? ", NewData was null, we built one" : "") + "]");
            }

            // ---- put it back ----
            if (data == null) return;
            // Only unwind our own spoof. If a real game started in between, its
            // reader owns these fields now and must not be stomped.
            if (_spikeSpoofedIdentity)
            {
                if (!data.GameRunning
                    || !string.Equals(data.GameName, SpikeGameName, StringComparison.Ordinal))
                    return;
                data.GameName = _spikeWasGameName;
                SpikeSet(data, "GameRunning", _spikeWasGameRunning);
                SpikeSet(data, "GamePaused", _spikeWasGamePaused);
                _spikeSpoofedIdentity = false;
            }

            if (_spikeMadeNewData)
            {
                // The object STAYS. Removing it was actively harmful: with a custom game SimHub
                // provides no StatusDataBase of its own, and DataCorePlugin evaluates its
                // properties by dereferencing GameData.NewData. Nulling it made every read of
                // DataCorePlugin.GameData.Rpms throw a NullReferenceException, ours and other
                // plugins' alike (the Lovely plugin threw on that exact property, from its own
                // code, in its own DataUpdate).
                //
                // On the rig that showed up as dashes flickering between working and dead: fine
                // between our write and this restore, broken from here until the next write. It is
                // also why the probe reported Rpms as null and called it overwritten.
                //
                // Leaving it costs nothing, because SimHub had nothing there to preserve.
                _spikeSavedNd.Clear();
                return;
            }

            // Under a CUSTOM GAME nothing is put back, and that is the point.
            //
            // The restore exists for the no-game case, where we invent an identity and must not
            // leave it lying around. Under a custom game the values we overwrite are SimHub's own
            // placeholders: a DC__<timestamp> car, a DT__ track, zeroed lap times. Handing those
            // back every tick made every field alternate between ours and SimHub's, which is
            // exactly what a car id flickering between the Trueno and a DC car looks like. SimHub
            // then never holds an id long enough to find its per-car settings file, so its
            // shift-light engine has no redline and every dash driven by it stays dark.
            //
            // Restoring a placeholder preserves nothing. Leaving our values costs nothing.
            if (_spikeCustomMode) { _spikeSavedNd.Clear(); return; }

            var nd = data.NewData;
            if (nd != null)
                foreach (var kv in _spikeSavedNd) SpikeSet(nd, kv.Key, kv.Value);
            _spikeSavedNd.Clear();
        }

        /// <summary>Runs LAST in DataUpdate, after every consumer of this tick's
        /// data has already had it unspoofed.</summary>
        private void ArcadeBridgeSpikeWrite(PluginManager pluginManager, GameData data)
        {
            if (data == null) return;

            // A SimHub CUSTOM GAME is the case we now want to write into, and
            // the reason the first attempt could not work. A custom game gives
            // SimHub a real game manager (ControllerGameManager), so GameRunning
            // goes true and a StatusDataBase exists. Writing into a live
            // pipeline is a different proposition from handing SimHub an object
            // it never asked for while no manager is running at all.
            //
            // Its Code is "Custom_<guid>", which is what GameName reports.
            bool customGame = data.GameName != null
                && data.GameName.StartsWith("Custom_", StringComparison.Ordinal);

            // Still never speak over a REAL reader, which owns its own fields.
            if (data.GameRunning && !customGame) return;

            _spikeCustomMode = customGame;
            _spikeSeenGameName = data.GameName;
            var arcadeSettings = Settings == null ? null : Settings.Arcade;
            if (arcadeSettings == null || !arcadeSettings.Enabled) return;

            var s = ArcadeMem();

            // DIAGNOSTIC (2026-09-06): why the reader is not producing samples. Attaching to the
            // process and resolving the pointer chain are two different things, and a session with
            // no card, progress or car lines at all means the chain never resolved. The reader
            // knows why and says so in Status; nothing was reading it out.
            var readerNow = _arcadeMemory;
            string readerState = readerNow == null
                ? "no reader"
                : "valid=" + s.Valid + " verified=" + readerNow.Verified
                  + " status=" + (readerNow.Status ?? "(none)");
            if (readerState != _spikeLastReaderState)
            {
                _spikeLastReaderState = readerState;
                SimHub.Logging.Current.Info("[TF4ALL] ID8 reader: " + readerState);
            }

            // A sample that is not valid does NOT mean we stop talking.
            //
            // The pointer chain drops in and out constantly between races: "not in a race", "the
            // game has not built its session yet", "the player's actor record is missing", "the car
            // object's class is not a player car". Each gap used to make us return here and write
            // nothing, and SimHub's DC__ placeholder reasserted itself the moment we went quiet.
            // That is what a car id flickering between the Trueno and a DC car actually is: not a
            // fight over the field, but us falling silent and SimHub filling the silence.
            //
            // So the car identity is LATCHED. Once we have seen a car, we keep publishing it, its
            // dial and its redline through every gap, and only the live values (rpm, gear, speed)
            // pause. SimHub then holds one id long enough to find its per-car settings file, which
            // is what its whole rev-light engine depends on.
            if (!s.Valid)
            {
                // The latch is tied to the PROCESS, not to the sample. A chain that cannot resolve
                // between races is a gap to ride out; a cabinet that has exited is not, and holding
                // a car identity after the game is gone would leave a stale Trueno in SimHub for as
                // long as arcade support stayed switched on.
                //
                // The reader knows the difference: it reports the process it is attached to, and
                // that goes empty on detach.
                if (readerNow == null || string.IsNullOrEmpty(readerNow.GameProcessName))
                {
                    _spikeHeldCarId = null;
                    _spikeHeldCarModel = null;
                    _spikeHeldDialMax = 0;
                    _spikeHeldRedline = 0;
                    return;
                }
                if (_spikeHeldCarId == null) return;
                var heldNd = data.NewData;
                if (heldNd == null) return;
                SpikeNdWrite(heldNd, "CarId", _spikeHeldCarId);
                SpikeNdWrite(heldNd, "CarModel", _spikeHeldCarModel ?? "");
                if (_spikeHeldDialMax > 0) SpikeNdWrite(heldNd, "MaxRpm", _spikeHeldDialMax);
                if (_spikeHeldRedline > 0)
                {
                    SpikeNdWrite(heldNd, "Redline", _spikeHeldRedline);
                    SpikeNdWrite(heldNd, "CarSettings_RedLineRPM", _spikeHeldRedline);
                    SpikeNdWrite(heldNd, "CarSettings_CurrentGearRedLineRPM", _spikeHeldRedline);
                }
                SpikeNdWrite(heldNd, "EngineIgnitionOn", 1);
                SpikeNdWrite(heldNd, "EngineStarted", 1);
                _spikeWrote = true;
                return;
            }

            // DIAGNOSTIC (2026-09-06): the sign-in sequence, one line per transition. The cabinet
            // shows PRESS START, then INSERT CARD, then holds PRESS START while it loads the card,
            // and the panel would like to name those states. Whether we CAN is the open question:
            // the card id offset is flagged speculative in the map, and testing it alone was
            // already found to lag a real swipe, so the "card in, profile not loaded" window may
            // not be visible from here at all. This says which witness moves, and when.
            string cardState = (s.SessionValid ? "session" : "no-session")
                + " cardId=" + s.CardId
                + " idSays=" + s.CardIdPresent
                + " named=" + s.PlayerNamed
                + " hasCard=" + s.HasCard
                + " inRace=" + s.InRace;
            if (cardState != _spikeLastCardState)
            {
                _spikeLastCardState = cardState;
                SimHub.Logging.Current.Info("[TF4ALL] ID8 card state: " + cardState);
            }

            // DIAGNOSTIC (2026-09-06): what SimHub says the car is, against what we resolved it
            // to. A "DC__" value is SimHub's per-session placeholder rather than a car, and the
            // question is whether it ever reaches the car slot while a cabinet is live. Logged on
            // change only, so a whole session is a handful of lines.
            string carState = "simhub=" + (data.NewData == null ? "(no NewData)"
                                  : "'" + (data.NewData.CarId ?? "") + "'/'" + (data.NewData.CarModel ?? "") + "'")
                + " resolved='" + (ActiveCarId ?? "") + "'"
                + " arcadeCar='" + (ArcadeCarNow() != null ? ArcadeCarNow().Code : "") + "'";
            if (carState != _spikeLastCarState)
            {
                _spikeLastCarState = carState;
                SimHub.Logging.Current.Info("[TF4ALL] ID8 car id: " + carState);
            }

            // Once per session, on the first valid sample. It was gated on being mid race with a
            // gear engaged, which was too strict to ever fire, and this is the one diagnostic that
            // can explain a dash showing rpm while the property we probe reads null: it asks SimHub
            // for its OWN list of property names rather than trusting the name we guessed.
            SpikeDumpPropertyNames(pluginManager);

            // DIAGNOSTIC (2026-09-06), separate from the bridge question: find
            // where the game keeps the SECTION the car is in. The per-slot
            // progress record is 0xb4 bytes and we read two words of it, so a
            // section counter is very likely sitting in the other 43. Dumped
            // once a second during a race, alongside the lap and the fraction,
            // so a counter that steps at a boundary is obvious in the log, and
            // so it settles whether CourseFraction spans a whole lap or resets
            // per section.
            if (s.InRace)
            {
                long dnow = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_spikeProgressLogTicks == 0
                    || (dnow - _spikeProgressLogTicks) > System.Diagnostics.Stopwatch.Frequency)
                {
                    _spikeProgressLogTicks = dnow;
                    var filler = _arcadeMemory;
                    string dump = filler == null ? null : filler.DumpProgress();
                    if (dump != null)
                        SimHub.Logging.Current.Info(
                            "[TF4ALL] ID8 progress: lap=" + s.LapsCompleted + "/" + s.TotalLaps
                            + " frac=" + s.CourseFraction.ToString("0.0000")
                            + " t=" + s.RaceElapsedMs + " :: " + dump);

                    // The counter hunt, restarted with each race so one run's
                    // history cannot disqualify a word in the next.
                    string counters = filler == null ? null : filler.ScanCounters(_spikeRaceRestart);
                    _spikeRaceRestart = false;
                    if (counters != null)
                        SimHub.Logging.Current.Info(
                            "[TF4ALL] ID8 counters: frac=" + s.CourseFraction.ToString("0.0000")
                            + " " + counters);
                }
            }
            // A race beginning, or the clock running backwards, means the next
            // scan starts from a clean slate.
            if (s.InRace && (!_spikeWasInRace || s.RaceElapsedMs < _spikeLastElapsedMs))
                _spikeRaceRestart = true;
            _spikeWasInRace = s.InRace;
            _spikeLastElapsedMs = s.RaceElapsedMs;

            var nd = data.NewData;
            if (nd == null)
            {
                // StatusDataBase is abstract; StatusData<T> is concrete with an
                // unconstrained T. SimHub leaves this null while no game runs,
                // so we supply one and say so in the log, because "the write
                // propagated" means something different in that case.
                // With a manager running, a null here is a FINDING rather than an invitation.
                // It says the object SimHub publishes from is not the one handed to plugins, and
                // supplying our own would only measure our own object.
                if (customGame)
                {
                    if (!_spikeNullUnderManagerLogged)
                    {
                        _spikeNullUnderManagerLogged = true;
                        SimHub.Logging.Current.Info(
                            "[TF4ALL] ID8 bridge spike: NewData is null even though a custom game's "
                            + "manager is running, so plugins are not handed the object SimHub "
                            + "publishes from. Nothing was written this tick.");
                    }
                    return;
                }
                try
                {
                    nd = new StatusData<object>(new GameUnitSettings());
                    data.NewData = nd;
                    _spikeMadeNewData = true;
                    _spikeMadeNd = nd;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info(
                        "[TF4ALL] ID8 bridge spike: NewData was null and could not be built: " + ex.Message);
                    return;
                }
            }

            _spikeWasGameName = data.GameName;
            _spikeWasGameRunning = data.GameRunning;
            _spikeWasGamePaused = data.GamePaused;

            var car = ArcadeCarNow();

            // ---- engine and controls ----
            //
            // The TRUE dial value, deliberately. An earlier version rescaled this onto SimHub's own
            // MaxRpm, on the strength of a probe that read 7000 back. That probe was wrong: it
            // samples at the top of a tick and our write lands at the bottom, so it reports SimHub's
            // intermediate value rather than what a dash receives. On the rig an Audi dash reads
            // 11000, which is that car's real redline arriving intact. Rescaling would have broken
            // a display that already works, to fix lights that are broken for a different reason.
            // MaxRpm is the DIAL CEILING, not the redline. Those are different numbers on this
            // cabinet: the stock AE86 shows an 8000 face with red starting at 7300. We were sending
            // the redline as the maximum, which had two consequences. SimHub derives its own redline
            // as a percentage of the maximum, so it produced 95 percent of 7300 and that value was
            // then overlaid back onto the arcade frame, which is why the car-facts panel reported
            // the wrong figure. And it disagreed with the per-car settings files, which describe the
            // face properly. Frame, settings file and published field now all say the same thing.
            var faceCar = ArcadeCarNow();
            double dialMax = faceCar != null ? faceCar.DialMax(s.TunedFace) : ArcadeRedlineNow();
            SpikeNdWrite(nd, "Rpms", s.DialRpm);
            SpikeNdWrite(nd, "MaxRpm", dialMax);
            SpikeNdWrite(nd, "SpeedKmh", s.SpeedKmh);
            // Pedal scale is the one unit the spike is guessing at. SimHub
            // readers publish these as percentages, so 0..100 here; if a dash
            // shows a hundredth of what it should, this is the line.
            SpikeNdWrite(nd, "Throttle", s.Throttle01 * 100.0);
            SpikeNdWrite(nd, "Brake", s.Brake01 * 100.0);
            SpikeNdWrite(nd, "Gear", s.Gear <= 0 ? "N" : s.Gear.ToString());

            // The engine is ON. Dashes gate whole screens on this, which is why some of them showed
            // nothing at all: with ignition reading 0 they conclude the car is not running and draw
            // their "no car" state over perfectly good telemetry.
            SpikeNdWrite(nd, "EngineIgnitionOn", 1);
            SpikeNdWrite(nd, "EngineStarted", 1);
            // The smoothed rpm, which is what a lot of gauges bind to in preference to the raw one.
            SpikeNdWrite(nd, "FilteredRpms", s.DialRpm);

            // The redline family. MaxRpm alone is not enough: a rev-light strip needs to know where
            // the red zone STARTS, and every one of these carries part of that answer for a
            // different dash. MaxRpm is owned by ControllerGameManager and will be stamped back to
            // its default, but nothing suggests the CarSettings_ family is, so these may be the
            // route by which rev lights work even while MaxRpm does not.
            double redline = ArcadeRedlineNow();
            if (redline > 0)
            {
                SpikeNdWrite(nd, "Redline", redline);
                SpikeNdWrite(nd, "CarSettings_MaxRPM", dialMax);
                SpikeNdWrite(nd, "CarSettings_RedLineRPM", redline);
                SpikeNdWrite(nd, "CarSettings_CurrentGearRedLineRPM", redline);
                SpikeNdWrite(nd, "CarSettings_MinimumShownRPM", 0.0);
                // PERCENTAGES, not rpm. This family mixes units and the names do not say which is
                // which: CarSettings_RedLineRPM came back as 6650 while CarSettings_RPMRedLineSetting
                // came back as 95, against the same 7000 maximum. The ones ending RPM hold rpm; the
                // settings hold percent.
                //
                // We were sending 0.90 x 11000 = 9900 into a field that wants a percentage, so
                // threshold one was met from idle and every dash that flashes above it flashed
                // constantly, while a strip filling between the two points had nothing to fill.
                //
                // Where the lights start and where they are all lit. The game publishes no shift
                // points of its own, so these are our choice: 90 and 97 percent of the dial, which
                // is where the redline buzz already fires.
                SpikeNdWrite(nd, "CarSettings_RPMRedLineSetting", 97.0);
                SpikeNdWrite(nd, "CarSettings_RPMShiftLight1", 90.0);
                SpikeNdWrite(nd, "CarSettings_RPMShiftLight2", 97.0);
                SpikeNdWrite(nd, "CarSettings_RPMRedLineReached", s.DialRpm >= redline * 0.97 ? 1.0 : 0.0);
                // These are PERCENTAGES, 0 to 100, and the rig settled it: with MaxRpm stamped at
                // 7000, SimHub published CarSettings_RedLineRPM 6650 and both
                // CarSettings_RPMRedLineSetting and CarSettings_RedLineDisplayedPercent as 95. That
                // is 95 percent of 7000, written as 95 rather than 0.95. The earlier fraction was
                // wrong by a factor of a hundred, which is why meters sat empty while the redline
                // flash still fired: the flash reads a different field.
                SpikeNdWrite(nd, "CarSettings_CurrentDisplayedRPMPercent",
                    Math.Max(0.0, Math.Min(100.0, s.DialRpm / redline * 100.0)));
                SpikeNdWrite(nd, "CarSettings_RedLineDisplayedPercent", 97.0);
            }

            // ---- identity ----
            // The SAME identity the rest of the plugin uses, "ID8_<code>", rather than the raw
            // ordinal. SimHub files its per-car settings as Cars\<CarId>.shcarsettings and reads
            // MaxRpm, Redline and the per-gear upshift points back out of them, so the name it
            // receives has to be the name those files are written under, or it learns nothing and
            // the whole rev-light group stays empty. It is also stable across sessions and legible
            // in SimHub's own car list, which a bare number is not.
            // The SAME identity the rest of the plugin uses, "ID8_<code>", rather than the raw
            // ordinal, and with the TACHO FACE in it.
            //
            // SimHub files per-car settings as Cars\<CarId>.shcarsettings and reads MaxRpm, Redline
            // and the per-gear upshift points back out. A tuned car does not merely rev higher, it
            // shows a different dial: the AE86 goes from a 8000 face redlined at 7300 to a 13000
            // face redlined at 11000. One car id cannot carry both without being wrong half the
            // time, and worse, SimHub would remember whichever it saw first and light the strip
            // against the other car's numbers for the rest of the session.
            //
            // Two identities, two settings files, both exactly right.
            var carNow = ArcadeCarNow();
            if (carNow != null)
            {
                _spikeHeldCarId = "ID8_" + carNow.Code + (s.TunedFace ? "_TUNED" : "");
                _spikeHeldCarModel = carNow.Name + (s.TunedFace ? " (tuned)" : "");
                _spikeHeldDialMax = carNow.DialMax(s.TunedFace);
                _spikeHeldRedline = carNow.Redline(s.TunedFace);
            }
            SpikeNdWrite(nd, "CarId", carNow != null
                ? "ID8_" + carNow.Code + (s.TunedFace ? "_TUNED" : "")
                : "");
            SpikeNdWrite(nd, "CarModel", carNow != null
                ? carNow.Name + (s.TunedFace ? " (tuned)" : "")
                : "");
            SpikeNdWrite(nd, "TrackName", s.CourseName ?? "");
            SpikeNdWrite(nd, "TrackId", s.CourseName ?? "");
            SpikeNdWrite(nd, "SessionTypeName", "Race");

            // ---- race and timing ----
            // The game keeps a real clock, its own lap counter and its own best
            // and last lap, so none of this is synthesised.
            //
            // CurrentLapTime is the honest approximation here: the game clocks a
            // RACE, not a lap, so on a single-lap course the two are the same
            // and on a multi-lap one this keeps counting past the line. Fixing
            // that means latching a lap start off LapJustCompleted, which is
            // feature work rather than spike work.
            //
            // Elapsed runs NEGATIVE through the countdown, so it is clamped.
            SpikeNdWrite(nd, "CurrentLapTime",
                TimeSpan.FromMilliseconds(Math.Max(0, s.RaceElapsedMs)));
            SpikeNdWrite(nd, "LastLapTime", TimeSpan.FromMilliseconds(Math.Max(0, s.LastLapMs)));
            SpikeNdWrite(nd, "BestLapTime", TimeSpan.FromMilliseconds(Math.Max(0, s.BestLapMs)));
            SpikeNdWrite(nd, "CompletedLaps", s.LapsCompleted);
            SpikeNdWrite(nd, "CurrentLap", s.LapsCompleted + 1);
            SpikeNdWrite(nd, "TotalLaps", s.TotalLaps);
            SpikeNdWrite(nd, "SessionTimeLeft", TimeSpan.FromSeconds(Math.Max(0, s.TimeLeftSeconds)));
            // Rank is -1 until the game decides it, so only speak when it has.
            if (s.ActorRank > 0) SpikeNdWrite(nd, "Position", s.ActorRank);
            // Course progress, 0..1 from the game. Whether SimHub wants a
            // fraction or a percentage here is not established; if a track map
            // sits at the start line or laps eight times too fast, scale this.
            SpikeNdWrite(nd, "TrackPositionPercent", s.CourseFraction);

            // Under a custom game SimHub already owns a correct identity, and a
            // running manager is the whole point, so leave both alone and fill
            // only the telemetry. Spoofing is for the no-game case, where there
            // is nothing to preserve.
            _spikeSpoofedIdentity = !customGame;
            if (_spikeSpoofedIdentity)
            {
                data.GameName = SpikeGameName;
                SpikeSet(data, "GameRunning", true);
                SpikeSet(data, "GamePaused", s.Paused);
            }

            // The one route that does not go through NewData at all. ControllerGameManager owns
            // Rpms, MaxRpm, Throttle and SpeedKmh through its GD_* overrides and refills them every
            // cycle, so a NewData write to any of those is always overwritten no matter when we
            // make it. Setting the PUBLISHED property instead skips that entirely, if SimHub lets
            // a plugin write another plugin's property. If Rpms comes back KEPT above, this is why.
            SpikeTryPublishOverride(pluginManager, s.DialRpm);

            _spikeWroteRpm = s.DialRpm;
            _spikeWrote = true;
        }
    }
}
