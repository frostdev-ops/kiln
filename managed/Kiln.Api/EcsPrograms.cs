// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>A worker-thread ECS update. Its context and component spans cannot escape this callback.</summary>
public delegate void EcsProgramCallback(EcsProgramContext context);

/// <summary>Callbacks bound to scheduler declarations in the mod's kiln.programs.json. The native service reserves
/// these declarations before the world starts. Bind in OnLoad; disposing and hot reload leave an inert scheduler
/// entry, and changing declarations requires a server restart.</summary>
public static unsafe class EcsPrograms
{
    internal static EcsWorldTable* Table => Services.Get<EcsWorldTable>("kiln.world", 2);
    /// <summary>Whether boot declarations and callback binding work on this build.</summary>
    public static bool Available => Table != null && (Table->Capabilities() & 4) != 0;
    /// <summary>Whether tracked other-entity writes work on this build.</summary>
    public static bool SupportsOtherWrites => Table != null && (Table->Capabilities() & 1) != 0;

    /// <summary>Binds one declaration belonging to the calling mod. The callback runs on game worker threads;
    /// ordinary tick-thread services require Scheduler.Post. Mod unload drains the callback before releasing it.</summary>
    [TickThreadOnly]
    public static IDisposable Bind(string key, EcsProgramCallback callback)
    {
        Services.RequireTickThread("EcsPrograms.Bind");
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(callback);
        var mod = ModContext.CurrentMod ?? ModContext.OwnerOf(callback)
            ?? throw new InvalidOperationException("EcsPrograms.Bind must belong to a loaded mod");
        if (!mod.Alive) throw new InvalidOperationException("the mod is unloading");
        var table = Table;
        if (table == null || (table->Capabilities() & 4) == 0) throw new NotSupportedException("kiln.world programs v2 is unavailable");
        var binding = new Binding(mod, key, callback, table);
        binding.Root = GCHandle.Alloc(binding);
        var idBytes = Encoding.UTF8.GetBytes(mod.Info.Id);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        ulong handle = 0;
        int result;
        fixed (byte* id = idBytes, name = keyBytes)
            result = table->Bind(mod.Owner, new NativeStr { Ptr = id, Len = (nuint)idBytes.Length },
                new NativeStr { Ptr = name, Len = (nuint)keyBytes.Length }, &Each,
                (void*)GCHandle.ToIntPtr(binding.Root), &handle);
        if (result != 0)
        {
            binding.Root.Free();
            throw new InvalidOperationException($"cannot bind ECS program '{key}' ({result}); declare it in kiln.programs.json before starting the server");
        }
        binding.Handle = handle;
        mod.Retiring += binding.Dispose;
        return binding;
    }

    internal sealed class Binding(ModContext mod, string key, EcsProgramCallback callback, EcsWorldTable* table) : IDisposable
    {
        internal readonly ModContext Mod = mod;
        internal readonly string Key = key;
        internal readonly EcsProgramCallback Callback = callback;
        internal readonly EcsWorldTable* Native = table;
        internal GCHandle Root;
        internal ulong Handle;
        internal bool Failed;

        // What the callbacks looked up, per (kind, name): the native slot, the game type's size and the UTF-8 name. Resolved
        // on first use and published copy-on-write, so the per-entity path is a scan of a small array: no allocation, no
        // native lookup and no lock (workers that miss at the same time both resolve; one array wins, the result is equal).
        private Entry[] _entries = [];
        private int _typesMissingLogged;

        internal sealed class Entry(int kind, string name, byte[] utf8, int slot, int size)
        {
            internal readonly int Kind = kind;
            internal readonly string Name = name;
            internal readonly byte[] Utf8 = utf8;
            /// <summary>The access-list slot (or, for <see cref="HashKind"/>, the component hash); -1 when undeclared.</summary>
            internal readonly int Slot = slot;
            /// <summary>The game type's size; -1 when kiln.types does not know it (or is missing).</summary>
            internal readonly int Size = size;
        }

        internal const int HashKind = -1;

        internal Entry Lookup(int kind, string name)
        {
            var entries = Volatile.Read(ref _entries);
            foreach (var e in entries)
                if (e.Kind == kind && (ReferenceEquals(e.Name, name) || e.Name == name)) return e;
            return Resolve(kind, name);
        }

