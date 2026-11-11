// SPDX-License-Identifier: MIT
// ShroudForge's menu shortcut (F9, shroudforge.ui.toggle_modloader()) when Kiln answers it: the first press of a session
// opens the Kiln menu on ShroudForge even before the mod list was read, and the next press closes it. No game.
using Kiln.Ui.Tests;
using Xunit;

namespace Kiln.ShroudForgeMenuTests;

[Collection(UiSerial.Name)]
public sealed class ShroudForgeMenuTests : IDisposable
{
    private readonly IKilnBackend _previous;
    private static ModManagerPage Page => ModManagerHost.Page;

    public ShroudForgeMenuTests()
    {
        _previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        Page.Section = ModManagerSection.Mods;
        Page.Filter = EngineFilter.All;
    }

    public void Dispose()
    {
        Page.Section = ModManagerSection.Mods;
        Page.Filter = EngineFilter.All;
        Page.Update(CatalogSnapshot.Empty with { Version = -2 }, false);
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeUiTable.Free();
    }

    private static CatalogSnapshot WithShroudForge(long version) => new()
    {
        Version = version,
        Mods = [new CatalogMod { Engine = "shroudforge", Id = "glider", Name = "Glider" }],
        Engines = [new CatalogEngine { Kind = "shroudforge", Name = "ShroudForge", Count = 1 }],
    };

    [Fact]
    public void An_engine_filter_waits_for_the_first_mod_list()
    {
        var page = new ModManagerPage { Filter = new EngineFilter("shroudforge") };
        page.Update(CatalogSnapshot.Empty, false);  // the menu opened before any list was read
        Assert.Equal("shroudforge", page.Filter.Kind);
        page.Update(WithShroudForge(7), false);
        Assert.Equal("shroudforge", page.Filter.Kind);
        Assert.Equal(["glider"], page.Rows.Select(r => r.Mod.Id));
        page.Update(WithShroudForge(8) with { Mods = [], Engines = [] }, false);  // a real list without it
        Assert.Equal(EngineFilter.All, page.Filter);
    }

    [Fact]
    public void The_second_press_closes_what_the_first_opened_even_before_a_frame()
    {
        Assert.False(FakeUiTable.MenuOpen);
        ShroudForgeMenu.Toggle();  // F9
        Assert.True(FakeUiTable.MenuOpen);
        ShroudForgeMenu.Toggle();  // F9 again, before the page drew a frame
        Assert.False(FakeUiTable.MenuOpen);
        Assert.Null(Page.PendingRequest);  // closing drops the request: Insert later opens the page as it was
    }

    [Fact]
    public void After_the_page_took_the_request_the_next_press_closes()
    {
        ShroudForgeMenu.Toggle();
        Page.Update(CatalogSnapshot.Empty, false);
        Page.TakeRequest();  // what the first frame does
        Page.Update(CatalogSnapshot.Empty, false);
        Assert.Equal(("shroudforge", ModManagerSection.Mods), (Page.Filter.Kind, Page.Section));
        ShroudForgeMenu.Toggle();
        Assert.False(FakeUiTable.MenuOpen);
    }
}
