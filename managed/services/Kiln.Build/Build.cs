// SPDX-License-Identifier: MIT
// Managed API over kiln.build (native/core/abi/services/kiln_build.h). Usage: docs/kiln/services/build.md.
using System.Numerics;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>
/// Changes the world the way the game saves and replicates it, so vanilla clients see the result: voxel cells
/// (terrain and building blocks), shape edits (dig, add, flatten, smooth, paint, replace), props, schematics
/// (<see cref="Schematic"/>), a per-session undo journal and <see cref="PlaceOnce"/>.
/// </summary>
/// <remarks>
/// Everything that touches the world runs on the tick thread (a mod's handlers, <c>OnLoad</c>, continuations after
/// <c>await</c>). The game streams voxel tiles in only around players and bases on a dedicated server; the async
/// methods ask for the tiles they need and wait until they are loaded (<see cref="BuildOptions.LoadTimeout"/>), the
/// sync ones throw <see cref="BuildException"/> when a tile is missing (and ask for it, so a retry a few steps later
/// works). Kiln's edits bypass <c>Gameplay.Protect</c> zones unless <see cref="BuildFlags.RespectZones"/> is set and
/// refuse to touch player bases unless <see cref="BuildFlags.InBases"/>. The game itself saves terrain only inside
/// bases; Kiln adds the chunks it edits to the game's save (unless <see cref="BuildFlags.Transient"/>).
/// </remarks>
public static partial class Build
{
    private static unsafe BuildNative.Table* T => BuildNative.T;

    /// <summary>True when kiln.build runs on this game build.</summary>
    public static unsafe bool Available => T != null;

    internal static string Caller => ModContext.CurrentMod?.Info.Id ?? "kiln";

    private static void Ensure(string api)
    {
        if (!Available) throw new NotSupportedException("kiln.build is unavailable on this game build");
        Services.RequireTickThread(api);
    }

    private static unsafe BuildNative.Table* Require(string api)
    {
        var t = T;
        if (t == null) throw new NotSupportedException("kiln.build is unavailable on this game build");
        Services.RequireTickThread(api);
        return t;
    }

    /// <summary>kiln.build's state (any thread). Null when unavailable.</summary>
    public static unsafe BuildStatus? Status
    {
        get
        {
            var t = T;
            if (t == null) return null;
            BuildNative.Status s;
            s.Size = (uint)sizeof(BuildNative.Status);
            if (t->GetStatus(&s) != BuildNative.Ok) return null;
            return new BuildStatus(s.Read != 0, s.Write != 0, s.Props != 0, s.World != 0, s.Persist != 0, s.Reads, s.Writes,
                s.CellsRead, s.CellsWritten, s.BlocksWritten, s.PropsPlaced, s.PropsRemoved, s.Refused, s.KeptChunks,
                s.SavesSeen, BuildNative.Utf8Z(s.Detail, 160));
        }
    }

    private static VoxelGrid? _grid;

    /// <summary>The voxel grid's geometry (tick thread; null before the world runs).</summary>
    public static unsafe VoxelGrid? Grid
    {
        get
        {
            if (_grid is not null) return _grid;
            var t = Require("Build.Grid");
            BuildNative.Grid g;
            g.Size = (uint)sizeof(BuildNative.Grid);
            if (t->GetGrid(&g) != BuildNative.Ok) return null;
            return _grid = new VoxelGrid(g.CellSize, g.Origin.ToPosition(), g.CellsMax[0], g.CellsMax[2], (int)g.TileCells[0],
                (int)g.Level);
        }
    }

    private static VoxelGrid G => Grid ?? throw new BuildException("the voxel world is not running yet", BuildNative.Unavailable);

    // ---- coordinates ----

    /// <summary>The cell that contains a world position.</summary>
    public static CellPos CellOf(WorldPosition p)
    {
        var g = G;
        return new CellPos((int)Math.Floor((p.X - g.Origin.X) / g.CellSize), (int)Math.Floor((p.Y - g.Origin.Y) / g.CellSize),
            (int)Math.Floor((p.Z - g.Origin.Z) / g.CellSize));
    }

    /// <summary>World position of a cell's low corner.</summary>
    public static WorldPosition CornerOf(CellPos c)
    {
        var g = G;
        return new WorldPosition(g.Origin.X + c.X * g.CellSize, g.Origin.Y + c.Y * g.CellSize, g.Origin.Z + c.Z * g.CellSize);
    }

