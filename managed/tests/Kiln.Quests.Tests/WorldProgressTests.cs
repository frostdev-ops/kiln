// SPDX-License-Identifier: MIT
// World progress without a game: the readers of the game's data (query db, scene chunks, templates) on synthetic blobs
// laid out like the game's, the catalog rules that turn them into spires, shrines, roots, obelisks and bosses, the vanilla
// quest names and the quest log's reading of the game's journal states. Names and ids here are invented.
using Xunit;

namespace Kiln.QuestTests;

public sealed class WorldProgressTests
{
    // ---- builders ----

    private static byte[] QueryDb(params (uint Id, string Name, (byte Type, string Name, uint Id, byte QType, bool Player)[] Actions)[] queries)
    {
        var w = new BlobWriter(44);
        var q0 = w.Array(0x10, queries.Length, GameLayout.QueryEntry);
        for (var i = 0; i < queries.Length; i++)
        {
            var e = q0 + i * GameLayout.QueryEntry;
            w.String(e, queries[i].Name);
            w.U32(e + 8, queries[i].Id);
            var acts = queries[i].Actions;
            var a0 = w.Array(e + 0x18, acts.Length, GameLayout.ActionEntry);
            for (var k = 0; k < acts.Length; k++)
            {
                var a = a0 + k * GameLayout.ActionEntry;
                w.U8(a, acts[k].Type);
                w.String(a + 4, acts[k].Name);
                w.U32(a + 0xc, acts[k].Id);
                w.U32(a + 0x10, 1);
                w.U8(a + 0x14, 2);
                w.U8(a + 0x15, acts[k].QType);
                w.U8(a + 0x16, (byte)(acts[k].Player ? 1 : 0));
            }
        }
        return w.ToArray();
    }

    private sealed record E(int Template, double X, double Y, double Z, (byte Ctx, uint Id, byte Type)[]? Ops = null, uint QueryId = 0,
        byte QueryType = 1, uint Loot = 0);

    private static byte[] Chunk(Guid[] templates, params E[] entities)
    {
        var w = new BlobWriter(140);
        var t0 = w.Array(GameLayout.ChunkTemplates, templates.Length, 16);
        for (var i = 0; i < templates.Length; i++) w.Guid(t0 + 16 * i, templates[i]);
        var e0 = w.Array(GameLayout.ChunkEntities, entities.Length, GameLayout.ChunkEntity);
        var sd = GameLayout.ChunkSpawnData;
        var n0 = w.Array(sd + GameLayout.SpawnEntries, entities.Length, 4);
        var comps = new List<(uint Hash, int Size, Action<int> Fill)>();
        var index = new List<byte>();
        for (var i = 0; i < entities.Length; i++)
        {
            var en = entities[i];
            var at = e0 + i * GameLayout.ChunkEntity;
            w.U16(at, (ushort)en.Template);
            void Pos(int off, double v)
            {
                var f = (uint)(v * 65536);
                w.U16(at + 2 + off, (ushort)(f & 0xffff));
                w.U16(at + 2 + off + 2, (ushort)(f >> 16));
            }
            Pos(0, en.X);
            Pos(4, en.Y);
            Pos(8, en.Z);
            var mine = new List<(uint, int, Action<int>)>();
            if (en.Ops is { } ops)
                mine.Add((GameLayout.SceneKnowledgeTrigger, GameLayout.SceneKnowledgeTriggerSize, d =>
                {
                    for (var k = 0; k < ops.Length; k++)
                    {
                        w.U8(d + 16 * k, ops[k].Ctx);
                        w.U32(d + 16 * k + 4, ops[k].Id);
                        w.U8(d + 16 * k + 8, ops[k].Type);
                        w.U32(d + 16 * k + 12, 1);
                    }
                }));
            if (en.QueryId != 0)
                mine.Add((GameLayout.KnowledgeQuery, GameLayout.KnowledgeQuerySize, d =>
                {
                    w.U32(d, en.QueryId);
                    w.U32(d + 4, 1);
                    w.U8(d + 8, 2);
                    w.U8(d + 9, en.QueryType);
                }));
            if (en.Loot != 0) mine.Add((GameLayout.SceneLootNodeId, GameLayout.SceneLootNodeIdSize, d => w.U32(d, en.Loot)));
            w.U16(n0 + 4 * i + 2, (ushort)index.Count);
            index.Add((byte)mine.Count);
            if (mine.Count > 0)
            {
                index.Add((byte)(comps.Count & 0xff));
                index.Add((byte)(comps.Count >> 8));
            }
            comps.AddRange(mine);
        }
        var c0 = w.Array(sd + GameLayout.SpawnComponents, comps.Count, 12);
        for (var k = 0; k < comps.Count; k++)
        {
            var d = w.Variant(c0 + 12 * k, comps[k].Hash, comps[k].Size);
            comps[k].Fill(d);
        }
        var i0 = w.Array(sd + GameLayout.SpawnIndices, index.Count, 1);
        for (var k = 0; k < index.Count; k++) w.U8(i0 + k, index[k]);
        return w.ToArray();
    }

