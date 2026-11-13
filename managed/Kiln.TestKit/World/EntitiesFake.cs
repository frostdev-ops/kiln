// SPDX-License-Identifier: MIT
// The state behind the fake kiln.world: entities, their components as blocks of native memory, and tracked writes with
// the native service's own rules (native/services/world/write_queue.h): what the mods' manifests list and which names
// the program takes, the queue and its limits, one write, the watch over waiting requests' entities, the check one step
// later, the answers and their delivery. native/services/world/tests/parity.json holds the boundaries this file and the
// native service are both tested on (FakeWorldParityTests).
using System.Runtime.InteropServices;
using System.Text;
using Kiln.Host;

namespace Kiln.TestKit;

/// <summary>Which listed component types the write program takes, by kiln.world's rule (write_queue.h
/// WriteDeclarations add + admit): mods in boot order, each mod's names in its order, a name listed twice counted once; a
/// name the world has no component type for is refused as unknown and takes no accessor; of the others the first 92 are
/// the program's and the rest over the limit.</summary>
internal static class WriteLists
{
    internal sealed record Result(Dictionary<string, HashSet<uint>> Listed, Dictionary<uint, uint> Types, Dictionary<uint, string> Names);

    public static Result Admit(IEnumerable<(string Mod, IReadOnlyList<string> Writes)> mods, Func<uint, bool>? inWorld)
    {
        var listed = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        var names = new Dictionary<uint, string>();
        var order = new List<uint>();
        foreach (var (mod, writes) in mods)
        {
            var set = listed.TryGetValue(mod, out var had) ? had : listed[mod] = [];
            foreach (var name in writes.Take(WorldAbi.MaxPerMod))
            {
                var hash = WorldAbi.Hash(name);
                if (names.TryGetValue(hash, out var known) && known != name) continue;  // two names, one hash: never mixed up
                set.Add(hash);
                if (names.TryAdd(hash, name)) order.Add(hash);
            }
        }
        var types = new Dictionary<uint, uint>();
        var taken = 0;
        foreach (var hash in order)
            types[hash] = inWorld is not null && !inWorld(hash) ? WorldAbi.Unknown
                : taken++ < WorldAbi.MaxTypes ? WorldAbi.Ready
                : WorldAbi.OverLimit;
        return new Result(listed, types, names);
    }
}

internal sealed unsafe class EntitiesFake : IDisposable
{
    internal readonly record struct Slot(nint Data, int Size);

    private sealed class Entity
    {
        public readonly Dictionary<uint, Slot> Components = [];
        public FakePlayer? Player;  // a fake player's entity: its record lives as long as that player holds the slot
    }

    private sealed class Job
    {
        public ulong Ticket, Owner, Submitted;
        public uint Entity, Component, Offset, Length, Flags;
        public byte[]? Data;
        public delegate* unmanaged[Cdecl]<uint, void*, uint, void*, byte> Change;
        public delegate* unmanaged[Cdecl]<WorldAbi.WriteResult*, void*, void> Callback;
        public void* User;
        public string Mod = "";
        public bool Lost;
        public uint Status = WorldAbi.Dropped, OutFlags, ComponentSize, Writers, Steps;
        public byte[]? Before, After;
        public string Reason = "";
    }

    internal sealed class GameWriter
    {
        public required uint Component;
        public required string Program;
        public Action<uint, nint, int>? Each;
    }

    /// <summary>What WriteInfo and Status read on any thread: published whole on the tick thread after every change.</summary>
    private sealed record Meta(
        IReadOnlyDictionary<uint, string[]> Writers,
        IReadOnlyDictionary<uint, (bool Save, bool Replication)> Tracking,
        IReadOnlySet<uint> Attributes,
        IReadOnlyDictionary<uint, int> Sizes,
        IReadOnlyDictionary<uint, string> Names,
        WriteLists.Result? Lists);  // null before the first step: the lists are read from the mods folder then

    private const int TransformSize = 56;  // keen::ecs::CurrentTransform on this build (Kiln.Game's table says when it has it)