    /// <summary>World position of a cell's centre.</summary>
    public static WorldPosition CenterOf(CellPos c)
    {
        var g = G;
        return new WorldPosition(g.Origin.X + (c.X + 0.5) * g.CellSize, g.Origin.Y + (c.Y + 0.5) * g.CellSize,
            g.Origin.Z + (c.Z + 0.5) * g.CellSize);
    }

    /// <summary>The cells covering the box between two world positions.</summary>
    public static CellBox BoxBetween(WorldPosition a, WorldPosition b) => CellBox.FromCorners(CellOf(a), CellOf(b));

    /// <summary>The cells covering <paramref name="center"/> ± half extents (metres).</summary>
    public static CellBox BoxAround(WorldPosition center, double halfX, double halfY, double halfZ) =>
        BoxBetween(new(center.X - halfX, center.Y - halfY, center.Z - halfZ), new(center.X + halfX, center.Y + halfY, center.Z + halfZ));

    // ---- loading ----

    /// <summary>Whether every voxel tile under the box is loaded. Asks the game for the missing ones (they arrive within
    /// a few seconds), so polling this each step loads the area.</summary>
    public static unsafe bool IsLoaded(CellBox box)
    {
        var t = Require("Build.IsLoaded");
        CheckBox(box);
        int* o = stackalloc int[3] { box.X, box.Y, box.Z };
        uint* d = stackalloc uint[3] { (uint)box.SizeX, (uint)box.SizeY, (uint)box.SizeZ };
        return t->Ready(o, d) == BuildNative.Ok;
    }