    private static byte[] Template(string name, Guid? spawns = null, Guid[]? randomSpawns = null, uint bossBarTagSeed = 0, bool marker = false,
        (bool Scene, uint Id)? kill = null, params string[] extra)
    {
        var w = new BlobWriter(24);
        w.String(GameLayout.TemplateName, name);
        var list = new List<(uint Hash, int Size, Action<int> Fill)>();
        if (spawns is { } s) list.Add((GameLayout.TriggerSpawner, GameLayout.TriggerSpawnerSize, d => w.Guid(d, s)));
        if (randomSpawns is { } rs)
            list.Add((GameLayout.RandomSpawner, GameLayout.RandomSpawnerSize, d =>
            {
                var e = w.Array(d, 1, GameLayout.SpawnerEntry);
                var g = w.Array(e, rs.Length, 16);
                for (var i = 0; i < rs.Length; i++) w.Guid(g + 16 * i, rs[i]);
            }));
        if (bossBarTagSeed != 0) list.Add((GameLayout.BossHealthBar, GameLayout.BossHealthBarSize, d => w.U32(d, bossBarTagSeed)));
        if (marker) list.Add((GameLayout.MapMarker, GameLayout.MapMarkerSize, d => w.U32(d, 0x1234)));
        if (kill is { } k)
            list.Add((GameLayout.KnowledgeTriggerOnKill, GameLayout.KnowledgeTriggerOnKillSize, d =>
            {
                w.U8(d, (byte)(k.Scene ? 1 : 0));
                w.U32(d + 0x5c, k.Id);
            }));
        foreach (var x in extra) list.Add((Fnv.Component(x), 4, _ => { }));
        var c0 = w.Array(GameLayout.TemplateComponents, list.Count, 12);
        for (var i = 0; i < list.Count; i++) list[i].Fill(w.Variant(c0 + 12 * i, list[i].Hash, list[i].Size));
        return w.ToArray();
    }

    private static ProgressScope Scope(uint id) => id >= 0x9000 ? ProgressScope.Character : ProgressScope.Both;

    // ---- readers ----

    [Fact]
    public void The_query_db_reader_reads_names_ids_and_actions()
    {
        var db = QueryDb((0x10, "achievement_all_FlameTemplesAndShrines_found_01_testvale", [(0, "testvale_flametemple_01", 0x9001, 1, false), (0, "flameshrine_02", 0x9002, 1, false)]),
            (0x11, "Other_Query", [(1, "nested", 0x10, 0, false)]));
        var q = GameDataReader.ReadQueryDb(db);
        Assert.Equal(2, q.Count);
        Assert.Equal("achievement_all_FlameTemplesAndShrines_found_01_testvale", q[0].Name);
        Assert.Equal(0x10u, q[0].Id);
        Assert.Equal(2, q[0].Actions.Count);
        Assert.Equal("flameshrine_02", q[0].Actions[1].Name);
        Assert.Equal(0x9002u, q[0].Actions[1].Query.Id);
        Assert.True(q[0].Actions[1].IsSimple);
        Assert.False(q[1].Actions[0].IsSimple);
    }

