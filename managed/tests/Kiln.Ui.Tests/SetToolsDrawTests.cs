// SPDX-License-Identifier: MIT
// The SET button on a SET mod's details (SetTools.Draw): it asks kiln-set's tools for the page of the mod by the name
// kiln-set gives it, the folder name in shrouded_engine_tweaks/mods, which kiln-set compares byte for byte. The rows come
// from kiln-set's own set.json through the real catalog. No game: fake kiln.ui and kiln.set.ui tables.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kiln.FakeUi;
using Xunit;

namespace Kiln.SetToolsDrawTests;

[Collection(UiSerial.Name)]
public sealed unsafe class SetToolsDrawTests : IDisposable
{
    private static string? _asked;
    private readonly IKilnBackend _previous;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-settools-" + Guid.NewGuid().ToString("N"));
    private readonly SetTools.Table* _table;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Open(NativeStr mod)
    {
        _asked = mod.ToString();
        return 0;
    }

    public SetToolsDrawTests()
    {
        _previous = Backend.Current;
        FakeUiTable.Install();
        _table = (SetTools.Table*)NativeMemory.AllocZeroed((nuint)sizeof(SetTools.Table));
        _table->Header = new ServiceHeader { Size = (uint)sizeof(SetTools.Table), Version = 1 };
        _table->Open = &Open;
        FakeUiTable.Other["kiln.set.ui"] = (nint)_table;
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        _asked = null;
    }

    public void Dispose()
    {
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeUiTable.Free();
        NativeMemory.Free(_table);
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    [Fact]
    public void The_set_button_asks_for_the_page_by_kiln_sets_own_name_for_the_mod()
    {
        var kiln = Path.Combine(_root, "kiln");
        var state = Path.Combine(kiln, "state");
        Directory.CreateDirectory(state);
        var mods = Path.Combine(_root, "shrouded_engine_tweaks", "mods");
        // a profile entry under the Library's id, and the folder SET reads under another case (NTFS: the same folder)
        var file = Path.Combine(kiln, "profile.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { format = 1, id = "p", generation = 1, mods = new { set = new[] { new { id = "hotbar-plus", version = "1", path = Path.Combine(mods, "hotbar-plus") } } } }));
        File.WriteAllText(Path.Combine(state, "boot.json"), JsonSerializer.Serialize(new { format = 1, pid = 7, profile = new { mode = "active", id = "p", file } }));
        File.WriteAllText(Path.Combine(state, "set.json"), JsonSerializer.Serialize(new
        {
            format = 1, pid = 7, status = "running", mods = new[] { new { id = "Hotbar-Plus", name = "Hotbar Plus", version = "1", status = "loaded", reason = "", folder = Path.Combine(mods, "Hotbar-Plus") } },
        }));
        var snap = ModCatalog.Read(new CatalogSources { KilnRoot = kiln, Pid = 7 });
        var row = Assert.Single(snap.Mods, m => m.Engine == "set");
        FakeUiTable.Click = "SET settings and tools";
        SetTools.Draw(new UiDraw(), row);
        Assert.Equal("Hotbar-Plus", _asked);
    }
}
