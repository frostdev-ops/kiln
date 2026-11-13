// SPDX-License-Identifier: MIT
// The state behind the fake kiln.world: entities, their components as blocks of native memory, and tracked writes with
// the native service's own rules (native/services/world/write_queue.h): what the mods' manifests list, the queue, one
// write, the check one step later, the answers.
using System.Runtime.InteropServices;
using System.Text;
using Kiln.Host;

namespace Kiln.TestKit;

internal sealed unsafe class EntitiesFake : IDisposable
{
    internal readonly record struct Slot(nint Data, int Size);

    private sealed class Entity
    {
        public WorldPosition? Position;
        public readonly Dictionary<uint, Slot> Components = [];
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

    private readonly KilnHarness _harness;
    private readonly object _lock = new();
    private readonly Dictionary<uint, Entity> _entities = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly HashSet<uint> _attributes = [];
    private readonly Dictionary<uint, (bool Save, bool Replication)> _tracking = [];
    private readonly List<GameWriter> _writers = [];
    private readonly List<FakeWorldWrite> _record = [];
    private readonly List<Job> _pending = [], _checking = [], _done = [];
    private Dictionary<string, HashSet<uint>>? _listed;      // by mod id, fixed at the first step (the server's boot)
    private readonly Dictionary<uint, uint> _programTypes = [];  // listed type -> _READY, _UNKNOWN or _OVER_LIMIT
    private uint _nextEntity = 2000;
    private ulong _lastTicket, _steps, _submitted, _written, _unchanged, _failed;

    public EntitiesFake(KilnHarness harness) => _harness = harness;

    /// <summary>The first step ran: the server world exists and the lists are fixed.</summary>
    public bool Started => _listed is not null;

    public ulong Steps => _steps;

    public int Count => _entities.Count;

    public IReadOnlyList<FakeWorldWrite> Record
    {
        get
        {
            lock (_lock) return [.. _record];
        }
    }

    public IReadOnlyList<uint> Entities => [.. _entities.Keys.Order()];

    // ---- entities and components (tick thread) ----

    public uint Create(WorldPosition? position)
    {
        var id = _nextEntity++;
        _entities[id] = new Entity { Position = position };
        return id;
    }

    public bool Destroy(uint entity)
    {
        if (!_entities.Remove(entity, out var e)) return false;
        foreach (var slot in e.Components.Values) NativeMemory.Free((void*)slot.Data);
        return true;
    }

    private FakePlayer? Player(uint entity) =>
        _harness.PlayersFake?.Present.FirstOrDefault(p => p.Entity.Value == entity);

    /// <summary>An entity of the fake world, or a fake player's (which stands in the world without being made here).</summary>
    public bool Exists(uint entity) => _entities.ContainsKey(entity) || Player(entity) is not null;

    public WorldPosition? PositionOf(uint entity) =>
        _entities.TryGetValue(entity, out var e) && e.Position is { } p ? p : Player(entity)?.Position;

    public void SetPosition(uint entity, WorldPosition position) => Need(entity).Position = position;

    // The entity's record; a fake player's entity gets one at its first component.
    private Entity Need(uint entity)
    {
        if (_entities.TryGetValue(entity, out var e)) return e;
        if (Player(entity) is null) throw new ArgumentException($"entity {entity} is not in the fake world (make it with Create, or join a fake player)");
        return _entities[entity] = new Entity();
    }

    public Slot? Block(uint entity, uint hash) =>
        _entities.TryGetValue(entity, out var e) && e.Components.TryGetValue(hash, out var slot) ? slot : null;

    public void Set(uint entity, string name, ReadOnlySpan<byte> bytes, bool attribute)
    {
        if (bytes.Length == 0) throw new ArgumentException($"component {name} has no bytes");
        var hash = WorldAbi.Hash(name);
        var e = Need(entity);
        _names[hash] = name;
        if (attribute) _attributes.Add(hash);
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
        if (!_entities.TryGetValue(entity, out var e) || !e.Components.Remove(WorldAbi.Hash(name), out var slot)) return false;
        NativeMemory.Free((void*)slot.Data);
        return true;
    }

    public byte[]? Bytes(uint entity, string name) =>
        Block(entity, WorldAbi.Hash(name)) is { } slot ? new ReadOnlySpan<byte>((void*)slot.Data, slot.Size).ToArray() : null;

    /// <summary>Every entity that has the component; null when this world has no component type of that hash (no entity
    /// ever had it and Kiln.Game does not name it).</summary>
    public List<uint>? With(uint hash)
    {
        var found = _entities.Where(e => e.Value.Components.ContainsKey(hash)).Select(e => e.Key).Order().ToList();
        return found.Count > 0 || _names.ContainsKey(hash) || TypesFake.IsComponent(hash) ? found : null;
    }

