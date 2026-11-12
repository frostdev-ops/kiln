// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace TypegenProbe;

/// <summary>
/// Lab check of the generated game types (docs/kiln/typegen.md section 5). On a server with a loaded world it reads
/// every generated ECS component that has live entities through <c>Kiln.Ecs</c> (most entities first, up to 200 each)
/// and compares (1) the typed copy with the raw component bytes from kiln.world, (2) every leaf reached through the
/// generated struct with the game's own reflection (<see cref="Reflected"/>) for the same path, (3) the nine legacy
/// types through <c>Kiln.GameTypes.Components</c> with the raw bytes, (4) <c>Ecs.Supports</c>/<c>WhyNot</c>, and
/// (5) that the comparer reports a flipped byte. A file <c>expect-refused.json</c> in the mod's data folder
/// (<c>{"keen::ecs::X": "text the refusal must contain"}</c>) turns the run into the wrong-layout run: those types must
/// be refused, every other type must still pass. The summary goes to <c>summary.json</c> in the data folder.
/// </summary>
public sealed class TypegenProbeMod : Mod
{
    const int MaxEntities = 200;
    static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(20);   // work per server step before yielding

    // The nine dev.24 names, and the kinds of type the brief wants in the list.
    static readonly string[] Legacy = ["NetworkHealth", "Health", "Mana", "Stamina", "CurrentTransform", "LastTransform", "Velocity", "TeleportCount", "NpcCommon"];

    List<CheckedType> _plan = [];
    Dictionary<string, string> _refused = new(StringComparer.Ordinal);
    List<LegacyResult> _legacy = [];
    readonly List<string> _notes = [];
    string _selfTest = "not run";

    public override void OnLoad() => Kiln.Testing.ClientChecks.Register(this);

    IEnumerable<CheckedType> Checked => _plan.Where(c => !_refused.ContainsKey(c.QualifiedName));

    [KilnTest(Order = 0)]
    public void Plan_every_generated_component_with_live_entities()
    {
        Assert.True(Ecs.Available, "kiln.world is loaded");
        var path = Path.Combine(Context.DataDirectory, "expect-refused.json");
        _refused = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
            : [];
        _plan = Plan(MaxEntities);
        Log.Info($"typegen-check: {_plan.Count} generated components have live entities ({_plan.Sum(c => c.Entities.Count)} entities planned)" +
                 (_refused.Count > 0 ? $"; expected refused: {string.Join(", ", _refused.Keys)}" : ""));
        Assert.True(_plan.Count >= 20, $"at least 20 component types with live entities, found {_plan.Count}");
        foreach (var q in _refused.Keys) Assert.True(_plan.Any(c => c.QualifiedName == q), $"{q} has live entities");
        foreach (var (what, ok) in Coverage(_plan)) Assert.True(ok, $"the checked types include {what}");
    }

    /// <summary>Every generated component with live entities, most entities first.</summary>
    internal static List<CheckedType> Plan(int maxEntities)
    {
        var plan = new List<CheckedType>();
        foreach (var c in TypedReadCheck.AllComponentTypes())
        {
            var ents = World.Find(c.Name);
            if (ents.Count == 0) continue;
            c.LiveEntities = ents.Count;
            c.Entities.AddRange(ents.Take(maxEntities));
            TypedReadCheck.Describe(c);
            plan.Add(c);
        }
        return plan.OrderByDescending(c => c.LiveEntities).ThenBy(c => c.QualifiedName, StringComparer.Ordinal).ToList();
    }

