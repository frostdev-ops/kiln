// SPDX-License-Identifier: MIT
using System.Text.Json;
using Kiln;

namespace NewItems;

/// <summary>
/// Sample: new items and recipes (Forge's DeferredRegister) with kiln.content.
/// <list type="bullet">
/// <item>Frost Shard (a clone of Resin): crafted by hand from 2 twigs.</item>
/// <item>Frost Edge (a clone of the Bronze Sword, 1.5x damage, Rare): crafted by hand from 3 Frost Shards and 2 twigs.</item>
/// <item>Frost Lantern: defined in content/lantern.json (the data-pack form).</item>
/// <item>Glacier (kitbash): the Bronze Sword's behaviour with a T4 sword's model, two perks of a T5 sword, its own PNG
/// icon and texts in English, German and French; crafted by hand from a Frost Edge and 5 Frost Shards.</item>
/// <item>Frostbite (content/frostbite.json, kiln.gamedata clones): a buff of its own (a copy of Poisoned, 8 s) and a perk
/// "Frostbite Edge" that applies it, with texts in English, German and French; Glacier's third perk.</item>
/// <item>Frost Mail (kitbash): a T1 warrior chest wearing the T2 tank chest's model; crafted from 4 Frost Shards only
/// (unlocked through item knowledge).</item>
/// <item>On the server (or a hosting client), each player gets a starter kit once: 1 Frost Edge, 3 Frost Shards and
/// 6 twigs to try the recipes (kiln/data/newitems/kits.json remembers who got one).</item>
/// </list>
/// Install it on the server and on the players' games ("side": "both"): the GUIDs are fixed, so the ids agree everywhere
/// and saved items stay valid across restarts.
/// </summary>
public sealed class NewItemsMod : Mod
{
    public static readonly Guid FrostShard = Guid.Parse("e532604c-c64e-4014-8a0e-eba083edc3f5");
    public static readonly Guid FrostEdge = Guid.Parse("54b131f3-525f-451c-9976-a4bbcd8393c6");
    public static readonly Guid Glacier = Guid.Parse("7d3e9b52-1c84-4f0a-a6d7-5e2b8c9f1a03");
    public static readonly Guid FrostMail = Guid.Parse("b84f2c6e-9a17-4d3b-8e50-c1f7a2d6e9b4");
    private static readonly Guid GlacierRecipe = Guid.Parse("e2a6c8f4-3b19-4d75-9c02-7f5e1a8b4d36");
    private static readonly Guid FrostMailRecipe = Guid.Parse("4c9b1e7a-62d8-4f13-b5a0-8e3d7c2f9b51");
    private static readonly Guid FrostShardRecipe = Guid.Parse("bf0b62aa-c96e-4354-b776-032580a8222f");
    private static readonly Guid FrostEdgeRecipe = Guid.Parse("51805e95-e1ea-44ef-85ea-cfbfc7882644");

    private HashSet<string> _kits = new();
    private IDisposable? _frostbite, _wearFrost;

