// Made by tools/docs-site/snippets.py from docs/kiln/services/npc-speech.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_services_npc_speech_1
{
    using Kiln;
    using static DocSnippets.docs_services_npc_speech_1.Stubs;
    internal static class Stubs
    {
        public static EntityId npc => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 61 "docs/kiln/services/npc-speech.md"


// once: over the NPC's head now, for 6 seconds, no voice
Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(6));
Npcs.Say(npc, UiText.Of(("en", "I'm starving..."), ("de", "Ich verhungere...")), TimeSpan.FromSeconds(6));

// while hungry: its greetings are yours
var speech = Npcs.Speech(npc);       // this mod's override of that NPC
speech.Priority = 10;                // optional: outranks other mods' overrides of the same NPC
speech.Replace("No food, no work.", "My stomach is louder than my anvil.");
// or: speech.Suppress();            // no greeting bubble at all

// fed again
speech.Clear();
Npcs.Say(npc, "Thank you, Flameborn!", TimeSpan.FromSeconds(5));
#line default
        }
    }
}

namespace DocSnippets.docs_services_npc_speech_2
{
    using static DocSnippets.docs_services_npc_speech_2.Stubs;
    internal static class Stubs
    {
        public static EntityId npc => default!;
        public static HashSet<EntityId> fed => default!;
        public static void Feed(EntityId npc, EntityId player) { }
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 83 "docs/kiln/services/npc-speech.md"
// greetings: a new line at each greeting (the default is SpeechPick.Random, never the same twice in a row)
var speech = Npcs.Speech(npc);
speech.Pick = SpeechPick.InOrder;                  // or SpeechPick.Seeded(42), or SpeechPick.By(ctx => ...)
speech.Replace("No food, no work.", "My stomach is louder than my anvil.");

// farewells: the line in the menu's speech box when a player leaves it
Npcs.Farewells(npc).Replace("Come back with something to eat.", "Don't forget me, Flameborn.");

// the menu's own line when a player opens it; per player: SpeechPick.By (-1 = no line for that player)
var plea = Npcs.MenuLine(npc);
plea.Pick = SpeechPick.By(ctx => fed.Contains(ctx.Player) ? -1 : 0);
plea.Replace("Please, Flameborn, I haven't eaten in days.");
plea.Refresh();                                    // after `fed` changed: ask again for every player now

// a talk page: your topics after the NPC's own (a villager without topics gets a page)
Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.")
{
    Reply = "Bread! Bless you.",                   // in the speech box when picked (null: none)
    Hint = NpcTopicHint.Attention,                 // the list's marker
    OnPick = ctx =>                                // on the host, for the player who picked it
    {
        Feed(ctx.Npc, ctx.Player);
        ctx.Reply("Now I can work again.");        // to that player only (Npcs.MenuSay)
    },
});
Npcs.Topics(npc).Clear();
#line default
        }
    }
}

namespace DocSnippets.docs_services_npc_speech_3
{
    using Kiln.TestKit;
    using Xunit;
    using static DocSnippets.docs_services_npc_speech_3.Stubs;
    internal static class Stubs
    {
        public static EntityId npc => default!;
    }
    public sealed partial class SnippetTest
    {
        public void Run()
        {
#line 283 "docs/kiln/services/npc-speech.md"
using var h = new KilnHarness();
h.Step();
h.Run(() => Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(30)));
Assert.Equal("I'm starving...", h.NpcSpeech.Saying(npc));
h.Run(() => Npcs.Speech(npc).Replace("No food, no work."));
Assert.Equal(SpeechMode.Replace, h.NpcSpeech.ModeOf(npc));
Assert.Equal(["No food, no work."], h.NpcSpeech.LinesOf(npc));      // LinesOf(npc, "de-de"): German
#line default
        }
    }
}

namespace DocSnippets.docs_services_npc_speech_4
{
    using Kiln.TestKit;
    using Xunit;
    using static DocSnippets.docs_services_npc_speech_4.Stubs;
    internal static class Stubs
    {
        public static EntityId npc => default!;
        public static EntityId player => default!;
    }
    public sealed partial class SnippetTest
    {
        public void Run()
        {
            using var h = new KilnHarness();
#line 297 "docs/kiln/services/npc-speech.md"
h.Run(() => Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.") { OnPick = ctx => ctx.Reply("Thanks!") }));
Assert.Equal(["Here, have some bread."], h.NpcSpeech.TopicsOf(npc));
Assert.True(h.NpcSpeech.PickTopic(npc, "feed", player));
Assert.Equal([(npc, "Thanks!")], h.NpcSpeech.RepliesTo(player));
#line default
        }
    }
}
