// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessRegistry? _registryAuthoring;
    /// <summary>A seeded, recording-only OnRegistry authoring fake. It never advertises native mapping or applies game bytes.</summary>
    [Experimental("KILNX031")]
    public HarnessRegistry Registry => _registryAuthoring ??= new(this);
}

/// <summary>Tests one authoring pass against explicit copied JSON seeds and returns its IR. No native live service, composition,
/// restart/mapping proof, automatic entry-point scheduling or game writes are simulated. Seed the next pass's view explicitly.</summary>
[Experimental("KILNX031")]
public sealed class HarnessRegistry
{
    private readonly KilnHarness _harness;
    private readonly Dictionary<(string Registry, string Id, string Field), string> _seeds = [];
    private readonly List<string> _reauthors = [];
    internal HarnessRegistry(KilnHarness harness) => _harness = harness;
    /// <summary>The mod ids of every <c>Kiln.Registry.Registry.Reauthor(RegistryEntryPoint.WorldLoad)</c> request the loaded
    /// mods made, in order (recorded: offline no pass runs by itself; run the next pass with <see cref="Author"/>). A request
    /// for another point is refused (false) as in a game and not listed.</summary>
    public IReadOnlyList<string> Reauthors { get { lock (_reauthors) return _reauthors.ToArray(); } }
    internal string? RecordReauthor(ModContext mod, RegistryEntryPoint point)
    {
        if (point != RegistryEntryPoint.WorldLoad) return $"only the WorldLoad point is authored again on request, not {point}";
        lock (_reauthors) _reauthors.Add(mod.Info.Id);
        return null;
    }
    /// <summary>Seeds a stable field path with a copied JSON value, for example items/game:wood/maxStack/10.</summary>
    public void Seed(string registry, string id, string field, string valueJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(registry); ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(field); ArgumentNullException.ThrowIfNull(valueJson);
        var copy = JsonNode.Parse(valueJson)?.ToJsonString() ?? "null";
        _harness.Run(() => _seeds[(registry, id, field)] = copy);
    }
    /// <summary>Runs the loaded instance's OnRegistry on the harness thread. Returns copied IR, or null for no edits.
    /// A throw returns no IR; every context and typed cell expires before this method returns.</summary>
    public string? Author(ModHandle mod, RegistryEntryPoint point = RegistryEntryPoint.GameStart,
        RegistryPhase phase = RegistryPhase.Alter, RegistryWorld? world = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (!mod.BelongsTo(_harness)) throw new ArgumentException("the mod belongs to another harness", nameof(mod));
        if (point is not (RegistryEntryPoint.GameStart or RegistryEntryPoint.ModLoad or RegistryEntryPoint.WorldLoad or RegistryEntryPoint.HotReload or RegistryEntryPoint.Manual))
            throw new ArgumentOutOfRangeException(nameof(point));
        if (phase is not (RegistryPhase.Add or RegistryPhase.Alter or RegistryPhase.Final)) throw new ArgumentOutOfRangeException(nameof(phase));
        return _harness.Run(() =>
        {
            var frame = new RegistryContext(mod.Context, point, phase, world, "testkit-recording",
                (registry, id, field) => _seeds.TryGetValue((registry, id, field), out var value) ? value : throw new KeyNotFoundException("the recording registry field was not seeded"),
                (registry, pattern) => _seeds.Keys.Where(x => x.Registry == registry).Select(x => x.Id).Distinct(StringComparer.Ordinal)
                    .Where(id => Regex.IsMatch(id, "\\A" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "\\z", RegexOptions.CultureInvariant))
                    .Order(StringComparer.Ordinal).ToArray());
            using var scope = frame.Enter();
            try { mod.Instance.OnRegistry(frame); return frame.CaptureIr(); }
            finally { frame.Close(); }
        });
    }
}
