// Made by tools/docs-site/snippets.py from docs/kiln/modding/npc-dialogue.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_npc_dialogue_1
{
#line 18 "docs/kiln/modding/npc-dialogue.md"
public sealed class HungryNpcMod : Mod
{
    private readonly HashSet<EntityId> _hungry = [];

    [Command("hunger", Description = "make the nearest NPC hungry", Permission = CommandPermission.Admin)]
    private void Hunger(CommandContext c)
    {
        var at = World.Position(c.Source.Player) ?? throw new CommandException("your position is not known");
        var npc = Villagers.List().Where(v => v.Position is not null && Npcs.CanSpeak(v.Entity))
                           .OrderBy(v => v.Position!.Value.DistanceTo(at)).FirstOrDefault()?.Entity
                  ?? throw new CommandException("no NPC with speech near you");
        Starve(npc);
        c.Reply("it is hungry now");
    }

    private void Starve(EntityId npc)
    {
        if (!_hungry.Add(npc)) return;
        _ = Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(8));      // once, at once, over its head, no voice

        var speech = Npcs.Speech(npc);                                      // its greetings while hungry
        speech.Pick = SpeechPick.InOrder;                                   // a new complaint at each greeting, in turn
        _ = speech.Replace("No food, no work.", "My stomach is louder than my anvil.");

        _ = Npcs.Farewells(npc).Replace("Come back with something to eat.");   // the line in the menu's box when a player leaves
        _ = Npcs.MenuLine(npc).Replace("Please, Flameborn, I can't craft on an empty stomach.");   // what it says when the menu opens

        _ = Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.")  // a page of your own, after the NPC's topics
        {
            Reply = "Bread! Bless you.",
            OnPick = ctx => Feed(ctx.Npc),                                   // runs on the host, for the player who picked it
        });
    }

    private void Feed(EntityId npc)
    {
        if (!_hungry.Remove(npc)) return;
        Npcs.Speech(npc).Clear();                                            // its own lines and page come back
        Npcs.Farewells(npc).Clear();
        Npcs.MenuLine(npc).Clear();
        Npcs.Topics(npc).Clear();
        _ = Npcs.Say(npc, "Thank you, Flameborn! Back to work.", TimeSpan.FromSeconds(6));
    }
}
#line default
}