    private readonly KilnHarness _harness;
    private readonly object _lock = new();  // the queue: Submit and Cancel may come from any thread
    private readonly Dictionary<uint, Entity> _entities = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly HashSet<uint> _attributes = [];
    private readonly Dictionary<uint, int> _sizes = [];
    private readonly Dictionary<uint, (bool Save, bool Replication)> _tracking = [];
    private readonly List<GameWriter> _writers = [];
    private readonly List<FakeWorldWrite> _record = [];
    private readonly List<Job> _pending = [], _checking = [], _done = [];
    private readonly Dictionary<ulong, ulong> _delivering = [];  // drained in this step's delivery: ticket -> owner
    private Meta _meta;
    private uint _nextEntity = 2000;
    private ulong _lastTicket, _steps, _submitted, _written, _unchanged, _failed;

    public EntitiesFake(KilnHarness harness)
    {
        _harness = harness;
        _meta = new Meta(new Dictionary<uint, string[]>(), new Dictionary<uint, (bool, bool)>(), new HashSet<uint>(),
            new Dictionary<uint, int>(), new Dictionary<uint, string>(), null);
    }

    /// <summary>The first step ran: the server world exists and the lists are fixed.</summary>
    public bool Started => Volatile.Read(ref _meta).Lists is not null;

    public ulong Steps => _steps;

    public int Count
    {
        get
        {
            RetireDeparted();
            return _entities.Count;
        }
    }

    public IReadOnlyList<FakeWorldWrite> Record
    {
        get
        {
            lock (_lock) return [.. _record];
        }
    }

    public IReadOnlyList<uint> Entities
    {
        get
        {
            RetireDeparted();
            return [.. _entities.Keys.Order()];
        }
    }

    // The tick thread changed what WriteInfo answers from: publish it whole.
    private void Publish(WriteLists.Result? lists = null) =>
        Volatile.Write(ref _meta, new Meta(
            _writers.GroupBy(w => w.Component).ToDictionary(g => g.Key, g => g.Select(w => w.Program).ToArray()),
            new Dictionary<uint, (bool, bool)>(_tracking), new HashSet<uint>(_attributes), new Dictionary<uint, int>(_sizes),
            new Dictionary<uint, string>(_names), lists ?? _meta.Lists));

    // ---- entities and components (tick thread) ----

    public uint Create(WorldPosition? position)
    {
        var id = _nextEntity++;
        _entities[id] = new Entity();
        if (position is { } p) SetPosition(id, p);
        return id;
    }

    public bool Destroy(uint entity)
    {
        if (!_entities.Remove(entity, out var e)) return false;
        Free(e);
        // A request still waiting for this entity saw it go: in the game an entity is gone for at least a step before its
        // id can come back (a player's), and kiln.world's watch over waiting requests sees that step.
        lock (_lock)
            foreach (var job in _pending)
                if (job.Entity == entity) job.Lost = true;
        return true;
    }

    private static void Free(Entity e)
    {
        foreach (var slot in e.Components.Values) NativeMemory.Free((void*)slot.Data);
        e.Components.Clear();
    }

    private FakePlayer? Player(uint entity) =>
        _harness.PlayersFake?.Present.FirstOrDefault(p => p.Entity.Value == entity);

    // A fake player's record ends with the player: the game removes the entity when the player leaves, and the next
    // player in the slot gets a new one under the same id.
    private void RetireDeparted()
    {
        List<uint>? gone = null;
        foreach (var (id, e) in _entities)
            if (e.Player is { Phase: FakePlayer.Stage.Left }) (gone ??= []).Add(id);
        if (gone is null) return;
        foreach (var id in gone) Destroy(id);
    }

    private Entity? Live(uint entity)
    {
        if (!_entities.TryGetValue(entity, out var e)) return null;
        if (e.Player is { Phase: FakePlayer.Stage.Left })
        {
            Destroy(entity);
            return null;
        }
        return e;
    }

    /// <summary>An entity of the fake world, or a fake player's (which stands in the world without being made here).</summary>
    public bool Exists(uint entity) => Live(entity) is not null || Player(entity) is not null;

    // The entity's record; a fake player's entity gets one at its first component.
    private Entity Need(uint entity)
    {
        if (Live(entity) is { } e) return e;
        if (Player(entity) is not { } player) throw new ArgumentException($"entity {entity} is not in the fake world (make it with Create, or join a fake player)");
        return _entities[entity] = new Entity { Player = player };
    }

    public Slot? Block(uint entity, uint hash)
    {
        if (hash == WorldAbi.CurrentTransform) FollowPlayer(entity);
        return Live(entity) is { } e && e.Components.TryGetValue(hash, out var slot) ? slot : null;
    }

