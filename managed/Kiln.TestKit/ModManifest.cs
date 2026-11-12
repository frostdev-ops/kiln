// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.TestKit;

/// <summary>A <c>kiln.mod.json</c> built in code, for <see cref="KilnHarness.Load{TMod}(ModManifest?)"/>. The harness adds
/// <c>entryAssembly</c> and <c>entryType</c> (the assembly and the class of <c>TMod</c>) and writes the file into the mod's
/// folder; Kiln's own manifest parser and resolver read it from there, so a bad value fails as it would in the game.</summary>
/// <example><code>
/// kiln.Load&lt;MyMod&gt;(new ModManifest("my-mod").DependsOn("other-mod", "^1.0").Requires("items.fuel"));
/// </code></example>
public sealed class ModManifest
{
    private readonly List<(string Id, string Range, bool Optional)> _dependencies = [];
    private readonly List<string> _features = [];
    private readonly List<string> _exports = [];
    private readonly Dictionary<string, JsonNode?> _extra = new(StringComparer.Ordinal);

    /// <summary>A manifest built in code for a mod loaded by the harness.</summary>
    /// <param name="id">The mod id (<c>[a-z0-9][a-z0-9._-]*</c>); also the mod's folder name under <c>kiln/mods</c>.</param>
    public ModManifest(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Id = id;
    }

    /// <summary>The mod id.</summary>
    public string Id { get; }

    /// <summary>Display name; default the id.</summary>
    public string? Name { get; set; }

    /// <summary>The mod's version (semantic versioning); default 1.0.0.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>The Kiln API the mod was built for; default this Kiln's (<see cref="Mods.RuntimeApiVersion"/>).</summary>
    public string ApiVersion { get; set; } = Mods.RuntimeApiVersion;

    /// <summary>Server (default), client or both. The harness is a dedicated server: a client mod is skipped.</summary>
    public ModSide Side { get; set; } = ModSide.Server;

    /// <summary>The <c>advanced</c> tier flag.</summary>
    public bool Advanced { get; set; }

    /// <summary>A folder whose files become the mod's <c>locales/</c> (not written to the manifest).</summary>
    public string? LocalesDirectory { get; set; }

    /// <summary>Adds a dependency (<c>"kiln"</c> is Kiln itself).</summary>
    public ModManifest DependsOn(string id, string versionRange = "*", bool optional = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _dependencies.Add((id, versionRange, optional));
        return this;
    }

    /// <summary>Adds required features (<c>features</c>): the resolver refuses the mod while one is unavailable.</summary>
    public ModManifest Requires(params string[] features)
    {
        _features.AddRange(features);
        return this;
    }

    /// <summary>Adds exported assemblies (<c>exports</c>, file names such as <c>MyApi.dll</c>).</summary>
    public ModManifest Exports(params string[] files)
    {
        _exports.AddRange(files);
        return this;
    }

    /// <summary>Any other manifest key (<c>tests</c>, <c>loadAfter</c>, <c>incompatible</c>...), as JSON.</summary>
    public ModManifest Set(string key, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _extra[key] = value;
        return this;
    }

    /// <summary>The manifest as the mod's <c>kiln.mod.json</c> would hold it.</summary>
    public string ToJson(string entryAssembly, string? entryType)
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name ?? Id,
            ["version"] = Version,
            ["apiVersion"] = ApiVersion,
            ["entryAssembly"] = entryAssembly,
            ["side"] = Side.ToString().ToLowerInvariant(),
        };
        if (entryType is not null) o["entryType"] = entryType;
        if (_dependencies.Count > 0)
        {
            var deps = new JsonObject();
            foreach (var (id, range, optional) in _dependencies)
                deps[id] = new JsonObject { ["version"] = range, ["optional"] = optional };
            o["dependencies"] = deps;
        }
        if (_features.Count > 0) o["features"] = new JsonArray([.. _features.Select(f => (JsonNode)f)]);
        if (_exports.Count > 0) o["exports"] = new JsonArray([.. _exports.Select(f => (JsonNode)f)]);
        if (Advanced) o["advanced"] = true;
        foreach (var (key, value) in _extra) o[key] = value?.DeepClone();
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
