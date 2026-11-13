// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;

namespace BakeTest;

/// <summary>
/// Definitions the game reads only at start, made in code: two quests built in a loop, a game-tree node and a VFX. Kiln.Sdk's bake
/// wrote them into this mod at build; a server started with an empty kiln/data folder must have each at this, its first start.
/// </summary>
public sealed class BakeTestMod : Mod
{
    /// <summary>The node's GUID: never change a released one.</summary>
    public static readonly Guid NodeGuid = new("7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a");

    // what the code computes its definitions from
    private static readonly (string Id, string Name, int Count)[] Errands = [("cinder_count", "Count the Cinders", 3), ("ash_count", "Count the Ash", 5)];

    private readonly List<QuestDefinition> _quests = [];
    private IReadOnlyList<GameNodeInfo> _nodeReport = [];
    private VfxBlockHandle? _spark;
    private bool _dataEmptyAtLoad, _questsKeptAtLoad, _nodeKeptAtLoad, _vfxKeptAtLoad;
    private int _pass, _fail;

    public override void OnLoad()
    {
        // What Kiln keeps from code for the next start, before this start's code ran: nothing, on a first start.
        var data = Path.GetDirectoryName(Context.DataDirectory.TrimEnd('/', '\\')) ?? "";
        _questsKeptAtLoad = File.Exists(Path.Combine(data, "kiln-quests", "native", "bake-test.json"));
        _nodeKeptAtLoad = File.Exists(Path.Combine(data, "skills", "defined", "bake-test.json"));
        _vfxKeptAtLoad = Directory.Exists(Path.Combine(data, "vfx", "pending", "bake-test"));
        _dataEmptyAtLoad = !_questsKeptAtLoad && !_nodeKeptAtLoad && !_vfxKeptAtLoad;

        foreach (var (id, name, count) in Errands)
            _quests.Add(Quests.Define(new QuestDefinition
            {
                Id = id,
                Name = UiText.Of(("en", name), ("de", name + " (de)")),
                Scope = QuestScope.World,
                Steps = [new QuestStep { Id = "count", Text = $"Count {count}", Objectives = [new QuestObjective { Kind = QuestObjectiveKind.Custom, Count = count, Progress = _ => 0 }] }],
            }));

        if (GameTree.Available)
            _nodeReport = GameTree.Define(new GameNodeDefinition
            {
                Guid = NodeGuid,
                Name = "baketest_cinder",
                Type = "Attribute",
                Class = "Mage",
                Like = "0xcfa5985b",
                From = ["0xcfa5985b"],
                Offset = (170, 40),
                Cost = 1,
                Texts = new Dictionary<string, (string, string, string?)> { ["en"] = ("Cinder", "Baked at build.", null) },
            });

        _spark = Blocks.Define("baked_spark", new VfxBlock
        {
            Particles =
            [
                new ParticleBlock
                {
                    Burst = 6,
                    Lifetime = (0.5f, 1.0f),
                    Speed = (0.5f, 1.5f),
                    Spread = Shape.Sphere,
                    Size = Curve.Linear(0.2f, 0.6f),
                    Color = "#ffb050",
                    Alpha = Curve.Linear(1f, 0f),
                },
            ],
        });
    }

    [Subscribe]
    private void OnStarted(ServerStartedEvent e) => _ = Check();

    private async Task Check()
    {
        var dir = Context.Info.Directory;
        Info($"kiln/data at load: quests kept {_questsKeptAtLoad}, node kept {_nodeKeptAtLoad}, VFX kept {_vfxKeptAtLoad}" +
             (_dataEmptyAtLoad ? " (a first start: nothing Kiln keeps from code yet)" : " (not a first start: a check below proves less)"));

        // Quests: the game's journal is built at boot, before this code ran; only the mod's files can have them now.
        Expect(File.Exists(Path.Combine(dir, "quests", "generated", "bake-test.json")), "the mod has quests/generated/bake-test.json, baked at build");
        foreach (var q in _quests)
            Expect(NativeQuests.ShownByGame(q), $"quest {q.FullId} is in the game's journal at this start");

        // The game-tree node: kiln.skills read it at boot from the mod's skills/bake-test.generated.json.
        if (GameTree.Available)
        {
            var def = GameTree.Definitions.FirstOrDefault(d => d.Guid == NodeGuid);
            Expect(def is not null && def.Source.EndsWith("bake-test.generated.json", StringComparison.OrdinalIgnoreCase),
                $"node baketest_cinder is defined from the baked file ({def?.Source ?? "not defined"})");
            Expect(def?.State == "in-tree", $"node baketest_cinder is in the game's tree at this start (state {def?.State ?? "none"})");
            Expect(_nodeReport.Count == 1 && _nodeReport[0].State == "in-tree",
                $"GameTree.Define in OnLoad left the baked node as it was ({string.Join(", ", _nodeReport.Select(r => r.State))})");
        }
        else Info("kiln.skills is not here: the node is not checked");

        // The VFX: kiln.vfx builds at start (a client, or a lab with kiln/config/vfx.json {"sim": true}); a dedicated server has none.
        if (_spark is { } spark)
        {
            if (spark.Status == VfxBlockStatus.NotHere) Info("kiln.vfx is not here (a dedicated server): the VFX is not checked");
            else Expect(spark.Status == VfxBlockStatus.Registered, $"VFX baked_spark is registered at this start (status {spark.Status})");
        }

        // What the code defines at run time is what was baked: Kiln.Quests writes its own copy within a second; the same document.
        for (var i = 0; i < 100; i++) await Context.Scheduler.NextTick();
        var data = Path.GetDirectoryName(Context.DataDirectory.TrimEnd('/', '\\')) ?? "";
        var written = Path.Combine(data, "kiln-quests", "native", "bake-test.json");
        var baked = Path.Combine(dir, "quests", "generated", "bake-test.json");
        if (File.Exists(written) && File.Exists(baked))
        {
            var a = JsonNode.Parse(File.ReadAllText(written))?["quests"];
            var b = JsonNode.Parse(File.ReadAllText(baked))?["quests"];
            Expect(JsonNode.DeepEquals(a, b), "the quests the code defines at run time equal the baked ones");
        }
        else Expect(false, $"both quest documents exist (run time {File.Exists(written)}, baked {File.Exists(baked)})");
        if (GameTree.Available)
            Expect(!File.Exists(Path.Combine(data, "skills", "defined", "bake-test.json")) || _nodeKeptAtLoad,
                "kiln.skills keeps nothing for the next start: the node the code defines is the baked one");
        if (_spark is { Status: not VfxBlockStatus.NotHere })
            Expect(!Directory.Exists(Path.Combine(data, "vfx", "pending", "bake-test")) || _vfxKeptAtLoad,
                "kiln.vfx keeps nothing for the next start: the VFX the code defines is the baked one");
        Info($"bake-test done: {_pass} PASS, {_fail} FAIL");
    }

    private void Expect(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"bake-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Info(string what) => Log.Info($"bake-test: INFO {what}");
}
