using Kiln;

namespace KilnItems1;

/// <summary>New content from code (Forge's DeferredRegister): a sword that is a clone of the Bronze Sword with more
/// damage and its own texts, and a hand recipe for it. content/items.json adds a material the same way as data.
/// Register in OnLoad: kiln.content puts the definitions into the game's registries when the world loads, before the
/// save is read, so saved items stay valid across restarts.</summary>
public sealed class KilnItems1Mod : Mod
{
    // Fixed forever (dotnet new made them for this project): the item GUID gives the ItemId the saves store.
    private static readonly Guid Blade = Guid.Parse("6e1c3a52-0b7d-4f3e-9c1a-2d5b8e7f4a10");
    private static readonly Guid BladeRecipe = Guid.Parse("a8f24c61-5d3e-4b9a-8e07-1c6f2b9d3e54");

    public override void OnLoad()
    {
        // new items are kiln.content's (Content.Available) with its feature content.new; NewContent.Available also asks
        // that this kiln.content is one that defines items
        if (!Content.Available || !Capabilities.Is("content.new") || !NewContent.Available)
        {
            Log.Warn("kiln.content cannot add items on this game build; nothing registered");
            return;
        }
        Content.Items.Register(new ItemDefinition
        {
            Guid = Blade,
            Name = "KilnItems1_Blade",
            BaseOn = "Weapon_T1_1H_SwordEpic_01",               // behaviour, stats and model of the Bronze Sword
            Icon = "icons/shard.png",                           // a PNG of your own (Kiln clients with the mod)
            Texts = new Dictionary<string, ItemTexts>
            {
                ["en"] = new() { Name = "KilnItems1 Blade", Description = "A bronze blade that hits a little harder." },
                ["de"] = new() { Name = "KilnItems1-Klinge", Description = "Eine Bronzeklinge, die etwas fester zuschlägt." },
            },
            // A crafted item is what its recipe tooltip shows: this rarity, at the base's lowest level, or the level you set with
            // ["itemLevelRange.minLevel"]. RolledLoot = true is for loot items only (a random level and rarity).
            Fields = new Dictionary<string, object> { ["damageSetup.dmgMod"] = 1.25, ["rarity"] = "Rare" },
        });
        Content.Recipes.Register(new RecipeDefinition
        {
            Guid = BladeRecipe,
            Name = "KilnItems1_Blade_Recipe",
            Station = "hand",
            // Code definitions register before the data packs apply, so a code recipe takes game items (or items
            // defined in code), not the data pack's KilnItems1_Shard.
            Inputs = [("Material_T1_Resin", 2), ("Material_T1_Twigs", 2)],
            Outputs = [("KilnItems1_Blade", 1)],
        });
        Content.OnReady(_ =>
        {
            var shard = Content.Items.Get("KilnItems1_Shard");
            var blade = Content.Items.Get("KilnItems1_Blade");
            Log.Info($"new items: KilnItems1_Shard {(shard != null ? "registered" : "MISSING")}, " +
                     $"KilnItems1_Blade {(blade != null ? $"registered (dmgMod {blade.Get("damageSetup.dmgMod")})" : "MISSING")}");
        });
    }
}
