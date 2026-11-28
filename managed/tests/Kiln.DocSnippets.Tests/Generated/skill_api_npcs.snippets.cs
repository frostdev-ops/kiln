// Made by tools/docs-site/snippets.py from agent/skills/kiln-modding/references/api-npcs.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.skill_api_npcs_1
{
    using static DocSnippets.skill_api_npcs_1.Stubs;
    internal static class Stubs
    {
        public static EntityId npc => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 21 "agent/skills/kiln-modding/references/api-npcs.md"
foreach (var v in Villagers.List().Where(v => Npcs.CanSpeak(v.Entity))) { /* an NPC with dialogue lines */ }
_ = Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(6));      // once, now, no voice-over; 0.5 s to 60 s
var speech = Npcs.Speech(npc);                                      // this mod's override of that NPC's greetings
speech.Priority = 10;                                               // outranks other mods' overrides of the same NPC
speech.Pick = SpeechPick.InOrder;                                   // set before Replace
_ = speech.Replace("No food, no work.", "My stomach is louder than my anvil.");
// speech.Suppress();                                               // or: no greeting bubble at all
speech.Clear();                                                     // the game's lines (or the next mod's) again
#line default
        }
    }
}

namespace DocSnippets.skill_api_npcs_2
{
    using static DocSnippets.skill_api_npcs_2.Stubs;
    internal static class Stubs
    {
        public static EntityId npc => default!;
        public static HashSet<EntityId> fed => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 68 "agent/skills/kiln-modding/references/api-npcs.md"
var plea = Npcs.MenuLine(npc);
plea.Pick = SpeechPick.By(ctx => fed.Contains(ctx.Player) ? -1 : 0);   // no plea for a player who fed it
_ = plea.Replace("Please, Flameborn, I haven't eaten in days.");
// later, when `fed` changes:
plea.Refresh();
_ = Npcs.Farewells(npc).Replace("Come back with something to eat.", "Don't forget me, Flameborn.");
#line default
        }
    }
}

namespace DocSnippets.skill_api_npcs_3
{
    using static DocSnippets.skill_api_npcs_3.Stubs;
    internal static class Stubs
    {
        public static EntityId npc => default!;
        public static void FeedNpc(EntityId npc) { }
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 79 "agent/skills/kiln-modding/references/api-npcs.md"
_ = Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.")
{
    Reply = "Bread! Bless you.",                 // in the speech box when picked (null: none)
    Hint = NpcTopicHint.Attention,               // the list's marker
    OnPick = ctx =>                              // on the host, as your mod, for the player who picked it
    {
        FeedNpc(ctx.Npc);
        _ = ctx.Reply("Now I can work again.");      // to that player only (Npcs.MenuSay)
    },
});
#line default
        }
    }
}
