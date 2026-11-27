// SPDX-License-Identifier: MIT
// What a calibration session measures: kiln/data/pets-clearance-lab/plan.json (tools/pets-clearance/calibrate.sh writes it).
//   {"creatures": [{"id": "game:enemy_wildbeast_wolf_earlygame", "scale": 1}], "fixtures": null (all), "horizontal": [0.5, 1, 2],
//    "vertical": [1, 2, 3], "native": true, "census": false, "walkSeconds": 20, "nativeSeconds": 20,
//    "roster": true, "rosterSeconds": 60}
// A creature is a Kiln id: a game creature (game:<name>) or a creature block of a mod deployed with the lab (<mod>:<name>).
using System.Text.Json.Nodes;

namespace PetsClearanceLab;

internal sealed record PlanCreature(string Id, double Scale);

internal sealed record Plan
{
    public List<PlanCreature> Creatures { get; init; } = [new("game:enemy_wildbeast_wolf_earlygame", 1)];
    /// <summary>Fixture names to build and walk; null: all of <see cref="FixtureSpec.All"/>.</summary>
    public List<string>? Fixtures { get; init; }
    public double[] Horizontal { get; init; } = [0.5, 1, 2];
    public double[] Vertical { get; init; } = [1, 2, 3];
    /// <summary>Also walk each fixture with the body's own walker (the game's guard walk, AI on), as the comparison.</summary>
    public bool Native { get; init; } = true;
    /// <summary>Write the census of the game's enemy creatures (their walker and collider numbers).</summary>
    public bool Census { get; init; }
    public double WalkSeconds { get; init; } = 20;
    public double NativeSeconds { get; init; } = 20;
    /// <summary>The owner step: a pet of the first creature follows a connected player (LABBOT's bot) through the Pets
    /// roster; skipped when no player is connected.</summary>
    public bool Roster { get; init; } = true;
    public double RosterSeconds { get; init; } = 60;

    public IReadOnlyList<FixtureSpec> Specs => Fixtures is null ? FixtureSpec.All : FixtureSpec.All.Where(f => Fixtures.Contains(f.Name)).ToList();

    public static Plan Read(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return new Plan();
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new FormatException("not an object");
            var p = new Plan();
            if (o["creatures"] is JsonArray cs)
                p = p with
                {
                    Creatures = cs.OfType<JsonObject>().Select(c => new PlanCreature(c["id"]!.GetValue<string>(), c["scale"]?.GetValue<double>() ?? 1)).ToList(),
                };
            if (o["fixtures"] is JsonArray fs) p = p with { Fixtures = fs.Select(f => f!.GetValue<string>()).ToList() };
            if (o["horizontal"] is JsonArray hs) p = p with { Horizontal = hs.Select(h => h!.GetValue<double>()).ToArray() };
            if (o["vertical"] is JsonArray vs) p = p with { Vertical = vs.Select(v => v!.GetValue<double>()).ToArray() };
            if (o["native"] is JsonValue n) p = p with { Native = n.GetValue<bool>() };
            if (o["census"] is JsonValue c0) p = p with { Census = c0.GetValue<bool>() };
            if (o["walkSeconds"] is JsonValue w) p = p with { WalkSeconds = w.GetValue<double>() };
            if (o["nativeSeconds"] is JsonValue ns) p = p with { NativeSeconds = ns.GetValue<double>() };
            if (o["roster"] is JsonValue r) p = p with { Roster = r.GetValue<bool>() };
            if (o["rosterSeconds"] is JsonValue rs) p = p with { RosterSeconds = rs.GetValue<double>() };
            return p;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or System.Text.Json.JsonException or NullReferenceException)
        {
            problem = $"plan.json not read ({e.Message}); the default plan runs";
            return new Plan();
        }
    }
}