    // A fake player's CurrentTransform is where the test says the player stands, as the game's movement writes it.
    private void FollowPlayer(uint entity)
    {
        if (Player(entity) is not { LivePosition: { } position }) return;
        var e = Need(entity);
        if (e.Components.TryGetValue(WorldAbi.CurrentTransform, out var slot) && slot.Size >= WorldAbi.TransformMinSize)
        {
            var now = WorldAbi.DecodeTransform(new ReadOnlySpan<byte>((void*)slot.Data, slot.Size));
            if (now.Position == position) return;
        }
        SetPosition(entity, position);
    }

    // ---- the transform: CurrentTransform's bytes are the only record of where an entity stands ----

    public WorldPosition? PositionOf(uint entity) => TransformOf(entity)?.Position;

    public WorldAbi.Decoded? TransformOf(uint entity) =>
        Block(entity, WorldAbi.CurrentTransform) is { Size: >= WorldAbi.TransformMinSize } slot
            ? WorldAbi.DecodeTransform(new ReadOnlySpan<byte>((void*)slot.Data, slot.Size))
            : null;

    /// <summary>Moves an entity: the position of its CurrentTransform (rotation and scale stay; a new one gets the identity
    /// rotation and scale 1).</summary>
    public void SetPosition(uint entity, WorldPosition position)
    {
        var e = Need(entity);
        var size = TypesFake.Find(WorldAbi.CurrentTransform)?.Size is int s and >= WorldAbi.TransformMinSize ? s : TransformSize;
        var bytes = e.Components.TryGetValue(WorldAbi.CurrentTransform, out var old) && old.Size >= WorldAbi.TransformMinSize
            ? new ReadOnlySpan<byte>((void*)old.Data, old.Size).ToArray()
            : WorldAbi.IdentityTransform(size);
        WorldAbi.EncodePosition(bytes, position);
        Set(entity, "CurrentTransform", bytes, false);
    }