    private string NameOf(uint hash) => _names.TryGetValue(hash, out var n) ? n : TypesFake.Find(hash)?.Name ?? $"component 0x{hash:x8}";

    private bool IsAttribute(uint hash) => _attributes.Contains(hash) || TypesFake.IsAttribute(hash);

    // ---- the game's side, scripted by the test ----

    public GameWriter AddWriter(string component, string program, Action<uint, nint, int>? each)
    {
        var hash = WorldAbi.Hash(component);
        _names[hash] = component;
        var writer = new GameWriter { Component = hash, Program = program, Each = each };
        _writers.Add(writer);
        return writer;
    }

    public void RemoveWriter(GameWriter writer) => _writers.Remove(writer);

    public void Track(string component, bool save, bool replication)
    {
        var hash = WorldAbi.Hash(component);
        _names[hash] = component;
        _tracking[hash] = (save, replication);
    }

    // ---- what the mods listed ----

    // The manifests staged in kiln/mods, as kiln.world reads them when the server starts (boot_programs.cpp): every
    // server mod's "writes", folders in name order; the first 92 types are the write program's.
    private (Dictionary<string, HashSet<uint>> Listed, Dictionary<uint, uint> Types) ReadLists()
    {
        var listed = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        var types = new Dictionary<uint, uint>();
        if (!Directory.Exists(_harness.ModsDirectory)) return (listed, types);
        foreach (var dir in Directory.GetDirectories(_harness.ModsDirectory).Order(StringComparer.Ordinal))
        {
            Manifest manifest;
            try
            {
                manifest = Manifest.Read(dir);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;  // the loader reports it
            }
            if (manifest.Side == ManifestSide.Client || manifest.Writes.Count == 0) continue;
            var set = listed[manifest.Id] = [];
            foreach (var name in manifest.Writes.Take(WorldAbi.MaxPerMod))
            {
                var hash = WorldAbi.Hash(name);
                set.Add(hash);
                _names.TryAdd(hash, name);
                if (types.ContainsKey(hash)) continue;
                // A name Kiln.Game does not know as a component is "unknown" only when the kit has its type table.
                types[hash] = TypesFake.Available && !TypesFake.IsComponent(hash) && !_entities.Values.Any(e => e.Components.ContainsKey(hash)) ? WorldAbi.Unknown
                    : types.Values.Count(s => s == WorldAbi.Ready) >= WorldAbi.MaxTypes ? WorldAbi.OverLimit
                    : WorldAbi.Ready;
            }
        }
        return (listed, types);
    }

