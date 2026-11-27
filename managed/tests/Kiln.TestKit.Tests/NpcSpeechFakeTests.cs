// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.TestKit.Tests;

/// <summary>The kit's kiln.npcspeech fake (Fakes/NpcSpeech): Kiln.Npcs runs on it unchanged, as the host of a world, and
/// KilnHarness.NpcSpeech reads what a Kiln client's speech bubble of each NPC would be told.</summary>
public sealed class NpcSpeechFakeTests
{
    private static readonly EntityId Npc = new(1159);

    [Fact]
    public void A_hungry_craftsperson_says_so_then_complains_then_greets_again()
    {
        using var h = new KilnHarness();
        h.Step();
        Assert.True(h.Run(() => Npcs.Available));
        Assert.True(h.Run(() => Npcs.Say(Npc, UiText.Of(("en", "I'm starving..."), ("de", "Ich verhungere...")), TimeSpan.FromSeconds(30)).Ok));
        Assert.Equal("I'm starving...", h.NpcSpeech.Saying(Npc));
        Assert.Equal("Ich verhungere...", h.NpcSpeech.Saying(Npc, "de-de"));

        Assert.True(h.Run(() => Npcs.Speech(Npc).Replace("No food, no work.", "My stomach...").Ok));
        Assert.Equal(SpeechMode.Replace, h.NpcSpeech.ModeOf(Npc));
        Assert.Equal(["No food, no work.", "My stomach..."], h.NpcSpeech.LinesOf(Npc));

        h.Run(() => Npcs.StopSaying(Npc));
        Assert.Null(h.NpcSpeech.Saying(Npc));
        h.Run(() => Npcs.Speech(Npc).Clear());
        Assert.Equal(SpeechMode.Game, h.NpcSpeech.ModeOf(Npc));
        Assert.Empty(h.NpcSpeech.LinesOf(Npc));
    }

    [Fact]
    public void Suppress_is_recorded_and_the_same_call_again_sends_nothing()
    {
        using var h = new KilnHarness();
        h.Step();
        h.Run(() => Npcs.Speech(Npc).Suppress());
        Assert.Equal(SpeechMode.Suppress, h.NpcSpeech.ModeOf(Npc));
        var calls = h.NpcSpeech.Calls;
        h.Run(() => Npcs.Speech(Npc).Suppress());
        Assert.Equal(calls, h.NpcSpeech.Calls);
        h.Run(() => Npcs.Speech(Npc).Clear());
    }

    [Fact]
    public void A_replace_says_how_it_picks_per_greeting()
    {
        using var h = new KilnHarness();
        h.Step();
        h.Run(() => Npcs.Speech(Npc).Replace("No food, no work.", "My stomach..."));
        Assert.Equal("Random", h.NpcSpeech.PickOf(Npc));
        h.Run(() =>
        {
            var s = Npcs.Speech(Npc);
            s.Pick = SpeechPick.InOrder;
            s.Replace("No food, no work.", "My stomach...");
        });
        Assert.Equal("InOrder", h.NpcSpeech.PickOf(Npc));
        Assert.Equal(["No food, no work.", "My stomach..."], h.NpcSpeech.LinesOf(Npc));
        h.Run(() =>
        {
            var s = Npcs.Speech(Npc);
            s.Pick = SpeechPick.Seeded(7);
            s.Replace("No food, no work.", "My stomach...");
        });
        Assert.Equal("Seeded(7)", h.NpcSpeech.PickOf(Npc));
        h.Run(() => Npcs.Speech(Npc).Clear());
        Assert.Null(h.NpcSpeech.PickOf(Npc));
    }

    [Fact]
    public void A_farewell_is_recorded_per_language_apart_from_the_greeting()
    {
        using var h = new KilnHarness();
        h.Step();
        h.Run(() => Npcs.Farewells(Npc).Replace(new[] { UiText.Of(("en", "Eat well!"), ("de", "Guten Appetit!")) }));
        Assert.Equal(["Eat well!"], h.NpcSpeech.FarewellsOf(Npc));
        Assert.Equal(["Guten Appetit!"], h.NpcSpeech.FarewellsOf(Npc, "de-de"));
        Assert.Equal("Random", h.NpcSpeech.PickOf(Npc, NpcLineKind.Farewell));
        Assert.Equal(SpeechMode.Game, h.NpcSpeech.ModeOf(Npc));
        h.Run(() => Npcs.Farewells(Npc).Clear());
        Assert.Empty(h.NpcSpeech.FarewellsOf(Npc));
    }

    [Fact]
    public void The_menu_line_and_replies_to_a_player_are_recorded()
    {
        using var h = new KilnHarness();
        h.Step();
        var player = new EntityId(501);
        h.Run(() => Npcs.MenuLine(Npc).Replace("I can't work like this."));
        Assert.Equal(["I can't work like this."], h.NpcSpeech.MenuLinesOf(Npc));
        Assert.True(h.Run(() => Npcs.MenuSay(Npc, player, "Thank you!").Ok));
        Assert.Equal([(Npc, "Thank you!")], h.NpcSpeech.RepliesTo(player));
        h.Run(() => Npcs.MenuLine(Npc).Clear());
        Assert.Empty(h.NpcSpeech.MenuLinesOf(Npc));
    }

    [Fact]
    public void A_second_harness_shows_the_same_texts_and_starts_clear()
    {
        for (var round = 0; round < 2; round++)
        {
            using var h = new KilnHarness();
            h.Step();
            Assert.Equal(SpeechMode.Game, h.NpcSpeech.ModeOf(Npc));   // nothing left from the last harness
            Assert.True(h.Run(() => Npcs.Say(Npc, "Same words", TimeSpan.FromSeconds(30)).Ok));
            Assert.Equal("Same words", h.NpcSpeech.Saying(Npc));
            Assert.True(h.Run(() => Npcs.Speech(Npc).Replace("Same line").Ok));
            Assert.Equal(["Same line"], h.NpcSpeech.LinesOf(Npc));
        }
    }
}
