// SPDX-License-Identifier: MIT
// Client and server have to serve the same player animation graph: a mod's player state is an id the server's sequences
// send to every client, and a client whose graph lacks it (or has it in another place) plays something else. Each side's
// kiln.playeranim merges the enabled mods' states at boot and hashes the graph it serves (native/core/abi/services/
// kiln_playeranim.h, graph_hash). HELLO and WELCOME carry that hash and the side's live states by name; the server refuses
// a difference, as WorldTablesGate does for building materials. A side with no service, or with the game's own graph,
// sends sixteen zeros, and a peer that sends no key (an older Kiln) counts as that. Reads the table itself, so it needs no
// reference to Kiln.PlayerAnim.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

internal static unsafe class PlayerGraphGate
{
    public const string KeyHash = "kiln.playeranim", KeyStates = "kiln.playeranim.states", KeyCount = "kiln.playeranim.count";
    /// <summary>The game's own graph, or no service at all (an older Kiln, a client without Kiln).</summary>
    public const string GameHash = "0000000000000000";
    /// <summary>At most this many names go into the handshake; the count says how many there are.</summary>
    public const int MaxNames = 128;

    [StructLayout(LayoutKind.Sequential)]
    private struct Str
    {
        public byte* Ptr;
        public nuint Len;
        public readonly string Text() => Ptr == null || Len == 0 ? "" : Encoding.UTF8.GetString(Ptr, (int)Len);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State  // KilnPaState
    {
        public uint Size, Status, Kind, Id;
        public fixed byte Guid[16];
        public Str Mod, Name, Like, Machine, Reason;
    }

    /// <summary>The front of KilnPlayerAnimApi (v1) up to state_at: all the gate reads.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public uint Size, Version;
        public delegate* unmanaged[Cdecl]<int> State;
        public delegate* unmanaged[Cdecl]<Str> GraphHash;
        public void* GameState, GameStateIds, ModState;
        public delegate* unmanaged[Cdecl]<uint> StateCount;
        public delegate* unmanaged[Cdecl]<uint, State*, int> StateAt;
    }

    private static Table* Native => Services.Get<Table>("kiln.playeranim", 1);

    /// <summary>This side's states in the served graph, as "mod/state", by mod id then name.</summary>
    private static List<string> Live()
    {
        var list = new List<string>();
        var t = Native;
        if (t == null) return list;
        var n = t->StateCount();
        for (uint i = 0; i < n; i++)
        {
            State s = default;
            s.Size = (uint)sizeof(State);
            if (t->StateAt(i, &s) != 0 || s.Status != 0) continue;  // KILN_PA_LIVE only
            list.Add($"{s.Mod.Text()}/{s.Name.Text()}");
        }
        return list;
    }

    private static string OwnHash()
    {
        var t = Native;
        return t == null ? GameHash : Hash(t->GraphHash().Text());
    }

    /// <summary>A hash as sent: 16 hex digits, lower case; anything else counts as the game's own graph.</summary>
    internal static string Hash(string? h) =>
        h is { Length: 16 } && h.All(Uri.IsHexDigit) ? h.ToLowerInvariant() : GameHash;

    /// <summary>What goes in HELLO and WELCOME.</summary>
    public static Dictionary<string, string> Properties()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal) { [KeyHash] = OwnHash() };
        var live = Live();
        if (live.Count > 0)
        {
            d[KeyStates] = Join(live);
            d[KeyCount] = live.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return d;
    }

    /// <summary>Null when a client with these handshake properties serves this server's player graph; else what a player reads.</summary>
    public static string? Difference(IReadOnlyDictionary<string, string> client)
    {
        var mine = OwnHash();
        var theirs = Hash(client.GetValueOrDefault(KeyHash, GameHash));
        if (mine == theirs) return null;
        return Describe(mine, Live(), theirs, client.GetValueOrDefault(KeyStates, ""), client.GetValueOrDefault(KeyCount, ""));
    }

    /// <summary>"a/b,c/d", each name with '%' and ',' escaped, at most <see cref="MaxNames"/>.</summary>
    internal static string Join(IEnumerable<string> names) =>
        string.Join(',', names.Take(MaxNames).Select(n => n.Replace("%", "%25").Replace(",", "%2C")));

    internal static List<string> Split(string names) =>
        names.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => n.Replace("%2C", ",").Replace("%25", "%")).ToList();

    /// <summary>The difference in words: how many mod states each side has, and which, by name where they were sent. Plain
    /// text, no game state: the handshake tests read it.</summary>
    internal static string Describe(string serverHash, IReadOnlyList<string> server, string clientHash, string clientStates, string clientCount)
    {
        var client = Split(clientStates);
        var clientTotal = int.TryParse(clientCount, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var c) && c >= client.Count
            ? c : client.Count;
        var clientSet = client.ToHashSet(StringComparer.Ordinal);
        var serverSet = server.ToHashSet(StringComparer.Ordinal);

        string Count(int n) => n == 1 ? "1 state" : $"{n} states";
        string Names(IReadOnlyList<string> names, int total)
        {
            if (names.Count == 0) return "";
            var shown = string.Join(", ", names.Take(6));
            var more = total - Math.Min(names.Count, 6);
            return $" ({shown}{(more > 0 ? $", and {more} more" : "")})";
        }

        string serverPart = serverHash == GameHash || server.Count == 0
            ? "the server plays the game's own player animations"
            : $"the server has {Count(server.Count)} from mods{Names(server, server.Count)}";
        string clientPart = clientHash == GameHash || clientTotal == 0
            ? "your game plays the game's own"
            : $"your game has {Count(clientTotal)}{Names(client, clientTotal)}";

        var notes = new List<string>();
        var lacking = server.Where(s => !clientSet.Contains(s)).ToList();
        // The client's list may be cut at MaxNames: what it has beyond that can't be named, so "only you" is said only
        // of a complete list.
        var extra = clientTotal == client.Count ? client.Where(s => !serverSet.Contains(s)).ToList() : [];
        if (clientHash != GameHash && clientTotal > 0 && lacking.Count > 0 && client.Count == clientTotal)
            notes.Add("you don't have " + string.Join(", ", lacking.Take(6)) + (lacking.Count > 6 ? $" and {lacking.Count - 6} more" : ""));
        if (extra.Count > 0 && serverHash != GameHash)
            notes.Add("this server doesn't have " + string.Join(", ", extra.Take(6)) + (extra.Count > 6 ? $" and {extra.Count - 6} more" : ""));
        if (serverHash != GameHash && clientHash != GameHash && lacking.Count == 0 && extra.Count == 0 && server.Count == clientTotal)
            notes.Add("the same states are merged differently (another version of a mod that defines them, or of the game)");
        var detail = notes.Count > 0 ? "; " + string.Join("; ", notes) : "";
        return $"This server's player animations don't match your game's: {serverPart}, {clientPart}{detail}. " +
               "Join with the same mods, and restart if you just changed them.";
    }
}