    private uint StateFor(string mod, uint hash)
    {
        var (listed, types) = _listed is null ? ReadLists() : (_listed, _programTypes);
        if (!types.TryGetValue(hash, out var state)) return WorldAbi.StateNotDeclared;
        if (mod.Length > 0 && !(listed.TryGetValue(mod, out var own) && own.Contains(hash))) return WorldAbi.NotListed;
        return state == WorldAbi.Ready && _listed is null ? WorldAbi.Pending : state;
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

    // ---- the queue ----

    public int Submit(ulong owner, WorldAbi.WriteRequest* r, ulong* ticket)
    {
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
        lock (_lock)
        {
            if (_pending.Count + _checking.Count >= WorldAbi.MaxPending) return FakeTables.BufferTooSmall;
            job.Ticket = ++_lastTicket;
            if (ticket != null) *ticket = job.Ticket;
            _submitted++;
            var state = StateFor(job.Mod, job.Component);
            if (state is not (WorldAbi.Ready or WorldAbi.Pending))
            {
                // known now: it only waits for its delivery
                job.Status = WorldAbi.NotDeclared;
                job.Reason = Refusal(state, NameOf(job.Component), job.Mod);
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
            return false;
        }
    }

    public void Info(string mod, uint hash, WorldAbi.WriteInfo* output)
    {
        output->State = StateFor(mod, hash);
        var writers = _writers.Where(w => w.Component == hash).Select(w => w.Program).ToList();
        output->Writers = (uint)writers.Count;
        output->Flags = FlagsOf(hash);
        output->ComponentSize = (uint)(TypesFake.Find(hash)?.Size ?? _entities.Values.Select(e => e.Components.TryGetValue(hash, out var s) ? s.Size : 0).FirstOrDefault(n => n > 0));
        var names = Encoding.UTF8.GetBytes(string.Join(", ", writers));
        names.AsSpan(0, Math.Min(names.Length, 231)).CopyTo(new Span<byte>(output->WriterNames, 231));
    }

    private uint FlagsOf(uint hash) =>
        (_writers.Any(w => w.Component == hash) ? WorldAbi.GameWrites : 0) |
        (IsAttribute(hash) ? WorldAbi.Attribute : 0) |
        (_tracking.TryGetValue(hash, out var t) ? WorldAbi.BitsKnown | (t.Save ? WorldAbi.Saved : 0) | (t.Replication ? WorldAbi.Replicated : 0) : 0);

    public void Status(WorldAbi.WriteStatus* output)
    {
        var types = _listed is null ? ReadLists().Types : _programTypes;
        output->Program = _listed is null ? WorldAbi.ProgramPending : WorldAbi.ProgramActive;
        output->Available = 1;
        output->Running = _listed is null ? (byte)0 : (byte)1;
        output->Bits = _tracking.Count > 0 ? (byte)1 : (byte)0;
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
    /// Kiln made one step before, Kiln's write program (last in the step), then the answers that are due.</summary>
    public void BeforeStep(ulong step)
    {
        _listed ??= Boot();
        _steps = step;
        foreach (var writer in _writers.ToArray())
            if (writer.Each is { } each)
                foreach (var (id, e) in _entities.ToArray())
                    if (e.Components.TryGetValue(writer.Component, out var slot)) each(id, slot.Data, slot.Size);
        Job[] due, run, done;
        lock (_lock)
        {
            due = [.. _checking];
            _checking.Clear();
            run = [.. _pending];
            _pending.Clear();
        }
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
            var held = Run(job);
            lock (_lock) _record.Add(new FakeWorldWrite(step, job.Mod.Length == 0 ? null : job.Mod, new EntityId(job.Entity), NameOf(job.Component),
                (int)job.Offset, (WorldWriteStatus)job.Status, job.Before ?? [], job.After ?? []));
            if (held)
                lock (_lock) _checking.Add(job);
            else Complete(job, step);
        }
        lock (_lock)
        {
            done = [.. _done];
            _done.Clear();
        }
        foreach (var job in done) Deliver(job, step);
    }

    private Dictionary<string, HashSet<uint>> Boot()
    {
        var (listed, types) = ReadLists();
        foreach (var (hash, state) in types) _programTypes[hash] = state;
        if (types.Count > 0)
            KitBackend.Instance.Log(0, LogLevel.Info,
                $"testkit: tracked writes: {types.Values.Count(s => s == WorldAbi.Ready)} component type(s) can be written " +
                $"({string.Join(", ", listed.Select(l => $"{l.Key}: {string.Join(" ", l.Value.Select(NameOf))}"))}), " +
                $"{types.Values.Count(s => s != WorldAbi.Ready)} listed name(s) refused");
        return listed;
    }

    private void Complete(Job job, ulong step)
    {
        lock (_lock)
        {
            if (job.Status == WorldAbi.Done) _written++;
            else if (job.Status == WorldAbi.Unchanged) _unchanged++;
            else _failed++;
            _done.Add(job);
        }
    }

    // write_queue.h write_run, on the fake's blocks. True: the job waits for its check one step later.
    private bool Run(Job job)
    {
        bool Fail(uint status, string reason)
        {
            job.Status = status;
            job.Reason = reason;
            return false;
        }
        var name = NameOf(job.Component);
        var state = StateFor(job.Mod, job.Component);
        if (state != WorldAbi.Ready)
            return Fail(state == WorldAbi.Pending ? WorldAbi.Dropped : WorldAbi.NotDeclared, Refusal(state, name, job.Mod));
        job.OutFlags = FlagsOf(job.Component);
        job.Writers = (uint)_writers.Count(w => w.Component == job.Component);
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
        if (IsAttribute(job.Component) && job.Offset < WorldAbi.AttributeHeader)
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

    private void Deliver(Job job, ulong step)
    {
        if (job.Callback == null) return;
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
            r.Steps = (uint)(step - job.Submitted);
            r.Writers = job.Writers;
            r.Before = job.Before is null ? null : before;
            r.After = job.After is null ? null : after;
            r.Reason = new NativeStr { Ptr = text, Len = (nuint)reason.Length };
            job.Callback(&r, job.User);
        }
    }

    public void Dispose()
    {
        foreach (var e in _entities.Values)
            foreach (var slot in e.Components.Values) NativeMemory.Free((void*)slot.Data);
        _entities.Clear();
        lock (_lock)
        {
            _pending.Clear();
            _checking.Clear();
            _done.Clear();
        }
    }
}