    [Fact]
    public void The_chunk_reader_reads_positions_triggers_queries_and_loot_ids()
    {
        var t = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var bytes = Chunk(t,
            new E(0, 100.5, 700.25, 200.75),
            new E(1, 3000, 800, 4000, Ops: [(TriggerOp.Kill, 0x55, 0), (TriggerOp.Invalid, 0x56, 0)], QueryId: 0x77, Loot: 0xabc),
            new E(0, 10, 20, 30, Loot: 0xdef));
        var (templates, placements) = GameDataReader.ReadChunk(bytes, 7);
        Assert.Equal(t, templates);
        var p = Assert.Single(placements);
        Assert.Equal(t[1], p.Template);
        Assert.Equal(7, p.Chunk);
        Assert.Equal(1, p.Index);
        Assert.Equal(new WorldPosition(3000, 800, 4000), p.Position);
        Assert.Equal([0x55u, 0x56u], p.Triggers.Select(o => o.Id));
        Assert.Equal(TriggerOp.Kill, p.Triggers[0].Context);
        Assert.Equal(0x77u, p.Query!.Value.Id);
        Assert.Equal(0xabcu, p.LootId);

        var all = GameDataReader.ReadChunk(bytes, 7, all: true).Placements;
        Assert.Equal(3, all.Count);
        Assert.Equal(100.5, all[0].Position.X, 3);
        Assert.Equal(700.25, all[0].Position.Y, 3);
        var loot = GameDataReader.ReadChunk(bytes, 7, withLoot: true).Placements;
        Assert.Equal([0xabcu, 0xdefu], loot.Select(x => x.LootId));
    }

    [Fact]
    public void A_truncated_resource_is_refused_not_misread()
    {
        var bytes = Chunk([Guid.NewGuid()], new E(0, 1, 2, 3, Ops: [(0, 0x5, 0)]));
        Assert.Throws<InvalidDataException>(() => GameDataReader.ReadChunk(bytes.AsSpan(0, bytes.Length - 40), 0));
        Assert.Throws<InvalidDataException>(() => GameDataReader.ReadQueryDb(new byte[20]));
    }

    [Fact]
    public void A_component_of_another_size_is_skipped()
    {
        var w = new BlobWriter(24);
        w.String(0, "Odd_Spawner");
        var c0 = w.Array(GameLayout.TemplateComponents, 1, 12);
        w.Guid(w.Variant(c0, GameLayout.TriggerSpawner, 60), Guid.NewGuid());  // a newer layout: 60 bytes, not 56
        var t = GameDataReader.ReadTemplate(Guid.NewGuid(), w.ToArray());
        Assert.Empty(t.Spawns);
        Assert.True(t.Has("TriggerSpawner"));
    }

    [Fact]
    public void The_template_reader_reads_spawners_boss_bars_markers_and_kill_triggers()
    {
        var target = Guid.NewGuid();
        var g = Guid.NewGuid();
        var spawner = GameDataReader.ReadTemplate(g, Template("Spawnpoint_Test", spawns: target, kill: (true, 0)));
        Assert.Equal("Spawnpoint_Test", spawner.Name);
        Assert.Equal([target], spawner.Spawns);
        Assert.True(spawner.KillUsesScene);
        Assert.Null(spawner.KillOp);

        var random = GameDataReader.ReadTemplate(Guid.NewGuid(), Template("Random_Test", randomSpawns: [Guid.NewGuid(), target]));
        Assert.Equal(2, random.Spawns.Count);

        var boss = GameDataReader.ReadTemplate(target, Template("Enemy_Test_Boss", bossBarTagSeed: 0x99, kill: (false, 0x4242)));
        Assert.True(boss.IsBoss);
        Assert.NotEqual(0u, boss.BossBarTag);
        Assert.Equal(0x4242u, boss.KillOp!.Value.Id);

        var chest = GameDataReader.ReadTemplate(Guid.NewGuid(), Template("LootChest_Test", marker: true, extra: ["RandomLootContainer", "InventoryEmptyState"]));
        Assert.True(chest.IsChest);
        Assert.NotEqual(0u, chest.MarkerTag);
        Assert.False(chest.IsBoss);
    }

    // ---- catalog rules ----

