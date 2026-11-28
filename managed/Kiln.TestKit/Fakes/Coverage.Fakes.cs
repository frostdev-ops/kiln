// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public static partial class Coverage
{
    static partial void AddFakes(List<CoverageRow> rows)
    {
        void Row(string area, CoverageKind kind, string note, params string[] caps) => rows.Add(new CoverageRow(area, kind, caps, note));
        Row("Screen and window placement (since 0.2.0-dev.31.11)", CoverageKind.Real,
            "Kiln.Ui's own placement code: UiScreenOptions.Position, MoveTo and Resize on UiScreen and UiWindow (applied once, " +
            "on the next frame; on a closed screen when it next opens), Placement, RememberPlacement in ui-placement.json in the " +
            "harness's config folder (per mod and id, clamped into the viewport; a corrupt file is kept as .bad). " +
            "KilnHarness.UiPlacement.Frame plays one overlay frame on a viewport the test sets, with ImGui's Always, FirstUseEver " +
            "and Appearing and its 32-pixel minimum; PlayerPlaces is the player's drag, Restart a game start. Window docking " +
            "(since 0.2.0-dev.31.16, mod API 0.2.23: Ui.FindWindow, AcceptDock, DockTo, Undock, DockChanged) with Kiln.Ui's own dock code: " +
            "FrameWindows plays a frame of every open window (the group's leader first, the others beside it in the same frame, " +
            "kept inside the viewport, a docked window waiting for its target, Shift, a drop beside the old side), PlayerDrags " +
            "is the player holding and dragging one. Offline: kiln.ui itself (nothing is drawn), imgui.ini, size constraints a " +
            "native plugin sets, which window's edge has the pointer where two touch");
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
            "Kiln.Items' own code on the kit's kiln.items (the v1 table, v8's gear functions and v9's container renames): the items are the ones the test adds " +
            "(KilnHarness.Items.AddItem, with their stack size), an inventory per fake player in game (FakePlayer.Inventory, 5 generic " +
            "8-slot inventories unless the test says) and per container entity (AddContainer); Give and Take run as one transaction " +
            "at the start of the next step (stacks topped up first, gear one per slot with an item entity, Partial or NoRoom when it " +
            "does not fit, take all or nothing, dry runs), ItemsGivenEvent and ItemsTakenEvent are Kiln.Items'; Read, FindInventories " +
            "and the catalog read the fake; KilnHarness.Items.Calls records each. Gear: every piece has enhancement slots, a level, " +
            "durability and a stamp (FakeSlot.Ref); GearOf, SetRarity, SetEnhancements and SetLevel run with kiln.items' rules at the " +
            "next step, the shown rarity and level a step later, the gear events are Kiln.Items' (the game's own change: " +
            "KilnHarness.Items.SetGear; perk slots of a type: SetPerkSlots). Rarity upgrades: Items.UpgradeRarity runs Kiln.Items' own " +
            "code and the fake takes the cost from the payers' plain stacks and makes the write in the same step, all or nothing " +
            "(CantPay, payers, refusals as kiln.items); KilnHarness.Items.SetUpgradeRolls names the rolls. Per-instance values (package P7): " +
            "Items.SetPower, SetArmor and ValuesOf on the kit's scale table (no damage or armour is computed), SetDurabilityMax and " +
            "SetLevel with AnyLevel with kiln.items' rules. Container renames (v9): Items.SetContainerName with kiln.items' rules (the " +
            "game's Rename field's limits and the entity at the call, the name stored at the next step, \"\" clears it; read with " +
            "KilnHarness.Items.ContainerName). GiveEquip models logical equipment, slot-type checks (SetEquipmentSlotType), " +
            "Backpack or Take replacement, DryRun and EquipChangedEvent in one step, without modelling the game's page layout. Offline: water, moves, reading container names, transfer " +
            "hooks, item instances, equip, recipe results, unlocked upgrades and gem slots at creation, the game's player inventory layout, max " +
            "durability computed again after a gear change",
            [ItemsAbi.Service, .. ItemsFake.Features, "items.gear.read", "items.gear.write", "items.gear.durability", "items.gear.upgrade", .. ItemsFake.OfflineRows]);
        Row("Inventory owner kinds", CoverageKind.RecordingFake,
            "Kiln.Items' FindInventoryOwners (and Kiln.Blocks' Bases.Chests on top of it) on the kit's kiln.items.inventory_kinds: the " +
            "owners FindInventories finds, with kiln.items' flags, guards and bounded buffer; players are Player, player storage holders " +
            "PlayerStorage, a container what the test sets (FakeInventory.Kind; Unknown by default, as an ordinary chest). Offline: the " +
            "game's components (FactoryStation, the bases' crafting stock) are not read",
            [InventoryKindsNative.Service, Items.InventoryKindsCapability]);
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
        Row("Building input (Kiln.Client)", CoverageKind.Unavailable,
            "no fake: BuildingInput emits the local player's ordinary building input through a game client's input path; the " +
            "kit's harness is a server with no client world, so every submit throws KilnUnavailableException (offline). " +
            "Kiln.Api.Tests drive the managed side on a fake kiln.client table",
            BuildingInput.Capability);
        Row("Game input (Kiln.Client, since 0.2.0-dev.31.30)", CoverageKind.Unavailable,
            "no fake: GameInput reads and writes the local player's resolved actions in a game client's input writer; the kit's " +
            "harness is a server with no client world, so GameInput.Available is false, IsHeld and WasPressed answer false and " +
            "OnSample and Repeat throw InvalidOperationException. Kiln.Api.Tests drive the managed side on a fake " +
            "kiln.client.game_input table",
            "kiln.client.game_input", "client.game_input");
        Row("Steam peer messages (Kiln.Network)", CoverageKind.Unavailable,
            "no fake: SteamMessages moves a mod's own Steam networking messages through kiln.net and the game's Steam; the kit " +
            "has neither, so Open throws KilnUnavailableException (offline) and Status answers not answered. " +
            "Kiln.Network.Tests drive the managed side on a fake kiln.net table",
            SteamMessages.Capability);
        Row("kiln.gemcraft", CoverageKind.Unavailable,
            "no fake: Gems.Socket, Unsocket, Upgrade and Recycle run the game's own forge transaction in a server world, and " +
            "GemCraftingEvent / GemCraftedEvent come from a hook on the game's gem handler; the kit has neither, so the calls " +
            "end Unavailable (Kiln.Abilities.Tests drive the managed side on a fake kiln.gemcraft table)",
            "kiln.gemcraft");
        foreach (var fake in KitFakes.All) fake.AddCoverage(rows);  // dev.28 on: Fakes/<Service>/ (IKitFake)
    }
}