    /// <summary>Waits (on the tick thread) until the tiles under the box are loaded. False after the timeout. Mod code
    /// waits on its mod's scheduler, Kiln's own code (such as the <c>/build</c> command) on Kiln's service tick.</summary>
    public static async Task<bool> Load(CellBox box, TimeSpan? timeout = null)
    {
        var mod = ModContext.CurrentMod;
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            if (IsLoaded(box)) return true;
            if (DateTime.UtcNow > until) return false;
            if (mod is not null) await mod.Scheduler.NextTick();
            else await NextServiceTick();
        }
    }

    private static readonly object WaitLock = new();
    private static List<TaskCompletionSource> _waiting = [];
    private static IDisposable? _waitTick;

    // Completes at the next service tick (tick thread, no mod context): Kiln's own code has no mod scheduler to wait on.
    private static Task NextServiceTick()
    {
        var next = new TaskCompletionSource();
        lock (WaitLock)
        {
            _waiting.Add(next);
            _waitTick ??= ServiceTick.Add("kiln.build waits", static (in TickInfo _) =>
            {
                List<TaskCompletionSource> due;
                lock (WaitLock)
                {
                    if (_waiting.Count == 0) return;
                    due = _waiting;
                    _waiting = [];
                }
                foreach (var w in due) w.TrySetResult();
            });
        }
        return next.Task;
    }

    // ---- cells ----

    /// <summary>Reads the cells of a box (x fastest). Throws <see cref="BuildException"/> when a tile is not loaded.</summary>
    public static Voxel[] Read(CellBox box) =>
        TryRead(box, out var cells) ? cells : throw new BuildException($"voxel tiles under {box} are not loaded yet", BuildNative.Unavailable);

    /// <summary>Reads the cells of a box; false (cells still filled from what is loaded) when a tile is missing.</summary>
    public static unsafe bool TryRead(CellBox box, out Voxel[] cells)
    {
        var t = Require("Build.Read");
        CheckBox(box);
        cells = new Voxel[box.Count];
        int* o = stackalloc int[3] { box.X, box.Y, box.Z };
        uint* d = stackalloc uint[3] { (uint)box.SizeX, (uint)box.SizeY, (uint)box.SizeZ };
        int rc;
        fixed (Voxel* p = cells) rc = t->Read(o, d, (ushort*)p, (nuint)cells.Length);
        if (rc == BuildNative.Unavailable)
        {
            t->Ready(o, d);  // ask for the tiles
            return false;
        }
        Check(rc, "read");
        return true;
    }

    /// <summary>Loads the tiles, then reads.</summary>
    public static async Task<Voxel[]> ReadAsync(CellBox box, TimeSpan? timeout = null)
    {
        if (!await Load(box, timeout)) throw new BuildException($"voxel tiles under {box} did not load", BuildNative.Unavailable);
        return Read(box);
    }

    /// <summary>One cell.</summary>
    public static Voxel ReadCell(CellPos c) => Read(new CellBox(c.X, c.Y, c.Z, 1, 1, 1))[0];

    /// <summary>Writes exact cells (x fastest) through the game's block writer. Tiles must be loaded
    /// (<see cref="WriteAsync"/> waits for them). <paramref name="mask"/>: optional, same layout, false = keep the
    /// world's cell.</summary>
    public static BuildResult Write(CellBox box, ReadOnlySpan<Voxel> cells, BuildOptions? options = null, ReadOnlySpan<bool> mask = default)
    {
        Ensure("Build.Write");
        var o = options ?? BuildOptions.Default;
        if (!Editing(BuildEditKind.Write, box, o)) return Canceled(box);
        var entry = o.Journal ? Journal.Begin(BuildEditKind.Write, o) : null;
        var r = WriteCore(box, cells, mask, o, entry);
        Finish(BuildEditKind.Write, box, o, r, entry);
        return r;
    }

    /// <summary>Loads the tiles, then <see cref="Write"/>s.</summary>
    public static async Task<BuildResult> WriteAsync(CellBox box, Voxel[] cells, BuildOptions? options = null, bool[]? mask = null)
    {
        var o = options ?? BuildOptions.Default;
        if (!await Load(box, o.LoadTimeout)) return NotLoaded(box);
        return Write(box, cells, o, mask);
    }

    // ---- shapes ----

    /// <summary>Removes terrain and blocks inside the shape (a smooth one-cell edge).</summary>
    public static Task<BuildResult> Dig(BuildShape shape, BuildOptions? options = null) =>
        RunShape(ShapeOp.Dig, shape, 0, 0, double.NaN, 0, 0, options);

    /// <summary>Fills the shape with <paramref name="material"/> (a voxel material id, e.g. from <see cref="MaterialOf"/>).</summary>
    public static Task<BuildResult> Add(BuildShape shape, byte material, BuildOptions? options = null) =>
        RunShape(ShapeOp.Add, shape, material, 0, double.NaN, 0, 0, options);

    /// <summary>Levels the shape's footprint to <paramref name="levelY"/> metres: solid below (each column's own
    /// material, or <paramref name="material"/>), air above, within the shape's height.</summary>
    public static Task<BuildResult> Flatten(BuildShape shape, double levelY, byte material = 0, BuildOptions? options = null) =>
        RunShape(ShapeOp.Flatten, shape, material, 0, levelY, 0, 0, options);

    /// <summary>Blurs the surface inside the shape (<paramref name="strength"/> 0..1 per pass, 1..8 passes).</summary>
    public static Task<BuildResult> Smooth(BuildShape shape, float strength = 0.5f, int passes = 2, BuildOptions? options = null) =>
        RunShape(ShapeOp.Smooth, shape, 0, 0, double.NaN, strength, passes, options);

    /// <summary>Gives every non-air cell inside <paramref name="material"/>, keeping its fill.</summary>
    public static Task<BuildResult> Paint(BuildShape shape, byte material, BuildOptions? options = null) =>
        RunShape(ShapeOp.Paint, shape, material, 0, double.NaN, 0, 0, options);

    /// <summary>Cells of material <paramref name="from"/> inside become <paramref name="to"/>.</summary>
    public static Task<BuildResult> Replace(BuildShape shape, byte from, byte to, BuildOptions? options = null) =>
        RunShape(ShapeOp.Replace, shape, to, from, double.NaN, 0, 0, options);

    private static unsafe BuildNative.Shape MakeShape(ShapeOp op, BuildShape s, byte material, byte from, double level, float strength, int passes,
        BuildOptions o)
    {
        return new BuildNative.Shape
        {
            Size = (uint)sizeof(BuildNative.Shape),
            Flags = (uint)o.Flags,
            Op = (uint)op,
            Kind = (uint)s.Kind,
            Center = new BuildNative.Vec3(s.Center),
            Half = new BuildNative.Vec3(s.Half.X, s.Half.Y, s.Half.Z),
            LevelY = level,
            Strength = strength,
            Passes = (uint)Math.Clamp(passes, 0, 8),
            Material = material,
            FromMaterial = from,
            Actor = o.Actor.Value,
        };
    }

    /// <summary>The cells a shape edit touches.</summary>
    public static unsafe CellBox ShapeBox(ShapeOp op, BuildShape shape, double levelY = double.NaN)
    {
        var t = Require("Build.ShapeBox");
        var sh = MakeShape(op, shape, 1, 1, levelY, 0, 1, BuildOptions.Default);
        int* o = stackalloc int[3];
        uint* d = stackalloc uint[3];
        Check(t->ShapeBox(&sh, o, d), "shape");
        return new CellBox(o[0], o[1], o[2], (int)d[0], (int)d[1], (int)d[2]);
    }

    private static async Task<BuildResult> RunShape(ShapeOp op, BuildShape shape, byte material, byte from, double level, float strength,
        int passes, BuildOptions? options)
    {
        Ensure("Build." + op);
        var o = options ?? BuildOptions.Default;
        var box = ShapeBox(op, shape, level);
        if (!await Load(box, o.LoadTimeout)) return NotLoaded(box);
        return ShapeCore(op, shape, material, from, level, strength, passes, o, box);
    }

    private static unsafe BuildResult ShapeCore(ShapeOp op, BuildShape shape, byte material, byte from, double level, float strength,
        int passes, BuildOptions o, CellBox box)
    {
        var t = Require("Build." + op);
        var kind = (BuildEditKind)((int)BuildEditKind.Dig + ((int)op - 1));
        if (!Editing(kind, box, o)) return Canceled(box);
        var entry = o.Journal ? Journal.Begin(kind, o) : null;
        Voxel[]? before = entry is null ? null : Read(box);
        var sh = MakeShape(op, shape, material, from, level, strength, passes, o);
        var originBytes = System.Text.Encoding.UTF8.GetBytes(Caller);
        BuildNative.Result r;
        r.Size = (uint)sizeof(BuildNative.Result);
        int rc;
        fixed (byte* ob = originBytes)
        {
            sh.OriginMod = new NativeStr { Ptr = ob, Len = (nuint)originBytes.Length };
            rc = t->RunShape(&sh, &r);
        }
        var result = ToResult(rc, &r, box);
        if (entry is not null && before is not null && result.Changed) entry.AddCells(box, before, Read(box));
        Finish(kind, box, o, result, entry);
        return result;
    }

    // ---- internals shared with schematics ----

    internal static unsafe BuildResult WriteCore(CellBox box, ReadOnlySpan<Voxel> cells, ReadOnlySpan<bool> mask, BuildOptions o, JournalEntry? entry)
    {
        var t = Require("Build.Write");
        CheckBox(box);
        if (cells.Length != box.Count) throw new ArgumentException($"{cells.Length} cells for a box of {box.Count}", nameof(cells));
        if (!mask.IsEmpty && mask.Length != box.Count) throw new ArgumentException("mask size", nameof(mask));
        Voxel[]? before = null;
        if (entry is not null)
        {
            if (!TryRead(box, out before)) return NotLoaded(box);
        }
        byte[]? maskBytes = null;
        if (!mask.IsEmpty)
        {
            maskBytes = new byte[mask.Length];
            for (var i = 0; i < mask.Length; i++) maskBytes[i] = mask[i] ? (byte)1 : (byte)0;
        }
        var originBytes = System.Text.Encoding.UTF8.GetBytes(Caller);
        BuildNative.Write w = default;
        w.Size = (uint)sizeof(BuildNative.Write);
        w.Flags = (uint)o.Flags;
        w.Origin[0] = box.X; w.Origin[1] = box.Y; w.Origin[2] = box.Z;
        w.Dims[0] = (uint)box.SizeX; w.Dims[1] = (uint)box.SizeY; w.Dims[2] = (uint)box.SizeZ;
        w.Actor = o.Actor.Value;
        BuildNative.Result r;
        r.Size = (uint)sizeof(BuildNative.Result);
        int rc;
        fixed (Voxel* c = cells)
        fixed (byte* m = maskBytes)
        fixed (byte* ob = originBytes)
        {
            w.Cells = (ushort*)c;
            w.Mask = m;
            w.OriginMod = new NativeStr { Ptr = ob, Len = (nuint)originBytes.Length };
            rc = t->WriteCells(&w, &r);
        }
        var result = ToResult(rc, &r, box);
        if (entry is not null && before is not null && result.Changed) entry.AddCells(box, before, Read(box));
        return result;
    }

    private static unsafe BuildResult ToResult(int rc, BuildNative.Result* r, CellBox box)
    {
        var refusal = BuildRefusal.None;
        if ((r->Flags & BuildNative.RZone) != 0) refusal = BuildRefusal.Zone;
        else if ((r->Flags & BuildNative.RBase) != 0) refusal = BuildRefusal.Base;
        else if ((r->Flags & BuildNative.RNotLoaded) != 0) refusal = BuildRefusal.NotLoaded;
        else if ((r->Flags & BuildNative.RDryRun) != 0) refusal = BuildRefusal.DryRun;
        if (refusal == BuildRefusal.None && rc != BuildNative.Ok) Check(rc, "edit");
        return new BuildResult(refusal is BuildRefusal.None or BuildRefusal.DryRun ? (long)r->CellsChanged : 0, (int)r->BlocksWritten,
            (r->Flags & BuildNative.RVerified) != 0, (r->Flags & BuildNative.RKept) != 0, box, r->Milliseconds, refusal, r->Zone);
    }

    internal static BuildResult NotLoaded(CellBox box) => new(0, 0, false, false, box, 0, BuildRefusal.NotLoaded);
    internal static BuildResult Canceled(CellBox box) => new(0, 0, false, false, box, 0, BuildRefusal.Canceled);

    internal static bool Editing(BuildEditKind kind, CellBox box, BuildOptions o)
    {
        if (!Events.HasSubscribers<BuildEditingEvent>()) return true;
        return !Events.Post(new BuildEditingEvent(kind, box, o.Actor, Caller, o.Label)).Canceled;
    }

    internal static void Finish(BuildEditKind kind, CellBox box, BuildOptions o, BuildResult? r, JournalEntry? entry)
    {
        long id = 0;
        if (entry is not null && entry.HasContent) id = Journal.Commit(entry, o.Label ?? kind.ToString().ToLowerInvariant());
        if (Events.HasSubscribers<BuildEditEvent>())
            Events.Post(new BuildEditEvent(kind, box, o.Actor, Caller, o.Label, r, entry?.Placed ?? [], entry?.RemovedIds ?? [], id));
    }

    internal static void CheckBox(CellBox box)
    {
        if (box.IsEmpty || box.SizeX > 4096 || box.SizeY > 4096 || box.SizeZ > 4096 || box.Count > (1 << 22))
            throw new ArgumentException($"cell box {box} is empty or too big (at most 4096 per axis, 4194304 cells)", nameof(box));
    }

    internal static void Check(int rc, string what)
    {
        switch (rc)
        {
            case BuildNative.Ok: return;
            case BuildNative.InvalidArgument: throw new ArgumentException($"kiln.build {what}: invalid argument");
            case BuildNative.NotFound: throw new BuildException($"kiln.build {what}: not found", rc);
            case BuildNative.Unavailable: throw new BuildException($"kiln.build {what}: unavailable (world not running, or a tile not loaded)", rc);
            case BuildNative.WrongThread: throw new InvalidOperationException($"kiln.build {what} must run on the tick thread");
            case BuildNative.Revoked: throw new BuildException($"kiln.build {what}: refused (zone or base)", rc);
            default: throw new BuildException($"kiln.build {what} failed ({rc}); see kiln.log", rc);
        }
    }

    // ---- props ----

    /// <summary>Places a prop the way the game's building helper does (CurrentTransform, DisableRandomLootOnDestroy,
    /// UsedItem). <paramref name="template"/>: a template name ("Prop_Storage_24_T1_Box"), GUID text, or the debug
    /// name of the item that places it. <paramref name="itemId"/>: what dismantling gives back (0 = the item that
    /// places the template, when known; <see cref="uint.MaxValue"/> = none).</summary>
    public static EntityId PlaceProp(string template, WorldPosition position, Quaternion? rotation = null, uint itemId = 0,
        BuildOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        return Guid.TryParse(template, out var g)
            ? PlaceProp(g, position, rotation, itemId, options)
            : PlacePropCore(Guid.Empty, template, position, rotation ?? Quaternion.Identity, itemId, options ?? BuildOptions.Default, null);
    }

    public static EntityId PlaceProp(Guid template, WorldPosition position, Quaternion? rotation = null, uint itemId = 0,
        BuildOptions? options = null) =>
        PlacePropCore(template, null, position, rotation ?? Quaternion.Identity, itemId, options ?? BuildOptions.Default, null);

    internal static unsafe EntityId PlacePropCore(Guid template, string? name, WorldPosition position, Quaternion rotation, uint itemId,
        BuildOptions o, JournalEntry? into)
    {
        var t = Require("Build.PlaceProp");
        var box = new CellBox(CellOf(position).X, CellOf(position).Y, CellOf(position).Z, 1, 1, 1);
        var own = into is null;
        if (own && !Editing(BuildEditKind.PlaceProp, box, o)) return EntityId.None;
        var p = new BuildNative.Prop
        {
            Size = (uint)sizeof(BuildNative.Prop),
            Flags = (uint)o.Flags,
            Position = new BuildNative.Vec3(position),
            ItemId = itemId,
            Actor = o.Actor.Value,
        };
        p.Rotation[0] = rotation.X; p.Rotation[1] = rotation.Y; p.Rotation[2] = rotation.Z; p.Rotation[3] = rotation.W;
        if (template != Guid.Empty) BuildNative.WriteGuid(template, &p.Template);
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name ?? "");
        var originBytes = System.Text.Encoding.UTF8.GetBytes(Caller);
        uint id = 0;
        int rc;
        fixed (byte* n = nameBytes)
        fixed (byte* ob = originBytes)
        {
            p.TemplateName = new NativeStr { Ptr = n, Len = (nuint)nameBytes.Length };
            p.OriginMod = new NativeStr { Ptr = ob, Len = (nuint)originBytes.Length };
            rc = t->PropPlace(&p, &id);
        }
        if (rc == BuildNative.Revoked) return EntityId.None;  // zone / base / altar: refused
        Check(rc, $"place prop {name ?? template.ToString()}");
        var e = new EntityId(id);
        if (own)
        {
            var entry = o.Journal ? Journal.Begin(BuildEditKind.PlaceProp, o) : null;
            entry?.Placed.Add(e);
            Finish(BuildEditKind.PlaceProp, box, o, null, entry);
        }
        else
        {
            into!.Placed.Add(e);
        }
        return e;
    }

    /// <summary>Removes a prop (any static entity; never creatures, villagers or players). False when it does not
    /// exist.</summary>
    public static bool RemoveProp(EntityId entity, BuildOptions? options = null)
    {
        Ensure("Build.RemoveProp");
        var o = options ?? BuildOptions.Default;
        var info = Prop(entity);
        if (info is null) return false;
        var box = new CellBox(CellOf(info.Position).X, CellOf(info.Position).Y, CellOf(info.Position).Z, 1, 1, 1);
        if (!Editing(BuildEditKind.RemoveProp, box, o)) return false;
        var entry = o.Journal ? Journal.Begin(BuildEditKind.RemoveProp, o) : null;
        var ok = RemovePropCore(info, entry);
        if (ok) Finish(BuildEditKind.RemoveProp, box, o, null, entry);
        return ok;
    }

    internal static unsafe bool RemovePropCore(PlacedProp info, JournalEntry? into)
    {
        var t = Require("Build.RemoveProp");
        var rc = t->PropRemove(info.Entity.Value);
        if (rc == BuildNative.NotFound) return false;
        Check(rc, "remove prop");
        into?.Removed.Add(info);
        return true;
    }

    /// <summary>The prop's template, transform and UsedItem; null when it does not exist.</summary>
    public static unsafe PlacedProp? Prop(EntityId entity)
    {
        var t = Require("Build.Prop");
        BuildNative.PropInfo p;
        p.Size = (uint)sizeof(BuildNative.PropInfo);
        return t->PropInfoOf(entity.Value, &p) == BuildNative.Ok ? ToProp(&p) : null;
    }

    /// <summary>Props whose position lies in the box between <paramref name="min"/> and <paramref name="max"/>,
    /// nearest to its centre first. Child entities (lights, offers) come with their parent and are not listed.</summary>
    public static unsafe IReadOnlyList<PlacedProp> Props(WorldPosition min, WorldPosition max, PropFilter filter = PropFilter.Placed)
    {
        var t = Require("Build.Props");
        var lo = new BuildNative.Vec3(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z));
        var hi = new BuildNative.Vec3(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z));
        var cap = 256;
        while (true)
        {
            var buf = new BuildNative.PropInfo[cap];
            for (var i = 0; i < cap; i++) buf[i].Size = (uint)sizeof(BuildNative.PropInfo);
            nuint count = 0;
            int rc;
            fixed (BuildNative.PropInfo* b = buf) rc = t->PropsInBox(lo, hi, (uint)filter, b, (nuint)cap, &count);
            if (rc == BuildNative.BufferTooSmall || (int)count > cap)
            {
                cap = (int)count + 16;
                continue;
            }
            Check(rc, "props");
            var list = new List<PlacedProp>((int)count);
            fixed (BuildNative.PropInfo* b = buf)
                for (var i = 0; i < (int)count; i++) list.Add(ToProp(b + i));
            return list;
        }
    }

    /// <summary>Props inside a cell box (positions within its world-space bounds).</summary>
    public static IReadOnlyList<PlacedProp> Props(CellBox box, PropFilter filter = PropFilter.Placed) =>
        Props(CornerOf(box.Min), CornerOf(box.End), filter);

    private static unsafe PlacedProp ToProp(BuildNative.PropInfo* p) => new(new EntityId(p->Entity), BuildNative.ReadGuid(&p->Template),
        BuildNative.Utf8Z(p->Name, 128), p->Position.ToPosition(), new Quaternion(p->Rotation[0], p->Rotation[1], p->Rotation[2], p->Rotation[3]),
        new Vector3(p->Scale[0], p->Scale[1], p->Scale[2]), p->ItemId, (TemplateKind)p->Kind, (PropFlags)p->Flags);

    // ---- bases, items, regions ----

    /// <summary>Player bases (the game's base build zones, rebuilt every step).</summary>
    public static unsafe IReadOnlyList<PlayerBase> Bases
    {
        get
        {
            var t = Require("Build.Bases");
            var buf = new BuildNative.Base[64];
            for (var i = 0; i < buf.Length; i++) buf[i].Size = (uint)sizeof(BuildNative.Base);
            nuint n = 0;
            int rc;
            fixed (BuildNative.Base* b = buf) rc = t->Bases(b, (nuint)buf.Length, &n);
            if (rc == BuildNative.Unavailable) return [];
            if (rc != BuildNative.BufferTooSmall) Check(rc, "bases");
            var list = new List<PlayerBase>();
            for (var i = 0; i < Math.Min((int)n, buf.Length); i++)
                list.Add(new PlayerBase(new EntityId(buf[i].Altar), buf[i].Center.ToPosition(),
                    new Vector3((float)buf[i].Half.X, (float)buf[i].Half.Y, (float)buf[i].Half.Z), buf[i].Abandoned != 0));
            return list;
        }
    }

    /// <summary>The voxel material a building-material item places ("Stone Block" ...), 0 when none.</summary>
    public static unsafe byte MaterialOf(uint itemId) => T == null ? (byte)0 : (byte)T->ItemMaterial(itemId);

    /// <summary>The template a prop item places, or null.</summary>
    public static unsafe Guid? TemplateOf(uint itemId)
    {
        var t = T;
        if (t == null) return null;
        BuildNative.GuidBytes g;
        return t->ItemTemplate(itemId, &g) == BuildNative.Ok ? BuildNative.ReadGuid(&g) : null;
    }

    /// <summary>Adds a box to Kiln's save regions: the game then saves every terrain change there, players' digging
    /// included (as inside a base). Kiln's own edits are kept by default.</summary>
    public static unsafe void Keep(CellBox box)
    {
        var t = Require("Build.Keep");
        CheckBox(box);
        int* o = stackalloc int[3] { box.X, box.Y, box.Z };
        uint* d = stackalloc uint[3] { (uint)box.SizeX, (uint)box.SizeY, (uint)box.SizeZ };
        Check(t->Keep(o, d), "keep");
    }

    /// <summary>Forgets the save regions inside a box: their terrain reverts at the next restart unless a base covers
    /// it. Returns the 16 m chunks released.</summary>
    public static unsafe long Release(CellBox box)
    {
        var t = Require("Build.Release");
        CheckBox(box);
        int* o = stackalloc int[3] { box.X, box.Y, box.Z };
        uint* d = stackalloc uint[3] { (uint)box.SizeX, (uint)box.SizeY, (uint)box.SizeZ };
        ulong n = 0;
        Check(t->Release(o, d, &n), "release");
        return (long)n;
    }
}
