// SPDX-License-Identifier: MIT
// Measured path clearance (19d §4.3a, gate C6): clearance.json format 1, keyed by creature, scale and game build, checked
// against the body it was measured on; the defining mod's profiles, then the server's, then Kiln's shipped ones; a game
// update or a changed body means catch-up only (ClearanceMappingUnverified), and the follower asks for exactly the
// accepted pair.
using System.Numerics;
using System.Text.Json.Nodes;
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

public sealed class ClearanceTests : IDisposable
{
    private const int Build = 1287369;
    private static readonly PetClearanceBody Wolf = new(0, 2, 0.8, 1.4);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-pets-clearance-" + Guid.NewGuid().ToString("N"));
    private readonly PetDefinition _def = new() { Id = "mod-a:wolf", Creature = "game:enemy_wildbeast_wolf" };
    private double _clock;

    public ClearanceTests()
    {
        Directory.CreateDirectory(_dir);
        PetClearance.ResetForTests();
        PetFollower.ResetForTests();
        PetClearance.SourcesOn = true;
        PetClearance.GameBuild = () => Build;
        PetClearance.BodyOf = _ => Wolf;
        PetClearance.ShippedJson = () => null;
        PetClearance.ServerFile = () => Path.Combine(_dir, "server", "clearance.json");
        PetClearance.ModFolder = id => Path.Combine(_dir, "mods", id.ToLowerInvariant());
        PetClearance.Clock = () => _clock;
    }

