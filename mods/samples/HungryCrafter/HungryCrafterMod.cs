// SPDX-License-Identifier: MIT
// A hungry craftsperson, in the game's own speech bubble and dialogue menu (Kiln.Npcs, docs/kiln/services/npc-speech.md):
//   - hunger starts: one Say, at once, over the NPC's head;
//   - while hungry: its greetings are the mod's complaints (Replace), a new one each time it greets, in turn (InOrder);
//     its farewells are the mod's; its menu opens with a plea, worded once for each player (SpeechPick.By, per player);
//     its talk page gets a topic "Here, have some bread." (a villager without a page gets one), which feeds it;
//   - fed: Clear (its own lines and page come back) and one thank-you Say.
// /hunger starve [all] and /hunger feed [all] act on the nearest NPC with speech (or all of them). Everything runs on the
// server's tick thread; the server decides and every Kiln player sees the same bubbles and menus.
using Kiln;

namespace HungryCrafter;

public sealed class HungryCrafterMod : Mod
{
    private static readonly UiText Starving = UiText.Of(("en", "I'm starving... I can't work like this."), ("de", "Ich verhungere... So kann ich nicht arbeiten."));
    private static readonly UiText Thanks = UiText.Of(("en", "Thank you, Flameborn! Back to work."), ("de", "Danke, Flammengeborener! Zurück an die Arbeit."));
    private static readonly UiText[] Complaints =
    [
        UiText.Of(("en", "No food, no work."), ("de", "Kein Essen, keine Arbeit.")),
        UiText.Of(("en", "My stomach is louder than my anvil."), ("de", "Mein Magen ist lauter als mein Amboss.")),
        UiText.Of(("en", "Is there nothing to eat around here?"), ("de", "Gibt es hier nichts zu essen?")),
    ];
    private static readonly UiText[] Farewells =
    [
        UiText.Of(("en", "Come back with something to eat."), ("de", "Komm mit etwas zu essen wieder.")),
        UiText.Of(("en", "Don't forget me, Flameborn."), ("de", "Vergiss mich nicht, Flammengeborener.")),
    ];
    private static readonly UiText[] Pleas =
    [
        UiText.Of(("en", "Please, Flameborn, I haven't eaten in days. I can't craft on an empty stomach."),
                  ("de", "Bitte, Flammengeborener, ich habe seit Tagen nichts gegessen. Mit leerem Magen kann ich nichts herstellen.")),
        UiText.Of(("en", "Still nothing to eat?"), ("de", "Immer noch nichts zu essen?")),
    ];

    private readonly HashSet<EntityId> _hungry = new();
    private IDisposable? _command;

    public override void OnLoad()
    {
        _command = Commands.Register("hunger", OnHunger, CommandPermission.Admin, usage: "starve|feed [all]",
            description: "make the nearest NPC (or all) hungry, or feed it; shows Kiln.Npcs speech bubbles and menu lines");
    }

    public override void OnUnload() => _command?.Dispose();  // Kiln ends this mod's overrides, Says and topics by itself

    private void OnHunger(CommandContext c)
    {
        var verb = c.Count > 0 ? c.Args[0].ToLowerInvariant() : "";
        if (verb is not ("starve" or "feed"))
        {
            c.Reply("usage: /hunger starve|feed [all]");
            return;
        }
        var targets = c.Count > 1 && c.Args[1] == "all" ? Villagers.List().Select(v => v.Entity).ToList() : Nearest(c);
        foreach (var npc in targets.Where(Npcs.CanSpeak))
        {
            var r = verb == "starve" ? Starve(npc) : Feed(npc);
            if (!r.Ok) c.Reply($"{npc}: {r}");
        }
        c.Reply($"{verb}: {targets.Count} NPC(s); hungry now: {_hungry.Count}");
    }

    private SpeechResult Starve(EntityId npc)
    {
        if (!_hungry.Add(npc)) return new SpeechResult(true, "already hungry");
        Npcs.Say(npc, Starving, TimeSpan.FromSeconds(8));                 // once, at once, no voice-over

        var speech = Npcs.Speech(npc);                                    // its greetings while hungry
        speech.Priority = 10;                                             // hunger outranks a mod's chit-chat
        speech.Pick = SpeechPick.InOrder;                                 // a new complaint at each greeting, in turn
        var r = speech.Replace(Complaints);

        Npcs.Farewells(npc).Replace(Farewells);                           // a new one at random each time (the default)

        var plea = Npcs.MenuLine(npc);                                    // the menu opens with a plea:
        plea.Pick = SpeechPick.By(ctx => ctx.Opens == 0 ? 0 : 1);         // the long one the first time for each player
        plea.Replace(Pleas);

        Npcs.Topics(npc).Set(new NpcTopic("feed", UiText.Of(("en", "Here, have some bread."), ("de", "Hier, etwas Brot.")))
        {
            Reply = UiText.Of(("en", "Bread! Bless you."), ("de", "Brot! Gott segne dich.")),
            Hint = NpcTopicHint.Attention,
            OnPick = ctx => Feed(ctx.Npc),                                 // runs on the host for the player who picked it
        });
        return r;
    }

    private SpeechResult Feed(EntityId npc)
    {
        if (!_hungry.Remove(npc)) return new SpeechResult(true, "not hungry");
        Npcs.Speech(npc).Clear();                                         // its own lines again
        Npcs.Farewells(npc).Clear();
        Npcs.MenuLine(npc).Clear();
        Npcs.Topics(npc).Clear();                                         // its page as the game has it
        return Npcs.Say(npc, Thanks, TimeSpan.FromSeconds(6));
    }

    private static List<EntityId> Nearest(CommandContext c)
    {
        var who = !c.Source.Player.IsNone ? c.Source.Player : EntityId.None;
        if (who.IsNone || World.Position(who) is not { } at) return Villagers.List().Take(1).Select(v => v.Entity).ToList();
        return Villagers.List().Where(v => v.Position is not null).OrderBy(v => v.Position!.Value.DistanceTo(at)).Take(1).Select(v => v.Entity).ToList();
    }
}