    public void Set(uint entity, string name, ReadOnlySpan<byte> bytes, bool attribute)
    {
        if (bytes.Length == 0) throw new ArgumentException($"component {name} has no bytes");
        var hash = WorldAbi.Hash(name);
        // An entity of the game has only components the game has: a name Kiln.Game's types do not know is a mistake of the
        // test, not a component a mod could ever meet (and it would make an unknown type look writable).
        if (TypesFake.Available && !TypesFake.IsComponent(hash))
            throw new ArgumentException($"this game build has no component type '{name}' (Kiln.Game's types beside the tests do not name it)");
        var e = Need(entity);
        var publish = _names.TryAdd(hash, name) | (attribute && _attributes.Add(hash)) | (!_sizes.TryGetValue(hash, out var known) || known != bytes.Length);
        _sizes[hash] = bytes.Length;
        if (publish) Publish();
        if (e.Components.TryGetValue(hash, out var old) && old.Size == bytes.Length)
        {
            bytes.CopyTo(new Span<byte>((void*)old.Data, old.Size));  // the block stays where it is, as in the game
            return;
        }
        if (old.Data != 0) NativeMemory.Free((void*)old.Data);
        var data = NativeMemory.AllocZeroed((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        e.Components[hash] = new Slot((nint)data, bytes.Length);
    }

    public bool Remove(uint entity, string name)
    {
        if (Live(entity) is not { } e || !e.Components.Remove(WorldAbi.Hash(name), out var slot)) return false;
        NativeMemory.Free((void*)slot.Data);
        return true;
    }

    public byte[]? Bytes(uint entity, string name) =>
        Block(entity, WorldAbi.Hash(name)) is { } slot ? new ReadOnlySpan<byte>((void*)slot.Data, slot.Size).ToArray() : null;

    /// <summary>Every entity that has the component; null when this world has no component type of that hash (Kiln.Game's
    /// types do not name it; without them, no entity ever had it).</summary>
    public List<uint>? With(uint hash)
    {
        RetireDeparted();
        if (hash == WorldAbi.CurrentTransform)
            foreach (var p in _harness.PlayersFake?.Present ?? []) FollowPlayer(p.Entity.Value);
        var found = _entities.Where(e => e.Value.Components.ContainsKey(hash)).Select(e => e.Key).Order().ToList();
        return found.Count > 0 || TypesFake.IsComponent(hash) || (!TypesFake.Available && _names.ContainsKey(hash)) ? found : null;
    }

    private static string NameOf(Meta meta, uint hash) =>
        meta.Names.TryGetValue(hash, out var n) ? n : meta.Lists?.Names.TryGetValue(hash, out var l) == true ? l! : TypesFake.Find(hash)?.Name ?? $"component 0x{hash:x8}";

    private static bool IsAttribute(Meta meta, uint hash) => meta.Attributes.Contains(hash) || TypesFake.IsAttribute(hash);

    // ---- the game's side, scripted by the test ----

    public GameWriter AddWriter(string component, string program, Action<uint, nint, int>? each)
    {
        var hash = WorldAbi.Hash(component);
        _names.TryAdd(hash, component);
        var writer = new GameWriter { Component = hash, Program = program, Each = each };
        _writers.Add(writer);
        Publish();
        return writer;
    }

    public void RemoveWriter(GameWriter writer)
    {
        _writers.Remove(writer);
        Publish();
    }

    public void Track(string component, bool save, bool replication)
    {
        var hash = WorldAbi.Hash(component);
        _names.TryAdd(hash, component);
        _tracking[hash] = (save, replication);
        Publish();
    }

    // ---- what the mods listed ----

    // The manifests staged in kiln/mods, as kiln.world reads them when the server starts (boot_programs.cpp): the
    // drop-in folders in name order, without a folder that has a .disabled file, a client-only mod, a manifest that
    // does not read, or an id two folders share. Pure: it reads the folder and touches no state of the fake.
    private WriteLists.Result ReadLists()
    {
        var mods = new List<(string Mod, IReadOnlyList<string> Writes)>();
        if (Directory.Exists(_harness.ModsDirectory))
        {
            var read = new List<Manifest>();
            foreach (var dir in Directory.GetDirectories(_harness.ModsDirectory).Order(StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(dir, ".disabled"))) continue;
                try
                {
                    read.Add(Manifest.Read(dir));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // the loader reports it; kiln.world takes nothing of it
                }
            }
            var shared = read.GroupBy(m => m.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var m in read)
                if (m.Side != ManifestSide.Client && m.Writes.Count > 0 && !shared.Contains(m.Id)) mods.Add((m.Id, m.Writes));
        }
        return WriteLists.Admit(mods, TypesFake.Available ? TypesFake.IsComponent : null);
    }

    private uint StateFor(Meta meta, string mod, uint hash)
    {
        var lists = meta.Lists ?? ReadLists();
        if (!lists.Types.TryGetValue(hash, out var state)) return WorldAbi.StateNotDeclared;
        if (mod.Length > 0 && !(lists.Listed.TryGetValue(mod, out var own) && own.Contains(hash))) return WorldAbi.NotListed;
        // before the world: listed names wait (which of them the program takes is the world's to say)
        return meta.Lists is null && state is WorldAbi.Ready or WorldAbi.OverLimit or WorldAbi.Unknown ? WorldAbi.Pending : state;
    }

    private static string Refusal(uint state, string component, string mod) => state switch
    {
        WorldAbi.StateNotDeclared =>
            "no loaded mod lists this component under \"writes\" in its kiln.mod.json: add it there (Kiln.Sdk does that for a mod whose " +
            "code calls World.Write or World.Update with the type) and restart the server; the game fixes a program's access when the world starts",
        WorldAbi.NotListed =>
            $"mod '{mod}' does not list '{component}' under \"writes\" in its kiln.mod.json (another mod does): add \"writes\": [\"{component}\"] " +
            "and restart the server",
        WorldAbi.Unknown => $"this game build has no component type '{component}' (listed under \"writes\"; check the name)",
        WorldAbi.OverLimit =>
            $"'{component}' was listed after the write program's {WorldAbi.MaxTypes} component types were taken (the game's limit of one " +
            "program); fewer mods or fewer \"writes\" entries are needed",
        WorldAbi.Pending => "no server world runs yet: the write program has not been registered",
        _ => "tracked writes are off",
    };

    private static string Lost(uint entity, string component) =>
        $"entity {entity} lost its {component} while the write waited (it was removed, or removed and made again under the same id, as a " +
        "player's entity is when the slot is taken by the next player): nothing was written";

    // ---- the queue ----

    public int Submit(ulong owner, WorldAbi.WriteRequest* r, ulong* ticket)
    {
        var meta = Volatile.Read(ref _meta);
        var job = new Job
        {
            Owner = owner, Entity = r->Entity, Component = r->Component, Flags = r->Flags, Change = r->Change, Callback = r->Callback,
            User = r->User, Mod = r->ModId.Ptr == null ? "" : r->ModId.ToString(), Submitted = _steps,
        };
        if (r->Data != null)
        {
            job.Offset = r->Offset;
            job.Length = r->Length;
            job.Data = new ReadOnlySpan<byte>(r->Data, (int)r->Length).ToArray();
        }
        var state = StateFor(meta, job.Mod, job.Component);
        lock (_lock)
        {
            // every request not delivered yet counts, refusals included (write_queue.h kWriteMaxPending)
            if (_pending.Count + _checking.Count + _done.Count >= WorldAbi.MaxPending) return FakeTables.BufferTooSmall;
            job.Ticket = ++_lastTicket;
            if (ticket != null) *ticket = job.Ticket;
            _submitted++;
            if (state != WorldAbi.Ready)
            {
                // known now: it only waits for its delivery. Pending: no world yet, so no entity id can be one of its.
                job.Status = state is WorldAbi.Pending or WorldAbi.Off ? WorldAbi.Dropped : WorldAbi.NotDeclared;
                job.Reason = Refusal(state, NameOf(meta, job.Component), job.Mod);
                _failed++;
                _done.Add(job);
            }
            else _pending.Add(job);
        }
        return FakeTables.Ok;
    }

    public bool Cancel(ulong ticket)
    {
        lock (_lock)
        {
            if (_pending.RemoveAll(j => j.Ticket == ticket) > 0) return true;
            foreach (var job in _checking.Concat(_done))
                if (job.Ticket == ticket && job.Callback != null)
                {
                    job.Callback = null;
                    return true;
                }
            return _delivering.Remove(ticket);
        }
    }

    // Any thread: only the published metadata, never the entities or the lists the tick thread changes.
    public void Info(string mod, uint hash, WorldAbi.WriteInfo* output)
    {
        var meta = Volatile.Read(ref _meta);
        output->State = StateFor(meta, mod, hash);
        var writers = meta.Writers.TryGetValue(hash, out var w) ? w : [];
        output->Writers = (uint)writers.Length;
        output->Flags = FlagsOf(meta, hash);
        output->ComponentSize = (uint)(TypesFake.Find(hash)?.Size ?? (meta.Sizes.TryGetValue(hash, out var size) ? size : 0));
        var names = Encoding.UTF8.GetBytes(string.Join(", ", writers));
        names.AsSpan(0, Math.Min(names.Length, 231)).CopyTo(new Span<byte>(output->WriterNames, 231));
    }

    private static uint FlagsOf(Meta meta, uint hash) =>
        (meta.Writers.ContainsKey(hash) ? WorldAbi.GameWrites : 0) |
        (IsAttribute(meta, hash) ? WorldAbi.Attribute : 0) |
        (meta.Tracking.TryGetValue(hash, out var t) ? WorldAbi.BitsKnown | (t.Save ? WorldAbi.Saved : 0) | (t.Replication ? WorldAbi.Replicated : 0) : 0);

    public void Status(WorldAbi.WriteStatus* output)
    {
        var meta = Volatile.Read(ref _meta);
        var types = (meta.Lists ?? ReadLists()).Types;
        output->Program = meta.Lists is null ? WorldAbi.ProgramPending : WorldAbi.ProgramActive;
        output->Available = 1;
        output->Running = meta.Lists is null ? (byte)0 : (byte)1;
        output->Bits = meta.Tracking.Count > 0 ? (byte)1 : (byte)0;
        output->Declared = (uint)types.Values.Count(s => s == WorldAbi.Ready);
        output->Refused = (uint)types.Values.Count(s => s != WorldAbi.Ready);
        lock (_lock)
        {
            output->Pending = (uint)(_pending.Count + _checking.Count);
            output->Submitted = _submitted;
            output->Written = _written;
            output->Unchanged = _unchanged;
            output->Failed = _failed;
        }
        output->Updates = _steps;
    }

    // ---- one step of the world, at the start of the harness's next step (the native services' Pre) ----

    /// <summary>What the game did since the last step, in its order: its own programs' writes, the checks of the writes
    /// Kiln made one step before, Kiln's write program (last in the step: at most 512 requests, the rest wait and are
    /// watched), then the answers that are due (at most 1024 a step).</summary>
    public void BeforeStep(ulong step)
    {
        if (_meta.Lists is null) Boot();
        _steps = step;
        RetireDeparted();
        foreach (var p in _harness.PlayersFake?.Present ?? []) FollowPlayer(p.Entity.Value);
        foreach (var writer in _writers.ToArray())
            if (writer.Each is { } each)
                foreach (var (id, e) in _entities.ToArray())
                    if (e.Components.TryGetValue(writer.Component, out var slot)) each(id, slot.Data, slot.Size);
        Job[] due, run;
        lock (_lock)
        {
            due = [.. _checking];
            _checking.Clear();
            run = [.. _pending.Take(WorldAbi.PerUpdate)];
            _pending.RemoveRange(0, run.Length);
        }
        var meta = _meta;
        foreach (var job in due)
        {
            job.OutFlags |= WorldAbi.Checked;
            if (Block(job.Entity, job.Component) is { } now && job.Offset <= now.Size && job.Length <= now.Size - job.Offset &&
                new ReadOnlySpan<byte>((byte*)now.Data + job.Offset, (int)job.Length).SequenceEqual(job.After))
                job.OutFlags |= WorldAbi.Kept;
            Complete(job, step);
        }
        foreach (var job in run)
        {
            var held = Run(meta, job);
            lock (_lock) _record.Add(new FakeWorldWrite(step, job.Mod.Length == 0 ? null : job.Mod, new EntityId(job.Entity), NameOf(meta, job.Component),
                (int)job.Offset, (WorldWriteStatus)job.Status, job.Before ?? [], job.After ?? []));
            if (held)
                lock (_lock) _checking.Add(job);
            else Complete(job, step);
        }
        // What still waits: an entity that lacks its component now may be gone, and its id may name another entity by the
        // time the request runs (write_queue.h WriteQueue::watch).
        lock (_lock)
            foreach (var job in _pending)
                if (!job.Lost && Block(job.Entity, job.Component) is null) job.Lost = true;
        Deliver(step);
    }

    private void Boot()
    {
        var lists = ReadLists();
        Publish(lists);
        if (lists.Types.Count > 0)
            KitBackend.Instance.Log(0, LogLevel.Info,
                $"testkit: tracked writes: {lists.Types.Values.Count(s => s == WorldAbi.Ready)} component type(s) can be written " +
                $"({string.Join(", ", lists.Listed.Select(l => $"{l.Key}: {string.Join(" ", l.Value.Select(h => lists.Names[h]))}"))}), " +
                $"{lists.Types.Values.Count(s => s != WorldAbi.Ready)} listed name(s) refused");
    }

    private void Complete(Job job, ulong step)
    {
        lock (_lock)
        {
            job.Steps = (uint)(step - job.Submitted);
            if (job.Status == WorldAbi.Done) _written++;
            else if (job.Status == WorldAbi.Unchanged) _unchanged++;
            else _failed++;
            _done.Add(job);
        }
    }

    // write_queue.h write_run, on the fake's blocks. True: the job waits for its check one step later.
    private bool Run(Meta meta, Job job)
    {
        bool Fail(uint status, string reason)
        {
            job.Status = status;
            job.Reason = reason;
            return false;
        }
        var name = NameOf(meta, job.Component);
        var state = StateFor(meta, job.Mod, job.Component);
        if (state != WorldAbi.Ready)
            return Fail(state is WorldAbi.Pending or WorldAbi.Off ? WorldAbi.Dropped : WorldAbi.NotDeclared, Refusal(state, name, job.Mod));
        job.OutFlags = FlagsOf(meta, job.Component);
        job.Writers = (uint)(meta.Writers.TryGetValue(job.Component, out var w) ? w.Length : 0);
        if (job.Lost) return Fail(WorldAbi.NoComponent, Lost(job.Entity, name));
        if (Block(job.Entity, job.Component) is not { } slot)
            return Fail(WorldAbi.NoComponent, $"entity {job.Entity} has no {name} (or does not exist)");
        var now = new Span<byte>((void*)slot.Data, slot.Size);
        job.ComponentSize = (uint)slot.Size;
        byte[] next;
        if (job.Change != null)
        {
            if (slot.Size > WorldAbi.MaxComponent)
                return Fail(WorldAbi.OutOfRange, $"{name} is {slot.Size} bytes; a change callback takes components up to {WorldAbi.MaxComponent} (write a field instead)");
            job.Offset = 0;
            job.Length = (uint)slot.Size;
            next = now.ToArray();
            byte accepted;
            fixed (byte* copy = next) accepted = job.Change(job.Entity, copy, (uint)slot.Size, job.User);
            if (accepted == 0)
            {
                job.Before = now.ToArray();
                job.After = job.Before;
                return Fail(WorldAbi.Refused, "the change callback returned false; the component is as it was");
            }
        }
        else
        {
            if ((job.Flags & WorldAbi.Whole) != 0 && (job.Offset != 0 || job.Length != slot.Size))
                return Fail(WorldAbi.OutOfRange, $"the value is {job.Length} bytes and {name} of entity {job.Entity} is {slot.Size}: the layout is not this game build's");
            if (job.Offset > slot.Size || job.Length > slot.Size - job.Offset)
                return Fail(WorldAbi.OutOfRange, $"bytes {job.Offset}..{job.Offset + job.Length} are outside {name} ({slot.Size} bytes)");
            next = job.Data!;
        }
        var range = now.Slice((int)job.Offset, (int)job.Length);
        job.Before = range.ToArray();
        job.After = job.Before;
        if (range.SequenceEqual(next))
        {
            job.Status = WorldAbi.Unchanged;
            return false;
        }
        if (IsAttribute(meta, job.Component) && job.Offset < WorldAbi.AttributeHeader)
        {
            var n = (int)Math.Min(job.Length, WorldAbi.AttributeHeader - job.Offset);
            if (!range[..n].SequenceEqual(next.AsSpan(0, n)))
                return Fail(WorldAbi.Refused, $"{name} is an attribute: its first {WorldAbi.AttributeHeader} bytes (group, values offset, flags, count) are the " +
                                              "game's and cannot change; change the values of a copy read from the entity (World.Update)");
        }
        next.CopyTo(range);  // the game's attribute formulas (a clamp to the maximum) do not run offline
        job.After = range.ToArray();
        job.Status = WorldAbi.Done;
        return (job.Flags & WorldAbi.Verify) != 0 && job.Writers > 0;
    }

    // write_queue.h drain + write_deliver: a bounded share, each callback checked right before it runs (a callback before
    // it may have cancelled it).
    private void Deliver(ulong step)
    {
        Job[] batch;
        lock (_lock)
        {
            batch = [.. _done.Take(WorldAbi.DeliverPerPre)];
            _done.RemoveRange(0, batch.Length);
            _delivering.Clear();
            foreach (var job in batch)
                if (job.Callback != null) _delivering[job.Ticket] = job.Owner;
        }
        foreach (var job in batch)
        {
            bool wanted;
            lock (_lock) wanted = _delivering.ContainsKey(job.Ticket);
            if (job.Callback == null || !wanted) continue;
            var reason = Encoding.UTF8.GetBytes(job.Reason);
            fixed (byte* before = job.Before, after = job.After, text = reason)
            {
                WorldAbi.WriteResult r;
                r.Size = (uint)sizeof(WorldAbi.WriteResult);
                r.Status = job.Status;
                r.Ticket = job.Ticket;
                r.Entity = job.Entity;
                r.Component = job.Component;
                r.Offset = job.Offset;
                r.Length = job.Length;
                r.ComponentSize = job.ComponentSize;
                r.Flags = job.OutFlags;
                r.Steps = job.Steps;
                r.Writers = job.Writers;
                r.Before = job.Before is null ? null : before;
                r.After = job.After is null ? null : after;
                r.Reason = new NativeStr { Ptr = text, Len = (nuint)reason.Length };
                job.Callback(&r, job.User);
            }
        }
        lock (_lock) _delivering.Clear();
    }

    public void Dispose()
    {
        foreach (var e in _entities.Values) Free(e);
        _entities.Clear();
        lock (_lock)
        {
            _pending.Clear();
            _checking.Clear();
            _done.Clear();
            _delivering.Clear();
        }
    }
}
