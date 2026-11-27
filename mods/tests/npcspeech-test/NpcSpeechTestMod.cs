// SPDX-License-Identifier: MIT
// Lab driver for Kiln.Npcs: one console command per call, each replying with the result, so tools/nettest/npcspeechtest.py
// and npcdialogtest.py can line up what they get as Kiln peers on kiln:npcspeech and kiln:npcdialog. Entity ids need not
// be real NPCs: the host checks nothing about the entity (a client's bubble and menu do), which lets a fresh lab world
// without villagers test the server half; `near <player>` finds a real one next to a player (a LABBOT bot).
using Kiln;

namespace NpcSpeechTest;

public sealed class NpcSpeechTestMod : Mod
{
    private static readonly string[] Lines = ["No food, no work.", "My stomach...", "Is there nothing to eat?"];
    private static int _byCalls, _picks;
    private static readonly List<string> Seen = new();
    private IDisposable? _command;

    public override void OnLoad() =>
        _command = Commands.Register("npcspeech-test", Run, CommandPermission.Admin,
            usage: "say|replace|suppress|clear|stop|status|pick <mode> [seed]|farewell|farewell-clear|menuline|menuline-clear|topics|" +
                   "topics-clear|refresh|reply <player>|near <player>|place <player>|calls <entity>",
            description: "Kiln.Npcs lab driver");

    public override void OnUnload() => _command?.Dispose();

    private static void Run(CommandContext c)
    {
        var verb = c.Count > 0 ? c.Args[0] : "status";
        var npc = c.Count > 1 && uint.TryParse(c.Args[1], out var id) && id != 0 ? new EntityId(id) : new EntityId(900001);
        var speech = Npcs.Speech(npc);
        string? extra = null;
        SpeechResult? r = verb switch
        {
            "say" => Npcs.Say(npc, UiText.Of(("en", "I'm starving..."), ("de", "Ich verhungere...")), TimeSpan.FromSeconds(30)),
            "replace" => speech.Replace(Lines),
            "suppress" => speech.Suppress(),
            "clear" => Do(speech.Clear),
            "stop" => Do(() => Npcs.StopSaying(npc)),
            // dev.32: how a Replace picks, farewells, the menu's line (a host callback per player), topics, replies
            "pick" => Pick(speech, c.Count > 2 ? c.Args[2] : "random", c.Count > 3 && int.TryParse(c.Args[3], out var seed) ? seed : 0),
            "farewell" => Farewell(npc),
            "farewell-clear" => Do(() => Npcs.Farewells(npc).Clear()),
            "menuline" => MenuLine(npc),
            "menuline-clear" => Do(() => Npcs.MenuLine(npc).Clear()),
            "refresh" => Do(() => Npcs.MenuLine(npc).Refresh()),
            "topics" => Npcs.Topics(npc).Set(
                new NpcTopic("feed", UiText.Of(("en", "Here, have some bread."), ("de", "Hier, etwas Brot.")))
                {
                    Reply = "Thank you, Flameborn!",
                    Hint = NpcTopicHint.Attention,
                    OnPick = ctx =>
                    {
                        Interlocked.Increment(ref _picks);
                        lock (Seen) Seen.Add($"pick {ctx.Topic.Id} by {ctx.Player.Value}");
                    },
                },
                new NpcTopic("bye", "I have to go.") { Reply = "Take care.", Close = true }),
            "topics-clear" => Do(() => Npcs.Topics(npc).Clear()),
            "reply" => Npcs.MenuSay(npc, new EntityId(c.Count > 2 && uint.TryParse(c.Args[2], out var p) ? p : 0u) is { IsNone: false } who
                ? who
                : new EntityId(1), "Thank you!"),
            "near" => Near(c, out extra),
            "place" => Place(c),
            _ => null,
        };
        var villagers = Villagers.List();
        string seen;
        lock (Seen) seen = string.Join(" / ", Seen.TakeLast(4));
        c.Reply($"npcspeech-test {verb} {npc.Value}: {(r is null ? "-" : r.ToString())}; mode {Npcs.SpeechOf(npc)}, saying {Npcs.IsSaying(npc)}, " +
                $"farewells {Npcs.Farewells(npc).Mode}, menu line {Npcs.MenuLine(npc).Mode}, topics {Npcs.Topics(npc).List.Count}, " +
                $"by calls {_byCalls}, picks {_picks}{(seen.Length > 0 ? $" [{seen}]" : "")}, " +
                $"available {Npcs.Available}, villagers {villagers.Count} (can speak {villagers.Count(v => Npcs.CanSpeak(v.Entity))}), " +
                $"kiln.npcspeech {Capabilities.Get("kiln.npcspeech").State}, farewell row {Capabilities.Get("npcspeech.farewell").State}" +
                (extra is null ? "" : $"; {extra}"));
    }