        private Entry Resolve(int kind, string name)
        {
            var utf8 = Encoding.UTF8.GetBytes(name);
            int slot, size = -1;
            fixed (byte* text = utf8)
            {
                var str = new NativeStr { Ptr = text, Len = (nuint)utf8.Length };
                if (kind == HashKind)
                    slot = unchecked((int)((delegate* unmanaged[Cdecl]<NativeStr, uint>)Native->Prefix.ComponentHash)(str));
                else
                    slot = ((delegate* unmanaged[Cdecl]<ulong, int, NativeStr, int>)Native->Prefix.ProgramSlot)(Handle, kind, str);
            }
            if ((kind is 0 or 1 or 2) && slot >= 0)   // an undeclared name throws in Declared: nothing to size or warn about
            {
                var type = GameTypes.Table == null ? null : GameTypes.Find(name);
                if (type is not null) size = type.Size;
                else if (Interlocked.Exchange(ref _typesMissingLogged, 1) == 0)
                    Mod.Log.Warn(GameTypes.Table == null
                        ? $"ECS program '{Key}': kiln.types is not running on this build, so TryRead/TryWrite always return false (TryReadOther/TryWriteOther still work)"
                        : $"ECS program '{Key}': kiln.types does not know component '{name}', so TryRead/TryWrite on it return false");
            }
            var entry = new Entry(kind, name, utf8, slot, size);
            while (true)
            {
                var current = Volatile.Read(ref _entries);
                foreach (var e in current)
                    if (e.Kind == kind && e.Name == name) return e;
                if (Interlocked.CompareExchange(ref _entries, [.. current, entry], current) == current) return entry;
            }
        }
        public void Dispose()
        {
            if (Handle == 0) return;
            Services.RequireTickThread("EcsPrograms.Dispose");
            var result = Native->Unregister(Handle);
            if (result != 0) throw new InvalidOperationException($"cannot drain ECS program ({result})");
            Handle = 0;
            Mod.Retiring -= Dispose;
            Root.Free();
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Each(void* context, ulong* iterator, uint entity, void* user)
    {
        try
        {
            var binding = (Binding)GCHandle.FromIntPtr((nint)user).Target!;
            if (!binding.Mod.Alive || binding.Failed) return;
            var previous = ModContext.CurrentMod;
            try
            {
                ModContext.CurrentMod = binding.Mod;
                binding.Callback(new EcsProgramContext(binding, context, iterator, entity));
            }
            catch (Exception error)
            {
                binding.Failed = true;
                binding.Mod.Log.Error($"ECS program disabled: {error}");
            }
            finally { ModContext.CurrentMod = previous; }
        }
        catch { /* no managed exception may cross an unmanaged worker frame */ }
    }
}

/// <summary>Access declared by this program. Entity is zero for a system program. All methods are synchronous,
/// worker-thread only; the context and returned spans last only until the current callback returns.</summary>
public readonly unsafe ref struct EcsProgramContext
{
    private readonly EcsPrograms.Binding _binding;
    private readonly void* _context;
    private readonly ulong* _iterator;
    internal EcsProgramContext(EcsPrograms.Binding binding, void* context, ulong* iterator, uint entity)
    { _binding = binding; _context = context; _iterator = iterator; Entity = new EntityId(entity); }
    public EntityId Entity { get; }
    private EcsPrograms.Binding.Entry Declared(int kind, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var entry = _binding.Lookup(kind, name);
        if (entry.Slot < 0) throw new ArgumentException($"'{name}' was not declared in access list {kind}", nameof(name));
        return entry;
    }
    private int Slot(int kind, string name) => Declared(kind, name).Slot;
    /// <summary>Reads a declared read component of the current entity. Optional absent components return false.
    /// Set template for a tpl/tplOptional declaration. False on builds without kiln.types (logged once per program).</summary>
    public bool TryRead<T>(string component, out T value, bool template = false) where T : unmanaged
    {
        var entry = Declared(template ? 0 : 1, component);
        var data = (void*)_iterator[entry.Slot];
        if (data == null || entry.Size != sizeof(T)) { value = default; return false; }
        value = *(T*)data;
        return true;
    }
    /// <summary>Writes a declared write component of the current entity and marks its replication/save dirtiness.
    /// False on builds without kiln.types (logged once per program).</summary>
    public bool TryWrite<T>(string component, T value) where T : unmanaged
    {
        var entry = Declared(2, component);
        var data = (void*)_iterator[entry.Slot];
        if (data == null || entry.Size != sizeof(T)) return false;
        var mark = (delegate* unmanaged[Cdecl]<void*, ulong*, void>)_binding.Native->Prefix.MarkWritten;
        mark(_context, _iterator + entry.Slot);
        *(T*)data = value;
        return true;
    }
    /// <summary>Reads another entity through an otherRead declaration. False when absent or the layout size differs.</summary>
    public bool TryReadOther<T>(EntityId entity, string component, out T value) where T : unmanaged
    {
        uint size = 0;
        var read = (delegate* unmanaged[Cdecl]<void*, ulong, uint, uint*, void*>)_binding.Native->Prefix.ReadOther;
        void* data = read(_context, _iterator[Slot(5, component)], entity.Value, &size);
        if (data == null || size != sizeof(T)) { value = default; return false; }
        value = *(T*)data;
        return true;
    }
    /// <summary>Writes another entity through an otherWrite declaration, marking replication/save dirtiness with
    /// the game's job tracker. False when absent, unavailable, or the layout size differs.</summary>
    public bool TryWriteOther<T>(EntityId entity, string component, T value) where T : unmanaged
    {
        uint size = 0;
        void* data = _binding.Native->WriteOther(_context, _iterator[Slot(6, component)], entity.Value, &size);
        if (data == null || size != sizeof(T)) return false;
        *(T*)data = value;
        return true;
    }
    /// <summary>A copy of each event in this declared input stream, in queue order. Multiple input types are allowed.</summary>
    public byte[][] ReadEvents(string name)
    {
        var bytes = Declared(7, name).Utf8;
        var result = new List<byte[]>();
        var root = GCHandle.Alloc(result);
        try
        {
            int error;
            fixed (byte* text = bytes)
                error = _binding.Native->Events(_context, new NativeStr { Ptr = text, Len = (nuint)bytes.Length }, &CopyEvent,
                    (void*)GCHandle.ToIntPtr(root));
            if (error != 0) throw new InvalidOperationException($"cannot read program events '{name}' ({error})");
            return result.ToArray();
        }
        finally { root.Free(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CopyEvent(void* data, uint size, void* user)
    {
        try { ((List<byte[]>)GCHandle.FromIntPtr((nint)user).Target!).Add(new ReadOnlySpan<byte>(data, checked((int)size)).ToArray()); }
        catch { /* never unwind into a native update */ }
    }
    /// <summary>Sends an exact event payload through a declared output stream. The native service stamps GameEvent
    /// timestamps with program time. False when the per-step event buffer is full.</summary>
    public bool EmitEvent(string name, ReadOnlySpan<byte> data)
    {
        var bytes = Declared(8, name).Utf8;
        int result;
        fixed (byte* text = bytes, payload = data)
            result = _binding.Native->Emit(_context, new NativeStr { Ptr = text, Len = (nuint)bytes.Length }, payload, (uint)data.Length);
        if (result is not (0 or 5)) throw new InvalidOperationException($"cannot emit program event '{name}' ({result})");
        return result == 0;
    }
    /// <summary>Adds component bytes at the end of this step. An existing component keeps its old value.</summary>
    public bool QueueAdd(EntityId entity, string component, ReadOnlySpan<byte> data)
    {
        var queue = (delegate* unmanaged[Cdecl]<void*, uint, uint, void*, uint, int>)_binding.Native->Prefix.QueueAdd;
        int result;
        fixed (byte* payload = data) result = queue(_context, entity.Value, ComponentHash(component), payload, (uint)data.Length);
        if (result is not (0 or 5)) throw new InvalidOperationException($"cannot queue component add ({result})");
        return result == 0;
    }
    /// <summary>Removes a component at the end of this step.</summary>
    public bool QueueRemove(EntityId entity, string component)
    {
        var queue = (delegate* unmanaged[Cdecl]<void*, uint, uint, int>)_binding.Native->Prefix.QueueRemove;
        var result = queue(_context, entity.Value, ComponentHash(component));
        if (result is not (0 or 5)) throw new InvalidOperationException($"cannot queue component removal ({result})");
        return result == 0;
    }
    private uint ComponentHash(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return unchecked((uint)_binding.Lookup(EcsPrograms.Binding.HashKind, name).Slot);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EcsWorldTable
{
    public WorldPrefix Prefix;
    public nint Teleport, TeleportCancel, Status;
    public delegate* unmanaged[Cdecl]<void*, ulong, uint, uint*, void*> WriteOther;
    public delegate* unmanaged[Cdecl]<ulong, int> Unregister;
    public delegate* unmanaged[Cdecl]<ulong, NativeStr, NativeStr, delegate* unmanaged[Cdecl]<void*, ulong*, uint, void*, void>, void*, ulong*, int> Bind;
    public delegate* unmanaged[Cdecl]<uint> Capabilities;
    public delegate* unmanaged[Cdecl]<void*, NativeStr, delegate* unmanaged[Cdecl]<void*, uint, void*, void>, void*, int> Events;
    public delegate* unmanaged[Cdecl]<void*, NativeStr, void*, uint, int> Emit;
}