    public override void OnLoad()
    {
        if (!NewContent.Available)
        {
            Log.Warn("kiln.content cannot add items on this game build; NewItems does nothing");
            return;
        }
        // Register in OnLoad: kiln.content puts them into the registries when the world loads, before the save is read.
        Content.Items.Register(new ItemDefinition
        {
            Guid = FrostShard,
            Name = "Kiln_Sample_FrostShard",
            BaseOn = "Material_T1_Resin",
            DisplayName = "Frost Shard",
            Description = "A splinter of never-melting ice. Added by the Kiln NewItems sample.",
        });
        Content.Items.Register(new ItemDefinition
        {
            Guid = FrostEdge,
            Name = "Kiln_Sample_FrostEdge",
            BaseOn = "Weapon_T1_1H_SwordEpic_01",
            DisplayName = "Frost Edge",
            Description = "A bronze blade quenched in frost. Hits harder than it looks.",
            // A crafted Frost Edge is what its recipe tooltip shows: Rare at the base's lowest level (1), though the Bronze Sword
            // is a loot weapon. kiln.content makes new items fixed; ["itemLevelRange.minLevel"] chooses another level.
            Fields = new Dictionary<string, object> { ["damageSetup.dmgMod"] = 1.5, ["rarity"] = "Rare" },
        });
        // Kitbash (kiln.content v4): a new sword with another sword's model, its own stats, perks and icon, texts per
        // language; a new chest armour that wears another set's model. Nothing new is loaded: every reference points at
        // something the game already has.
        Content.Items.Register(new ItemDefinition
        {
            Guid = Glacier,
            Name = "Kiln_Sample_Glacier",
            BaseOn = "Weapon_T1_1H_SwordEpic_01",            // behaviour: a one-handed sword (Bronze Sword)
            Model = "item:Weapon_T4_1H_SwordEpic_02",        // looks: another sword's model and model setup
            Icon = "icons/glacier.png",                       // its own icon (kiln.assets on Kiln clients)
            Perks = ["item:Weapon_T5_1H_SwordEpic_01", "item:Weapon_T5_1H_SwordEpic_01"],  // two perk slots of a T5 sword
            DisplayName = "Glacier",
            Texts = new Dictionary<string, ItemTexts>
            {
                ["en"] = new() { Name = "Glacier", Description = "A blade of old ice, borrowed from a greater sword's mould." },
                ["de"] = new() { Name = "Gletscher", Description = "Eine Klinge aus altem Eis, in der Form eines größeren Schwerts." },
                ["fr"] = new() { Name = "Glacier", Description = "Une lame de glace ancienne, coulée dans le moule d'une plus grande épée." },
            },
            Fields = new Dictionary<string, object> { ["damageSetup.dmgMod"] = 1.6, ["rarity"] = "Epic" },
        });
        Content.Items.Register(new ItemDefinition
        {
            Guid = FrostMail,
            Name = "Kiln_Sample_FrostMail",
            BaseOn = "Armor_T1_Warrior_DPS_Chest",
            Model = "item:Armor_T2_Warrior_Tank_Chest",       // worn: another set's chest model
            Texts = new Dictionary<string, ItemTexts>
            {
                ["en"] = new() { Name = "Frost Mail", Description = "Chain links rimed with frost." },
                ["de"] = new() { Name = "Frostkettenhemd", Description = "Kettenglieder voller Raureif." },
            },
            Fields = new Dictionary<string, object> { ["armorSetup.distribution.physical"] = 1.5, ["rarity"] = "Rare" },
        });
        Content.Recipes.Register(new RecipeDefinition
        {
            Guid = GlacierRecipe,
            Name = "Kiln_Sample_Glacier_Recipe",
            Station = "hand",
            Inputs = [("Kiln_Sample_FrostEdge", 1), ("Kiln_Sample_FrostShard", 5)],
            Outputs = [("Kiln_Sample_Glacier", 1)],
        });
        Content.Recipes.Register(new RecipeDefinition   // only new items as ingredients: unlocked through item knowledge
        {
            Guid = FrostMailRecipe,
            Name = "Kiln_Sample_FrostMail_Recipe",
            Station = "hand",
            Inputs = [("Kiln_Sample_FrostShard", 4)],
            Outputs = [("Kiln_Sample_FrostMail", 1)],
        });
        Content.Recipes.Register(new RecipeDefinition
        {
            Guid = FrostShardRecipe,
            Name = "Kiln_Sample_FrostShard_Recipe",
            Station = "hand",
            Inputs = [("Material_T1_Twigs", 2)],
            Outputs = [("Kiln_Sample_FrostShard", 1)],
        });
        Content.Recipes.Register(new RecipeDefinition
        {
            Guid = FrostEdgeRecipe,
            Name = "Kiln_Sample_FrostEdge_Recipe",
            Station = "hand",
            Inputs = [("Kiln_Sample_FrostShard", 3), ("Material_T1_Twigs", 2)],
            Outputs = [("Kiln_Sample_FrostEdge", 1)],
        });
        Content.OnReady(_ =>
        {
            foreach (var d in NewContent.Definitions().Where(d => d.Name.StartsWith("Kiln_Sample_", StringComparison.Ordinal)))
                Log.Info($"NewItems: {d}");
            GlacierPerk();
        });
        // "/frostbite" (admins; also from kiln/control/console in an offline world): the Frostbite buff on yourself, through
        // the game's applyBuff (Kiln.Abilities), to see the clone's name, description and 8 s in the buff bar.
        // A command handler returns void: it starts the async Task, which catches what it can throw (never async void).
        _frostbite = Commands.Register("frostbite", c => _ = ApplyFrostbite(c), CommandPermission.Admin,
            description: "put the Frostbite buff (a kiln.gamedata clone) on yourself for its 8 s");
        // "/wearfrost" (admins): puts Frost Mail on your chest through the game's own equip (Items.Equip, dev.27); what you
        // wore there goes into the backpack. "/wearfrost off" takes the chest piece off again (Items.Unequip).
        _wearFrost = Commands.Register("wearfrost", c => _ = WearFrost(c), CommandPermission.Admin, usage: "[off]",
            description: "put Frost Mail on (or take the chest piece off) through the game's own equip");
        // Starter kit: only where the server world runs (a dedicated server, or a game that hosts).
        if (Players.Available)
        {
            LoadKits();
            Players.Joined += p => _ = GiveKit(p);
        }
    }

    // Glacier's third perk slot: the perk clone of content/frostbite.json (kiln.gamedata). Set here, where both the item
    // and the perk exist (the pack's clones register after new items); perkIds[2] follows (FNV-1a of the perk's GUID).
    // A code layer of this mod: on the server and on every client that has the mod, so tooltips show it too.
    private void GlacierPerk()
    {
        if (!Content.GameDataAvailable) return;
        var glacier = Content.ItemData.Get(Glacier);
        var perk = Content.Perks.Get("Kiln_Sample_FrostbiteEdge");
        if (glacier == null || perk == null)
        {
            Log.Warn($"NewItems: Glacier {(glacier == null ? "missing" : "ok")}, perk Kiln_Sample_FrostbiteEdge {(perk == null ? "missing (see the pack report)" : "ok")}");
            return;
        }
        glacier.Set("perkReferences[2]", perk.Guid);
        var buff = Content.Buffs.Get("Kiln_Sample_Frostbite");
        Log.Info($"NewItems: Glacier perk 3 = {perk.Name} (0x{perk.Id:x8}), which applies {buff?.Name} (0x{buff?.Id:x8}, {buff?.Get("defaultLifeTime")})");
    }