    internal static List<(string What, bool Ok)> Coverage(List<CheckedType> plan)
    {
        bool Has(string name) => plan.Any(c => c.Name == name);
        var list = new List<(string, bool)>();
        foreach (var n in Legacy)
            if (Has(n)) list.Add(($"the dev.24 type {n}", true));
        list.Add(("an attribute component (Health or Fuel)", Has("Health") || Has("Fuel")));
        list.Add(("a transform (CurrentTransform or LastTransform)", Has("CurrentTransform") || Has("LastTransform")));
        list.Add(("NpcCommon", Has("NpcCommon")));
        foreach (var f in new[] { "enum", "bitmask", "array-of-structs", "depth3" })
            list.Add(($"a type with feature {f} ({string.Join(", ", plan.Where(c => c.Features.Contains(f)).Take(3).Select(c => c.Name))})",
                      plan.Any(c => c.Features.Contains(f))));
        return list;
    }

    [KilnTest(Order = 1)]
    public void Supports_is_true_and_WhyNot_null_for_every_checked_type()
    {
        RequirePlan();
        var bad = new List<string>();
        foreach (var c in _plan)
        {
            var why = c.Checker.WhyNot();
            if (_refused.TryGetValue(c.QualifiedName, out var expect))
            {
                if (c.Checker.Supports() || why is null) bad.Add($"{c.QualifiedName}: expected refused, Supports is true");
                else if (!why.Contains(expect, StringComparison.Ordinal)) bad.Add($"{c.QualifiedName}: refused with \"{why}\", which does not name \"{expect}\"");
                else
                {
                    Log.Info($"typegen-check: {c.QualifiedName} refused as expected: {why}");
                    if (c.Checker.TypedBytes(c.Entities[0], out _) is not null) bad.Add($"{c.QualifiedName}: Ecs.TryGet returned true for a refused type");
                }
            }
            else if (!c.Checker.Supports() || why is not null) bad.Add($"{c.QualifiedName}: Supports false: {why}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(40)));
    }

    [KilnTest(Order = 2)]
    public async Task Typed_bytes_equal_the_raw_component_for_every_checked_type(TestContext t)
    {
        RequirePlan();
        var sw = Stopwatch.StartNew();
        foreach (var c in Checked)
        {
            TypedReadCheck.RunBytes(c, MaxEntities);           // one type in one step: the bytes cannot change between reads
            if (sw.Elapsed > Slice) { await t.Ticks(1); sw.Restart(); }
        }
        var types = Checked.ToList();
        Log.Info($"typegen-check: bytes: {types.Count} types, {types.Sum(c => c.BytesCompared)} entities compared, " +
                 $"{types.Sum(c => c.BytesMatched)} equal, {types.Sum(c => c.BytesGone)} gone since the plan");
        var bad = types.SelectMany(c => c.Mismatches).ToList();
        Assert.True(bad.Count == 0, $"{bad.Count} mismatch(es):\n" + string.Join("\n", bad.Take(40)));
        // A component whose every planned entity lost it between the plan and this step is transient (an *Init
        // component the game removes after one step): said, not failed. Every other type compared an entity.
        var transient = types.Where(c => c.BytesCompared == 0 && c.BytesGone > 0).Select(c => c.Name).ToList();
        if (transient.Count > 0) Log.Info($"typegen-check: bytes: transient, every planned entity lost it since the plan: {string.Join(", ", transient)}");
        Assert.True(types.All(c => c.BytesCompared > 0 || c.BytesGone > 0), "every checked type compared an entity");
        Assert.True(types.Count(c => c.BytesCompared > 0) >= 20, "at least 20 types compared");
    }

    [KilnTest(Order = 3)]
    public async Task Typed_fields_equal_the_games_reflection_for_every_checked_type(TestContext t)
    {
        RequirePlan();
        if (!GameTypes.Available) Assert.Skip($"kiln.types is not loaded ({GameTypes.Status}): Reflected cannot read fields");
        var sw = Stopwatch.StartNew();
        foreach (var c in Checked)
            foreach (var e in c.Entities)
            {
                TypedReadCheck.RunFields(c, e);                // one entity in one step
                if (sw.Elapsed > Slice) { await t.Ticks(1); sw.Restart(); }
            }
        var types = Checked.ToList();
        Log.Info($"typegen-check: fields: {types.Count} types, {types.Sum(c => c.FieldEntities)} entities, " +
                 $"{types.Sum(c => c.FieldsCompared)} leaves compared, {types.Sum(c => c.FieldsMatched)} equal, " +
                 $"{types.Sum(c => c.Skipped.Values.Sum())} skipped, {types.Sum(c => c.FieldsGone)} entities gone since the plan");
        foreach (var g in types.SelectMany(c => c.Skipped).GroupBy(kv => kv.Key))
            Log.Info($"typegen-check: skipped {g.Sum(kv => kv.Value)}: {g.Key}");
        var bad = types.SelectMany(c => c.Mismatches).ToList();
        Assert.True(bad.Count == 0, $"{bad.Count} mismatch(es):\n" + string.Join("\n", bad.Take(40)));
        Assert.True(types.Sum(c => c.FieldsCompared) > 0, "some leaves were compared");
    }

    [KilnTest(Order = 4)]
    public void The_legacy_nine_through_Kiln_GameTypes_equal_the_raw_bytes()
    {
        _legacy = LegacyCheck.Run(MaxEntities);
        foreach (var r in _legacy)
            Log.Info($"typegen-check: legacy {r.Name} ({r.Size} B): {r.Entities} entities, {r.Matched} equal");
        var bad = _legacy.SelectMany(r => r.Mismatches).ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(40)));
        Assert.True(_legacy.Any(r => r.Entities > 0), "at least one legacy type has live entities");
    }

