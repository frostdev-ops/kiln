// Made by tools/docs-site/snippets.py from docs/kiln/modding/taming.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_taming_1
{
#line 39 "docs/kiln/modding/taming.md"
public sealed class WolfTamerMod : Mod
{
    private readonly Dictionary<PlayerRef, TamingProgress> _attempts = [];

    [Command("tame begin", Description = "start taming the nearest wolf (3 m)")]
    private async Task Begin(CommandContext c)
    {
        if (!Taming.Available) throw new CommandException($"taming is off: {Capabilities.Why(Taming.Capability)}");
        var who = PlayerRef.Of(c.Source.Player);
        var at = World.Position(c.Source.Player) ?? throw new CommandException("your position is not known");
        foreach (var wolf in Creatures.Near(at, 3))
        {
            var r = await Taming.Begin(who, EntityRef.Of(wolf), "wolf-tamer:wolf", TamingRequest.New());
            if (r.Refusal == TamingRefusal.TemplateMismatch) continue;                // not a wolf
            if (r.Progress is { } p) _attempts[who] = p;
            c.Reply(r.Ok ? "taming started: /tame step, then /tame feed" : $"refused: {r.Reason}");
            return;
        }
        c.Reply("no wolf within 3 m");
    }

    [Command("tame step", Description = "the step that takes no item (weaken)")]
    private async Task Step(CommandContext c)
    {
        if (!Taming.Available) throw new CommandException($"taming is off: {Capabilities.Why(Taming.Capability)}");
        var who = PlayerRef.Of(c.Source.Player);
        var p = _attempts[who];
        var r = await Taming.Advance(who, p.Id, TamingRequest.New(p.Token));
        if (r.Progress is { } next) _attempts[who] = next;
        c.Reply(r.Ok ? $"step {r.Progress!.CompletedSteps}/{r.Progress.TotalSteps}" : $"refused: {r.Reason}");
    }

    [Command("tame feed", Description = "pay the feeding step from the first slot that can")]
    private async Task Feed(CommandContext c)
    {
        if (!Taming.Available) throw new CommandException($"taming is off: {Capabilities.Why(Taming.Capability)}");
        var who = PlayerRef.Of(c.Source.Player);
        var p = _attempts[who];
        var choices = Taming.Choices(who, p.Id);
        if (!choices.Ok || choices.Choices.Count == 0) throw new CommandException(choices.Reason ?? "nothing pays this step");
        var r = await Taming.Advance(who, p.Id, TamingRequest.New(choices.Token), choices.Choices[0].Selection);
        if (r.Progress is { } next) _attempts[who] = next;
        c.Reply(r.Ok ? (r.Pet is { } pet ? $"tamed: your new pet {pet.Id}" : "fed") : $"refused: {r.Reason}");
    }
}
#line default
}

namespace DocSnippets.docs_modding_taming_2
{
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 98 "docs/kiln/modding/taming.md"
Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Tamed Wolf" });
var defined = Taming.Define(new TamingDefinition
{
    Id = "my-mod:wolf",
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
    Steps = [new WeakenStep(0.4), new FeedStep(Items.Lookup("Food_T1_raw_bad_meat_LeanMeat")!.Id, 2)],
    Pet = "my-mod:tame-wolf",
    Refund = TamingRefundPolicy.RefundOnFail,
});
if (!defined.Ok) Log.Warn($"{defined.Refusal}: {defined.Reason}");   // DefinitionInUse: attempts of the old one run
#line default
        }
    }
}

namespace DocSnippets.docs_modding_taming_3
{
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 159 "docs/kiln/modding/taming.md"
var bait = Blocks.Define("wolf-bait", new ItemBlock { Name = "Wolf Bait", Stack = 20 });
var pet = Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Tamed Wolf" });
Blocks.Define("wolf", new TamingBlock
{
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
    Steps = [TamingStepBlock.Weaken(0.4), TamingStepBlock.Feed(bait, 2)],
    Pet = (BlockHandle<PetBlock>)pet,
    Refund = TamingRefundPolicy.RefundOnFail,
});
#line default
        }
    }
}

namespace DocSnippets.docs_modding_taming_4
{
    using static DocSnippets.docs_modding_taming_4.Stubs;
    internal static class Stubs
    {
        public static PlayerRef player => default!;
        public static EntityId wolf => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 189 "docs/kiln/modding/taming.md"
if (!Taming.Available) return;                              // taming.steps: a server world runs
var r = await Taming.Begin(player, EntityRef.Of(wolf), "my-mod:wolf", TamingRequest.New());
var p = r.Progress!;                                        // p.Token: the challenge, the revision and the step
r = await Taming.Advance(player, p.Id, TamingRequest.New(p.Token));   // weaken: no selection
p = r.Progress!;
var choices = Taming.Choices(player, p.Id);
var pick = choices.Choices[0].Selection;                    // the server's read of the slot the player chose
r = await Taming.Advance(player, p.Id, TamingRequest.New(choices.Token), pick);
if (r.Ok && r.Pet is { } pet) { /* tamed: pet is the new Kiln.Pets record */ }
else if (r.Refusal == TamingRefusal.CommitDeferred) { /* every step paid: Taming.Retry later, nothing more is taken */ }
#line default
        }
    }
}
