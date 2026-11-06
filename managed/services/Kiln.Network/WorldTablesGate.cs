// SPDX-License-Identifier: MIT
// Client and server have to agree on building-material ids: cells replicate as raw bytes with no table, so a different list would show the wrong walls. Each side's kiln.worldtables
// has a hash over its live materials' ids and GUIDs; HELLO carries the client's, and the server refuses a difference by name (docs/kiln/services/worldtables.md). Kept materials of absent
// mods aren't in it: they come after every live id, and no client draws them.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

internal static unsafe class WorldTablesGate
{
    public const string KeyHash = "kiln.worldtables", KeyIds = "kiln.worldtables.ids";
    /// <summary>A side with no live material, or with no service at all (an older Kiln).</summary>
    public const string EmptyHash = "0000000000000000";

    [StructLayout(LayoutKind.Sequential)]
    private struct Str
    {
        public byte* Ptr;
        public nuint Len;
        public readonly string Text() => Ptr == null || Len == 0 ? "" : Encoding.UTF8.GetString(Ptr, (int)Len);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Material
    {
        public uint Size, State;
        public fixed byte Guid[16];
        public uint CellId, Reused, ItemId, Reserved;
        public Str Mod, Block, ModDir, Definition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public uint Size, Version;
        public delegate* unmanaged[Cdecl]<int> State;
        public delegate* unmanaged[Cdecl]<Str> TableHash;
        public delegate* unmanaged[Cdecl]<uint> MaterialCount;
        public delegate* unmanaged[Cdecl]<uint, Material*, int> MaterialAt;
    }

    private static Table* Native => Services.Get<Table>("kiln.worldtables", 1);

    /// <summary>This side's live materials, by id: (id, GUID without dashes, "block (mod x)").</summary>
    private static List<(int Id, string Guid, string Label)> Live()
    {
        var list = new List<(int, string, string)>();
        var t = Native;
        if (t == null) return list;
        var n = t->MaterialCount();
        for (uint i = 0; i < n; i++)
        {
            Material m = default;
            m.Size = (uint)sizeof(Material);
            if (t->MaterialAt(i, &m) != 0 || m.State != 0) continue;
            list.Add(((int)m.CellId, new Guid(new ReadOnlySpan<byte>(m.Guid, 16)).ToString("N"), $"{m.Block.Text()} (mod {m.Mod.Text()})"));
        }
        return list;
    }

    private static string OwnHash()
    {
        var t = Native;
        return t == null ? EmptyHash : t->TableHash().Text();
    }

    /// <summary>What goes in HELLO and WELCOME.</summary>
    public static Dictionary<string, string> Properties()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal) { [KeyHash] = OwnHash() };
        var live = Live();
        if (live.Count > 0) d[KeyIds] = string.Join(',', live.Select(m => $"{m.Id}={m.Guid}"));
        return d;
    }

    /// <summary>Null when a client with these handshake properties has this server's ids; else what a player reads.</summary>
    public static string? Difference(IReadOnlyDictionary<string, string> client)
    {
        var mine = OwnHash();
        var theirs = client.GetValueOrDefault(KeyHash, EmptyHash);
        if (mine == theirs) return null;
        return Describe(Live(), client.GetValueOrDefault(KeyIds, ""));
    }

    /// <summary>The difference in words: which materials, by name where this side knows it. Plain text, no game state: the handshake tests read it.</summary>
    internal static string Describe(IReadOnlyList<(int Id, string Guid, string Label)> server, string clientIds)
    {
        var theirs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in clientIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && int.TryParse(part[..eq], out var id)) theirs[part[(eq + 1)..]] = id;
        }
        var notes = new List<string>();
        foreach (var m in server)
        {
            if (!theirs.TryGetValue(m.Guid, out var id)) notes.Add($"you don't have {m.Label}");
            else if (id != m.Id) notes.Add($"{m.Label} is id {m.Id} here and {id} on your game");
            theirs.Remove(m.Guid);
        }
        foreach (var (guid, id) in theirs.OrderBy(x => x.Value)) notes.Add($"your game has material {guid} (id {id}), and this server doesn't");
        var what = notes.Count == 0 ? "the lists differ" : string.Join("; ", notes.Take(6)) + (notes.Count > 6 ? $"; and {notes.Count - 6} more" : "");
        return $"This server's building and terrain materials don't match your game's: {what}. Join with the same mods, and restart if you just changed them.";
    }
}
