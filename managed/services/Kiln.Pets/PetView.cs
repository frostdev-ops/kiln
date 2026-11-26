// SPDX-License-Identifier: MIT
// The owner's view of their pets (19d §4.8, §5.4; package 7): what the server sends each owner on the channel kiln:pets v1
// (full bounded binary snapshots, never deltas or JSON graphs, never to anyone else), the client's cache of it, and the
// geometry of the built-in bar. A mod may hide the bar and draw its own from PetsClient.Snapshot; the HUD never changes
// gameplay.
using System.Buffers.Binary;
using System.Text;

namespace Kiln;

/// <summary>A pet body as the network names it: informational, never an entity to resolve on a client. Since 0.2.0-dev.32.</summary>
/// <param name="NetworkEntityId">The server's entity id of the body.</param>
/// <param name="BodyGeneration">The body's generation.</param>
public sealed record PetBodyViewKey(uint NetworkEntityId, ulong BodyGeneration);

/// <summary>One pet in its owner's view. Since 0.2.0-dev.32.</summary>
public sealed record PetViewEntry
{
    /// <summary>The pet.</summary>
    public PetId Id { get; init; }
    /// <summary>Its definition's id.</summary>
    public string Definition { get; init; } = "";
    /// <summary>Its name.</summary>
    public string Label { get; init; } = "";
    /// <summary>Alive or down.</summary>
    public PetLife Life { get; init; }
    /// <summary>Where it is.</summary>
    public PetPresence Presence { get; init; }
    /// <summary>What is happening to it.</summary>
    public PetTransition Transition { get; init; }
    /// <summary>Its body; null while it has none.</summary>
    public PetBodyViewKey? Body { get; init; }
    /// <summary>Its recovery anchor while one is shown.</summary>
    public WorldPosition? RecoveryAnchor { get; init; }
    /// <summary>Its order.</summary>
    public PetOrder RequestedOrder { get; init; }
    /// <summary>Its level.</summary>
    public int Level { get; init; }
    /// <summary>Its health.</summary>
    public int Current { get; init; }
    /// <summary>Its maximum health.</summary>
    public int Maximum { get; init; }
    /// <summary>The revive channel running (its operation id, for a cancel); null when none.</summary>
    public Guid? ReviveOperation { get; init; }
    /// <summary>Milliseconds of the channel done.</summary>
    public uint ReviveElapsedMs { get; init; }
    /// <summary>Milliseconds the channel takes.</summary>
    public uint ReviveDurationMs { get; init; }
    /// <summary>Its state revision.</summary>
    public ulong StateRevision { get; init; }
    /// <summary>Its durable revision.</summary>
    public ulong DurableRevision { get; init; }
    /// <summary>The definition wants it in the built-in bar (<see cref="PetHudPolicy.Bar"/>).</summary>
    public bool ShowBar { get; init; } = true;
    /// <summary>The definition wants its recovery anchor marked (<see cref="PetHudPolicy.RecoveryMarker"/>).</summary>
    public bool ShowMarker { get; init; } = true;
}

/// <summary>An owner's whole view of their pets, as one snapshot. Since 0.2.0-dev.32.</summary>
/// <param name="WorldEpoch">The server world session.</param>
/// <param name="CharacterSession">The owner's join (a fresh random token each time).</param>
/// <param name="Sequence">Strictly increasing within the session.</param>
/// <param name="Entries">The pets (at most 128).</param>
public sealed record PetOwnerView(ulong WorldEpoch, ulong CharacterSession, ulong Sequence, IReadOnlyList<PetViewEntry> Entries);

/// <summary>Whether this game's view of its owner's pets is current. Since 0.2.0-dev.32.</summary>
public enum PetViewState
{
    /// <summary>No view: not in a world, not a pet owner, or the server sends none.</summary>
    Disconnected = 0,
    /// <summary>The view is the server's, and fresh.</summary>
    Current = 1,
    /// <summary>No keepalive for 3 s: the view is old (the bar is hidden until the next one).</summary>
    Stale = 2,
}

/// <summary>Where and how the built-in pet bar draws, on this game only (never a server state). Since 0.2.0-dev.32.</summary>
/// <param name="Enabled">The bar is drawn.</param>
/// <param name="RecoveryMarkers">Recovery anchors are marked in the world.</param>
/// <param name="AnchorX">The bar's left edge, 0..1 of the screen width.</param>
/// <param name="AnchorY">The bar's top edge, 0..1 of the screen height.</param>
/// <param name="WidthPixels">The bar's width at UI scale 1, 80..600.</param>
public sealed record PetHudOptions(bool Enabled = true, bool RecoveryMarkers = true, double AnchorX = 0.03, double AnchorY = 0.72, double WidthPixels = 220);

/// <summary>The wire codec of kiln:pets v1 (message 1: the owner's view). Bounds are checked before anything is allocated.</summary>
internal static class PetViewCodec
{
    public const byte Protocol = 1;
    public const ushort ViewMessage = 1;
    public const int MaxBytes = 64 * 1024, MaxEntries = 128, MaxEntryBytes = 384, MaxLabelBytes = 64, MaxDefinitionBytes = 128;

