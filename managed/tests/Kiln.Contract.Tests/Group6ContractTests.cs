// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Ui by the contract: client side, nothing throws for a missing capability.</summary>
public sealed class UiContractTests : ContractTest
{
    [Fact]
    public void With_no_service_the_features_are_lowered_and_the_reads_answer_empty()
    {
        StartRows(typeof(Ui).Assembly);
        Assert.False(Ui.Available);
        Assert.False(Render.Available);
        Assert.False(Ui.Screens.Available);
        AssertOffline("kiln.ui");
        AssertOffline("kiln.render");
        foreach (var name in (string[])["ui.screens", "ui.main_menu", "ui.menu_page", "ui.crafting", "ui.language"])
            AssertOffOffline(name, "needs kiln.ui");
        AssertOffOffline("render.camera", "needs kiln.render");
    }

    [Fact]
    public void Types_carry_their_side_and_thread()
    {
        foreach (var type in (Type[])[typeof(Ui), typeof(Ui.Screens), typeof(Ui.Menus), typeof(Ui.Crafting), typeof(Hud), typeof(Render), typeof(KeyBindings), typeof(UiDraw), typeof(RenderDraw), typeof(WorldDraw)])
            AssertHas<ClientOnlyAttribute>(type);
        AssertHas<RenderThreadAttribute>(typeof(UiDraw));
        AssertHas<AnyThreadAttribute>(Property(typeof(Ui.Screens), "Current"));
        AssertHas<AnyThreadAttribute>(Property(typeof(Render), "Camera"));
        AssertHas<AnyThreadAttribute>(Method(typeof(Hud), "Toast", typeof(string), typeof(ToastKind), typeof(double)));
        AssertHas<AnyThreadAttribute>(Method(typeof(StatusLines), "Set", typeof(string), typeof(string)));
    }
}

/// <summary>Kiln.Quests (knowledge, journal, world progress, the quest engine) by the contract.</summary>
public sealed class QuestsContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Knowledge).Assembly);
        Assert.False(Knowledge.Available);
        Assert.False(WorldProgress.Available);
        Assert.False(WorldProgress.PlacesReady);
        AssertOffline("kiln.knowledge");
        foreach (var name in (string[])["quests.knowledge", "quests.journal", "quests.dialogue", "quests.npc", "quests.changes", "quests.client_input"])
            AssertOffOffline(name, "needs kiln.knowledge");
        // the world's progress lists are built on demand (kiln.assets' data), not a capability: no row, and asking the
        // rows never starts the build
        Assert.Equal(CapabilityReason.Unknown, Capabilities.Get("world_progress.places").Reason);
    }

    [Fact]
    public void A_member_that_answers_without_the_service_is_not_marked_as_needing_it()
    {
        Assert.Null(Knowledge.TryQuery(new EntityId(3), KnowledgeQuery.IsSet(0xff6000e2)));
        Assert.Empty(WorldProgress.Survivors);
        AssertRequires(Method(typeof(Knowledge), "TryQuery", typeof(EntityId), typeof(KnowledgeQuery), typeof(KnowledgeMode)));
        AssertRequires(Property(typeof(WorldProgress), "Survivors"));
        AssertRequires(Method(typeof(ProgressFact), "Holds", typeof(EntityId)));
        AssertRequires(Property(typeof(Spire), "Unlocked"));
        AssertRequires(Method(typeof(Boss), "KilledBy", typeof(Player)));
        AssertRequires(Property(typeof(CharacterProgress), "Quests"));
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.knowledge", () => Knowledge.For(new EntityId(3)).Get(0xff6000e2));
        AssertUnavailable("kiln.knowledge", () => Journal.Quests());
    }

    [Fact]
    public void The_results_are_kiln_results_and_keep_their_own_reason_text()
    {
        var talked = new ConversationResult(true, "");
        Assert.Equal("", talked.Reason);
        Assert.Null(((IKilnResult)talked).Reason);
        Assert.Equal("too far", ((IKilnResult)new ConversationResult(false, "too far")).Reason);

        var started = new QuestResult(true, "", QuestStatus.Active);
        Assert.Null(((IKilnResult)started).Reason);
        IKilnResult locked = new QuestResult(false, "requires level 5", QuestStatus.Locked);
        Assert.False(locked.Ok);
        Assert.Equal("requires level 5", locked.Reason);

        var granted = new KnowledgeResult(1, KnowledgeOp.Grant, new EntityId(3), 7, true, "applied", 0, 1, true, 1, "already had the requirement");
        Assert.Equal("already had the requirement", granted.Reason);   // the record's own note stays
        Assert.Null(((IKilnResult)granted).Reason);                    // by the rule: null when the change was applied
        IKilnResult refused = granted with { Ok = false, Reason = "the player left" };
        Assert.Equal("the player left", refused.Reason);
    }

    [Fact]
    public void Members_carry_their_thread_and_capability()
    {
        var get = Method(typeof(KnowledgeScope), "Get", typeof(uint));
        AssertHas<TickThreadOnlyAttribute>(get);
        AssertRequires(get, "kiln.knowledge");
        AssertRequires(Method(typeof(Journal), "Quests"), "kiln.knowledge");
        // the quest engine is managed: it needs kiln.knowledge only for what a quest asks of it
        AssertRequires(Method(typeof(Quests), "Start", typeof(string), typeof(QuestHolder), typeof(bool)));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(Quests), "Start", typeof(string), typeof(QuestHolder), typeof(bool)));
    }
}
