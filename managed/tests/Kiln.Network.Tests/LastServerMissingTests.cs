// SPDX-License-Identifier: MIT
// last-server.json's "missing" after a WELCOME names only mods this player really lacks, of every engine (netjunk's
// report, 2026-11-26): a player hosting his own world was told "welcome from srv-8383ec52951c, missing creative,
// altar-cleanup, combat_tweaks, sf-survival, speedy-gonzales" for the Flame and ShroudForge mods the same game ran. The
// client looked only at its Kiln and native mods, and hashed any other kind by its whole folder, while the server
// advertises another engine's mod by its identity (AdvertiseIdentity.cs).
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Network.Tests;

[Collection(nameof(SteamGuardRelayTests))]  // ModsBackend, KilnPaths and NetAdvertise's statics: never at once with the engine tests
public sealed class LastServerMissingTests : IDisposable
{
    private const string Library = "kdh1:86446046e44846ce307a582a86c488b98a454013e75447dd849323cffeb3d773";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kiln-last-server-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ModsBackend.Current = null;
        NetEngine.TestServerRole = null;
        KilnPaths.Root = null!;
        NetAdvertise.Reset();
        Settle();
        try { Directory.Delete(_tmp, true); }
        catch (IOException) { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A game folder with the mods of netjunk's world, one of each engine the server advertises.</summary>
    private string Game(string name, bool sfSurvival = true)
    {
        var game = Path.Combine(_tmp, name);
        Directory.CreateDirectory(Path.Combine(game, "kiln", "state"));
        // Flame: a mod.json with its id, flame.json, its Lua
        Write(Path.Combine(game, "mods", "creative", "flame.json"), """{"entry":"main.lua"}""");
        Write(Path.Combine(game, "mods", "creative", "mod.json"), """{"id":"creative","name":"Creative","version":"3.2.0"}""");
        Write(Path.Combine(game, "mods", "creative", "main.lua"), "return {}");
        // ShroudForge, placed by Kiln's launcher (the receipt names it by the launcher's path)
        var altar = Path.Combine(game, "mods", "altar-cleanup");
        Write(Path.Combine(altar, "mod.json"), """{"id":"altar-cleanup","version":"1.0.0"}""");
        Write(Path.Combine(altar, "extended.mod.json"), """{"schemaVersion":1,"enabled":true,"targets":["client","server"]}""");
        Write(Path.Combine(altar, "altar.dll"), "MZ altar");
        var marker = new { format = 1, kind = "shroudforge", id = "altar-cleanup", version = "1.0.0", hash = Library };
        Write(Path.Combine(altar, ".kiln-library.json"), JsonSerializer.Serialize(marker));
        Write(Path.Combine(game, "kiln", "state", "library-placements.json"), JsonSerializer.Serialize(new[]
        {
            new
            {
                path = @"D:\SteamLibrary\steamapps\common\Enshrouded\mods\altar-cleanup", marker,
                files = new Dictionary<string, string>
                {
                    ["mod.json"] = Sha(Path.Combine(altar, "mod.json")), ["altar.dll"] = Sha(Path.Combine(altar, "altar.dll")),
                    ["extended.mod.json"] = "0000000000000000000000000000000000000000000000000000000000000000",
                },
            },
        }));
        // ShroudForge, a folder of its own (an id with "_")
        Write(Path.Combine(game, "mods", "combat_tweaks", "mod.json"), """{"id":"combat_tweaks","version":"0.7.2"}""");
        Write(Path.Combine(game, "mods", "combat_tweaks", "extended.mod.json"), """{"schemaVersion":1,"enabled":true}""");
        Write(Path.Combine(game, "mods", "combat_tweaks", "combat.dll"), "MZ combat");
        if (sfSurvival)
        {
            Write(Path.Combine(game, "mods", "sf-survival", "mod.json"), """{"id":"sf-survival","version":"1.0.0"}""");
            Write(Path.Combine(game, "mods", "sf-survival", "extended.mod.json"), """{"schemaVersion":1,"enabled":true}""");
            Write(Path.Combine(game, "mods", "sf-survival", "survival.dll"), "MZ survival");
        }
        // EML, EMF and Green Eye mods that say players need them
        Write(Path.Combine(game, "kiln", "eml", "mods", "eml-tweaks", "mod.json"), """{"id":"eml-tweaks","version":"2.0.0"}""");
        Write(Path.Combine(game, "kiln", "eml", "mods", "eml-tweaks", "src", "mod.lua"), "return {}");
        Write(Path.Combine(game, "kiln", "eml", "mods", "eml-tweaks", "config.ini"), "[a]\nb=1\n");
        Write(Path.Combine(game, "emf", "mods", "beastmaster", "mod.json"), """{"id":"beastmaster","version":"0.4.0","clients":"mod"}""");
        Write(Path.Combine(game, "emf", "mods", "beastmaster", "main.lua"), "return {}");
        Write(Path.Combine(game, "greeneye", "find", "mod.json"), """{"id":"find","version":"1.1.0","clients":"mod"}""");
        Write(Path.Combine(game, "greeneye", "find", "find.dll"), "MZ find");
        return game;
    }

    /// <summary>What the mods write and the player sets while the game runs.</summary>
    private static void Run(string game, string tick)
    {
        Write(Path.Combine(game, "mods", "creative", "creative.log"), "started " + tick);
        Write(Path.Combine(game, "mods", "altar-cleanup", "extended.mod.json"), """{"schemaVersion":1,"enabled":true,"targets":["client","server"],"settings":{"x":""" + tick.Length + "}}");
        Write(Path.Combine(game, "mods", "combat_tweaks", "extended.mod.json"), """{"schemaVersion":1,"enabled":true,"settings":{"t":""" + tick.Length + "}}");
        Write(Path.Combine(game, "mods", "combat_tweaks", "combat.log"), "hit " + tick);
        Write(Path.Combine(game, "kiln", "eml", "mods", "eml-tweaks", "config.ini"), "[a]\nb=" + tick.Length + "\n");
    }

    /// <summary>What a server in <paramref name="game"/> advertises: NetAdvertise's own recompute, no Kiln mods loaded.</summary>
    private static IReadOnlyList<NetRequiredMod> Advertised(string game)
    {
        KilnPaths.Root = Path.Combine(game, "kiln");
        ModsBackend.Current = new FakeMods();
        NetAdvertise.Refresh();
        Settle();
        return NetAdvertise.Required;
    }

    private static readonly string[] Engines = ["altar-cleanup", "beastmaster", "combat_tweaks", "creative", "eml-tweaks", "find", "sf-survival"];

    [Fact]
    public void The_server_advertises_each_engines_mods_players_need()
    {
        var game = Game("host");
        var required = Advertised(game);
        Assert.Equal(Engines, required.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Equal(Library, required.Single(r => r.Id == "altar-cleanup").Hash);
        Assert.Equal("flame", required.Single(r => r.Id == "creative").Kind);
    }

    // A hosted world: the client and the server are one game, one install. Nothing the world runs is missing, while the
    // mods write their logs and the player changes settings.
    [Fact]
    public void A_hosted_worlds_other_engine_mods_are_not_missing()
    {
        var game = Game("host");
        Run(game, "1");
        var required = Advertised(game);
        Run(game, "22");
        Assert.Empty(LastServer.Lacking(required, [], game));
    }

    // The same with the engine mods named (the client's list before the fix had only Kiln and native ones): another engine's
    // mod is compared by what the server advertises for it, its identity, not by its whole folder.
    [Fact]
    public void Another_engines_mod_is_compared_by_its_identity()
    {
        var game = Game("host");
        Run(game, "1");
        var required = Advertised(game);
        Run(game, "22");
        var local = required.Select(r => new LastServer.Local(r.Kind, r.Id, Folder(game, r))).ToList();
        Assert.Empty(LastServer.Missing(required, local));  // the game: KilnPaths.Server, as in the client
    }

    // A dedicated server: the player's own game folder has the same mods (its own logs and settings) but not sf-survival,
    // and another build of combat_tweaks. Only those two are missing; a Kiln mod of the same id is not a ShroudForge mod.
    [Fact]
    public void A_players_game_lacks_only_what_it_does_not_have()
    {
        var server = Game("server");
        Run(server, "1");
        var required = Advertised(server);
        var player = Game("player", sfSurvival: false);
        Run(player, "333");
        File.WriteAllText(Path.Combine(player, "mods", "combat_tweaks", "combat.dll"), "MZ combat, another build");
        var kilnNamesake = Path.Combine(_tmp, "kiln-mods", "creative");
        Write(Path.Combine(kilnNamesake, "kiln.mod.json"), """{"id":"creative","version":"3.2.0"}""");
        Assert.Equal(["combat_tweaks", "sf-survival"], LastServer.Lacking(required, [new LastServer.Local("kiln", "creative", kilnNamesake)], player).Order(StringComparer.Ordinal));
    }

    // A mod switched off in the player's game does not run there, and the server needs it running: it is missing, as a
    // Kiln mod switched off in the profile (Skipped, not in the client's list) is. ShroudForge's switch is enabled: false
    // in extended.mod.json (Kiln writes it so for a mod switched off in the profile and keeps the copy); a ShroudForge mod
    // whose targets leave out "client" does not run on a client either. Its identity is the same as the server's copy
    // (extended.mod.json is left out of it): a switched-off copy must not stand in for a running one.
    [Fact]
    public void A_mod_switched_off_in_the_players_game_is_missing()
    {
        var server = Game("server");
        var required = Advertised(server);
        var player = Game("player");
        Write(Path.Combine(player, "mods", "combat_tweaks", "extended.mod.json"), """{"schemaVersion":1,"enabled":false,"settings":{"t":1}}""");
        Write(Path.Combine(player, "mods", "altar-cleanup", "extended.mod.json"), """{"schemaVersion":1,"enabled":true,"targets":["server"]}""");
        Assert.Equal(["altar-cleanup", "combat_tweaks"], LastServer.Lacking(required, [], player).Order(StringComparer.Ordinal));
    }

    // The same id twice in the player's mods folder, one copy switched off: only the copy that runs counts.
    [Fact]
    public void A_switched_off_copy_does_not_stand_in_for_the_running_one()
    {
        var server = Game("server");
        var required = Advertised(server);
        var player = Game("player");
        File.WriteAllText(Path.Combine(player, "mods", "combat_tweaks", "combat.dll"), "MZ combat, another build");
        var old = Path.Combine(player, "mods", "combat_tweaks-old");
        Write(Path.Combine(old, "mod.json"), """{"id":"combat_tweaks","version":"0.7.2"}""");
        Write(Path.Combine(old, "extended.mod.json"), """{"schemaVersion":1,"enabled":false}""");
        Write(Path.Combine(old, "combat.dll"), "MZ combat");
        Assert.Equal(["combat_tweaks"], LastServer.Lacking(required, [], player));
    }

    [Fact]
    public void This_games_own_server_id_with_its_server_running_is_the_hosted_world()
    {
        var game = Game("host");
        KilnPaths.Root = Path.Combine(game, "kiln");
        Write(NetAdvertise.IdFile, "srv-8383ec52951c\n");
        NetAdvertise.Reset();
        NetEngine.TestServerRole = true;
        Assert.True(LastServer.IsHosted(Advert("srv-8383ec52951c")));
        Assert.False(LastServer.IsHosted(Advert("srv-0123456789ab")));  // a dedicated server
        Assert.False(LastServer.IsHosted(Advert(null)));                // an old Kiln server, or a vanilla one
        NetEngine.TestServerRole = false;
        Assert.False(LastServer.IsHosted(Advert("srv-8383ec52951c")));  // no world runs here: some other server with this id
    }

    [Fact]
    public void A_client_that_never_served_writes_no_server_id_when_it_joins()
    {
        var game = Game("player");
        KilnPaths.Root = Path.Combine(game, "kiln");
        NetAdvertise.Reset();
        NetEngine.TestServerRole = false;
        Assert.False(LastServer.IsHosted(Advert("srv-8383ec52951c")));
        Assert.False(File.Exists(NetAdvertise.IdFile));
    }

    // What last-server.json and kiln.log say: the hosted world is named as such, lacks nothing, and the entry says hosted.
    [Fact]
    public void The_welcome_of_a_hosted_world_says_so_and_lists_nothing()
    {
        var game = Game("host");
        var required = Advertised(game);
        var advert = Advert("srv-8383ec52951c", required);
        // even when the comparison would find a difference (a mod rewritten since the server hashed it), the world runs here
        File.WriteAllText(Path.Combine(game, "mods", "combat_tweaks", "combat.dll"), "MZ rebuilt");
        var hosted = LastServer.Answer("welcome", null, advert, "0.2.0-dev.31.17", null, hosted: true, [], game, DateTime.UtcNow);
        Assert.Empty((JsonArray)hosted["missing"]!);
        Assert.True(hosted["hosted"]!.GetValue<bool>());
        Assert.Equal("last-server.json: welcome from your hosted world (srv-8383ec52951c, this game), nothing missing", LastServer.LogLine(hosted));

        var remote = LastServer.Answer("welcome", null, advert with { ServerName = "Ember Isles" }, null, null, hosted: false, [], game, DateTime.UtcNow);
        Assert.False(remote["hosted"]!.GetValue<bool>());
        Assert.Equal(["combat_tweaks"], ((JsonArray)remote["missing"]!).Select(n => n!.GetValue<string>()));
        Assert.Equal("last-server.json: welcome from server srv-8383ec52951c \"Ember Isles\", missing combat_tweaks (this game lacks them, has them switched off or has another version)",
            LastServer.LogLine(remote));
    }

    // A refusal still names what the server named, hosted or not (a hosted world can refuse its own player: a mod switched
    // off in the profile the server still runs).
    [Fact]
    public void A_refusal_keeps_the_mods_the_server_named()
    {
        var game = Game("host");
        var advert = Advert("srv-8383ec52951c", Advertised(game));
        var e = LastServer.Answer("rejected", "needs mods", advert, null, ["creative 3.2.0 (missing)"], hosted: true, [], game, DateTime.UtcNow);
        Assert.Equal(["creative"], ((JsonArray)e["missing"]!).Select(n => n!.GetValue<string>()));
        Assert.Equal("last-server.json: rejected by your hosted world (srv-8383ec52951c, this game), missing creative", LastServer.LogLine(e));
    }

    // A refusal without details (refused before the handshake was read) names nothing: then the comparison says what this
    // game lacks, as for a welcome.
    [Fact]
    public void A_refusal_without_details_lists_what_the_game_lacks()
    {
        var advert = Advert("srv-0123456789ab", Advertised(Game("server")));
        var player = Game("player", sfSurvival: false);
        var e = LastServer.Answer("rejected", "needs mods", advert, null, [], hosted: false, [], player, DateTime.UtcNow);
        Assert.Equal(["sf-survival"], ((JsonArray)e["missing"]!).Select(n => n!.GetValue<string>()));
    }

    private static string Folder(string game, NetRequiredMod r) => r.Kind switch
    {
        "eml" => Path.Combine(game, "kiln", "eml", "mods", r.Id),
        "emf" => Path.Combine(game, "emf", "mods", r.Id),
        "greeneye" => Path.Combine(game, "greeneye", r.Id),
        _ => Path.Combine(game, "mods", r.Id),
    };

    private static NetServerAdvert Advert(string? id, IReadOnlyList<NetRequiredMod>? required = null) =>
        new(id, null, "0.2.0-dev.31.17", null, required ?? [], required?.Count ?? 0, false, false);

    // A recompute runs on the thread pool (NetAdvertise.Refresh): wait until none runs.
    private static void Settle()
    {
        var computing = typeof(NetAdvertise).GetField("_computing", BindingFlags.NonPublic | BindingFlags.Static)!;
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((int)computing.GetValue(null)! != 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.Equal(0, (int)computing.GetValue(null)!);
    }

    private sealed class FakeMods(params ModEntry[] mods) : IModsBackend
    {
        public IReadOnlyList<ModEntry> All => mods;
        public ModSide Side => ModSide.Server;
        public ModEntry? Get(string id) => mods.FirstOrDefault(m => m.Id == id);
        public ModStats Stats(string id) => default;
        public bool Send(ModContext? sender, string targetId, string method, object? payload) => false;
        public Task<ModOperationResult> Request(ModOperation op, string arg) => throw new NotSupportedException();
        public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) => throw new NotSupportedException();
    }
}