    [Fact]
    public void Shrines_roots_and_obelisks_come_from_the_achievement_queries_with_region_ids()
    {
        var queries = GameDataReader.ReadQueryDb(QueryDb(
            (1, "achievement_all_FlameTemplesAndShrines_found_01_testvale", [(0, "testvale_flametemple_01", 0x9001, 1, false), (0, "flameshrine_01", 0x9002, 1, false), (0, "flameshrine_01", 0x9003, 1, false)]),
            (2, "achievement_all_FlameTemplesAndShrines_found_02_FrostPeaks", [(0, "flameshrine_01", 0x9004, 1, false), (1, "some_query", 2, 0, false)]),
            (3, "DEPRECATED_achievement_all_FlameTemplesAndShrines_found_09_old", [(0, "flameshrine_01", 0x9005, 1, false)]),
            (4, "achievement_all_shroudRoots_destroyed_01_testvale", [(0, "well_00", 0x9010, 1, false), (0, "additional_skillpoint_01", 0x9011, 1, false)]),
            (5, "achievement_all_Obelisks_read_01_testvale", [(0, "obelisk_above_fog_read", 0x9020, 1, false)])));
        var shrineTemplate = Guid.NewGuid();
        var global = GameDataReader.ReadChunk(Chunk([shrineTemplate], new E(0, 500, 600, 700, Ops: [(0, 0x9002, 0)])), 1600).Placements;
        var templates = new Dictionary<Guid, TemplateData> { [shrineTemplate] = GameDataReader.ReadTemplate(shrineTemplate, Template("Test_FlameShrine_Marker", marker: true)) };
        var problems = new List<string>();
        var c = WorldCatalogBuilder.Places(queries, global, g => templates.GetValueOrDefault(g), Scope, id => id == 0x9010 ? 3 : 1, problems);

        Assert.Equal(["testvale_flametemple_01", "testvale_flameshrine_01", "testvale_flameshrine_01_2", "frostpeaks_flameshrine_01"], c.Shrines.Select(s => s.Id));
        Assert.Equal(ShrineKind.Sanctum, c.Shrines[0].Kind);
        Assert.Equal(ShrineKind.Shrine, c.Shrines[1].Kind);
        Assert.Equal("frostpeaks", c.Shrines[3].Region);
        Assert.Equal(new WorldPosition(500, 600, 700), c.Shrines[1].Position);
        Assert.Equal("Test_FlameShrine_Marker", c.Shrines[1].Template);
        Assert.Equal(ProgressScope.Character, c.Shrines[1].Discovery.Scope);
        Assert.Null(c.Shrines[0].Position);

        Assert.Equal(["testvale_well_00", "testvale_additional_skillpoint_01"], c.Roots.Select(r => r.Id));
        Assert.True(c.Roots[0].ElixirWell);
        Assert.Equal(3, c.Roots[0].SkillPoints);
        Assert.False(c.Roots[1].ElixirWell);
        Assert.Equal("testvale_obelisk_above_fog_read", Assert.Single(c.Obelisks).Id);
        Assert.Empty(problems);
    }

    [Fact]
    public void Spires_are_the_fast_travel_markers_with_their_top_and_the_nearest_beacon()
    {
        var queries = GameDataReader.ReadQueryDb(QueryDb(
            (1, "Achievement_Tower_Biome01_testvale", [(0, "Tower_Biome01_testvale_topReached", 0x9100, 1, false), (0, "Tower_Biome01_lastTeleport", 0x9101, 1, false)])));
        var marker = Guid.NewGuid();
        var beacon = Guid.NewGuid();
        var far = Guid.NewGuid();
        var templates = new Dictionary<Guid, TemplateData>
        {
            [marker] = GameDataReader.ReadTemplate(marker, Template("MapMarker_Tower_Testvale_FastTravel", marker: true)),
            [beacon] = GameDataReader.ReadTemplate(beacon, Template(WorldCatalogBuilder.BeaconTemplate)),
            [far] = GameDataReader.ReadTemplate(far, Template("MapMarker_Tower_Faraway_FastTravel_02_isle")),
        };
        var global = GameDataReader.ReadChunk(Chunk([marker, beacon, far],
            new E(0, 1000, 900, 1000, Ops: [(0, 0x500, 0), (0, 0x9100, 0)], QueryId: 0x500),
            new E(1, 1010, 950, 1030, QueryId: 0x600),
            new E(2, 5000, 900, 5000, Ops: [(4, 0x700, 0)], QueryId: 0x0a0b, QueryType: 0)), 1600).Placements;
        var c = WorldCatalogBuilder.Places(queries, global, g => templates.GetValueOrDefault(g), Scope, _ => 0, new List<string>());

        Assert.Equal(["faraway_02_isle", "testvale"], c.Spires.Select(s => s.Id));
        var s = c.Spires[1];
        Assert.Equal(0x500u, s.Unlock.Id);
        Assert.Equal(ProgressScope.Both, s.Unlock.Scope);
        Assert.Equal(0x9100u, s.TopReached!.Id);
        Assert.Equal(0x600u, s.Beacon!.Id);
        var isle = c.Spires[0];
        Assert.Null(isle.TopReached);
        Assert.Null(isle.Beacon);  // the only beacon is 5.6 km away
        Assert.Equal(KnowledgeQueryType.Extern, isle.Unlock.Query.Type);
        Assert.Equal(ProgressScope.World, isle.Unlock.Scope);
    }