    private static SpeechResult Pick(NpcSpeech speech, string mode, int seed)
    {
        speech.Pick = mode switch
        {
            "inorder" => SpeechPick.InOrder,
            "seeded" => SpeechPick.Seeded(seed),
            "by" => SpeechPick.By(ctx =>
            {
                Interlocked.Increment(ref _byCalls);
                lock (Seen) Seen.Add($"by {ctx.Kind} for {ctx.Player.Value} opens {ctx.Opens}");
                return ctx.Opens % ctx.Lines;
            }),
            _ => SpeechPick.Random,
        };
        return speech.Replace(Lines);
    }

    private static SpeechResult Farewell(EntityId npc)
    {
        var f = Npcs.Farewells(npc);
        f.Pick = SpeechPick.InOrder;
        return f.Replace(new[] { UiText.Of(("en", "Eat well!"), ("de", "Guten Appetit!")), new UiText("Bring bread next time.") });
    }

    private static SpeechResult MenuLine(EntityId npc)
    {
        var m = Npcs.MenuLine(npc);
        m.Pick = SpeechPick.By(ctx =>
        {
            Interlocked.Increment(ref _byCalls);
            lock (Seen) Seen.Add($"by {ctx.Kind} for {ctx.Player.Value}");
            return 0;
        });
        return m.Replace("I can't work like this. Bring me food.");
    }

    // The NPC with speech nearest to a player (a LABBOT bot), from Villagers and the one `place` put there.
    private static SpeechResult Near(CommandContext c, out string? extra)
    {
        extra = null;
        var who = new EntityId(c.Count > 2 && uint.TryParse(c.Args[2], out var p) ? p : 0u);
        if (who.IsNone || World.Position(who) is not { } at) return new SpeechResult(false, $"no position for player {who.Value}");
        var all = Villagers.List().Where(v => v.Position is not null && Npcs.CanSpeak(v.Entity))
            .Select(v => (v.Entity, Distance: v.Position!.Value.DistanceTo(at))).ToList();
        if (!_placed.IsNone && Npcs.CanSpeak(_placed) && World.Position(_placed) is { } pp && all.All(x => x.Entity != _placed))
            all.Add((_placed, pp.DistanceTo(at)));
        if (all.Count == 0) return new SpeechResult(false, $"no villager with speech in this world yet{(_placeError is null ? "" : $" ({_placeError})")}");
        var best = all.OrderBy(x => x.Distance).First();
        extra = $"near {best.Entity.Value} at {best.Distance:0} m";
        return new SpeechResult(true, null);
    }

    private static EntityId _placed;
    private static string? _placeError;

    // A fresh lab world has no villagers: Ramone's template (a villager with NpcDialog), spawned 3 m from a player (a
    // LABBOT bot), so the real-NPC checks have an NPC. `near` finds it a moment later.
    private const string Ramone = "fe2ce3af-e771-4179-9450-f2dee8d21209";

    private static SpeechResult Place(CommandContext c)
    {
        var who = new EntityId(c.Count > 2 && uint.TryParse(c.Args[2], out var p) ? p : 0u);
        if (who.IsNone || World.Position(who) is not { } at) return new SpeechResult(false, $"no position for player {who.Value}");
        _placeError = null;
        _ = PlaceAt(at with { X = at.X + 3 });
        return new SpeechResult(true, "placing Ramone 3 m from the player");
    }

    private static async Task PlaceAt(WorldPosition at)
    {
        try
        {
            _placed = await Creatures.Spawn(Ramone, at, 180, new CreatureSpawnOptions { Tag = "npcspeech-test" });
        }
        catch (Exception e)
        {
            _placeError = $"place: {e.GetType().Name}: {e.Message}";
        }
    }

    private static SpeechResult Do(Action a)
    {
        a();
        return new SpeechResult(true, null);
    }
}
