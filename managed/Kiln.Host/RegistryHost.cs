// SPDX-License-Identifier: MIT
// The registry stage's mod order (registry P1 slice S11; P1-PLAN.md section 5.1). kiln.registry (kiln-content.dll) runs every
// mod's registry edits in Kiln.Host's runtime load order, the order players see in the launcher profile (the resolver's:
// required and optional dependencies, loadAfter, loadBefore, ties by id). Kiln.Host hands it over before the boot phase
// through the Kiln-internal kiln.internal.registry table (native/services/content/include/kiln_registry_host.h), so the
// stage has it wherever it runs (resources-ready, or with OnBoot in mode onBoot). Without the table (an older or missing
// kiln-content.dll) the stage runs in kiln.content's mod-folder order and registry.json says "folders".
// OnBoot authors keep their own order (required boot dependencies, then id; BootPhase.cs); where the two differ for mods that
// are both, kiln.log names the difference (coordinator ruling, 2026-11-20).
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.Host;

/// <summary>kiln.internal.registry v3 (kiln_registry_host.h), 80 bytes: field order is the table's. v1 (24 bytes) ends
/// after SetMode; ReadPass (v2, 32 bytes) is a runtime pass's read without the reading mod's own layers (review4 NS5); v3
/// (dev.32 LOOT-2) prepares a Registries.Reauthor turn off the world thread and lands it on the tick.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnRegistryHostApi
{
    public uint Size, Version;
    public delegate* unmanaged[Cdecl]<KilnStr, int> SetOrder;
    public delegate* unmanaged[Cdecl]<uint, int> SetMode;
    public delegate* unmanaged[Cdecl]<ulong, KilnStr, KilnStr, KilnStr, byte*, nuint, nuint*, int> ReadPass;
    public delegate* unmanaged[Cdecl]<ulong> Epoch;
    public delegate* unmanaged[Cdecl]<ulong, ulong, KilnStr, KilnStr, KilnStr, byte*, nuint, nuint*, int> ReadPassIn;
    public delegate* unmanaged[Cdecl]<KilnRegistryPassCall*, nuint, KilnRegistryTurnItem*, nuint, ulong, ulong*, uint*, int> Prepare;
    public delegate* unmanaged[Cdecl]<ulong, uint, RegistryLiveReply*, nuint, ulong*, nuint, uint*, int> Install;
    public delegate* unmanaged[Cdecl]<ulong, int> Discard;
    public delegate* unmanaged[Cdecl]<int> BuildReport;

    public const string Name = "kiln.internal.registry";
    public const uint Version1 = 1, Version2 = 2, Version3 = 3;
    public const uint ModeResourcesReady = 0, ModeOnBoot = 1;
    public const uint SizeV2 = 32;
    public const uint InstallAnyway = 1;
}
/// <summary>kiln.internal.registry v3's KilnRegistryPassCall: one callback's IR of a re-authored pass.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KilnRegistryPassCall
{
    public ulong Owner;
    public uint Point, Reserved;
    public KilnStr Ir;
}
/// <summary>kiln.internal.registry v3's KilnRegistryTurnItem: a retained change a turn takes out.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KilnRegistryTurnItem
{
    public ulong Owner, Handle;
}

