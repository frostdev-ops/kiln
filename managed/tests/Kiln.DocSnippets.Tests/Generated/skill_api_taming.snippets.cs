// Made by tools/docs-site/snippets.py from agent/skills/kiln-modding/references/api-taming.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.skill_api_taming_1
{
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 25 "agent/skills/kiln-modding/references/api-taming.md"
// 1. data: a TamingBlock (blocks/*.json section "tamings"), items and pets named as blocks name them
var bait = Blocks.Define("wolf-bait", new ItemBlock { Name = "Wolf Bait", Stack = 20 });
var pet = Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Tamed Wolf" });
Blocks.Define("wolf", new TamingBlock
{
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
    Steps = [TamingStepBlock.Weaken(0.4), TamingStepBlock.Feed(bait, 2)],
    Pet = (BlockHandle<PetBlock>)pet,
    Refund = TamingRefundPolicy.RefundOnFail,
});

// 2. code: a TamingDefinition (game item ids resolve once Items.Available); Kiln keeps its own copy
var defined = Taming.Define(new TamingDefinition
{
    Id = "my-mod:dog", Creatures = ["game:animal_wildlife_dog_*"],
    Steps = [new NativeStep(1), new NativeStep(1)],
    InactivityTimeout = TimeSpan.FromMinutes(2), MaxAttemptDuration = TimeSpan.FromMinutes(10),
});
if (!defined.Ok) Log.Warn($"taming not defined: {defined.Reason}");   // DefinitionInUse: attempts of the old one run

// 3. JSON text: Taming.DefineJson(json) takes one object or an array
Taming.DefineJson("""
  { "id": "my-mod:wolf2", "creatures": ["game:enemy_wildbeast_wolf_earlygame"], "pet": "my-mod:tame-wolf",
    "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 },
               { "kind": "feed", "item": "game:food_t1_raw_bad_meat_leanmeat", "count": 2 } ],
    "refund": "refundOnFail" }
  """);
#line default
        }
    }
}

namespace DocSnippets.skill_api_taming_2
{
    using static DocSnippets.skill_api_taming_2.Stubs;
    internal static class Stubs
    {
        public static PlayerRef player => default!;
        public static EntityId wolf => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 79 "agent/skills/kiln-modding/references/api-taming.md"
if (!Capabilities.Is(Taming.Capability)) return;                         // "taming.steps": a server world runs
var r = await Taming.Begin(player, EntityRef.Of(wolf), "my-mod:wolf", TamingRequest.New());   // a begin takes no token
if (!r.Ok || r.Progress is not { } p) { Log.Info(r.Reason ?? "refused"); return; }     // Busy, TemplateMismatch, OutOfRange, DefinitionUnknown, ...
r = await Taming.Advance(player, p.Id, TamingRequest.New(p.Token));      // weaken: no selection; p.Token is the last answer's
if (!r.Ok || r.Progress is not { } q) { Log.Info(r.Reason ?? "refused"); return; }
var choices = Taming.Choices(player, q.Id);                               // slots the server read, and the view's token
r = await Taming.Advance(player, q.Id, TamingRequest.New(choices.Token), choices.Choices[0].Selection);
if (r.Ok && r.Pet is { } tamed) Log.Info($"tamed: {tamed.Id}");            // the new Kiln.Pets record
else if (r.Refusal == TamingRefusal.CommitDeferred) { /* every step paid: Taming.Retry later; nothing more is taken */ }
#line default
        }
    }
}
