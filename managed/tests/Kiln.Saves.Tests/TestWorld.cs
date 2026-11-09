// SPDX-License-Identifier: MIT
// Native-world stand-in for the real SpawnLink. Metadata policy and persistence run in the production assembly.
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

internal static unsafe class TestWorld
{
    internal readonly record struct Metadata(uint Kind, uint Flags, string Name);
    public static readonly Dictionary<uint, SpawnLink.Ent> World = [];
    public static readonly Dictionary<Guid, Metadata> Templates = [];
    public static bool RegistryReady = true;
    public static int RegistryResult;
    public static int FindCalls;
    private static readonly Table* Native = (Table*)NativeMemory.AllocZeroed((nuint)sizeof(Table));
    private static readonly FieldInfo TableField = typeof(SpawnLink).GetField("_t", BindingFlags.Static | BindingFlags.NonPublic)!;

    // Mirrors the native wire layout only. No metadata eligibility or identity logic is implemented here.
    [StructLayout(LayoutKind.Sequential)]
    private struct Vec3 { public double X, Y, Z; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Str { public byte* Ptr; public nuint Len; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Snapshot
    {
        public uint Size, HasTemplate;
        public fixed byte Template[16];
        public Vec3 Position;
        public fixed float Rotation[4];
        public float Yaw;
        public uint Tracked;
        public ulong Serial;
        public long SpawnTime;
        public uint HasSpawnTime, Parent;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TemplateInfo
    {
        public uint Size, Kind;
        public fixed byte Guid[16], Name[128], Display[64];
        public uint Source, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public uint Size, Version;
        public nint Spawn, SpawnItem, Destroy;
        public delegate* unmanaged[Cdecl]<uint, Snapshot*, int> EntityInfo;
        public delegate* unmanaged[Cdecl]<byte*, Str, Vec3, double, uint*, nuint, nuint*, int> Find;
        public nint TemplateFind;
        public delegate* unmanaged[Cdecl]<byte*, TemplateInfo*, int> TemplateGet;
        public nint TemplateCount, TemplateAt, TrackedCount, TrackedAt, Cleanup, Refresh, WorldKey;
        public delegate* unmanaged[Cdecl]<uint> WorldState;
    }

    public static bool Available
    {
        set
        {
            TableField.SetValue(null, Pointer.Box(value ? Native : null, TableField.FieldType));
            typeof(SpawnLink).GetField("_looked", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
        }
    }

    public static void Reset()
    {
        World.Clear();
        Templates.Clear();
        ClearMetadataCache();
        RegistryReady = true;
        RegistryResult = FindCalls = 0;
        Native->Size = (uint)sizeof(Table);
        Native->Version = 1;
        Native->EntityInfo = &EntityInfo;
        Native->Find = &Find;
        Native->TemplateGet = &TemplateGet;
        Native->WorldState = &WorldState;
        foreach (var (name, size) in new[] { ("Table", sizeof(Table)), ("Snapshot", sizeof(Snapshot)), ("TemplateInfo", sizeof(TemplateInfo)) })
            if (Marshal.SizeOf(typeof(SpawnLink).GetNestedType(name, BindingFlags.NonPublic)!) != size)
                throw new InvalidOperationException($"Spawn test ABI drift: {name}");
        Available = true;
    }

    public static void ClearMetadataCache() =>
        ((IDictionary)typeof(SpawnLink).GetField("Templates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Clear();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EntityInfo(uint id, Snapshot* snapshot)
    {
        if (!World.TryGetValue(id, out var entity)) return 2;
        *snapshot = new Snapshot { Size = (uint)sizeof(Snapshot), Position = new Vec3 { X = entity.Position.X, Y = entity.Position.Y, Z = entity.Position.Z }, Yaw = entity.Yaw };
        if (entity.Template is { } g)
        {
            snapshot->HasTemplate = 1;
            g.TryWriteBytes(new Span<byte>(snapshot->Template, 16));
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TemplateGet(byte* guid, TemplateInfo* info)
    {
        if (RegistryResult != 0) return RegistryResult;
        if (!Templates.TryGetValue(new Guid(new ReadOnlySpan<byte>(guid, 16)), out var template)) return 2;
        *info = new TemplateInfo { Size = (uint)sizeof(TemplateInfo), Kind = template.Kind, Flags = RegistryReady ? template.Flags : 0 };
        var name = Encoding.UTF8.GetBytes(template.Name);
        name.AsSpan(0, Math.Min(name.Length, 127)).CopyTo(new Span<byte>(info->Name, 128));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Find(byte* guid, Str _, Vec3 position, double radius, uint* output, nuint capacity, nuint* count)
    {
        FindCalls++;
        var g = new Guid(new ReadOnlySpan<byte>(guid, 16));
        var near = new WorldPosition(position.X, position.Y, position.Z);
        var entities = World.Values.Where(e => e.Template == g && e.Position.DistanceTo(near) <= radius)
            .OrderBy(e => e.Position.DistanceTo(near)).Take((int)capacity).ToArray();
        for (var i = 0; i < entities.Length; i++) output[i] = entities[i].Entity.Value;
        *count = (nuint)entities.Length;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint WorldState() => 2;
}
