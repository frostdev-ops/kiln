// Made by tools/docs-site/snippets.py from docs/kiln/modding/pets.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_pets_1
{
#line 17 "docs/kiln/modding/pets.md"
public sealed class HoundMod : Mod
{
    public override void OnLoad() =>
        Blocks.Define("hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Hound" });

    [Command("hound", Description = "get a hound and bring it out", Permission = CommandPermission.Admin)]
    private async Task Hound(CommandContext c)
    {
        if (!Capabilities.Is(Pets.RecordsCapability)) throw new CommandException($"pets.records is off: {Capabilities.Why(Pets.RecordsCapability)}");
     if (!Capabilities.Is(Pets.SummonCapability)) throw new CommandException($"pets.summon is off: {Capabilities.Why(Pets.SummonCapability)}");
        var key = CharacterData.KeyOf(c.Source.Player);
        var given = await Pets.Grant(key, "hound-mod:hound", PetRequest.New());       // the id is <mod id>:<name>
        if (!given.Ok) throw new CommandException($"refused: {given.Reason}");
        var summoned = await Pets.Summon(PlayerRef.Of(c.Source.Player), given.Pet!.Id, PetRequest.New());
        c.Reply(summoned.Ok ? "your hound is out" : $"refused: {summoned.Reason}");
    }
}
#line default
}

namespace DocSnippets.docs_modding_pets_2
{
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 49 "docs/kiln/modding/pets.md"
// a game creature, by its creatures-registry id: asks nothing of players' games
var hound = Blocks.Define("hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Hound" });

// a creature block of your own, and every choice spelled out
var wolf = Blocks.Define("wolf", new CreatureBlock { Body = Bodies.Vukah });
Blocks.Define("duelist", new PetBlock
{
    Creature = wolf,
    Name = "Duelist",
    Level = new PetLevelPolicy { Mode = PetLevelMode.OwnerAtSummon },
    Movement = new PetMovementPolicy { FollowDistance = 4, AllowCatchUp = false },
    Targets = new PetTargetPolicy { Pvp = PetPvpMode.ByModRule, HostileFactions = [Faction.Vukah, Faction.Fogger] },
    Downed = new PetDownedPolicy { Outcome = PetDownedOutcome.Absent },
    Revive = new ReviveBlock { ChannelDuration = TimeSpan.FromSeconds(5), Cost = new ReviveCostBlock("Material_T1_Twigs", 3) },
    Limits = new PetLimits { LivePerCharacter = 2 },
    Hud = new PetHudPolicy { RecoveryMarker = false },
});
#line default
        }
    }
}

namespace DocSnippets.docs_modding_pets_3
{
    using static DocSnippets.docs_modding_pets_3.Stubs;
    internal static class Stubs
    {
        public static EntityId player => default!;
        public static EntityId enemy => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 96 "docs/kiln/modding/pets.md"
if (!Capabilities.Is(Pets.RecordsCapability) || !Capabilities.Is(Pets.SummonCapability)) return;   // a joined client has neither
var owner = CharacterData.KeyOf(player);              // the character
var given = await Pets.Grant(owner, "my-mod:hound", PetRequest.New());
if (!given.Ok) { Log.Info($"not given: {given.Refusal}"); return; }
var who = PlayerRef.Of(player);
var pet = given.Pet!.Id;
var r = await Pets.Summon(who, pet, PetRequest.New());
if (!r.Ok) Log.Info($"not out: {r.Refusal}");          // a refusal is a result: Capacity, NoSafeGround, ...
if (Capabilities.Is(Pets.FollowCapability))
{
    r = await Pets.Command(who, pet, PetOrder.Stay, EntityRef.None, PetRequest.New());
    r = await Pets.Command(who, pet, PetOrder.Attack, EntityRef.Of(enemy), PetRequest.New());   // checked against the pet's rule
    if (r.Refusal == PetRefusal.TargetProtected) Log.Info("its rule protects that target");
}
if (Capabilities.Is(Pets.DownedCapability))
{
    r = await Pets.Revive(who, pet, PetRequest.New());  // stand at its recovery anchor
    if (!r.Ok) Log.Info($"no revive: {r.Refusal}");
}
r = await Pets.Release(who, pet, PetRequest.New());
if (!r.Ok) Log.Info($"not released: {r.Refusal}");
foreach (var p in Pets.Of(owner)) Log.Info($"{p.Label}: {p.Life} {p.Presence} {p.Health}/{p.HealthMax}");
#line default
        }
    }
}
