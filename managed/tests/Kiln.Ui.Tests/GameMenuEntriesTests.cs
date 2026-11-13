// SPDX-License-Identifier: MIT
// Mods' entries in the game's menus (GameMenuEntries, kiln.ui v6): the menus do not scroll, so past a few entries kiln.ui
// stops drawing them (native/services/ui/menu_rows.h, tests/menu_rows_test.cpp) and the Mods page lists them instead,
// each with a button that does what the entry does in the menu. A player's pause menu had Quit below the screen
// (2026-11-13). No game: the page is drawn through the real code against a fake kiln.ui table.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Kiln.FakeUi;
using Xunit;

namespace Kiln.GameMenuEntriesTests;

[Collection(UiSerial.Name)]
public sealed unsafe class GameMenuEntriesTests : IDisposable
{
    private readonly IKilnBackend _previous;
    private readonly UiDraw _ui = new();
    private UiNative.Table6Scroll* _t6;
    private static readonly List<ulong> Activated = [];
    private static readonly List<bool> MenuOpenAtActivation = [];
    private static readonly List<uint> Modes = [];

    public GameMenuEntriesTests()
    {
        _previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        _ui.Frame.DpiScale = 1;
        _ui.Frame.Width = 1920;
        _ui.Frame.Height = 1080;
        lock (Activated)
        {
            Activated.Clear();
            MenuOpenAtActivation.Clear();
        }
        lock (Modes) Modes.Clear();
    }