    public void Dispose()
    {
        PetClearance.ResetForTests();
        PetFollower.ResetForTests();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Profile(string creature = "game:enemy_wildbeast_wolf", double scale = 1, int game = Build, double h = 1, double v = 2,
        PetClearanceBody? body = null)
    {
        var b = body ?? Wolf;
        return new JsonObject
        {
            ["creature"] = creature, ["scale"] = scale, ["game"] = game, ["horizontalSpace"] = h, ["verticalSpace"] = v,
            ["body"] = new JsonObject { ["walkerRadius"] = b.WalkerRadius, ["walkerHeight"] = b.WalkerHeight, ["colliderWidth"] = b.ColliderWidth, ["colliderHeight"] = b.ColliderHeight },
            ["calibration"] = new JsonObject { ["lab"] = "test" },
        }.ToJsonString();
    }

    private static string File1(params string[] profiles) => $"{{\"format\": 1, \"profiles\": [{string.Join(",", profiles)}]}}";

    private void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string ServerFile => Path.Combine(_dir, "server", "clearance.json");
    private string ModFile(string mod) => Path.Combine(_dir, "mods", mod, "pets", "clearance.json");

    // ---- the format ----

    [Fact]
    public void A_format_1_file_gives_its_measured_profiles()
    {
        var list = PetClearance.Parse(File1(Profile(h: 1, v: 2), Profile("game:enemy_vukah_melee", 1.5, Build, 0.5, 3)), "test", out var problems);
        Assert.Empty(problems);
        Assert.Equal(2, list.Count);
        Assert.Equal(new PetClearanceEntry("game:enemy_wildbeast_wolf", 1, Build, new PetClearanceProfile(1, 2), Wolf, "test"), list[0]);
        Assert.Equal((1.5, 0.5, 3.0), (list[1].Scale, list[1].Profile.HorizontalSpace, list[1].Profile.VerticalSpace));
    }

    [Fact]
    public void A_profile_without_its_game_build_or_its_body_or_out_of_range_is_left_out_and_named()
    {
        var noGame = """{"creature": "game:a", "scale": 1, "horizontalSpace": 1, "verticalSpace": 2, "body": {"walkerRadius": 0, "walkerHeight": 2, "colliderWidth": 1, "colliderHeight": 1}}""";
        var noBody = """{"creature": "game:b", "scale": 1, "game": 5, "horizontalSpace": 1, "verticalSpace": 2}""";
        var badScale = Profile("game:c", 9);
        var badSpace = Profile("game:d", 1, Build, -1, 2);
        var badId = Profile("wolf");
        var list = PetClearance.Parse(File1(noGame, noBody, badScale, badSpace, badId, Profile()), "test", out var problems);
        Assert.Single(list);
        Assert.Equal(5, problems.Count);
        Assert.Contains(problems, p => p.Contains("game:a") && p.Contains("\"game\""));
        Assert.Contains(problems, p => p.Contains("game:b") && p.Contains("\"body\""));
        Assert.Contains(problems, p => p.Contains("game:c") && p.Contains("scale"));
        Assert.Contains(problems, p => p.Contains("game:d") && p.Contains("horizontalSpace"));
        Assert.Contains(problems, p => p.Contains("profiles[4]") && p.Contains("Kiln id"));
    }

    [Fact]
    public void The_unbuilt_bare_list_and_other_formats_give_nothing()
    {
        Assert.Empty(PetClearance.Parse("""[{"creature": "game:a", "scale": 1, "horizontalSpace": 1, "verticalSpace": 2}]""", "t", out var p1));
        Assert.Contains("bare list", Assert.Single(p1));
        Assert.Empty(PetClearance.Parse("""{"format": 2, "profiles": []}""", "t", out var p2));
        Assert.Single(p2);
        Assert.Empty(PetClearance.Parse("{not json", "t", out var p3));
        Assert.StartsWith("not JSON", Assert.Single(p3));
    }

    [Fact]
    public void The_shipped_profiles_embedded_in_Kiln_Pets_are_a_valid_format_1_file()
    {
        PetClearance.ResetForTests();   // the real resource reader
        using var s = typeof(PetClearance).Assembly.GetManifestResourceStream(PetClearance.ShippedResource);
        Assert.NotNull(s);
        var text = new StreamReader(s!).ReadToEnd();
        PetClearance.Parse(text, "shipped", out var problems);
        Assert.Empty(problems);
        Assert.Equal(PetClearance.Format, JsonNode.Parse(text)!["format"]!.GetValue<int>());
    }

    /// <summary>The game creatures Kiln calibrated (tools/pets-clearance, gate C6, on a dedicated lab server): the creature, its
    /// scale, its body as measured (walker radius and height, collider width and height) and the pair that passed.</summary>
    public static TheoryData<string, double, double, double, double, double, double, double> Calibrated => new()
    {
        { "game:enemy_vukah_melee", 1, 0, 2, 1, 2, 0.5, 2 },
        { "game:enemy_vukah_melee_elite_armored", 1, 0, 2, 1, 2.5, 0.5, 3 },
        { "game:enemy_wildbeast_wolf_earlygame", 1, 0, 2, 1, 1, 0.5, 2 },
        { "game:enemy_wildbeast_wolf_earlygame", 1.5, 0, 2, 1, 1, 2, 3 },
        { "game:enemy_wildbeast_wolf_hook_ag2", 1, 0, 2, 1, 1, 0.5, 2 },
        { "game:enemy_wildbeast_boar", 1, 0, 2, 1, 1, 0.5, 2 },
        { "game:enemy_fogger_wildbeast_infestedboar", 1, 0, 2, 1, 1.5, 0.5, 2 },
        { "game:enemy_wildbeast_sabertooth", 1, 0, 2, 1, 1, 0.5, 2 },
        { "game:enemy_scavenger_hound", 1, 0, 2, 1, 1, 0.5, 2 },
        { "game:enemy_skeleton_hound", 1, 0, 2, 1, 1, 0.5, 2 },
        { "game:enemy_wildbeast_spiderant_melee", 1, 0, 2, 1, 1, 0.5, 2 },
        { "game:enemy_wildbeast_spiderant_huge_hookpte", 1, 0, 2, 2, 2, 0.5, 2 },
        { "game:enemy_wildbeast_rat_hook", 1, 0, 2, 0.75, 0.75, 0.5, 2 },
    };

    [Theory]
    [MemberData(nameof(Calibrated))]
    public void Kiln_ships_the_measured_pair_of_each_calibrated_creature_for_its_game_build_and_body(string creature, double scale,
        double walkerRadius, double walkerHeight, double colliderWidth, double colliderHeight, double h, double v)
    {
        PetClearance.ResetForTests();   // the real shipped resource
        PetClearance.SourcesOn = true;
        PetClearance.GameBuild = () => Build;
        PetClearance.BodyOf = _ => new PetClearanceBody(walkerRadius, walkerHeight, colliderWidth, colliderHeight);
        PetClearance.ServerFile = () => ServerFile;
        PetClearance.ModFolder = id => Path.Combine(_dir, "mods", id);
        PetClearance.Clock = () => _clock;
        var def = new PetDefinition { Id = "mod-a:pet", Creature = creature, Level = new PetLevelPolicy { Scale = scale } };
        Assert.Equal(new PetClearanceProfile(h, v), PetClearance.For(def, out var why));
        Assert.Null(why);
        // measured at that scale only
        Assert.Null(PetClearance.For(def with { Level = new PetLevelPolicy { Scale = 2 } }, out _));
        // a game update: catch-up only until Kiln ships a new calibration
        PetClearance.GameBuild = () => Build + 1;
        Assert.Null(PetClearance.For(def, out why));
        Assert.Contains("was measured on game build 1287369", why);
    }

    // ---- the answer ----

    [Fact]
    public void Without_any_profile_the_creature_is_unverified()
    {
        Assert.Null(PetClearance.For(_def, out var why));
        Assert.StartsWith("ClearanceMappingUnverified: no measured path clearance for game:enemy_wildbeast_wolf at scale 1", why);
    }

    [Fact]
    public void A_profile_of_the_running_build_and_body_is_accepted()
    {
        Put(ServerFile, File1(Profile(h: 1, v: 2)));
        Assert.Equal(new PetClearanceProfile(1, 2), PetClearance.For(_def, out var why));
        Assert.Null(why);
    }

    [Fact]
    public void A_game_update_invalidates_the_profile()
    {
        Put(ServerFile, File1(Profile(game: Build - 1)));
        Assert.Null(PetClearance.For(_def, out var why));
        Assert.StartsWith("ClearanceMappingUnverified: the path clearance for game:enemy_wildbeast_wolf at scale 1 was measured on game build 1287368, this is 1287369", why);
    }

    [Fact]
    public void An_unknown_game_build_verifies_nothing()
    {
        Put(ServerFile, File1(Profile()));
        PetClearance.GameBuild = () => null;
        Assert.Null(PetClearance.For(_def, out var why));
        Assert.Contains("does not know this game build", why);
    }

    [Fact]
    public void A_changed_body_invalidates_the_profile_and_an_unreadable_one_verifies_nothing()
    {
        Put(ServerFile, File1(Profile()));
        PetClearance.BodyOf = _ => Wolf with { ColliderWidth = 1.2 };
        Assert.Null(PetClearance.For(_def, out var why));
        Assert.Contains("changed since its path clearance was measured", why);
        Assert.Contains("collider width 0.8", why);
        Assert.Contains("collider width 1.2", why);
        PetClearance.BodyOf = _ => null;
        Assert.Null(PetClearance.For(_def, out why));
        Assert.Contains("cannot be read here", why);
    }

    [Fact]
    public void A_profile_is_per_scale()
    {
        Put(ServerFile, File1(Profile(scale: 1)));
        var big = _def with { Level = new PetLevelPolicy { Scale = 1.5 } };
        Assert.Null(PetClearance.For(big, out var why));
        Assert.Contains("at scale 1.5", why);
        Assert.NotNull(PetClearance.For(_def, out _));
    }

    [Fact]
    public void The_defining_mods_own_profiles_come_first_and_apply_to_its_pets_only()
    {
        Put(ServerFile, File1(Profile(h: 1, v: 2)));
        PetClearance.ShippedJson = () => File1(Profile(h: 2, v: 3));
        Put(ModFile("mod-a"), File1(Profile(h: 0.5, v: 1)));
        Assert.Equal(new PetClearanceProfile(0.5, 1), PetClearance.For(_def, out _));
        // another mod's pet of the same creature: the server's file, not mod-a's
        Assert.Equal(new PetClearanceProfile(1, 2), PetClearance.For(_def with { Id = "mod-b:wolf" }, out _));
        // no server file: Kiln's shipped profile
        File.Delete(ServerFile);
        _clock += 11;
        Assert.Equal(new PetClearanceProfile(2, 3), PetClearance.For(_def with { Id = "mod-b:wolf" }, out _));
    }

    [Fact]
    public void A_later_source_answers_when_an_earlier_one_has_only_another_build()
    {
        Put(ModFile("mod-a"), File1(Profile(game: Build - 1, h: 0.5, v: 1)));
        PetClearance.ShippedJson = () => File1(Profile(h: 2, v: 3));
        Assert.Equal(new PetClearanceProfile(2, 3), PetClearance.For(_def, out _));
    }

    [Fact]
    public void A_changed_file_is_read_again_after_the_recheck_interval()
    {
        Assert.Null(PetClearance.For(_def, out _));
        Put(ServerFile, File1(Profile()));
        _clock += 1;
        Assert.Null(PetClearance.For(_def, out _));   // within 10 s: the earlier answer
        _clock += 10;
        Assert.NotNull(PetClearance.For(_def, out _));
    }

    [Fact]
    public void A_test_override_applies_whatever_the_build_and_body()
    {
        PetClearance.GameBuild = () => null;
        PetClearance.BodyOf = _ => null;
        PetClearance.SetForTests("game:enemy_wildbeast_wolf", 1, new PetClearanceProfile(0.5, 3));
        Assert.Equal(new PetClearanceProfile(0.5, 3), PetClearance.For(_def, out _));
        PetClearance.SetForTests("game:enemy_wildbeast_wolf", 1, null);
        Assert.Null(PetClearance.For(_def, out _));
    }

    [Fact]
    public void The_build_number_comes_from_Kilns_build_record_of_the_running_exe()
    {
        var builds = Path.Combine(_dir, "builds");
        var server = new string('a', 64);
        var client = new string('b', 64);
        Put(Path.Combine(builds, "server", server + ".json"), """{"sha256": "aaa", "side": "server", "svn": 1287369, "steam_build": 26430253}""");
        Put(Path.Combine(builds, "client", client + ".json"), """{"sha256": "bbb", "side": "client", "svn": 1287370}""");
        Assert.Equal(1287369, PetClearance.BuildNumber(builds, server));
        Assert.Equal(1287370, PetClearance.BuildNumber(builds, client));
        Assert.Null(PetClearance.BuildNumber(builds, new string('c', 64)));
        Put(Path.Combine(builds, "server", "d.json"), """{"sha256": "d", "side": "server"}""");
        Assert.Null(PetClearance.BuildNumber(builds, "d"));
    }

    // ---- the follower asks for exactly the accepted pair ----

    private sealed class Mover : IPetMover
    {
        public readonly List<PetClearanceProfile> Asked = [];
        public WorldPosition At = new(0, 0, 0);
        public bool CanDrive(PetBody body) => true;
        public bool Fighting(PetBody body) => false;
        public WorldPosition? Position(PetBody body) => At;
        public void Write(PetBody body, Vector3 move, Vector3 look, uint speed) { }
        public object? RequestPath(PetBody body, WorldPosition from, WorldPosition to, PetClearanceProfile clearance)
        {
            Asked.Add(clearance);
            return new object();
        }
        public (PetRoute? Route, string? Refusal)? PollPath(object request) => null;
        public void CancelPath(object request) { }
        public bool Teleport(PetBody body, WorldPosition to) => false;
        public WorldPosition? SafeGround(WorldPosition near, double min, double max, PetDefinition def) => null;
        public void Guard(PetBody body, EntityId owner) { }
        public void Attack(PetBody body, EntityId target) { }
        public bool TargetAlive(EntityRef target) => false;
    }

    private (PetFollower F, Mover M) Follow(PetDefinition def)
    {
        var m = new Mover();
        var f = new PetFollower();
        var pet = new PetState { Id = new PetId(Guid.NewGuid()), Body = new PetBody(new EntityRef(new EntityId(3000), 1), 1, 1), Presence = PetPresence.Live, Life = PetLife.Ready };
        var owner = new PetOwner(new CharacterKey(1, Guid.NewGuid()), new EntityRef(new EntityId(1), 1), new WorldPosition(20, 0, 0), 10, 1);
        f.Step(pet, def, owner, m, 0);
        return (f, m);
    }

    [Fact]
    public void The_follower_asks_for_the_measured_pair_of_the_running_build()
    {
        Put(ServerFile, File1(Profile(game: Build - 1, h: 2, v: 3), Profile(h: 0.5, v: 2)));
        var (f, m) = Follow(_def with { Movement = new PetMovementPolicy { AllowCatchUp = false } });
        Assert.Equal(new PetClearanceProfile(0.5, 2), Assert.Single(m.Asked));
        Assert.Null(f.Limited);
    }

    [Fact]
    public void After_a_game_update_the_follower_walks_no_route_and_says_why()
    {
        Put(ServerFile, File1(Profile(game: Build - 1)));
        var (f, m) = Follow(_def with { Movement = new PetMovementPolicy { AllowCatchUp = false } });
        Assert.Empty(m.Asked);
        Assert.StartsWith("ClearanceMappingUnverified: the path clearance for game:enemy_wildbeast_wolf at scale 1 was measured on game build 1287368", f.Limited);
    }
}
