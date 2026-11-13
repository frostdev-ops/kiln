// SPDX-License-Identifier: MIT
// The fake kiln.world (native/core/abi/services/kiln_world.h, v3) under the real Kiln.World: entity access and tracked
// writes answer from the running harness's FakeWorld; programs and teleport are not modelled and say so.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_world.h</c> v3. Its sizes are pinned by tests to the header's numbers (the native host test
/// asserts the same ones) and to Kiln.World's own private mirrors.</summary>
internal static unsafe class WorldAbi
{
    public const string Service = "kiln.world";
    public const uint Version = 3;
    public const uint CapTrackedWrites = 1u << 3;

    // KilnWorldWriteResult.status
    public const uint Done = 0, Unchanged = 1, NoComponent = 2, OutOfRange = 3, NotDeclared = 4, Refused = 5, Faulted = 6, Dropped = 7;
    // KilnWorldWriteRequest.flags
    public const uint Whole = 0x01, Verify = 0x02;
    // KilnWorldWriteResult.flags / KilnWorldWriteInfo.flags
    public const uint GameWrites = 0x01, BitsKnown = 0x02, Saved = 0x04, Replicated = 0x08, Attribute = 0x10, Checked = 0x20, Kept = 0x40;
    // KilnWorldWriteInfo.state
    public const uint Ready = 0, Pending = 1, StateNotDeclared = 2, NotListed = 3, Unknown = 4, OverLimit = 5, Off = 6, ListRefused = 7;
    // KilnWorldWriteInfo.flags only
    public const uint NotReady = 0x80;
    // KILN_PROGRAM_* (KilnWorldWriteStatus.program)
    public const uint ProgramPending = 0, ProgramActive = 1;
    public const int MaxBytes = 4096, MaxComponent = 65536, MaxTypes = 91, MaxPerMod = 64, MaxPending = 4096, AttributeHeader = 8;
    public const uint SpawnTime = 0x583963a3u;  // keen::ecs::SpawnTime: an entity's generation, 8 bytes
    public const int PerUpdate = 512, DeliverPerPre = 1024;  // write_queue.h kWritePerUpdate, kWriteDeliverPerPre

    // keen::ecs::CurrentTransform (native/services/world/transform_layout.h): i64 x, y, z in metres as 32.32 fixed point at
    // 0, a quaternion at 0x18, a scale at 0x28; 52 bytes at least.
    public const uint CurrentTransform = 0x027ac564u;
    public const int TransformMinSize = 52;

    public readonly record struct Decoded(WorldPosition Position, float RX, float RY, float RZ, float RW, float SX, float SY, float SZ);

    public static Decoded DecodeTransform(ReadOnlySpan<byte> b) => new(
        new WorldPosition(BitConverter.ToInt64(b) / 4294967296.0, BitConverter.ToInt64(b[8..]) / 4294967296.0, BitConverter.ToInt64(b[16..]) / 4294967296.0),
        BitConverter.ToSingle(b[0x18..]), BitConverter.ToSingle(b[0x1C..]), BitConverter.ToSingle(b[0x20..]), BitConverter.ToSingle(b[0x24..]),
        BitConverter.ToSingle(b[0x28..]), BitConverter.ToSingle(b[0x2C..]), BitConverter.ToSingle(b[0x30..]));

    public static byte[] IdentityTransform(int size)
    {
        var b = new byte[size];
        BitConverter.TryWriteBytes(b.AsSpan(0x24), 1f);   // rotation w
        for (var i = 0; i < 3; i++) BitConverter.TryWriteBytes(b.AsSpan(0x28 + 4 * i), 1f);
        return b;
    }

