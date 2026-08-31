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
    }

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
        public const uint   Version = 1;
        public const int    Size    = 152;

        public const int OffMagic = 0, OffVersion = 4, OffSeq = 8,
                         OffFfbValue = 12, OffFfbPure = 16, OffFfbFinal = 20, OffFfbMultiplier = 24,
                         OffSteerTorque = 28, OffSteerInput = 32, OffDt = 36;

        /// <summary>Decodes one copy of the block. WriterBusy means the seqlock
        /// was odd (the script was mid-update when the copy was taken): the
        /// caller re-reads. Header failures mean the map is not ours (or a
        /// newer script version) and re-reading will not help.</summary>
        public static AcCspBridgeParse TryParse(byte[] buf, out AcCspBridgeSample s)
        {
            s = default(AcCspBridgeSample);
            if (buf == null || buf.Length < Size) return AcCspBridgeParse.TooShort;
            if (BitConverter.ToUInt32(buf, OffMagic)   != Magic)   return AcCspBridgeParse.BadMagic;
            if (BitConverter.ToUInt32(buf, OffVersion) != Version) return AcCspBridgeParse.BadVersion;
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

        /// <summary>One control write. suppress = tell the script to return 0
        /// to the wheel. Call it steadily (each poll tick) so the script's
        /// liveness check keeps seeing fresh writes.</summary>
        public void Write(bool suppress)
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
                _view.Write(OffFlags, suppress ? 1u : 0u);
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
