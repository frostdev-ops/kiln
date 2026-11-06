// SPDX-License-Identifier: MIT
// kiln.worldtables from C#: what the boot merge made of every mod's material blocks. native/core/abi/services/kiln_worldtables.h.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>A building or terrain material in the game's tables that a mod made, or that a world keeps for an absent mod.</summary>
/// <param name="CellId">The id in a cell: 128..255 a building material, 1..127 a terrain one. It may change between sessions: a save follows the GUID.</param>
/// <param name="Guid">The material's GUID. It follows from the mod id and the block's name.</param>
/// <param name="Mod">The mod, as last seen for a kept material.</param>
/// <param name="Block">The block's name.</param>
/// <param name="Live">An enabled mod's. False: kept, so a world's cells of it stay while its mod is away.</param>
/// <param name="Reused">It took a slot the game has marked deprecated.</param>
public sealed record WorldMaterial(int CellId, Guid Guid, string Mod, string Block, bool Live, bool Reused)
{
    /// <summary>A terrain material (<see cref="TerrainMaterialBlock"/>): cell ids 1..127 are the game's terrain.</summary>
    public bool Terrain => CellId is > 0 and < 128;
}

/// <summary>The game's shared building-material tables, merged from every enabled mod at boot. docs/kiln/services/worldtables.md.</summary>
public static class WorldTables
{
    /// <summary>The merged tables are served, or there was nothing to change. False: this build doesn't resolve, or the service is off, and the game's tables are untouched.</summary>
    public static bool Available => WorldTablesNative.State is WorldTablesNative.Merged or WorldTablesNative.Untouched;

    /// <summary><c>off</c>, <c>waiting</c>, <c>untouched</c> or <c>merged</c>.</summary>
    public static string State => WorldTablesNative.State switch
    {
        WorldTablesNative.Waiting => "waiting",
        WorldTablesNative.Untouched => "untouched",
        WorldTablesNative.Merged => "merged",
        _ => "off",
    };

    /// <summary>What a client and a server have to agree on: 16 hex digits over the live materials' ids and GUIDs.</summary>
    public static string Hash => WorldTablesNative.Hash();

    /// <summary>Every material in the tables: the building materials, then the terrain ones, live ones first in each.</summary>
    public static IReadOnlyList<WorldMaterial> Materials => WorldTablesNative.Materials();

    /// <summary>The cell id of a material's GUID, or -1: it isn't in the tables.</summary>
    public static int CellIdOf(Guid material) => WorldTablesNative.MaterialId(material);

    /// <summary>JSON: state, hash, ids, tweaks, each table's bytes against the game's, what was refused and why.</summary>
    public static string StatusJson() => WorldTablesNative.StatusJson();

    /// <summary>A game material's cell id from what a mod writes: a <c>GameMaterials</c> name, <c>Cell&lt;id&gt;</c>, or its GUID. -1: not a game material, or the tables aren't merged.</summary>
    public static int GameMaterialId(string material) => WorldTablesNative.GameMaterialId(material);

    /// <summary>The tweaks of game materials in the tables, in load order: (cell id, mod, block).</summary>
    public static IReadOnlyList<(int CellId, string Mod, string Block)> Tweaks => WorldTablesNative.Tweaks();

    /// <summary>Stops keeping an absent mod's material in every world. From the next start a world's cells of it are lost when it loads. False: no world keeps it.</summary>
    public static bool Forget(Guid material) => WorldTablesNative.Forget(material);
}

internal static unsafe class WorldTablesNative
{
    public const string Name = "kiln.worldtables";
    public const uint Version = 1;
    public const int Off = 0, Waiting = 1, Untouched = 2, Merged = 3;
    public const string EmptyHash = "0000000000000000";
    private const int Ok = 0, BufferTooSmall = 7;