    private const byte HasBody = 1, HasAnchor = 2, HasRevive = 4, Bar = 8, Marker = 16;

    public static byte[] Write(PetOwnerView v)
    {
        if (v.Entries.Count > MaxEntries) throw new ArgumentException($"at most {MaxEntries} pets in a view");
        var w = new NetWriter(256);
        w.Byte(Protocol).UInt64(v.WorldEpoch).UInt64(v.CharacterSession).UInt64(v.Sequence).Byte(1).UInt16((ushort)v.Entries.Count);
        var e = new NetWriter(MaxEntryBytes);
        Span<byte> id = stackalloc byte[16];
        foreach (var x in v.Entries)
        {
            e.Clear();
            x.Id.Value.TryWriteBytes(id);
            e.Raw(id);
            Str(e, x.Definition, MaxDefinitionBytes);
            Str(e, x.Label, MaxLabelBytes);
            e.Byte((byte)x.Life).Byte((byte)x.Presence).Byte((byte)x.Transition).Byte((byte)x.RequestedOrder);
            var flags = (byte)((x.Body is not null ? HasBody : 0) | (x.RecoveryAnchor is not null ? HasAnchor : 0) | (x.ReviveOperation is not null ? HasRevive : 0) |
                               (x.ShowBar ? Bar : 0) | (x.ShowMarker ? Marker : 0));
            e.Byte(flags);
            if (x.Body is { } b) e.UInt32(b.NetworkEntityId).UInt64(b.BodyGeneration);
            if (x.RecoveryAnchor is { } a) e.Double(a.X).Double(a.Y).Double(a.Z);
            e.UInt16((ushort)Math.Clamp(x.Level, 0, ushort.MaxValue)).Int32(x.Current).Int32(x.Maximum);
            if (x.ReviveOperation is { } op)
            {
                op.TryWriteBytes(id);
                e.Raw(id).UInt32(x.ReviveElapsedMs).UInt32(x.ReviveDurationMs);
            }
            e.UInt64(x.StateRevision).UInt64(x.DurableRevision);
            if (e.Length > MaxEntryBytes) throw new ArgumentException($"a pet entry is at most {MaxEntryBytes} bytes");
            w.UInt16((ushort)e.Length).Raw(e.WrittenSpan);
        }
        if (w.Length > MaxBytes) throw new ArgumentException($"a view is at most {MaxBytes} bytes");
        return w.ToArray();
    }

    private static void Str(NetWriter w, string s, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        if (bytes.Length > max)
        {
            // cut on a character boundary, never inside a UTF-8 sequence
            var n = max;
            while (n > 0 && (bytes[n] & 0xC0) == 0x80) n--;
            bytes = bytes[..n];
        }
        w.Byte((byte)bytes.Length).Raw(bytes);
    }

    /// <summary>Reads a view, or null with why it was refused (oversized, malformed, an unknown protocol, a value out of range).</summary>
    public static PetOwnerView? Read(ReadOnlySpan<byte> data, out string? error)
    {
        error = null;
        if (data.Length > MaxBytes) { error = "oversized"; return null; }
        var r = new Reader(data);
        try
        {
            if (r.U8() != Protocol) { error = "protocol"; return null; }
            var epoch = r.U64();
            var session = r.U64();
            var seq = r.U64();
            if (r.U8() != 1) { error = "not a full snapshot"; return null; }
            var count = r.U16();
            if (count > MaxEntries) { error = "too many entries"; return null; }
            if (session == 0) { error = "no session"; return null; }
            var list = new List<PetViewEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var len = r.U16();
                if (len > MaxEntryBytes) { error = "entry too large"; return null; }
                var er = new Reader(r.Take(len));
                var id = new Guid(er.Take(16));
                var def = er.Str(MaxDefinitionBytes);
                var label = er.Str(MaxLabelBytes);
                var life = er.U8();
                var presence = er.U8();
                var transition = er.U8();
                var order = er.U8();
                if (life > 2 || presence > 3 || transition > 5 || order > 2) { error = "value out of range"; return null; }
                var flags = er.U8();
                PetBodyViewKey? body = (flags & HasBody) != 0 ? new PetBodyViewKey(er.U32(), er.U64()) : null;
                WorldPosition? anchor = null;
                if ((flags & HasAnchor) != 0)
                {
                    var (x, y, z) = (er.F64(), er.F64(), er.F64());
                    if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) { error = "anchor not finite"; return null; }
                    anchor = new WorldPosition(x, y, z);
                }
                var level = er.U16();
                var cur = er.I32();
                var max = er.I32();
                Guid? revive = null;
                uint elapsed = 0, duration = 0;
                if ((flags & HasRevive) != 0)
                {
                    revive = new Guid(er.Take(16));
                    elapsed = er.U32();
                    duration = er.U32();
                }
                var state = er.U64();
                var durable = er.U64();
                if (!er.End) { error = "entry has trailing bytes"; return null; }
                list.Add(new PetViewEntry
                {
                    Id = new PetId(id), Definition = def, Label = label, Life = (PetLife)life, Presence = (PetPresence)presence,
                    Transition = (PetTransition)transition, RequestedOrder = (PetOrder)order, Body = body, RecoveryAnchor = anchor, Level = level,
                    Current = cur, Maximum = max, ReviveOperation = revive, ReviveElapsedMs = elapsed, ReviveDurationMs = duration,
                    StateRevision = state, DurableRevision = durable, ShowBar = (flags & Bar) != 0, ShowMarker = (flags & Marker) != 0,
                });
            }
            if (!r.End) { error = "trailing bytes"; return null; }
            return new PetOwnerView(epoch, session, seq, list);
        }
        catch (FormatException e)
        {
            error = e.Message;
            return null;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private int _p;
        public bool End => _p == _d.Length;

        public ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _p + n > _d.Length) throw new FormatException("truncated");
            var s = _d.Slice(_p, n);
            _p += n;
            return s;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public string Str(int max)
        {
            var n = U8();
            if (n > max) throw new FormatException("string too long");   // checked before decoding
            return Encoding.UTF8.GetString(Take(n));
        }
    }
}

