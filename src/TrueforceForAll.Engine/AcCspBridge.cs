using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace TrueforceForAll.Core
{
    /// <summary>One snapshot of the TF4ALL CSP bridge, the shared-memory
    /// block written by gamemods/AssettoCorsaCsp/tf4all/ffb.lua at AC's
    /// physics rate. FfbPure is the sim's PRE-gain, pre-post-processing
    /// force (normalized, +-1 = full device force), the one value that still
    /// carries the car when the in-game gain sits at 0. FfbValue is the
    /// post-chain scalar CSP handed the script (what the wheel would get).</summary>
    public struct AcCspBridgeSample
    {
        public uint  Seq;
        public float FfbValue;
        public float FfbPure;
        public float FfbFinal;
        public float FfbMultiplier;
        public float SteerTorque;   // Nm at the steering column, per CSP
        public float SteerInput;
        public float Dt;
        public float FfbDamper;        // damper coefficient CSP handed the script (0 from a v1 script)
        public float SteerInputSpeed;  // steering input speed at physics rate (0 from a v1 script)
        /// <summary>Whether CSP's g27_lights module (the thing that drives the
        /// wheel's rev lights in AC) is active. False from a pre-v3 script,
        /// which is indistinguishable from "not active": read it with
        /// AcLedsKnown.</summary>
        public bool AcLedsModuleActive;
        /// <summary>That module's MODE, i.e. whether and how it writes the
        /// bar. Unknown from a pre-v3 script or a CSP too old for the query.</summary>
        public AcLedMode AcLedsMode;
        /// <summary>True once a v3 script has reported the LED state at all.</summary>
        public bool AcLedsKnown;

        /// <summary>How many shift lights the CAR itself defines (v4+), or 0
        /// when it models none. The RPMs themselves are on the reader, not
        /// here: they are constant for a session and copying twelve floats into
        /// every physics-rate sample to carry a value that never changes is
        /// waste on the hot path.</summary>
        public int   AcLedCount;
        /// <summary>RPM at which the car's own shift lights start flashing, 0
        /// if it does not say.</summary>
        public float AcLedBlinkRpm;
        /// <summary>How fast they flash, in Hz. 0 if the car does not say.</summary>
        public float AcLedBlinkHz;
    }

    /// <summary>CSP g27_lights MODE values. Disabled is the one that stops AC
    /// writing the wheel's rev lights, which CSP documents as the way to hand
    /// them to an external tool.</summary>
    public enum AcLedMode { Unknown = 0, DiBased = 1, Percentage = 2, AiBased = 3, Disabled = 4 }

    public enum AcCspBridgeParse { Ok, TooShort, BadMagic, BadVersion, WriterBusy }

    /// <summary>Which bridge field the FFB latch injects. Pure and Torque are
    /// the only ones that carry force with the in-game gain at 0 (Final and
    /// Value are post-gain and read 0 there); they exist for A/B comparison
    /// with the gain up. Pure is normalized to the car's reference torque;
    /// Torque is the raw column torque in Nm, scaled by the wheel's max Nm so
    /// a light car feels light and a heavy one heavy.</summary>
    public enum AcCspField { Pure, Final, Value, Torque }

    /// <summary>Wire layout of the bridge block (little-endian, packed; the
    /// per-wheel float[4] arrays after Dt are not decoded here).</summary>
    public static class AcCspBridgeLayout
    {
        public const string MapName = "TF4All.ACBridge.v1";
        public const uint   Magic   = 0x54463441;   // "TF4A"
        // v2 appends ffbDamper + steerInputSpeed at the tail; every v1
        // offset is unchanged, so the reader accepts both versions and reads
        // the tail only from a v2 writer (a v1 map's tail bytes read as 0).
        // v3 appends acLeds (who owns the wheel's rev lights) after v2's pair;
        // v4 appends the car's own shift-light thresholds after that. Same rule
        // throughout: every earlier offset is unchanged and the reader takes
        // the tail only from a writer new enough to have written it.
        public const uint   Version    = 5;
        public const uint   VersionMin = 1;
        public const int    Size       = 368;
        /// <summary>Room the wire reserves for per-LED switch-on RPMs. Cars
        /// run to about ten; the wheel's own bar is ten steps on a G PRO.</summary>
        public const int    AcLedMax   = 12;

        public const int OffMagic = 0, OffVersion = 4, OffSeq = 8,
                         OffFfbValue = 12, OffFfbPure = 16, OffFfbFinal = 20, OffFfbMultiplier = 24,
                         OffSteerTorque = 28, OffSteerInput = 32, OffDt = 36,
                         OffFfbDamper = 152, OffSteerInputSpeed = 156, OffAcLeds = 160,
                         OffAcLedCount = 164, OffAcLedRpm = 168,
                         OffAcLedBlinkRpm = 216, OffAcLedBlinkHz = 220,
                         OffAcLedRgb = 224;

        /// <summary>One AC dash LED's EMISSIVE turned into an sRGB triple.
        ///
        /// AC stores these as emissive INTENSITIES, not 0-255 colours: values
        /// above 255 are ordinary in these files. So the hue is what carries
        /// meaning and the magnitude does not, and each LED is scaled so its
        /// brightest channel reaches full. That keeps a car's amber amber and
        /// its red red, and leaves overall brightness where it belongs, with
        /// the user's own wheel setting.
        ///
        /// An all-zero triple means the car gave no colour; the caller decides
        /// what to do about that rather than being handed black.</summary>
        public static bool TryNormalizeEmissive(float r, float g, float b,
                                                out byte or_, out byte og, out byte ob)
        {
            or_ = og = ob = 0;
            if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b)) return false;
            if (r < 0f) r = 0f;
            if (g < 0f) g = 0f;
            if (b < 0f) b = 0f;
            float max = r > g ? (r > b ? r : b) : (g > b ? g : b);
            if (max <= 0f) return false;
            float k = 255f / max;
            or_ = Scale(r * k);
            og  = Scale(g * k);
            ob  = Scale(b * k);
            return true;
        }

        private static byte Scale(float v)
            => v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

        /// <summary>Decodes one copy of the block. WriterBusy means the seqlock
        /// was odd (the script was mid-update when the copy was taken): the
        /// caller re-reads. Header failures mean the map is not ours (or a
        /// newer script version) and re-reading will not help.</summary>
        public static AcCspBridgeParse TryParse(byte[] buf, out AcCspBridgeSample s)
        {
            s = default(AcCspBridgeSample);
            if (buf == null || buf.Length < Size) return AcCspBridgeParse.TooShort;
            if (BitConverter.ToUInt32(buf, OffMagic)   != Magic)   return AcCspBridgeParse.BadMagic;
            uint ver = BitConverter.ToUInt32(buf, OffVersion);
            if (ver < VersionMin || ver > Version) return AcCspBridgeParse.BadVersion;
            uint seq = BitConverter.ToUInt32(buf, OffSeq);
            if ((seq & 1) != 0) return AcCspBridgeParse.WriterBusy;
            s.Seq           = seq;
            s.FfbValue      = BitConverter.ToSingle(buf, OffFfbValue);
            s.FfbPure       = BitConverter.ToSingle(buf, OffFfbPure);
            s.FfbFinal      = BitConverter.ToSingle(buf, OffFfbFinal);
            s.FfbMultiplier = BitConverter.ToSingle(buf, OffFfbMultiplier);
            s.SteerTorque   = BitConverter.ToSingle(buf, OffSteerTorque);
            s.SteerInput    = BitConverter.ToSingle(buf, OffSteerInput);
            s.Dt            = BitConverter.ToSingle(buf, OffDt);
            if (ver >= 2)
            {
                s.FfbDamper       = BitConverter.ToSingle(buf, OffFfbDamper);
                s.SteerInputSpeed = BitConverter.ToSingle(buf, OffSteerInputSpeed);
            }
            if (ver >= 3)
            {
                uint leds = BitConverter.ToUInt32(buf, OffAcLeds);
                s.AcLedsModuleActive = (leds & 0xFF) != 0;
                byte mode = (byte)((leds >> 8) & 0xFF);
                s.AcLedsMode  = mode <= (byte)AcLedMode.Disabled ? (AcLedMode)mode : AcLedMode.Unknown;
                s.AcLedsKnown = true;
            }
            if (ver >= 4)
            {
                int cnt = (int)BitConverter.ToUInt32(buf, OffAcLedCount);
                s.AcLedCount    = cnt < 0 ? 0 : cnt > AcLedMax ? AcLedMax : cnt;
                s.AcLedBlinkRpm = BitConverter.ToSingle(buf, OffAcLedBlinkRpm);
                s.AcLedBlinkHz  = BitConverter.ToSingle(buf, OffAcLedBlinkHz);
            }
            return AcCspBridgeParse.Ok;
        }
    }

    /// <summary>Plugin -> script control channel. The plugin writes it; the
    /// TF4ALL CSP script reads it and, while the "suppress output" flag is set
    /// and the plugin's writes are fresh, returns 0 to the wheel so the game
    /// stops driving it (the plugin drives it from the exported ffbValue over
    /// ep3 instead). The freshness check lives in the script: when SimHub
    /// closes the writes stop, the flag goes stale, and the script reverts to
    /// a pure pass-through, so the wheel is never left silent. seq doubles as
    /// the seqlock and the liveness heartbeat (it advances by two per write).</summary>
    public sealed class AcCspControlWriter : IDisposable
    {
        public const string MapName = "TF4All.ACBridge.Control.v1";
        public const uint   Magic   = 0x54464331;   // "TFC1"
        public const uint   Version = 1;
        public const int    Size    = 16;
        public const int OffMagic = 0, OffVersion = 4, OffSeq = 8, OffFlags = 12;

        private MemoryMappedFile         _mmf;
        private MemoryMappedViewAccessor _view;
        private uint _writes;
        private bool _headerWritten;

        public bool IsOpen => _view != null;

        private bool EnsureOpen()
        {
            if (_view != null) return true;
            try
            {
                // Same name/namespace convention the bridge proves works both
                // ways between CSP and .NET in one session (plain name = the
                // session-local namespace). CreateOrOpen so a leftover section
                // from a crashed run is reused rather than throwing.
                _mmf  = MemoryMappedFile.CreateOrOpen(MapName, Size);
                _view = _mmf.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
                return true;
            }
            catch { _mmf = null; _view = null; return false; }
        }

        /// <summary>One control write. suppress (bit 0) = tell the script to
        /// zero the FORCE to the wheel; zeroDamper (bit 1) = zero the DAMPER
        /// too, the CSPFFB DAMP A/B switch. Call it steadily (each poll tick)
        /// so the script's liveness check keeps seeing fresh writes.</summary>
        public void Write(bool suppress, bool zeroDamper = false, int damperScale255 = -1)
        {
            if (!EnsureOpen()) return;
            try
            {
                if (!_headerWritten)
                {
                    _view.Write(OffMagic, Magic);
                    _view.Write(OffVersion, Version);
                    _headerWritten = true;
                }
                _writes++;
                uint odd  = _writes * 2u - 1u;
                uint even = _writes * 2u;
                _view.Write(OffSeq, odd);          // odd: writing
                uint flags = (suppress ? 1u : 0u) | (zeroDamper ? 2u : 0u);
                // bit 2 arms a damper SCALE override, carried in bits 8..15
                // (0..255 = 0..1). The DAMPTEST wiggle rides this; a script
                // without the feature ignores the upper bits.
                if (damperScale255 >= 0)
                    flags |= 4u | ((uint)Math.Min(damperScale255, 255) << 8);
                _view.Write(OffFlags, flags);
                _view.Write(OffSeq, even);         // even: stable, and advanced = alive
            }
            catch { Close(); }
        }

        public void Close()
        {
            try { _view?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            _view = null; _mmf = null; _headerWritten = false;
        }

        public void Dispose() => Close();
    }

    /// <summary>Opens the bridge map on demand (retrying every couple of
    /// seconds while AC or the script is not running) and hands back a
    /// sample each time the writer's seq has advanced. Single-threaded by
    /// design: the owner polls it from one thread and closes it from the
    /// same thread. Reads are torn-safe through the seqlock: a copy whose
    /// seq was odd, or whose seq moved during the copy, is discarded.</summary>
    public sealed class AcCspBridgeReader : IDisposable
    {
        private const int  MaxReadAttempts = 4;
        private static readonly long OpenRetryTicks = Stopwatch.Frequency * 2;

        private readonly string _mapName;
        private readonly byte[] _buf = new byte[AcCspBridgeLayout.Size];
        private MemoryMappedFile         _mmf;
        private MemoryMappedViewAccessor _view;
        private long _nextOpenAttemptTicks;
        private uint _lastSeq;
        private bool _haveSeq;

        public AcCspBridgeReader(string mapName = AcCspBridgeLayout.MapName) { _mapName = mapName; }

        /// <summary>The active car's own shift-light switch-on RPMs, ascending,
        /// or null when the car models none (or the script predates v4).
        ///
        /// Lives here rather than on the sample because it is constant for a
        /// session: the array is rebuilt only when the values actually change,
        /// so the physics-rate read path allocates nothing.</summary>
        public float[] AcLedStepRpms { get; private set; }

        /// <summary>The car's own shift-light COLOURS, three bytes per LED in
        /// the same order as AcLedStepRpms, or null when the car gives none.
        /// Rebuilt alongside the RPMs and on the same terms: constant for a
        /// session, so nothing allocates per read.</summary>
        public byte[] AcLedStepColors { get; private set; }

        private void RefreshLedSteps(int count)
        {
            if (count <= 0)
            {
                AcLedStepRpms = null;
                return;
            }
            var cur = AcLedStepRpms;
            bool same = cur != null && cur.Length == count;
            if (same)
            {
                for (int i = 0; i < count; i++)
                    if (cur[i] != BitConverter.ToSingle(_buf, AcCspBridgeLayout.OffAcLedRpm + i * 4))
                    { same = false; break; }
            }
            if (same) return;
            var next = new float[count];
            for (int i = 0; i < count; i++)
                next[i] = BitConverter.ToSingle(_buf, AcCspBridgeLayout.OffAcLedRpm + i * 4);
            AcLedStepRpms = next;
            AcLedStepColors = ReadLedColors(count);
        }

        /// <summary>Normalised colours for the LEDs, or null when the car gave
        /// none. All-or-nothing: a car that colours only some of its LEDs would
        /// otherwise paint the rest black, which reads as a broken strip rather
        /// than as missing data.</summary>
        private byte[] ReadLedColors(int count)
        {
            var rgb = new byte[count * 3];
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                int o = AcCspBridgeLayout.OffAcLedRgb + i * 12;
                if (!AcCspBridgeLayout.TryNormalizeEmissive(
                        BitConverter.ToSingle(_buf, o),
                        BitConverter.ToSingle(_buf, o + 4),
                        BitConverter.ToSingle(_buf, o + 8),
                        out byte r, out byte g, out byte b))
                    return null;
                rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
                any = true;
            }
            return any ? rgb : null;
        }

        public bool IsOpen => _view != null;
        /// <summary>True when the open map fails the magic/version check.</summary>
        public bool BadHeader { get; private set; }
        public int  OpenAttempts { get; private set; }

        /// <summary>Returns true with a fresh sample when the writer's seq has
        /// advanced since the last accepted sample. False while the map is
        /// missing, the header is wrong, the writer is mid-update on every
        /// attempt, or nothing new has been written. The last accepted seq
        /// survives Close(), so reopening the same frozen map does not
        /// re-deliver its stale contents as if they were new.</summary>
        public bool TryReadNew(long nowTicks, out AcCspBridgeSample s)
        {
            s = default(AcCspBridgeSample);
            if (_view == null)
            {
                if (nowTicks - _nextOpenAttemptTicks < 0 && OpenAttempts > 0) return false;
                _nextOpenAttemptTicks = nowTicks + OpenRetryTicks;
                if (!TryOpen()) return false;
            }
            try
            {
                for (int attempt = 0; attempt < MaxReadAttempts; attempt++)
                {
                    _view.ReadArray(0, _buf, 0, _buf.Length);
                    var r = AcCspBridgeLayout.TryParse(_buf, out s);
                    if (r == AcCspBridgeParse.WriterBusy) continue;
                    if (r != AcCspBridgeParse.Ok) { BadHeader = true; return false; }
                    // The copy is not atomic against the writer; a seq that
                    // moved while we copied means the fields may be mixed.
                    if (_view.ReadUInt32(AcCspBridgeLayout.OffSeq) != s.Seq) continue;
                    BadHeader = false;
                    RefreshLedSteps(s.AcLedCount);
                    if (_haveSeq && s.Seq == _lastSeq) return false;
                    _haveSeq = true;
                    _lastSeq = s.Seq;
                    return true;
                }
                return false;
            }
            catch
            {
                Close();
                return false;
            }
        }

        private bool TryOpen()
        {
            OpenAttempts++;
            // A plain name lands in the session-local namespace, which is
            // where CSP's writeMemoryMappedFile puts it; the prefixed forms
            // are cheap insurance against a differently-namespaced writer.
            foreach (var name in new[] { _mapName, "Local\\" + _mapName, "Global\\" + _mapName })
            {
                try
                {
                    var mmf  = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                    var view = mmf.CreateViewAccessor(0, AcCspBridgeLayout.Size, MemoryMappedFileAccess.Read);
                    _mmf = mmf; _view = view;
                    return true;
                }
                catch { }
            }
            return false;
        }

        public void Close()
        {
            try { _view?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            _view = null; _mmf = null;
            BadHeader = false;
        }

        public void Dispose() => Close();
    }
}