    public static void EncodePosition(Span<byte> b, WorldPosition p)
    {
        BitConverter.TryWriteBytes(b, (long)Math.Round(p.X * 4294967296.0));
        BitConverter.TryWriteBytes(b[8..], (long)Math.Round(p.Y * 4294967296.0));
        BitConverter.TryWriteBytes(b[16..], (long)Math.Round(p.Z * 4294967296.0));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Transform
    {
        public Vec3 Position;
        public fixed float Rotation[4];
        public fixed float Scale[3];
        public float Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Programs, World, Teleport, Find;
        public uint KilnPrograms, EnginePrograms, EngineMax, TeleportsPending;
        public ulong WorldSteps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WriteRequest
    {
        public uint Size, Flags, Entity, Component, Offset, Length;
        public void* Data;
        public delegate* unmanaged[Cdecl]<uint, void*, uint, void*, byte> Change;
        public delegate* unmanaged[Cdecl]<WriteResult*, void*, void> Callback;
        public void* User;
        public NativeStr ModId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WriteResult
    {
        public uint Size, Status;
        public ulong Ticket;
        public uint Entity, Component, Offset, Length, ComponentSize, Flags, Steps, Writers;
        public void* Before, After;
        public NativeStr Reason;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WriteInfo
    {
        public uint Size, State, Flags, Writers, InitWriters, ComponentSize;
        public fixed byte WriterNames[232];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WriteStatus
    {
        public uint Size, Program;
        public byte Available, Running, Attributes, Bits;
        public uint Declared, Refused, Pending, Reserved;
        public ulong Submitted, Written, Unchanged, Failed, Updates;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        // programs: not modelled
        public delegate* unmanaged[Cdecl]<ulong, void*, ulong*, int> RegisterProgram;
        public delegate* unmanaged[Cdecl]<ulong, int, NativeStr, int> ProgramSlot;
        public delegate* unmanaged[Cdecl]<ulong, void*, int> ProgramInfo;
        public delegate* unmanaged[Cdecl]<void*, ulong*, uint, int> IterBegin;
        public delegate* unmanaged[Cdecl]<void*, ulong*, uint, byte> IterNext;
        public delegate* unmanaged[Cdecl]<void*, uint> CurrentEntity;
        public delegate* unmanaged[Cdecl]<void*, void*> CommandBuffer;
        public delegate* unmanaged[Cdecl]<void*, byte, uint, uint, void*> QueuePush;
        public delegate* unmanaged[Cdecl]<void*, uint, uint, void*, uint, int> QueueAdd;
        public delegate* unmanaged[Cdecl]<void*, uint, uint, int> QueueRemove;
        public delegate* unmanaged[Cdecl]<void*, ulong*, void> MarkWritten;
        public delegate* unmanaged[Cdecl]<void*, ulong, uint, uint*, void*> ReadOther;
        // entities
        public delegate* unmanaged[Cdecl]<NativeStr, uint> ComponentHash;
        public delegate* unmanaged[Cdecl]<uint, uint, uint*, void*> Component;
        public delegate* unmanaged[Cdecl]<uint, byte> Exists;
        public delegate* unmanaged[Cdecl]<uint, Transform*, int> GetTransform;
        public delegate* unmanaged[Cdecl]<uint, Vec3*, int> GetPosition;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> Find;
        public delegate* unmanaged[Cdecl]<uint> EntityCount;
        public delegate* unmanaged[Cdecl]<nuint> ServerWorld;
        // teleport: not modelled
        public delegate* unmanaged[Cdecl]<ulong, void*, ulong*, int> Teleport;
        public delegate* unmanaged[Cdecl]<ulong, int> TeleportCancel;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        // v1, v2 appended: not modelled
        public delegate* unmanaged[Cdecl]<void*, ulong, uint, uint*, void*> WriteOther;
        public delegate* unmanaged[Cdecl]<ulong, int> UnregisterProgram;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, NativeStr, void*, void*, ulong*, int> BindProgram;
        public delegate* unmanaged[Cdecl]<uint> Capabilities;
        public delegate* unmanaged[Cdecl]<void*, NativeStr, void*, void*, int> ProgramEvents;
        public delegate* unmanaged[Cdecl]<void*, NativeStr, void*, uint, int> ProgramEmit;
        // v3: tracked writes
        public delegate* unmanaged[Cdecl]<ulong, WriteRequest*, ulong*, int> WriteSubmit;
        public delegate* unmanaged[Cdecl]<ulong, int> WriteCancel;
        public delegate* unmanaged[Cdecl]<NativeStr, uint, WriteInfo*, int> WriteInfoOf;
        public delegate* unmanaged[Cdecl]<WriteStatus*, int> GetWriteStatus;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, NativeStr*, uint, int> WriteDeclare;
    }

    /// <summary>FNV-1a-32 of "keen::ecs::" + name (the name as given when it contains "::"): the engine's component hash.</summary>
    public static uint Hash(ReadOnlySpan<byte> name)
    {
        var h = 0x811c9dc5u;
        if (name.IndexOf("::"u8) < 0)
            foreach (var b in "keen::ecs::"u8) h = (h ^ b) * 0x01000193u;
        foreach (var b in name) h = (h ^ b) * 0x01000193u;
        return h;
    }

    public static uint Hash(string name) => Hash(Encoding.UTF8.GetBytes(name));
}

/// <summary>The C# functions behind the fake <c>kiln.world</c> table. Modelled: component_hash, component, exists,
/// transform, position, find, entity_count, server_world, status, capabilities, and the tracked writes (write_submit,
/// write_cancel, write_info, write_status). Unavailable, each with the native service's own "no": program registration and
/// everything inside a program update (there is no scheduler), teleport, write_declare (a native service's list).</summary>
internal static unsafe class WorldTable
{
    private static WorldAbi.Table* _table;

#pragma warning disable CA2255  // a test kit: the table must be published before any Kiln service asks for one
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ModuleInit() => KitBackend.Instance.RegisterTable(WorldAbi.Service, Create());

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (WorldAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(WorldAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(WorldAbi.Table), Version = WorldAbi.Version };
        t->RegisterProgram = &RegisterProgram;
        t->ProgramSlot = &ProgramSlot;
        t->ProgramInfo = &ProgramInfo;
        t->IterBegin = &IterBegin;
        t->IterNext = &IterNext;
        t->CurrentEntity = &CurrentEntity;
        t->CommandBuffer = &CommandBuffer;
        t->QueuePush = &QueuePush;
        t->QueueAdd = &QueueAdd;
        t->QueueRemove = &QueueRemove;
        t->MarkWritten = &MarkWritten;
        t->ReadOther = &ReadOther;
        t->ComponentHash = &ComponentHash;
        t->Component = &Component;
        t->Exists = &Exists;
        t->GetTransform = &GetTransform;
        t->GetPosition = &GetPosition;
        t->Find = &Find;
        t->EntityCount = &EntityCount;
        t->ServerWorld = &ServerWorld;
        t->Teleport = &Teleport;
        t->TeleportCancel = &TeleportCancel;
        t->GetStatus = &GetStatus;
        t->WriteOther = &WriteOther;
        t->UnregisterProgram = &UnregisterProgram;
        t->BindProgram = &BindProgram;
        t->Capabilities = &Capabilities;
        t->ProgramEvents = &ProgramEvents;
        t->ProgramEmit = &ProgramEmit;
        t->WriteSubmit = &WriteSubmit;
        t->WriteCancel = &WriteCancel;
        t->WriteInfoOf = &WriteInfoOf;
        t->GetWriteStatus = &GetWriteStatus;
        t->WriteDeclare = &WriteDeclare;
        _table = t;
        return (nint)t;
    }

    private static EntitiesFake? Fake => FakeTables.Active(WorldAbi.Service)?.EntitiesFake;

    // Game state is the tick thread's, as in the game.
    private static EntitiesFake? OnTick => FakeTables.OnTickThread ? Fake : null;

    // ---- programs: there is no scheduler ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RegisterProgram(ulong owner, void* desc, ulong* handle)
    {
        if (handle != null) *handle = 0;
        return FakeTables.Unavailable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ProgramSlot(ulong program, int kind, NativeStr name) => -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ProgramInfo(ulong program, void* info) => FakeTables.NotFound;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IterBegin(void* ctx, ulong* iter, uint size) => FakeTables.WrongThread;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte IterNext(void* ctx, ulong* iter, uint size) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint CurrentEntity(void* ctx) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* CommandBuffer(void* ctx) => null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* QueuePush(void* ctx, byte kind, uint size, uint entity) => null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueueAdd(void* ctx, uint entity, uint hash, void* data, uint size) => FakeTables.WrongThread;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueueRemove(void* ctx, uint entity, uint hash) => FakeTables.WrongThread;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MarkWritten(void* ctx, ulong* record)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* ReadOther(void* ctx, ulong accessor, uint entity, uint* size)
    {
        if (size != null) *size = 0;
        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* WriteOther(void* ctx, ulong accessor, uint entity, uint* size)
    {
        if (size != null) *size = 0;
        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int UnregisterProgram(ulong program) => FakeTables.NotFound;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BindProgram(ulong owner, NativeStr mod, NativeStr key, void* each, void* user, ulong* handle)
    {
        if (handle != null) *handle = 0;
        return FakeTables.Unavailable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ProgramEvents(void* ctx, NativeStr name, void* callback, void* user) => FakeTables.WrongThread;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ProgramEmit(void* ctx, NativeStr name, void* data, uint size) => FakeTables.WrongThread;

    // Only tracked writes of the capability word: no write_other inside a program, no events, no bind.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Capabilities() => Fake is null ? 0 : WorldAbi.CapTrackedWrites;

    // ---- entities ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ComponentHash(NativeStr name) =>
        name.Ptr == null ? 0 : WorldAbi.Hash(new ReadOnlySpan<byte>(name.Ptr, checked((int)name.Len)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* Component(uint entity, uint hash, uint* size)
    {
        if (size != null) *size = 0;
        if (OnTick is not { } w || w.Block(entity, hash) is not { } block) return null;
        if (size != null) *size = (uint)block.Size;
        return (void*)block.Data;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Exists(uint entity) => OnTick is { } w && w.Exists(entity) ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetTransform(uint entity, WorldAbi.Transform* output)
    {
        if (output == null) return FakeTables.InvalidArgument;
        if (Fake is null) return FakeTables.Unavailable;
        if (OnTick is not { } w) return FakeTables.WrongThread;
        // the entity's CurrentTransform, as the native service reads it (entities.cpp transform)
        if (w.TransformOf(entity) is not { } t) return FakeTables.NotFound;
        *output = default;
        output->Position = new WorldAbi.Vec3 { X = t.Position.X, Y = t.Position.Y, Z = t.Position.Z };
        output->Rotation[0] = t.RX;
        output->Rotation[1] = t.RY;
        output->Rotation[2] = t.RZ;
        output->Rotation[3] = t.RW;
        output->Scale[0] = t.SX;
        output->Scale[1] = t.SY;
        output->Scale[2] = t.SZ;
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetPosition(uint entity, WorldAbi.Vec3* output)
    {
        if (output == null) return FakeTables.InvalidArgument;
        if (Fake is null) return FakeTables.Unavailable;
        if (OnTick is not { } w) return FakeTables.WrongThread;
        if (w.PositionOf(entity) is not { } p) return FakeTables.NotFound;
        *output = new WorldAbi.Vec3 { X = p.X, Y = p.Y, Z = p.Z };
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Find(uint hash, uint* output, uint capacity, uint* count)
    {
        if (count != null) *count = 0;
        if (count == null || (output == null && capacity != 0)) return FakeTables.InvalidArgument;
        if (Fake is null) return FakeTables.Unavailable;
        if (OnTick is not { } w) return FakeTables.WrongThread;
        var found = w.With(hash);
        if (found is null) return FakeTables.NotFound;  // no such component type in this world
        *count = (uint)found.Count;
        for (var i = 0; i < found.Count && i < capacity; i++) output[i] = found[i];
        return found.Count > capacity ? FakeTables.BufferTooSmall : FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint EntityCount() => OnTick is { } w ? (uint)w.Count : 0;

    // No memory stands behind it: a number that is not zero, for code that only asks whether a world exists.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint ServerWorld() => Fake is { Started: true } ? 0x4B494C4Eu : 0;

    // ---- teleport: not modelled ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Teleport(ulong owner, void* request, ulong* id)
    {
        if (id != null) *id = 0;
        return FakeTables.Unavailable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TeleportCancel(ulong id) => FakeTables.NotFound;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetStatus(WorldAbi.Status* output)
    {
        if (output == null || output->Size < (uint)sizeof(WorldAbi.Status)) return FakeTables.InvalidArgument;
        var size = output->Size;
        *output = default;
        output->Size = size;
        if (Fake is not { } w) return FakeTables.Ok;  // nothing works
        output->World = w.Started ? (byte)1 : (byte)0;
        output->Find = 1;
        output->WorldSteps = w.Steps;
        return FakeTables.Ok;
    }

    // ---- tracked writes ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteSubmit(ulong owner, WorldAbi.WriteRequest* request, ulong* ticket)
    {
        if (ticket != null) *ticket = 0;
        var r = request;
        if (r == null || r->Size < (uint)sizeof(WorldAbi.WriteRequest) || r->Entity == 0 || r->Component == 0) return FakeTables.InvalidArgument;
        if ((r->Data == null) == (r->Change == null)) return FakeTables.InvalidArgument;
        if (r->Data != null && (r->Length == 0 || r->Length > WorldAbi.MaxBytes)) return FakeTables.InvalidArgument;
        if ((r->Flags & ~(WorldAbi.Whole | WorldAbi.Verify)) != 0 || r->ModId.Len > 64) return FakeTables.InvalidArgument;
        if (Fake is not { } w) return FakeTables.Unavailable;
        if (!FakeTables.OnTickThread) return FakeTables.WrongThread;  // between two steps, as in the game
        return w.Submit(owner, r, ticket);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteCancel(ulong ticket)
    {
        if (Fake is not { } w) return FakeTables.NotFound;
        if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
        return w.Cancel(ticket) ? FakeTables.Ok : FakeTables.NotFound;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteInfoOf(NativeStr mod, uint component, WorldAbi.WriteInfo* output)
    {
        if (output == null || output->Size < (uint)sizeof(WorldAbi.WriteInfo)) return FakeTables.InvalidArgument;
        var size = output->Size;
        *output = default;
        output->Size = size;
        if (Fake is not { } w)
        {
            output->State = WorldAbi.Off;
            return FakeTables.Ok;
        }
        w.Info(mod.Ptr == null ? "" : mod.ToString(), component, output);
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetWriteStatus(WorldAbi.WriteStatus* output)
    {
        if (output == null || output->Size < (uint)sizeof(WorldAbi.WriteStatus)) return FakeTables.InvalidArgument;
        var size = output->Size;
        *output = default;
        output->Size = size;
        output->Program = WorldAbi.ProgramPending;
        Fake?.Status(output);
        return FakeTables.Ok;
    }

    // A native service's own list: there is none offline.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteDeclare(ulong owner, NativeStr who, NativeStr* names, uint count) => FakeTables.Unavailable;
}