    [Fact]
    public void Bosses_are_numbered_per_template_and_take_the_kill_record_by_priority()
    {
        var bossT = Guid.NewGuid();
        var spawnerT = Guid.NewGuid();
        var lonerT = Guid.NewGuid();
        var otherT = Guid.NewGuid();
        var templates = new[]
        {
            GameDataReader.ReadTemplate(bossT, Template("Enemy_Test_Golem", bossBarTagSeed: 7, kill: (true, 0))),
            GameDataReader.ReadTemplate(spawnerT, Template("Spawnpoint_Test_Golem", spawns: bossT)),
            GameDataReader.ReadTemplate(lonerT, Template("Enemy_Test_Loner", bossBarTagSeed: 8, kill: (false, 0x9333))),
            GameDataReader.ReadTemplate(otherT, Template("Spawnpoint_Test_Wolf", spawns: Guid.NewGuid())),
        };
        var placers = WorldCatalogBuilder.BossPlacers(templates);
        Assert.Equal(3, placers.Count);
        Assert.False(placers.ContainsKey(otherT));

        var chunkA = GameDataReader.ReadChunk(Chunk([spawnerT, otherT, lonerT, spawnerT],
            new E(0, 10, 0, 10, Ops: [(TriggerOp.Invalid, 0x9810, 0), (TriggerOp.Kill, 0x9811, 0)]),
            new E(1, 20, 0, 20),
            new E(2, 30, 0, 30),
            new E(3, 35, 0, 35, Ops: [(TriggerOp.Kill, 0x9812, 0), (TriggerOp.Invalid, 0x813, 0), (TriggerOp.Range, 0x814, 0)])), 3, want: placers.ContainsKey).Placements;
        var chunkB = GameDataReader.ReadChunk(Chunk([bossT, spawnerT],
            new E(0, 40, 0, 40),
            new E(1, 50, 0, 50, Ops: [(TriggerOp.Invalid, 0x820, 0)])), 1, want: placers.ContainsKey).Placements;
        var bosses = WorldCatalogBuilder.Bosses(chunkA.Concat(chunkB), placers, Scope);

        Assert.Equal(["enemy_test_golem_1", "enemy_test_golem_2", "enemy_test_golem_3", "enemy_test_loner", "enemy_test_golem_4"], bosses.Select(b => b.Id));
        Assert.Null(bosses[0].Kill);                              // chunk 1, placed directly: no scene record, the template defers to it
        Assert.Equal(0x820u, bosses[1].Kill!.Id);                 // chunk 1, a spawner's Invalid op
        Assert.Equal(0x9811u, bosses[2].Kill!.Id);                // chunk 3: only character records: a Kill op beats an Invalid one
        Assert.Equal(0x813u, bosses[4].Kill!.Id);                 // a world record beats a character one, whatever its context
        Assert.Equal("Spawnpoint_Test_Golem", bosses[2].Spawner);
        Assert.Equal(0x9333u, bosses[3].Kill!.Id);                // the template's own kill record
        Assert.Equal(ProgressScope.Character, bosses[3].Kill!.Scope);
        Assert.Null(bosses[3].Spawner);
        Assert.False(bosses[0].Tracked);
    }

    [Fact]
    public void Roots_get_the_position_of_the_trigger_that_destroys_them()
    {
        var roots = new List<ShroudRoot> { new("r1", "testvale", true, 3, null, new ProgressFact(KnowledgeQuery.IsSet(0x9010), ProgressScope.Character)) };
        var placed = GameDataReader.ReadChunk(Chunk([Guid.NewGuid()], new E(0, 7, 8, 9, Ops: [(TriggerOp.MiningNode, 0x9010, 0)])), 5).Placements;
        Assert.Equal(new WorldPosition(7, 8, 9), WorldCatalogBuilder.WithPositions(roots, placed)[0].Position);
    }

