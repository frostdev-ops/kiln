// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public static partial class Coverage
{
    static partial void AddFakes(List<CoverageRow> rows)
    {
        void Row(string area, CoverageKind kind, string note, params string[] caps) => rows.Add(new CoverageRow(area, kind, caps, note));
        Row("Players", CoverageKind.RecordingFake,
            "Kiln.Players' own code on the kit's kiln.players: KilnHarness.Players.Join/Leave take effect at the next step " +
            "(Players.All, Find, Joined/Left, PlayerJoinedEvent/PlayerLeftEvent); entity slot + 1, Steam id, admin bit, user-group " +
            "rights, the test's positions; 16 slots. Unavailable: raw records, the game-log replay, the position probe for " +
            "non-players, native subscribers", FakeTables.PlayersService);
        Row("Commands as a player, permissions", CoverageKind.Real,
            "Kiln.Chat's Commands.Execute with a fake player as source (KilnHarness.RunCommand(player, line)) and Kiln.Permissions' " +
            "real rules on permissions.json and enshrouded_server.json in the temp folder (WritePermissions, WriteServerConfig); " +
            "@p and player names resolve against the fake players. Replies come back in the result and, through the fake kiln.chat, " +
            "in the player's chat");
        Row("Saved data (WorldData, PlayerData)", CoverageKind.RecordingFake,
            "Kiln.Saves' store on the kit's kiln.saves: a world id and revision per server folder, the save cycle on the steps " +
            "(WorldSaving, staged generation, WorldSaved), the game's autosave (30 s virtual), player-left and shutdown saves; " +
            "a harness on the same folder reads the last save (KilnHarness.Kill: no shutdown save). Unavailable: backups, the " +
            "world file's path, the snapshot stage, a client's hosted world", FakeTables.SavesService);
        Row("EntityData", CoverageKind.RecordingFake,
            "on player entities: stored as PlayerData under the fake player's Steam id; on the fake world's entities (kiln.spawn's " +
            "fake finds them) kept with the world's mod data", FakeTables.SavesService);
        Row("Config (per world)", CoverageKind.Real,
            "Mod.WorldConfig<T>() binds to the harness world's folder (kiln/data/worlds/<id>/config) through Kiln.Saves and " +
            "differs between world ids");
        Row("Spawn", CoverageKind.RecordingFake,
            "Kiln.Spawn's own code on the kit's kiln.spawn: a spawn (template or item drop) makes an entity in the fake world at once, " +
            "at its position and yaw with a SpawnTime, from the step the server started (spawn.world pending before); the templates are " +
            "the ones the test adds (KilnHarness.Spawned.AddTemplate) plus the generic item pickup, an unknown one is refused; Destroy, " +
            "Inspect, Exists, Find, FindByName, EntityCount, the tracked list, CleanupTracked and Refresh read the fake world; " +
            "EntitySpawnedEvent and EntityDestroyedEvent are Kiln.Spawn's; KilnHarness.Spawned.Calls records every spawn, destroy and " +
            "cleanup. Offline: extra spawn components (spawn.components), what a template does, the level, DoNotSave, saving spawned " +
            "entities and re-finding tracked ones after a restart",
            SpawnAbi.Service, SpawnFake.CreateFeature, SpawnFake.DestroyFeature, SpawnFake.EntitiesFeature, SpawnFake.TemplatesFeature,
            SpawnFake.WorldRow, SpawnFake.ComponentsRow);
        Row("Chat", CoverageKind.RecordingFake,
            "Kiln.Chat's own code on the kit's kiln.chat: the fake players in game are the chat's players; a line a player types " +
            "(FakePlayer.Say) reaches Chat.Received, ChatMessageEvent and chat commands at the start of the next step, and one nobody " +
            "consumed is shown to every player; Tell, Broadcast, Say and command replies are shown at once from the step the server " +
            "started, per player (FakePlayer.Chat, KilnHarness.Chat). Offline: the game's inbox (Chat.Diagnostics.LastLine), " +
            "enableTextChat, the account hash and connection state of a chat player",
            ChatAbi.Service, ChatFake.SendFeature, ChatFake.ReceiveFeature, ChatFake.PlayersFeature);
        Row("Items", CoverageKind.RecordingFake,
            "Kiln.Items' own code on the kit's kiln.items (the v1 table): the items are the ones the test adds " +
            "(KilnHarness.Items.AddItem, with their stack size), an inventory per fake player in game (FakePlayer.Inventory, 5 generic " +
            "8-slot inventories unless the test says) and per container entity (AddContainer); Give and Take run as one transaction " +
            "at the start of the next step (stacks topped up first, gear one per slot with an item entity, Partial or NoRoom when it " +
            "does not fit, take all or nothing, dry runs), ItemsGivenEvent and ItemsTakenEvent are Kiln.Items'; Read, FindInventories " +
            "and the catalog read the fake; KilnHarness.Items.Calls records each. Offline: water, moves, container names, transfer " +
            "hooks, item instances, equip, recipe results, upgrades and gem slots, the game's player inventory layout",
            [ItemsAbi.Service, .. ItemsFake.Features, .. ItemsFake.OfflineRows]);
        Row("Combat", CoverageKind.RecordingFake,
            "Kiln.Combat's own code on the kit's kiln.combat (the v1 table): health is what the test gives an entity of the fake world " +
            "or a fake player (KilnHarness.Combat.SetHealth); a mod's Damage or Kill and the game's hit (KilnHarness.Combat.Hit) apply " +
            "at the start of the next step, the game's held one step for EntityHurtEvent / EntityHealEvent handlers, who may change or " +
            "cancel it; EntityDamagedEvent, EntityDeathEvent (health below 1, the killing blow's attacker), PlayerRespawnEvent " +
            "(KilnHarness.Combat.Respawn) at the next step; SetHealth (0 kills without a killer), Heal up to the maximum, god mode; " +
            "KilnHarness.Combat.Hits records each hit. Offline: attributes, the hold filter, fall damage, regeneration, attribute heals, " +
            "knockback, loot drops, the game's damage formulas and kill credit",
            [CombatAbi.Service, .. CombatFake.Features, .. CombatFake.OfflineRows]);
        Row("Time", CoverageKind.RecordingFake,
            "Kiln.Time's own code on the kit's kiln.time: the day/night clock runs on the harness's virtual time from the first step " +
            "(a new world at 06:00; day 06:00 to 18:00 in KilnHarness.Time.DayLength, 30 min by default, night in NightLength, 12 min), " +
            "times the speed a mod holds (Time.SetSpeed); Time.State, OfDay, IsDay, Set (forward, or the game's reset), SetCycle, " +
            "TimeSetEvent and the day and night events as Kiln.Time polls the clock; KilnHarness.Time.Calls records mods' changes. " +
            "Offline: the weather (its table, layers, choice and reset), the sync probe, sleeping, saving the time with the world",
            [TimeAbi.Service, .. TimeFake.Features, .. TimeFake.OfflineRows]);
        Row("Quests (Kiln.Quests' engine)", CoverageKind.Real,
            "a quest a mod defines (Quests.Define, quests/*.json) is started, advanced and completed for a fake player through " +
            "Quests.Start, Advance, Complete, SetStep, Fail and Abandon, with QuestStartedEvent, QuestStepEvent, QuestCompletedEvent and " +
            "its record in the world's saved data: the engine is Kiln's managed code on the fake players and saves. Unavailable: the " +
            "game's journal and knowledge (kiln.knowledge), quest markers (kiln.questmark), the game's NPCs and their conversations");
        Row("Skills (Kiln.Skills' talent trees)", CoverageKind.Real,
            "a talent tree a mod registers (Skills.Register, skills/*.json \"trees\"): Skills.Unlock, LevelOf, Points, State, " +
            "TalentUnlockingEvent (a handler may refuse) and TalentChangedEvent, per player in the saved data: Kiln's managed code on " +
            "the fake players and saves. Unavailable: the game's own skill tree (kiln.skills: GameTree), the game's levels and points " +
            "(kiln.progression), the tree screen and what an effect does in the game");
        Row("kiln.net", CoverageKind.Unavailable,
            "no fake: a true pair is a client hosting its world (a kiln.client fake, a client-side harness, a client tick) and " +
            "shares one channel registry, so no handshake refusal could be tested; Kiln.Network reports itself unavailable",
            "kiln.net");
        foreach (var fake in KitFakes.All) fake.AddCoverage(rows);  // dev.28 on: Fakes/<Service>/ (IKitFake)
    }
}