/// <summary>The client's cache of its owner's view (19d §4.8): the current server peer's latest snapshot by strictly
/// increasing sequence within one session; a new session's snapshot is taken only after the cache was cleared or went
/// stale (a delayed packet of an old connection never wins); stale after 3 s without one; cleared on disconnect, a
/// character switch or the world's end. Readers get one immutable snapshot.</summary>
internal sealed class PetViewCache
{
    public const double StaleSeconds = 3;
    private PetOwnerView? _view;
    private double _at = double.NegativeInfinity;
    private PetViewState _state = PetViewState.Disconnected;

    public PetOwnerView? View => Volatile.Read(ref _view);
    public PetViewState State => _state;

    /// <summary>Takes a snapshot; false when it is stale, out of order or of another session (the cache is unchanged).</summary>
    public bool Accept(PetOwnerView v, double now)
    {
        var have = _view;
        if (have is not null && _state == PetViewState.Current)
        {
            if (v.CharacterSession != have.CharacterSession || v.WorldEpoch != have.WorldEpoch) return false;
            if (v.Sequence <= have.Sequence) return false;
        }
        else if (have is not null && v.CharacterSession == have.CharacterSession && v.WorldEpoch == have.WorldEpoch && v.Sequence <= have.Sequence)
            return false;
        Volatile.Write(ref _view, v);
        _at = now;
        _state = PetViewState.Current;
        return true;
    }

    /// <summary>The clock moved: a view without a keepalive for 3 s goes stale. True when the state changed.</summary>
    public bool Tick(double now)
    {
        if (_state == PetViewState.Current && now - _at >= StaleSeconds)
        {
            _state = PetViewState.Stale;
            return true;
        }
        return false;
    }

    /// <summary>Disconnect, character switch, world end. True when anything was there.</summary>
    public bool Clear()
    {
        var had = _view is not null || _state != PetViewState.Disconnected;
        Volatile.Write(ref _view, null);
        _state = PetViewState.Disconnected;
        _at = double.NegativeInfinity;
        return had;
    }
}

/// <summary>The built-in bar's geometry (19d §4.8): at (AnchorX, AnchorY) of the screen, width x scale, rows of 24 x scale,
/// bars 14 x scale high, at most 6 rows: live pets first, then recovery anchors, then the rest, each by PetId.</summary>
internal static class PetHudLayout
{
    public const int MaxRows = 6;

    public readonly record struct Row(PetViewEntry Entry, float X, float Y, float Width, float BarHeight, float Fraction, string Status);

    public static IReadOnlyList<Row> Rows(PetOwnerView? view, PetHudOptions o, float screenW, float screenH, float scale)
    {
        if (view is null || !o.Enabled) return [];
        var x = (float)(o.AnchorX * screenW);
        var y = (float)(o.AnchorY * screenH);
        var w = (float)(o.WidthPixels * scale);
        var rows = new List<Row>();
        foreach (var e in view.Entries.Where(e => e.ShowBar && e.Life != PetLife.Released)
                     .OrderBy(e => e.Presence == PetPresence.Live ? 0 : e.Presence == PetPresence.RecoveryAnchor ? 1 : 2)
                     .ThenBy(e => e.Id.Value)
                     .Take(MaxRows))
        {
            var fraction = e.Maximum <= 0 ? 0f : Math.Clamp((float)e.Current / e.Maximum, 0f, 1f);
            var status = e.Transition == PetTransition.Reviving ? "Recovering" : e.Life == PetLife.Downed ? "Downed" : "Ready";
            rows.Add(new Row(e, x, y + rows.Count * 24 * scale, w, 14 * scale, fraction, status));
        }
        return rows;
    }
}