    [KilnTest(Order = 5)]
    public void The_comparer_reports_a_flipped_byte_naming_type_and_field()
    {
        RequirePlan();
        // a type with a named field at some offset: flip the first byte of its last leaf
        var c = Checked.First(x => x.BytesMatched > 0 && x.Size >= 8);
        var e = c.Entities.First(x => World.ReadBytes(x, c.Name) is not null);
        var raw = World.ReadBytes(e, c.Name)!;
        var typed = c.Checker.TypedBytes(e, out _)!;
        var leaf = TypedReadCheck.Leaves(c.Checker.FromBytes(typed), c.Type, new()).Last();
        var copy = (byte[])typed.Clone();
        copy[leaf.Offset] ^= 0x5A;
        var why = TypedReadCheck.CompareBytes(c, copy, raw);
        Log.Info($"typegen-check: self-test (bytes): {why}");
        Assert.NotNull(why);
        Assert.Contains(c.QualifiedName, why!);
        Assert.Contains(leaf.Path, why!);
        _selfTest = why!;
        if (GameTypes.Available)
        {
            var found = new List<string>();
            TypedReadCheck.RunFields(c, e, c.Checker.FromBytes(copy), found);
            Log.Info($"typegen-check: self-test (fields): {string.Join(" | ", found)}");
            Assert.True(found.Any(f => f.Contains($"{c.QualifiedName}.{leaf.Path}", StringComparison.Ordinal)),
                        $"the field comparer names {c.QualifiedName}.{leaf.Path}: {string.Join(" | ", found)}");
            _selfTest += " | " + string.Join(" | ", found);
        }
    }

