// SPDX-License-Identifier: MIT
// The Impact programs a content state's definitions name. A clone of a mod with "clients": "kiln" can run a program that
// mod made at load (a buff's impact, a template's Impact.program, a sequence's SpawnImpact): a Kiln client without the
// mod has no such program, so the definition it gets would name nothing it has, and kiln.gamedata refuses that field.
// The server therefore sends the bytes of every new program its document names ("@programs"); the client serves them
// through kiln.assets as the content sync's owner before it applies the document, so the definitions it gets are whole
// (kiln.gamedata finds a program reference among what kiln.assets serves). A joined client runs no Impact programs, the
// server does; where a world does run them, kiln.abilities registers what kiln.assets serves. The game's own programs are
// never sent, and a client never takes a program in place of one its game or its own mods already have.
//
// Kiln.Content reads kiln.assets and kiln.abilities through their native v1 tables (append-only ABI) instead of their
// managed assemblies, which depend on Kiln.Content. Layouts: native/core/abi/services/kiln_assets.h, kiln_abilities.h.
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Kiln;

internal static unsafe class SyncPrograms
{
    /// <summary>FNV-1a-32 of "keen::ImpactProgram": what an ObjectReference&lt;ImpactDefinition&gt; or an ImpactProgramReference names.</summary>
    internal const uint TypeImpactProgram = 0xd4922d88;
    private const uint PutNew = 1;
    private const int Ok = 0, Unavailable = 3;
    private const int MaxProgramBytes = 1 << 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct Key  // KilnAssetKey, 24 bytes
    {
        public fixed byte Guid[16];
        public uint Type;
        public uint Part;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AssetsTable  // the v1 prefix of KilnAssetsApi up to resource_is_new
    {
        public ServiceHeader Header;
        public nint IconRegisterPng, IconRegisterFile, IconRemove, IconState;
        public delegate* unmanaged[Cdecl]<ulong, Key*, void*, nuint, uint, int> ResourcePut;
        public delegate* unmanaged[Cdecl]<ulong, Key*, int> ResourceRemove;
        public delegate* unmanaged[Cdecl]<Key*, void*, nuint, nuint*, int> ResourceRead;
        public nint ResourceList, BlobAdd, BlobOverride, BlobRemove, BlobRead, TextureOverridePng, Dump, Status;
        public delegate* unmanaged[Cdecl]<ulong, void> RevertOwner;
        public nint LoadManifest;
        public delegate* unmanaged[Cdecl]<Key*, void**, nuint*, int> ResourceData;
        public delegate* unmanaged[Cdecl]<Key*, int> ResourceIsNew;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AbilitiesTable  // the v1 prefix of KilnAbilitiesApi up to program_refresh
    {
        public ServiceHeader Header;
        public nint Status, SetListener, HoldFilter, InstanceVars, InstanceRead, InstanceWrite, InstanceInfo, EntityOverrides;
        public nint ProgramCount, ProgramInfo, ProgramVars, ProgramRegister;
        public delegate* unmanaged[Cdecl]<uint*, int> ProgramRefresh;
    }

    private static AssetsTable* _assets;
    private static AbilitiesTable* _abilities;
    private static bool _looked;
    private static readonly object Lock = new();
    // client: the programs this game serves for the server (GUID -> FNV of the bytes), taken off on leave
    private static readonly Dictionary<Guid, uint> Served = new();

    private static AssetsTable* Assets
    {
        get
        {
            Look();
            return _assets;
        }
    }

    private static void Look()
    {
        if (_looked) return;
        _looked = true;
        var a = Services.Get<AssetsTable>("kiln.assets", 1);
        // resource_is_new was appended to v1: an older kiln.assets has a shorter table
        if (a != null && a->Header.Size >= (uint)sizeof(AssetsTable)) _assets = a;
        var b = Services.Get<AbilitiesTable>("kiln.abilities", 1);
        if (b != null && b->Header.Size >= (uint)sizeof(AbilitiesTable)) _abilities = b;
    }

    private static Key MakeKey(Guid g)
    {
        var k = new Key { Type = TypeImpactProgram };
        var b = g.ToByteArray();  // .NET order == the game's resource-id byte order
        for (var i = 0; i < 16; i++) k.Guid[i] = b[i];
        return k;
    }

    // ---- server ------------------------------------------------------------------------------------------------

    /// <summary>Server: adds <c>"@programs"</c>, <c>[{"guid", "data": base64}]</c> sorted by GUID, for every program
    /// the document names that kiln.assets serves as new (not the game's). Returns how many.</summary>
    internal static int Add(JsonObject doc)
    {
        var t = Assets;
        if (t == null) return 0;
        var guids = new SortedSet<Guid>();
        Collect(doc, guids);
        var list = new JsonArray();
        foreach (var g in guids)
        {
            var k = MakeKey(g);
            if (t->ResourceIsNew(&k) != 1) continue;
            nuint n = 0;
            t->ResourceRead(&k, null, 0, &n);
            if (n == 0 || n > MaxProgramBytes) continue;
            var bytes = new byte[n];
            int rc;
            fixed (byte* p = bytes) rc = t->ResourceRead(&k, p, n, &n);
            if (rc != Ok || n != (nuint)bytes.Length) continue;
            list.Add(new JsonObject { ["guid"] = g.ToString(), ["data"] = Convert.ToBase64String(bytes) });
        }
        if (list.Count > 0) doc["@programs"] = list;
        return list.Count;
    }

    // Every GUID a value of the document holds: references are written as GUID strings.
    private static void Collect(JsonNode? node, SortedSet<Guid> into)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, v) in o)
                    if (key != "@programs") Collect(v, into);
                break;
            case JsonArray a:
                foreach (var v in a) Collect(v, into);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length == 36 && Guid.TryParseExact(s, "D", out var g) && g != Guid.Empty:
                into.Add(g);
                break;
        }
    }

    // ---- client ------------------------------------------------------------------------------------------------

    /// <summary>Client (tick thread): serves the server's programs as <paramref name="owner"/>, before the document that
    /// names them is applied. A program this game has already (its own, a mod's) stays as it is. Returns how many were
    /// served now, and the problems.</summary>
    internal static (int Served, int Local, List<string> Errors) Serve(JsonObject server, ulong owner)
    {
        var errors = new List<string>();
        if (server["@programs"] is not JsonArray list || list.Count == 0) return (0, 0, errors);
        var t = Assets;
        if (t == null || owner == 0)
        {
            errors.Add($"{list.Count} program(s) from the server not served: kiln.assets is not available in this game");
            return (0, 0, errors);
        }
        int served = 0, local = 0;
        foreach (var n in list.OfType<JsonObject>())
        {
            if (!Guid.TryParse((string?)n["guid"], out var g) || n["data"] is not JsonValue dv || !dv.TryGetValue<string>(out var b64))
            {
                errors.Add("a program from the server without a GUID or data");
                continue;
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                errors.Add($"program {g}: not base64");
                continue;
            }
            if (bytes.Length == 0 || bytes.Length > MaxProgramBytes)
            {
                errors.Add($"program {g}: {bytes.Length} bytes");
                continue;
            }
            var hash = ContentSync.Fnv(bytes);
            var k = MakeKey(g);
            void* p0 = null;
            nuint len0 = 0;
            lock (Lock)
            {
                var ours = Served.TryGetValue(g, out var had);
                if (ours && had == hash) continue;
                if (!ours && t->ResourceData(&k, &p0, &len0) == Ok && p0 != null)
                {
                    local++;  // the game's, or this game's own mod serves it
                    continue;
                }
                int rc;
                // a new key only: a server never replaces a program this game has
                fixed (byte* p = bytes) rc = t->ResourcePut(owner, &k, p, (nuint)bytes.Length, ours ? 0u : PutNew);
                if (rc != Ok)
                {
                    errors.Add($"program {g}: kiln.assets refused it ({rc})");
                    continue;
                }
                Served[g] = hash;
            }
            served++;
        }
        // kiln.abilities registers what kiln.assets serves; before the client world's ImpactRegistry is up it does so itself, once it is
        if (served > 0 && Game.IsTickThread && _abilities != null)
        {
            uint added = 0;
            var rc = _abilities->ProgramRefresh(&added);
            if (rc != Ok && rc != Unavailable) errors.Add($"kiln.abilities did not register the programs ({rc})");
        }
        return (served, local, errors);
    }

    /// <summary>Client: takes the programs served for the server off (on leave).</summary>
    internal static int Release(ulong owner)
    {
        int n;
        lock (Lock)
        {
            n = Served.Count;
            Served.Clear();
        }
        if (n > 0 && owner != 0 && Assets is var t && t != null) t->RevertOwner(owner);
        return n;
    }
}