    public override void OnUnload()
    {
        _frostbite?.Dispose();
        _wearFrost?.Dispose();
    }

    // Items.Equip / Items.Unequip follow Kiln's error rule: a refusal by the game is a result (Ok false, Reason), a bad
    // argument throws ArgumentException, a capability that is off (items.equip) throws KilnUnavailableException.
    private async Task WearFrost(CommandContext c)
    {
        try
        {
            var who = !c.Source.Player.IsNone ? c.Source.Player : Players.All.FirstOrDefault()?.Entity ?? EntityId.None;
            if (who.IsNone)
            {
                c.Reply("no player to dress");
                return;
            }
            if (c.Count > 0 && c.Args[0] == "off")
            {
                var off = await Items.Unequip(who, EquipmentSlot.ArmourUpperBody);
                c.Reply(off.Ok ? $"chest piece {Items.Get(off.Item)?.Label} is in the backpack ({off.To})" : $"not taken off: {off.Reason}");
                return;
            }
            var mail = Items.Lookup("Kiln_Sample_FrostMail");
            if (mail == null)
            {
                c.Reply("Frost Mail is not registered here");
                return;
            }
            var slot = Items.Read(who)?.Slots.FirstOrDefault(s => s.Item == mail.Id && !s.IsEquipped);
            if (slot == null)
            {
                await Items.Give(who, mail.Id, 1);
                slot = Items.Read(who)?.Slots.FirstOrDefault(s => s.Item == mail.Id && !s.IsEquipped);
            }
            if (slot == null)
            {
                c.Reply("no Frost Mail in the inventory (the backpack is full?)");
                return;
            }
            var on = await Items.Equip(who, slot.Slot, EquipmentSlot.ArmourUpperBody);
            c.Reply(on.Ok
                ? $"Frost Mail on{(on.Replaced.IsNone ? "" : $"; {Items.Get(on.Replaced)?.Label} went to {on.ReplacedTo}")}"
                : $"not equipped: {on.Reason}");
        }
        catch (Exception e) when (e is KilnUnavailableException or ArgumentException)
        {
            c.Reply($"wearfrost: {e.Message}");
        }
    }

    private async Task ApplyFrostbite(CommandContext c)
    {
        try
        {
            var buff = Content.GameDataAvailable ? Content.Buffs.Get("Kiln_Sample_Frostbite") : null;
            if (buff == null || !Abilities.Available)
            {
                c.Reply($"Frostbite is not available here (buff {(buff == null ? "missing" : "ok")}, kiln.abilities {(Abilities.Available ? "ok" : "missing")})");
                return;
            }
            var who = !c.Source.Player.IsNone ? c.Source.Player : Players.All.FirstOrDefault()?.Entity ?? EntityId.None;
            if (who.IsNone)
            {
                c.Reply("no player to put it on");
                return;
            }
            var by = await Abilities.ApplyBuff(who, buff.Guid);
            c.Reply($"Frostbite (0x{buff.Id:x8}, {buff.Get("defaultLifeTime")}) applied to {who} by {by}");
        }
        catch (Exception e)
        {
            c.Reply($"Frostbite: {e.Message}");
        }
    }

    private string KitsFile => Path.Combine(Context.DataDirectory, "kits.json");

    private void LoadKits()
    {
        try
        {
            if (File.Exists(KitsFile)) _kits = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(KitsFile)) ?? new();
        }
        catch (Exception e)
        {
            Log.Warn($"NewItems: {KitsFile} unreadable ({e.Message}); starting a new list");
        }
    }

    private async Task GiveKit(Player p)
    {
        var key = p.SteamId != 0 ? p.SteamId.ToString() : p.Name;
        if (_kits.Contains(key)) return;
        await Context.Scheduler.NextTick();
        var edge = await Items.Give(p.Entity, new ItemId(NewContent.IdOf(FrostEdge)), 1, options: GiveOptions.Notify);
        var shards = await Items.Give(p.Entity, new ItemId(NewContent.IdOf(FrostShard)), 3, options: GiveOptions.Notify);
        var twigs = await Items.Give(p.Entity, "Material_T1_Twigs", 6, options: GiveOptions.Notify);  // to try the recipes
        Log.Info($"NewItems: starter kit for {p}: {edge}; {shards}; {twigs}");
        if (edge.Succeeded || shards.Succeeded)
        {
            _kits.Add(key);
            Directory.CreateDirectory(Context.DataDirectory);
            File.WriteAllText(KitsFile, JsonSerializer.Serialize(_kits));
        }
    }
}
