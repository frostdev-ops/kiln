// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

/// <summary>kiln.ui and kiln.knowledge publish their tables also when single hooks failed: the features those hooks carry
/// are off then (here with no kiln.caps: the backend's claims are the rows).</summary>
public sealed unsafe class HookRowTests : FakeTableTest
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeScreen(UiNative.Screen* s) => 3;  // unavailable: the UI frame hook failed

    private static byte _changes;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeKnowledgeStatus(KnowledgeNative.Status* s)
    {
        s->Knowledge = s->Journal = s->Npc = s->Dialogue = s->ClientInput = 1;
        s->Changes = _changes;  // the knowledge apply hook failed, or the game's apply faulted since
        return 0;
    }

    [Fact]
    public void A_failed_UI_frame_hook_takes_screens_menus_and_crafting_down()
    {
        foreach (var f in UiCaps.Features) Backend.Features.Add(f);
        var t = Alloc<UiNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(UiNative.Table), Version = 1 };
        t->GetScreen = &FakeScreen;
        Serve("kiln.ui", t, typeof(UiNative), "_table");
        StartRows(typeof(Ui).Assembly);
        foreach (var f in (string[])["ui.screens", "ui.main_menu", "ui.system_menu", "ui.crafting"])
        {
            Assert.False(Capabilities.Is(f), f);
            Assert.Equal(CapabilityReason.Faulted, Capabilities.Get(f).Reason);
        }
        Assert.True(Capabilities.Is("ui.language"));  // not carried by that hook
    }

    [Fact]
    public void A_failed_knowledge_hook_takes_set_and_revoke_down_and_nothing_else()
    {
        foreach (var f in QuestsCaps.Features) Backend.Features.Add(f);
        _changes = 1;
        var t = Alloc<KnowledgeNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(KnowledgeNative.Table), Version = 1 };
        t->Status = &FakeKnowledgeStatus;
        Swap(typeof(KnowledgeNative), "_looked", false);
        Serve("kiln.knowledge", t, typeof(KnowledgeNative), "_table");
        StartRows(typeof(Knowledge).Assembly);
        Assert.True(Capabilities.Is("quests.changes"));
        _changes = 0;  // the game's knowledge apply faulted: Set and Revoke stop
        QuestsCaps.Rows.Refresh();
        Assert.False(Capabilities.Is("quests.changes"));
        Assert.True(Capabilities.Is("quests.journal"));
    }
}