internal static unsafe class RegistryHost
{
    /// <summary>The order as set_order takes it: [{"mod", "version"}], every mod once, in order. A mod whose code uses the
    /// registry API carries "registry": true: the stage then decodes the game's base at resources-ready, before kiln.content
    /// writes packs into resident resource pages (review4 NS1). A mod of one side carries "side": "server" | "client": the join's
    /// registry fingerprint leaves out the fields it writes (docs/kiln/network.md "Registry boot values and order").</summary>
    internal static string OrderJson(IEnumerable<(string Id, string Version)> order, ISet<string>? registryUsers = null,
        IReadOnlyDictionary<string, ManifestSide>? sides = null)
    {
        var a = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, version) in order)
        {
            if (!seen.Add(id)) continue;
            var row = new JsonObject { ["mod"] = id, ["version"] = version };
            if (registryUsers?.Contains(id) == true) row["registry"] = true;
            if (sides is not null && sides.TryGetValue(id, out var side) && side != ManifestSide.Both)
                row["side"] = side == ManifestSide.Client ? "client" : "server";
            a.Add(row);
        }
        return a.ToJsonString();
    }

    /// <summary>kiln.log's line when the registry's order and OnBoot's differ for mods that are in both (null: they agree).</summary>
    internal static string? OnBootDifference(IReadOnlyList<string> load, IReadOnlyList<string> onBoot)
    {
        var both = new HashSet<string>(load.Intersect(onBoot, StringComparer.Ordinal), StringComparer.Ordinal);
        var l = load.Where(both.Contains).ToList();
        var b = onBoot.Where(both.Contains).ToList();
        if (l.SequenceEqual(b, StringComparer.Ordinal)) return null;
        // the first pair the two orders put the other way round
        for (var i = 0; i < l.Count; i++)
            for (var j = i + 1; j < l.Count; j++)
                if (b.IndexOf(l[i]) > b.IndexOf(l[j]))
                    return $"registry: the registry stage runs {l[i]} before {l[j]} (Kiln.Host's load order: {string.Join(", ", l)}); " +
                           $"their OnBoot ran {l[j]} first (boot dependencies, then id: {string.Join(", ", b)})";
        return null;
    }

    /// <summary>Hands the load order (and the stage mode, when kiln.json asks for one) to kiln.registry. Never throws.</summary>
    public static void Run(Func<string, uint, nint> service, ModManager mods, string? mode, Action<LogLevel, string> log)
    {
        try
        {
            var t = (KilnRegistryHostApi*)service(KilnRegistryHostApi.Name, KilnRegistryHostApi.Version1);
            if (t == null)
            {
                log(LogLevel.Info, "registry: no kiln.internal.registry table (kiln-content.dll does not run the registry stage): nothing to hand over");
                return;
            }
            if (t->Version < KilnRegistryHostApi.Version1 || t->Version > KilnRegistryHostApi.Version3 || t->Size < 24 ||
                (t->Version == KilnRegistryHostApi.Version2 && t->Size != KilnRegistryHostApi.SizeV2) ||
                (t->Version == KilnRegistryHostApi.Version3 && t->Size != (uint)sizeof(KilnRegistryHostApi)))
            {
                log(LogLevel.Warn, $"registry: kiln.internal.registry is v{t->Version}, {t->Size} bytes, not v1, v2 of {KilnRegistryHostApi.SizeV2} or v3 of " +
                                   $"{sizeof(KilnRegistryHostApi)}: the stage runs in the mod folders' order");
                return;
            }
            var order = mods.RegistryOrder();
            var users = mods.RegistryUsers();
            var sides = new Dictionary<string, ManifestSide>(StringComparer.Ordinal);
            foreach (var mod in order) sides.TryAdd(mod.Id, mod.Side);
            var json = OrderJson(order.Select(m => (m.Id, m.Version)), users, sides);
            var rc = NativeCalls.WithUtf8(json, s => t->SetOrder(s));
            log(rc == KilnAbi.Ok ? LogLevel.Info : LogLevel.Warn,
                rc == KilnAbi.Ok ? $"registry: stage order = Kiln.Host load order, {order.Count} mod(s), {users.Count} using the registry API"
                                 : $"registry: set_order refused the load order (rc {rc})");
            if (mode is null) return;
            var m = mode.Equals("onBoot", StringComparison.OrdinalIgnoreCase) ? KilnRegistryHostApi.ModeOnBoot
                : mode.Equals("resourcesReady", StringComparison.OrdinalIgnoreCase) ? KilnRegistryHostApi.ModeResourcesReady : uint.MaxValue;
            if (m == uint.MaxValue)
            {
                log(LogLevel.Warn, $"registry: kiln.json registry.stage \"{mode}\" is not onBoot or resourcesReady: the stage runs at resources-ready");
                return;
            }
            var mrc = t->SetMode(m);
            log(mrc == KilnAbi.Ok ? LogLevel.Info : LogLevel.Warn, $"registry: stage mode {mode} (rc {mrc})");
        }
        catch (Exception e)
        {
            log(LogLevel.Warn, $"registry: the load order was not handed over: {e.Message}");
        }
    }

    /// <summary>After the boot phase: names where the registry's order and OnBoot's differ.</summary>
    public static void CompareWithOnBoot(ModManager mods, IReadOnlyList<string> onBoot, Action<LogLevel, string> log)
    {
        try
        {
            if (onBoot.Count < 2) return;
            if (OnBootDifference(mods.RegistryOrder().Select(m => m.Id).ToList(), onBoot) is { } line) log(LogLevel.Info, line);
        }
        catch (Exception e)
        {
            log(LogLevel.Warn, $"registry: the OnBoot order was not compared: {e.Message}");
        }
    }

    /// <summary>kiln.json's "registry": {"stage": "onBoot" | "resourcesReady"} (null: not set).</summary>
    internal static string? ModeOf(string kilnJson)
    {
        try
        {
            if (!File.Exists(kilnJson)) return null;
            var root = JsonNode.Parse(File.ReadAllText(kilnJson), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return root?["registry"]?["stage"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal sealed partial class ModManager
{
    /// <summary>The runtime load order of this start's Kiln mods, as the resolver orders them (the order LoadAll loads them in):
    /// every enabled mod folder that resolves, dependencies first.</summary>
    internal List<(string Id, string Version, ManifestSide Side)> RegistryOrder()
    {
        var input = BootCandidates().Select(c => new ModCandidate { Manifest = c.Manifest, Directory = c.Directory }).ToList();
        var env = new ResolveEnvironment(_kilnVersion, _apiVersion, _options.Side, _options.HasFeature, ResolveEnvironment.NoneLoaded)
            { Hosting = _options.Side == ManifestSide.Client && _options.HostedServerMods, Capability = _options.Capability };
        return Resolver.Resolve(input, env).Order.Where(c => c.Manifest is not null).Select(c => (c.Manifest!.Id, c.Manifest!.Version, c.Manifest!.Side)).ToList();
    }

    /// <summary>The mods whose code references the registry API (OnRegistry, Registries.Change/Read/Ready, or the older name
    /// Registry's), from metadata only: the
    /// stage prepares the game's base for them at resources-ready instead of at first use (review4 NS1).</summary>
    internal HashSet<string> RegistryUsers()
    {
        var users = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var candidates = RegistryCandidates();
            var directories = candidates.Select(c => c.Directory).ToArray();
            foreach (var (manifest, directory) in candidates)
                if (manifest.HasCode && RegistryDeclarations.UsesRegistry(Path.Combine(directory, manifest.EntryAssembly), directories))
                    users.Add(manifest.Id);
        }
        catch (Exception) { /* metadata only: an unreadable folder is no registry user, and the stage stays lazy */ }
        return users;
    }
}
