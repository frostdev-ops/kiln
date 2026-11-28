// Made by tools/docs-site/snippets.py from agent/skills/kiln-modding/references/api-pets.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.skill_api_pets_1
{
    public sealed partial class Snippet : Mod
    {
#line 26 "agent/skills/kiln-modding/references/api-pets.md"
public override void OnLoad()
{
    // a game creature by its creatures-registry id: asks nothing of players' games
    Blocks.Define("hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Hound" });   // "my-mod:hound"

    // every choice spelled out, no Kiln.Blocks
    var def = Pets.Define(new PetDefinition
    {
        Id = "my-mod:duelist",
        Creature = "game:enemy_vukah_melee",
        Name = "Duelist",
        Level = new PetLevelPolicy { Mode = PetLevelMode.OwnerAtSummon },
        Movement = new PetMovementPolicy { FollowDistance = 4, RunDistance = 12 },
        Targets = new PetTargetPolicy { Pvp = PetPvpMode.ByModRule },
        Downed = new PetDownedPolicy { Outcome = PetDownedOutcome.Absent },
        Revive = new PetRevivePolicy { ChannelDuration = TimeSpan.FromSeconds(5), Cost = new PetCostDefinition(Items.Lookup("Material_T1_Twigs")!.Id, 3) },
        Limits = new PetLimits { LivePerCharacter = 2 },
        Hud = new PetHudPolicy { RecoveryMarker = false },
    });
    if (!def.Ok) Log.Warn($"pet not defined: {def.Reason}");   // DefinitionInUse, CreatureUnknown, CreatureUnsupported, ...
}
#line default
    }
}

namespace DocSnippets.skill_api_pets_2
{
    using static DocSnippets.skill_api_pets_2.Stubs;
    internal static class Stubs
    {
        public static EntityId player => default!;
        public static EntityId enemy => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 84 "agent/skills/kiln-modding/references/api-pets.md"
var key = CharacterData.KeyOf(player);                  // the character (never account-only)
var given = await Pets.Grant(key, "my-mod:hound", PetRequest.New());       // a Ready record, no body yet
var who = PlayerRef.Of(player);
var pet = given.Pet!.Id;
await Pets.Summon(who, pet, PetRequest.New());          // a protected body 2 to 4 m away, following
await Pets.Command(who, pet, PetOrder.Stay, EntityRef.None, PetRequest.New());
await Pets.Command(who, pet, PetOrder.Attack, EntityRef.Of(enemy), PetRequest.New());   // your rule says when; Kiln checks the target
await Pets.Revive(who, pet, PetRequest.New());          // stand at its recovery anchor; summon the anchor first
await Pets.Release(who, pet, PetRequest.New());         // for good: a tombstone until a save holds it
foreach (var p in Pets.Of(key)) Log.Info($"{p.Label}: {p.Life} {p.Presence} {p.Health}/{p.HealthMax}");
#line default
        }
    }
}

namespace DocSnippets.skill_api_pets_3
{
    using static DocSnippets.skill_api_pets_3.Stubs;
    internal static class Stubs
    {
        public static bool FarFromOwner(PetSnapshot pet) => false;
        public static bool OnCooldown(CharacterKey owner) => false;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 124 "agent/skills/kiln-modding/references/api-pets.md"
Events.On<PetDowningEvent>(e =>                         // a down is coming: choose what it becomes (it cannot be refused)
{
    if (e.Pet.Definition == "my-mod:hound" && FarFromOwner(e.Pet)) e.Outcome = PetDownedOutcome.Release;
});
Events.On<PetRevivingEvent>(e => { if (OnCooldown(e.Owner)) e.Cancel(); });    // nothing was taken yet
Events.On<PetRevivedEvent>(e => Log.Info($"{e.Change.After.Label} is back"));
#line default
        }
    }
}