    [Fact]
    public void Vanilla_quest_names_prefer_the_completion_query_and_drop_shared_names()
    {
        var names = new Dictionary<uint, string> { [1] = "Quest_A_done", [2] = "Quest_B_seen", [3] = "Shared_done" };
        KnowledgeQuery Ext(uint id) => new(id, KnowledgeQueryType.Extern, KnowledgeCompare.Equals, 0);
        var picked = WorldCatalogBuilder.QuestNames(
        [
            (0xa, Ext(2), Ext(1)),
            (0xb, Ext(2), KnowledgeQuery.IsSet(0x77)),
            (0xc, Ext(9), Ext(3)),
            (0xd, Ext(9), Ext(3)),
            (0xe, KnowledgeQuery.IsSet(1), KnowledgeQuery.IsSet(2)),
        ], names);
        Assert.Equal("Quest_A_done", picked[0xa]);
        Assert.Equal("Quest_B_seen", picked[0xb]);
        Assert.False(picked.ContainsKey(0xc));
        Assert.False(picked.ContainsKey(0xe));
    }

    // ---- quest log ----

    private static JournalQuest Quest(int steps) => new(0, 0x8000_0001, 0x55, QuestSource.Alchemist, QuestType.WorldQuest, true, false, false, 50,
        [.. Enumerable.Range(0, steps).Select(i => new JournalStep(i, (uint)i, 0, 0, 0, KnowledgeQuery.IsSet((uint)(0x100 + i)), KnowledgeQuery.IsSet(0x200), 0, 1))],
        0, [], []);

    [Fact]
    public void A_vanilla_quest_maps_to_locked_active_or_done_with_the_last_visible_step()
    {
        var q = Quest(3);
        JournalQuestState S(bool?[] visible, bool? done, bool? rewarded) =>
            new(q, new EntityId(3), [.. visible.Select((v, i) => new JournalStepState(q.Steps[i], v, done))], rewarded);

        var locked = Quests.VanillaEntry(S([false, false, false], false, false));
        Assert.Equal(QuestStatus.Locked, locked.Status);
        Assert.Equal(-1, locked.Step);
        Assert.Equal("quest:0x80000001", locked.Id);
        Assert.Equal(QuestKind.Vanilla, locked.Kind);

        var active = Quests.VanillaEntry(S([true, true, false], false, false));
        Assert.Equal(QuestStatus.Active, active.Status);
        Assert.Equal(1, active.Step);
        Assert.Equal("step2", active.StepId);

        var done = Quests.VanillaEntry(S([true, false, false], true, null));
        Assert.Equal(QuestStatus.Done, done.Status);
        Assert.Equal(2, done.Step);

        var rewarded = Quests.VanillaEntry(S([false, false, false], null, true));
        Assert.Equal(QuestStatus.Done, rewarded.Status);
    }

    [Fact]
    public void The_quest_log_finds_by_id_raw_id_and_name()
    {
        var q = Quest(1);
        var v = new QuestEntry("quest:0x80000001", "NPC_Test_Quest_done", QuestKind.Vanilla, QuestStatus.Active, 0, 1, "step1", 0, q, null);
        var log = new QuestLog(new EntityId(2), [v]);
        Assert.Same(v, log.Find("quest:0x80000001"));
        Assert.Same(v, log.Find("0x80000001"));
        Assert.Same(v, log.Find("npc_test_quest_DONE"));
        Assert.Null(log.Find("quest:0x1"));
        Assert.Equal(QuestStatus.Active, log.StatusOf("NPC_Test_Quest_done"));
        Assert.Equal(QuestStatus.Locked, log.StatusOf("nothing"));
        Assert.Single(log.Active);
        Assert.Empty(log.Completed);
    }

    // ---- facts ----

    [Fact]
    public void Difficulty_reads_numbers_and_the_custom_preset()
    {
        var d = new WorldDifficulty("Custom", new Dictionary<string, object> { ["enemyDamageFactor"] = 2.5, ["dayTimeDuration"] = 1800000000000L, ["enableDurability"] = true, ["weatherFrequency"] = "Rare" });
        Assert.True(d.IsCustom);
        Assert.Equal(2.5, d.Number("enemyDamageFactor"));
        Assert.Equal(1800000000000d, d.Number("dayTimeDuration"));
        Assert.Equal(1, d.Number("enableDurability"));
        Assert.Null(d.Number("weatherFrequency"));
        Assert.Null(d.Number("missing"));
    }

    [Fact]
    public void A_character_fact_has_no_world_value()
    {
        // without kiln.knowledge every fact is unreadable; a character fact asked of the world is never read
        var f = new ProgressFact(KnowledgeQuery.IsSet(0x9001), ProgressScope.Character);
        Assert.Null(f.Holds(EntityId.None));
        Assert.Null(new ProgressFact(KnowledgeQuery.IsSet(1), ProgressScope.World).Holds(EntityId.None));
    }
}
