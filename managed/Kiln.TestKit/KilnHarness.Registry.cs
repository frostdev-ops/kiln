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
    /// <summary>The mod ids of every <c>Kiln.Registry.Registries.Reauthor()</c> request the loaded
    /// mods made, in order (recorded: offline no pass runs by itself; run the next pass with <see cref="Author"/> or
    /// <see cref="RunReauthored"/>).</summary>
    /// <example><code>
    /// // the mod asks for a new pass when its settings change; the test sees the request and runs the pass itself
    /// settings.WoodStack = 800;
    /// Assert.Contains("my-mod", kit.Registry.Reauthors);
    /// </code></example>
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
    /// <summary>Why Kiln would refuse the call the last <see cref="Author"/> or <see cref="RunReauthored"/> ran, in Kiln's words;
    /// null when Kiln would take it (since 0.2.0-dev.32). Kiln refuses a call whole when its edits of a registry are over that
    /// registry's cap of edits per call (<c>RegistrySchema.EditsPerCall</c>), and lands nothing of a re-authored pass off the tick
    /// that called a tick-thread API (it runs the mod's passes on the tick from then on). The method then returns null.</summary>
    public string? Refused { get; private set; }

    /// <summary>Runs the loaded instance's OnRegistry on the harness thread. Returns copied IR, or null for no edits (or a call
    /// Kiln would refuse whole: <see cref="Refused"/> says why). A throw returns no IR; every context and typed cell expires
    /// before this method returns.</summary>
    public string? Author(ModHandle mod, RegistryEntryPoint point = RegistryEntryPoint.GameStart,
        RegistryPhase phase = RegistryPhase.Alter, RegistryWorld? world = null)
    {
        if (point is not (RegistryEntryPoint.GameStart or RegistryEntryPoint.ModLoad or RegistryEntryPoint.WorldLoad or RegistryEntryPoint.HotReload or RegistryEntryPoint.Manual))
            throw new ArgumentOutOfRangeException(nameof(point));
        return Pass(mod, point, phase, world, RegistryPassKind.Point);
    }

    /// <summary>Runs the loaded instance's WorldLoad pass as Kiln runs one <c>Registries.Reauthor</c> asked for (since
    /// 0.2.0-dev.32): with <paramref name="offTick"/> (Kiln's worker, the usual case) on a thread of its own, where the context
    /// says <see cref="RegistryContext.OffTick"/> and a tick-thread API throws as it does in a game; without it on the harness's
    /// tick (<see cref="RegistryPassKind.ReauthoredOnTick"/>). Returns copied IR as <see cref="Author"/> does; a pass off the tick
    /// that called a tick-thread API returns null whether it caught the refusal or not (<see cref="Refused"/> names the API), and
    /// an exception the pass did not catch is rethrown here.</summary>
    public string? RunReauthored(ModHandle mod, bool offTick = true, RegistryPhase phase = RegistryPhase.Alter, RegistryWorld? world = null)
        => Pass(mod, RegistryEntryPoint.WorldLoad, phase, world, offTick ? RegistryPassKind.ReauthoredOffTick : RegistryPassKind.ReauthoredOnTick);

    private string? Pass(ModHandle mod, RegistryEntryPoint point, RegistryPhase phase, RegistryWorld? world, RegistryPassKind kind)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (!mod.BelongsTo(_harness)) throw new ArgumentException("the mod belongs to another harness", nameof(mod));
        if (phase is not (RegistryPhase.Add or RegistryPhase.Alter or RegistryPhase.Final)) throw new ArgumentOutOfRangeException(nameof(phase));
        return _harness.Run(() =>
        {
            Refused = null;
            var owner = mod.Context;     // the harness's own reads, on its tick, before a worker runs the pass
            var instance = mod.Instance;
            string? Run()
            {
                var frame = new RegistryContext(owner, point, phase, world, "testkit-recording",
                    (registry, id, field) => _seeds.TryGetValue((registry, id, field), out var value) ? value : throw new KeyNotFoundException("the recording registry field was not seeded"),
                    (registry, pattern) => _seeds.Keys.Where(x => x.Registry == registry).Select(x => x.Id).Distinct(StringComparer.Ordinal)
                        .Where(id => Regex.IsMatch(id, "\\A" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "\\z", RegexOptions.CultureInvariant))
                        .Order(StringComparer.Ordinal).ToArray()) { Pass = kind };
                using var scope = frame.Enter();
                try
                {
                    instance.OnRegistry(frame);
                    var ir = frame.CaptureIr();
                    if (frame.CapRefusal is { } why) { Refused = why; return null; }  // Kiln refuses the call whole at submit
                    return ir;
                }
                finally { frame.Close(); }
            }
            if (kind != RegistryPassKind.ReauthoredOffTick) return Run();
            // Kiln's worker: a thread of its own, off the tick (Game.IsTickThread false there), its tick-thread refusals noted
            string? result = null, member = null;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
            var worker = new Thread(() =>
            {
                var outer = Game.OffTickPass;
                Game.OffTickPass = true; Game.OffTickRefused = null;
                try { result = Run(); }
                catch (Exception e) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
                finally { member = Game.OffTickRefused; Game.OffTickPass = outer; Game.OffTickRefused = null; }
            }) { IsBackground = true, Name = "kiln-testkit-reauthor" };
            worker.Start();
            worker.Join();
            if (member is not null)
            {
                Refused = $"{mod.Id}'s re-authored pass calls {member}, which needs the tick thread: Kiln lands nothing of the pass and runs " +
                          "this mod's passes on the world tick from then on";
                result = null;
            }
            error?.Throw();
            return result;
        });
    }
}
