// SPDX-License-Identifier: MIT
// Kiln.Taming from a mod (docs/kiln/modding/taming.md). Kiln runs the attempts, takes the exact costs with receipts,
// settles them at the end and binds the tame to a pet record; this mod makes every choice:
//   wolf   (JSON below) Kiln's steps: weaken it to 40 %, then feed it 2 raw lean meat from the slot the player picks;
//          a failure that is not the player's gives the meat back (refundOnFail); a tame makes a Kiln.Pets "tame-wolf" and
//          the pet comes out; the wild wolf goes.
//   dog    the game's own taming: the player baits and pets a wild dog as the game offers it; Kiln's native step follows
//          the game's count (no cost of Kiln's) and no pet is made: the dog stays the game's farm animal.
// Commands: /tame begin (the nearest wolf or wild dog in 3 m), /tame step, /tame feed, /tame retry, /tame cancel,
// /tame status.
using Kiln;

namespace TamingSample;

public sealed class TamingSampleMod : Mod
{
    private const string Wolf = """
        { "id": "taming-sample:wolf", "creatures": ["game:enemy_wildbeast_wolf_earlygame"], "pet": "taming-sample:tame-wolf",
          "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 },
                     { "kind": "feed", "item": "game:food_t1_raw_bad_meat_leanmeat", "count": 2 } ],
          "rangeMeters": 3, "inactivitySeconds": 60, "maxAttemptSeconds": 300, "refund": "refundOnFail" }
        """;

    private readonly Dictionary<PlayerRef, TamingProgress> _attempts = [];
    private bool _defined;

    public override void OnLoad()
    {
        Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Tamed Wolf" });
        Context.Tick.Pre += (in TickInfo t) =>
        {
            // a definition needs no world (Kiln.Taming loaded is enough), but the items registry ids in its JSON resolve only
            // once the game's items are readable (a TamingBlock names items, the mod's own too, and needs no wait)
            if (_defined || t.Step % 25 != 0 || !Items.Available) return;
            _defined = true;
            Taming.DefineJson(Wolf);
            Taming.Define(new TamingDefinition
            {
                Id = "taming-sample:dog", Creatures = ["game:animal_wildlife_dog_*"], Steps = [new NativeStep(1), new NativeStep(1)],
                InactivitySeconds = 120, MaxAttemptSeconds = 600,
            });
        };
        Events.On<TamingStepChangedEvent>(e => Log.Info($"{e.After.Definition}: step {e.After.CompletedSteps}/{e.After.TotalSteps}, paid {e.Receipts.Sum(r => r.Taken)}"));
        Events.On<TamedEvent>(e => Log.Info($"{e.After.Definition} tamed{(e.Pet is { } p ? $": pet {p.Id}" : "")}"));
        Events.On<TamingFailedEvent>(e => Log.Info($"{e.After.Definition} failed ({e.Cause}); refunds: {e.After.Refunds.Count}"));
        Events.On<NativeTamedEvent>(e => Log.Info($"the game tamed {e.Creature} after {e.Steps} step(s)"));
    }

    private static PlayerRef Player(CommandContext c) =>
        c.Source.Player.IsNone ? throw new CommandException("only a player in the world can tame") : PlayerRef.Of(c.Source.Player);

    private TamingProgress Attempt(PlayerRef who) =>
        _attempts.TryGetValue(who, out var p) ? p : throw new CommandException("no taming runs (/tame begin)");

    private string Answer(PlayerRef who, TamingResult r)
    {
        if (r.Progress is { } p) _attempts[who] = p;   // the next token is the last answer's
        var head = r.Ok ? "ok" : $"refused: {r.Reason}";
        var paid = r.Receipts.Count == 0 ? "" : $"; paid {string.Join(", ", r.Receipts.Select(x => $"{x.Taken} ({x.State})"))}";
        return $"{head}{paid}{(r.Progress is { } q ? $"; {q.State}, step {q.CompletedSteps}/{q.TotalSteps}" : "")}{(r.Pet is { } pet ? $"; your new pet {pet.Id}" : "")}";
    }

    [Command("tame begin", Description = "start taming the nearest wolf or wild dog (3 m)")]
    private void Begin(CommandContext c)
    {
        var who = Player(c);
        var at = World.Position(c.Source.Player) ?? throw new CommandException("your position is not known");
        foreach (var e in Creatures.Near(at, 3))
            foreach (var def in new[] { "taming-sample:wolf", "taming-sample:dog" })
            {
                var r = Taming.Begin(who, EntityRef.Of(e), def, TamingRequest.New());
                if (r.Refusal == TamingRefusal.TemplateMismatch) continue;
                c.Reply($"{def}: {Answer(who, r)}");
                return;
            }
        c.Reply("no wolf or wild dog within 3 m");
    }

    [Command("tame step", Description = "the next step that takes no item (weaken, the game's own step)")]
    private async Task Step(CommandContext c)
    {
        var who = Player(c);
        var p = Attempt(who);
        c.Reply(Answer(who, await Taming.Advance(who, p.Id, TamingRequest.New(p.Token))));
    }

    [Command("tame feed", Description = "feed from the first slot that can pay the step")]
    private async Task Feed(CommandContext c)
    {
        var who = Player(c);
        var p = Attempt(who);
        var choices = Taming.Choices(who, p.Id);
        if (!choices.Ok || choices.Choices.Count == 0) throw new CommandException(choices.Reason ?? "nothing in your inventories can pay this step");
        // a real UI shows the choices and sends back which one; the server revalidates it when the cost runs
        var token = TamingRequest.New(choices.Token);
        c.Reply(Answer(who, await Taming.Advance(who, p.Id, token, choices.Choices[0].Selection)));
    }

    [Command("tame retry", Description = "retry a tame that waits (every step is paid; nothing more is taken)")]
    private async Task Retry(CommandContext c)
    {
        var who = Player(c);
        var p = Attempt(who);
        c.Reply(Answer(who, await Taming.Retry(who, p.Id, TamingRequest.New(p.Token))));
    }

    [Command("tame cancel", Description = "stop taming (paid steps stay paid: the wolf's refund is for failures only)")]
    private async Task Cancel(CommandContext c)
    {
        var who = Player(c);
        var p = Attempt(who);
        c.Reply(Answer(who, await Taming.Cancel(who, p.Id, TamingRequest.New(p.Token))));
    }

    [Command("tame status", Description = "the taming now")]
    private void Status(CommandContext c)
    {
        var who = Player(c);
        var p = Taming.Inspect(Attempt(who).Id) ?? throw new CommandException("the taming is over");
        c.Reply($"{p.Definition}: {p.State}, step {p.CompletedSteps}/{p.TotalSteps}, expires at {p.ExpiresAtSimSeconds:0} s, paid {p.Spent.Count}");
        if (NativeTaming.Available && NativeTaming.Read(p.Target.Id) is { } n)
            c.Reply($"the game: {n.Phase}, {n.Progress}/{n.Required} step(s)");
    }
}