    public void Dispose()
    {
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeUiTable.Free();
        if (_t6 != null) NativeMemory.Free(_t6);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Activate(ulong h)
    {
        lock (Activated)
        {
            Activated.Add(h);
            MenuOpenAtActivation.Add(FakeUiTable.MenuOpen);
        }
        return h == 0 ? 3 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Shown(uint mode)
    {
        lock (Modes) Modes.Add(mode);
    }

    // kiln.ui v6: the fake v4 table with the three new slots, handed out for version 6
    private void InstallV6()
    {
        _t6 = (UiNative.Table6Scroll*)NativeMemory.AllocZeroed((nuint)sizeof(UiNative.Table6Scroll));
        _t6->Base.Base = *FakeUiTable.Table;
        _t6->Base.Base.Base.Base.Base.Header.Size = (uint)sizeof(UiNative.Table6Scroll);
        _t6->Base.Base.Base.Base.Base.Header.Version = 6;
        _t6->Base.MenuShown = &Shown;
        _t6->Base.MenuActivate = &Activate;
        FakeUiTable.Other["kiln.ui"] = (nint)_t6;
    }

    private static GameMenuRow Row(ulong h, string text, MenuLocation menu = MenuLocation.PauseMenu, string? mod = "Some mod",
        bool drawn = false, bool hidden = false) => new(h, menu, text, mod, drawn, hidden);

    [Fact]
    public void The_modes_are_kiln_ui_s_numbers()
    {
        // KILN_UI_MENU_SHOW_FEW / ALWAYS / NEVER in native/core/abi/services/kiln_ui.h
        Assert.Equal(0u, GameMenuEntries.NativeMode(ModMenuEntries.Few));
        Assert.Equal(1u, GameMenuEntries.NativeMode(ModMenuEntries.Always));
        Assert.Equal(2u, GameMenuEntries.NativeMode(ModMenuEntries.Never));
        Assert.Equal(3u, GameMenuEntries.NativeMode(ModMenuEntries.Auto));
        // the default: every entry where the pause menu scrolls, else the rule Few (kiln.ui decides, menu_rows.h)
        Assert.Equal(ModMenuEntries.Auto, new MenuSettings().ModEntries);
        Assert.Equal(ModMenuEntries.Auto, GameMenuEntries.Mode);
    }

    [Fact]
    public void The_entry_record_and_the_v6_table_match_kiln_ui_h()
    {
        // ui.cpp: static_assert(sizeof(KilnUiMenuEntry) == 40, entry at 8, text at 16, drawn at 36)
        Assert.Equal(40, sizeof(UiNative.MenuEntryInfo));
        Assert.Equal(8, (int)Marshal.OffsetOf<UiNative.MenuEntryInfo>(nameof(UiNative.MenuEntryInfo.Entry)));
        Assert.Equal(16, (int)Marshal.OffsetOf<UiNative.MenuEntryInfo>(nameof(UiNative.MenuEntryInfo.Text)));
        Assert.Equal(36, (int)Marshal.OffsetOf<UiNative.MenuEntryInfo>(nameof(UiNative.MenuEntryInfo.Drawn)));
        // menu_shown follows subscribe_window_input, which follows the v4 table
        Assert.Equal(sizeof(UiNative.Table4) + 8, (int)Marshal.OffsetOf<UiNative.Table6>(nameof(UiNative.Table6.MenuShown)));
        Assert.Equal(sizeof(UiNative.Table4) + 4 * 8, sizeof(UiNative.Table6));  // the first v6 tables end here
        Assert.Equal(sizeof(UiNative.Table4) + 5 * 8, sizeof(UiNative.Table6Scroll));
        Assert.Equal(sizeof(UiNative.Table6), (int)Marshal.OffsetOf<UiNative.Table6Scroll>(nameof(UiNative.Table6Scroll.MenuScroll)));
    }

    [Fact]
    public void A_record_becomes_a_row_with_its_label_and_state()
    {
        var text = Encoding.UTF8.GetBytes("Téléport home");
        fixed (byte* p = text)
        {
            var e = new UiNative.MenuEntryInfo { Size = 40, Menu = 2, Entry = 77, Text = p, TextLength = (uint)text.Length, Flags = 1, Drawn = 0 };
            var row = GameMenuEntries.Row(e, "Waypoints");
            Assert.Equal(new GameMenuRow(77, MenuLocation.PauseMenu, "Téléport home", "Waypoints", false, true), row);
        }
    }

    [Fact]
    public void The_page_lists_the_entries_of_the_menu_the_player_can_open_and_not_the_hidden_ones()
    {
        var all = new[]
        {
            Row(1, "SET tools", MenuLocation.MainMenu, null), Row(2, "SET tools", mod: null), Row(3, "Creative", mod: "Kiln"),
            Row(4, "Hidden by its mod", hidden: true), Row(5, "Talents"),
        };
        Assert.Equal([2ul, 3, 5], GameMenuEntries.Listed(all, MenuLocation.PauseMenu).Select(r => r.Handle));
        Assert.Equal([1ul], GameMenuEntries.Listed(all, MenuLocation.MainMenu).Select(r => r.Handle));
    }

    [Fact]
    public void The_summary_says_how_many_are_not_in_the_game_s_menu()
    {
        var rows = new[] { Row(1, "a"), Row(2, "b"), Row(3, "c"), Row(4, "d") };
        var few = GameMenuEntries.Summary(ModMenuEntries.Few, rows);
        Assert.Contains("at most 3", few);
        Assert.Contains("4 of them are not in the game's menu now", few);
        Assert.DoesNotContain("not in the game's menu", GameMenuEntries.Summary(ModMenuEntries.Always, [Row(1, "a", drawn: true)]));
        Assert.Contains("Never", GameMenuEntries.Summary(ModMenuEntries.Never, rows));
        Assert.Contains("scrolls when they do not fit", GameMenuEntries.Summary(ModMenuEntries.Auto, [Row(1, "a", drawn: true)], scrolls: true));
        Assert.Contains("does not scroll here", GameMenuEntries.Summary(ModMenuEntries.Auto, rows));
    }

    [Fact]
    public void The_player_is_told_once_when_scrolling_ended_the_game_before()
    {
        // KILN_UI_SCROLL_*: 0 unavailable, 1 on, 2 off after a crash, 3 off on record, 4 off after a failed check
        Assert.Contains("ended the game last time", GameMenuEntries.StartNotice(2));
        foreach (var quiet in new uint[] { 0, 1, 3, 4 }) Assert.Null(GameMenuEntries.StartNotice(quiet));
        Assert.Equal(0u, GameMenuEntries.ScrollStatus());  // no kiln.ui v6 here: nothing to say
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ScrollCrashed() => 2;

    [Fact]
    public void The_scroll_status_comes_from_kiln_ui()
    {
        InstallV6();
        _t6->MenuScroll = &ScrollCrashed;
        UiNative.ResetForTests();
        Assert.Equal(GameMenuEntries.ScrollOffCrashed, GameMenuEntries.ScrollStatus());
    }

    [Fact]
    public void A_v6_table_without_the_scroll_slot_still_lists_and_applies_the_rule()
    {
        // review of 235bcebb: kiln.ui of 781ee730 has v6 without menu_scroll; the managed side must keep its list and rule
        InstallV6();
        _t6->MenuScroll = &ScrollCrashed;  // past the old table's end: must not be read
        _t6->Base.Base.Base.Base.Base.Header.Size = (uint)sizeof(UiNative.Table6);
        UiNative.ResetForTests();
        Assert.True(UiNative.T6 != null);
        Assert.True(UiNative.MenuScrollSlot == null);
        Assert.Equal(0u, GameMenuEntries.ScrollStatus());
        Assert.True(GameMenuEntries.Activate(41));
    }

    [Fact]
    public void The_left_column_has_a_menu_entries_row_only_when_there_are_entries()
    {
        var snap = new CatalogSnapshot { Version = 1 };
        Assert.Equal(["All mods", "Menu entries", "Kiln settings"], ModManagerLogic.EngineRows(snap, true, 5).Select(r => r.Label));
        Assert.Equal("5", ModManagerLogic.EngineRows(snap, true, 5)[1].Count);
        Assert.Equal(["All mods", "Kiln settings"], ModManagerLogic.EngineRows(snap, true, 0).Select(r => r.Label));
        Assert.Equal(EngineFilter.Menus, ModManagerLogic.Valid(snap, EngineFilter.Menus, true, 1));
        Assert.Equal(EngineFilter.All, ModManagerLogic.Valid(snap, EngineFilter.Menus, true, 0));
        Assert.Empty(ModManagerLogic.Rows(snap, EngineFilter.Menus, ""));
    }

    [Fact]
    public void Choosing_an_entry_on_the_page_activates_it_and_closes_the_menu()
    {
        InstallV6();
        FakeUiTable.MenuOpen = true;
        var page = new ModManagerPage
        {
            Source = () => new CatalogSnapshot { Version = 1 },
            MenuSource = () => [Row(41, "Teleport##home"), Row(42, "Talents")],
            Filter = EngineFilter.Menus,
        };
        _ui.Frame.Number++;
        page.Draw(_ui, new PageRect(0, 0, 1800, 900), 1);
        Assert.Equal(EngineFilter.Menus, page.Filter);  // there are entries: the view stays
        lock (FakeUiTable.Texts)
        {
            Assert.Contains(FakeUiTable.Texts, t => t.Contains("2 of them are not in the game's menu now", StringComparison.Ordinal));
            Assert.Contains("Some mod   ·   not in the game's menu now", FakeUiTable.Texts);
        }
        // a mod's label is shown as text, "##" and all (review of 2f39dd13: as a button label it was blank)
        lock (FakeUiTable.Texts) Assert.Contains("Teleport##home", FakeUiTable.Texts);
        FakeUiTable.Click = "Open";  // the first row's button
        _ui.Frame.Number++;
        page.Draw(_ui, new PageRect(0, 0, 1800, 900), 1);
        lock (Activated)
        {
            Assert.Equal([41ul], Activated);
            // the Kiln menu was closed before the entry was handed over (review of 2f39dd13): an action that opens a Kiln
            // view of its own is not closed again by the page
            Assert.Equal([false], MenuOpenAtActivation);
        }
        Assert.False(FakeUiTable.MenuOpen);
    }

    [Fact]
    public void Without_entries_the_menus_view_goes_back_to_all_mods()
    {
        var page = new ModManagerPage { Source = () => new CatalogSnapshot { Version = 1 }, MenuSource = () => [], Filter = EngineFilter.Menus };
        _ui.Frame.Number++;
        page.Draw(_ui, new PageRect(0, 0, 1800, 900), 1);
        Assert.Equal(EngineFilter.All, page.Filter);
    }

    [Fact]
    public void Activate_needs_kiln_ui_v6()
    {
        Assert.False(GameMenuEntries.Activate(41));  // the v4 table only
        InstallV6();
        UiNative.ResetForTests();
        Assert.True(GameMenuEntries.Activate(41));
        Assert.False(GameMenuEntries.Activate(0));
        lock (Activated) Assert.Equal([41ul, 0], Activated);
    }
}