    [KilnTest(Order = 9)]
    public void Write_the_summary()
    {
        var path = Path.Combine(Context.DataDirectory, "summary.json");
        Directory.CreateDirectory(Context.DataDirectory);
        var doc = new
        {
            kilnGame = typeof(Ecs).Assembly.Location,
            gameTypesAvailable = GameTypes.Available,
            expectedRefused = _refused,
            types = _plan.Count,
            checkedTypes = Checked.Count(),
            entitiesBytes = Checked.Sum(c => c.BytesCompared),
            entitiesFields = Checked.Sum(c => c.FieldEntities),
            entitiesGoneBeforeFields = Checked.Sum(c => c.FieldsGone),
            leavesCompared = Checked.Sum(c => c.FieldsCompared),
            leavesEqual = Checked.Sum(c => c.FieldsMatched),
            caps = new { entitiesPerType = MaxEntities, arrayElements = TypedReadCheck.ArrayCap, leavesPerEntity = TypedReadCheck.LeafCap },
            skipped = Checked.SelectMany(c => c.Skipped).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)),
            mismatches = _plan.SelectMany(c => c.Mismatches).Take(200).ToList(),
            coverage = Coverage(_plan).Select(x => new { x.What, x.Ok }),
            selfTest = _selfTest,
            legacy = _legacy.Select(r => new { r.Name, r.Size, r.Entities, r.Matched, mismatches = r.Mismatches.Count }),
            perType = _plan.Select(c => new
            {
                c.QualifiedName, c.Size, c.LiveEntities, refused = _refused.ContainsKey(c.QualifiedName),
                features = c.Features.Order(StringComparer.Ordinal), bytes = c.BytesCompared, bytesEqual = c.BytesMatched,
                fieldEntities = c.FieldEntities, fieldsGone = c.FieldsGone, leaves = c.FieldsCompared, leavesEqual = c.FieldsMatched,
                skipped = c.Skipped.Values.Sum(), mismatches = c.Mismatches.Count,
            }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"typegen-check: summary written to {path}");
    }

    void RequirePlan()
    {
        if (_plan.Count == 0) Assert.Fail("no plan: the first test did not run or found nothing");
    }

    /// <summary>The same comparison on a world this game client hosts, where Kiln.Game's layouts meet the client exe.</summary>
    [ClientCheck("typegen.typed-reads", "Typed game types read the client's memory correctly", Area = "Typegen",
                 Since = "0.2.0-dev.25", Needs = ClientCheckNeeds.Hosted)]
    public async Task Typed_reads_on_a_hosted_world(CheckContext c)
    {
        c.Instruct("Nothing to do in the game: the check reads every generated component with live entities through " +
                   "Kiln.Ecs and compares it with the raw bytes and the game's reflection. Give Pass when every line is met.");
        c.Expect(Ecs.Available, "kiln.world is loaded");
        if (!Ecs.Available) { await c.Verdict(); return; }
        var plan = Plan(20);
        c.Expect(plan.Count >= 20, "at least 20 component types with live entities", $"{plan.Count} found");
        foreach (var (what, ok) in Coverage(plan)) c.Expect(ok, $"the checked types include {what}");
        var unsupported = plan.Where(x => !x.Checker.Supports()).Select(x => $"{x.QualifiedName}: {x.Checker.WhyNot()}").ToList();
        c.Expect(unsupported.Count == 0, "Ecs.Supports is true for every checked type", string.Join("; ", unsupported.Take(5)));
        var sw = Stopwatch.StartNew();
        foreach (var x in plan.Where(x => x.Checker.Supports()))
        {
            TypedReadCheck.RunBytes(x, 20);
            if (GameTypes.Available)
                foreach (var e in x.Entities) { TypedReadCheck.RunFields(x, e); if (sw.Elapsed > Slice) { await c.Ticks(1); sw.Restart(); } }
            if (sw.Elapsed > Slice) { await c.Ticks(1); sw.Restart(); }
        }
        var bad = plan.SelectMany(x => x.Mismatches).ToList();
        c.Expect(plan.Sum(x => x.BytesCompared) > 0 && bad.Count == 0, "typed bytes equal the raw bytes",
                 $"{plan.Sum(x => x.BytesCompared)} entities, {bad.Count} mismatches: {string.Join("; ", bad.Take(3))}");
        c.Expect(GameTypes.Available && plan.Sum(x => x.FieldsCompared) > 0, "every leaf equals the game's reflection",
                 $"{plan.Sum(x => x.FieldsCompared)} leaves compared, {plan.Sum(x => x.FieldsMatched)} equal");
        var legacy = LegacyCheck.Run(20);
        c.Expect(legacy.All(r => r.Mismatches.Count == 0), "the legacy nine equal the raw bytes",
                 string.Join("; ", legacy.SelectMany(r => r.Mismatches).Take(3)));
        await c.Verdict();
    }
}