    [StructLayout(LayoutKind.Sequential)]
    public struct Str
    {
        public byte* Ptr;
        public nuint Len;
        public readonly string Text() => Ptr == null || Len == 0 ? "" : Encoding.UTF8.GetString(Ptr, (int)Len);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Material
    {
        public uint Size, State;
        public fixed byte Guid[16];
        public uint CellId, Reused, ItemId, Reserved;
        public Str Mod, Block, ModDir, Definition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public uint Size, Version;
        public delegate* unmanaged[Cdecl]<int> State;
        public delegate* unmanaged[Cdecl]<Str> TableHash;
        public delegate* unmanaged[Cdecl]<uint> MaterialCount;
        public delegate* unmanaged[Cdecl]<uint, Material*, int> MaterialAt;
        public delegate* unmanaged[Cdecl]<byte*, int> MaterialId;
        public delegate* unmanaged[Cdecl]<int> FirstNewId;
        public void* AddWriter;
        public delegate* unmanaged[Cdecl]<Str, byte*, nuint, nuint*, int> DefineJson;
        public delegate* unmanaged[Cdecl]<Str, Str, int> PrunePending;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Status;
        public delegate* unmanaged[Cdecl]<byte*, int> Forget;
        public delegate* unmanaged[Cdecl]<uint> TweakCount;
        public delegate* unmanaged[Cdecl]<uint, Tweak*, int> TweakAt;
        public delegate* unmanaged[Cdecl]<Str, int> GameMaterialId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Tweak
    {
        public uint Size, CellId;
        public Str Mod, Block, ModDir, Definition;
    }

    private static Table* _table;
    private static bool _looked;

    public static Table* T
    {
        get
        {
            if (_looked) return _table;
            _looked = true;
            var t = (Table*)BlocksBridge.ServiceTable(Name, Version);
            // the size says which functions a table has; an older one is treated as absent
            _table = t != null && t->Size >= (uint)sizeof(Table) ? t : null;
            return _table;
        }
    }

    public static int State => T == null ? Off : T->State();

    public static string Hash() => T == null ? EmptyHash : T->TableHash().Text();

    public static IReadOnlyList<WorldMaterial> Materials()
    {
        var t = T;
        if (t == null) return [];
        var n = t->MaterialCount();
        var list = new List<WorldMaterial>((int)n);
        for (uint i = 0; i < n; i++)
        {
            Material m = default;
            m.Size = (uint)sizeof(Material);
            if (t->MaterialAt(i, &m) != Ok) continue;
            list.Add(new WorldMaterial((int)m.CellId, new Guid(new ReadOnlySpan<byte>(m.Guid, 16)), m.Mod.Text(), m.Block.Text(), m.State == 0, m.Reused != 0));
        }
        return list;
    }

    public static int MaterialId(Guid guid)
    {
        if (T == null) return -1;
        Span<byte> b = stackalloc byte[16];
        guid.TryWriteBytes(b);
        fixed (byte* p = b) return T->MaterialId(p);
    }

    public static int GameMaterialId(string text)
    {
        if (T == null || string.IsNullOrEmpty(text)) return -1;
        var b = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = b) return T->GameMaterialId(new Str { Ptr = p, Len = (nuint)b.Length });
    }

    public static IReadOnlyList<(int CellId, string Mod, string Block)> Tweaks()
    {
        var t = T;
        if (t == null) return [];
        var n = t->TweakCount();
        var list = new List<(int, string, string)>((int)n);
        for (uint i = 0; i < n; i++)
        {
            Tweak w = default;
            w.Size = (uint)sizeof(Tweak);
            if (t->TweakAt(i, &w) == Ok) list.Add(((int)w.CellId, w.Mod.Text(), w.Block.Text()));
        }
        return list;
    }

    public static bool Forget(Guid guid)
    {
        if (T == null) return false;
        Span<byte> b = stackalloc byte[16];
        guid.TryWriteBytes(b);
        fixed (byte* p = b) return T->Forget(p) == Ok;
    }

    private delegate int Reader(byte* buffer, nuint capacity, nuint* length);

    private static string Read(Reader read)
    {
        var buffer = new byte[16384];
        for (var i = 0; i < 2; i++)
        {
            nuint n = 0;
            int rc;
            fixed (byte* p = buffer) rc = read(p, (nuint)buffer.Length, &n);
            if ((int)n < buffer.Length) return Encoding.UTF8.GetString(buffer, 0, (int)n);
            buffer = new byte[(int)n + 1];
            _ = rc;
        }
        return "";
    }

    public static string StatusJson() => T == null ? "{\"state\":\"off\"}" : Read((b, c, n) => T->Status(b, c, n));

    /// <summary>The native report on a definition. Null: the service isn't here.</summary>
    public static string? DefineJson(string document)
    {
        var t = T;
        if (t == null) return null;
        var bytes = Encoding.UTF8.GetBytes(document);
        return Read((b, c, n) =>
        {
            fixed (byte* p = bytes) return t->DefineJson(new Str { Ptr = p, Len = (nuint)bytes.Length }, b, c, n);
        });
    }
}
