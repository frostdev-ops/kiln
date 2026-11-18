using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.GraphicsTests;

// Kiln's own graphics tools (0.2.0-dev.29.18, docs/kiln/graphics.md): kiln.json's "graphics" section (read, clamped,
// written back keeping the file's comments), the native tail's layout (graphics_set, graphics_status), what the Kiln
// menu's Graphics page says for a status, and the Graphics section in the menu's tab row.
[Collection(Kiln.FakeUi.UiSerial.Name)]
public unsafe class GraphicsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-graphics-" + Guid.NewGuid().ToString("N"));
    private UiNative.TableGraphics* _table;
    private static readonly List<UiNative.Graphics> Set = [];
    private static UiNative.GraphicsStatus _status;

    public GraphicsTests()
    {
        Directory.CreateDirectory(_dir);
        Set.Clear();
        _status = default;
    }

    public void Dispose()
    {
        GraphicsTools.ResetForTests();
        GraphicsPage.ResetForTests();
        UiNative.ResetForTests();
        if (_table != null) NativeMemory.Free(_table);
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSet(UiNative.Graphics* g)
    {
        Set.Add(*g);
        return g->Size == (uint)sizeof(UiNative.Graphics) ? 0 : 3;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeStatus(UiNative.GraphicsStatus* st)
    {
        if (st->Size < (uint)sizeof(UiNative.GraphicsStatus)) return 3;
        *st = _status;
        st->Size = (uint)sizeof(UiNative.GraphicsStatus);
        return 0;
    }

    private void Install()
    {
        _table = (UiNative.TableGraphics*)NativeMemory.AllocZeroed((nuint)sizeof(UiNative.TableGraphics));
        _table->GraphicsSet = &FakeSet;
        _table->GraphicsStatusFn = &FakeStatus;
        UiNative.GraphicsOverride = _table;
    }

    // ---- kiln.json ----

    [Fact]
    public void Reads_the_graphics_section_with_comments_clamps_and_defaults()
    {
        Assert.Equal(GraphicsSettings.Default, GraphicsTools.Parse(""));
        Assert.Equal(GraphicsSettings.Default, GraphicsTools.Parse("{}"));
        Assert.Equal(GraphicsSettings.Default, GraphicsTools.Parse("{\"graphics\": 3}"));
        Assert.False(GraphicsSettings.Default.DlssSet);
        var s = GraphicsTools.Parse("""
            // Kiln's settings
            {
              "ui": { "look": "kiln" },
              "graphics": {
                "fpsCap": 141,            // my monitor's VRR range
                "dlssPreset": "k",
                "dlssScale": "perMode",
                "dlssRenderScale": 0.1,
                "dlssQualityScale": 0.75,
                "dlssPerformanceScale": 2,
                "readout": true,
              },
            }
            """);
        Assert.Equal(new GraphicsSettings(141, "K", DlssScaleMode.PerMode, 0.33f, 0.75f, GraphicsSettings.DlssBalanced, 1f,
            GraphicsSettings.DlssUltraPerformance, true), s);
        Assert.True(s.DlssSet);
        var warnings = new List<string>();
        var bad = GraphicsTools.Parse("{\"graphics\": {\"fpsCap\": 5, \"dlssPreset\": \"E\", \"dlssScale\": \"half\", \"readout\": 1}}", warnings.Add);
        Assert.Equal(GraphicsSettings.Default with { FpsCap = 20 }, bad);
        Assert.Equal(2, warnings.Count);
        Assert.Equal(500, GraphicsTools.Parse("{\"graphics\": {\"fpsCap\": 9000}}").FpsCap);
        Assert.Equal(0, GraphicsTools.Parse("{\"graphics\": {\"fpsCap\": -3}}").FpsCap);
        Assert.Equal(GraphicsSettings.Default, GraphicsTools.Parse("{ not json", warnings.Add));
    }

    [Fact]
    public void Presets_are_the_ones_the_games_dlss_has_transformer_models_for()
    {
        Assert.Equal(["default", "J", "K", "L", "M"], GraphicsSettings.Presets);
        Assert.Equal(0u, GraphicsSettings.PresetValue("default"));
        Assert.Equal(10u, GraphicsSettings.PresetValue("J"));
        Assert.Equal(13u, GraphicsSettings.PresetValue("M"));
        Assert.Equal(0u, GraphicsSettings.PresetValue("E"));
        Assert.Equal("L", GraphicsSettings.PresetOf(" l "));
        Assert.Equal("default", GraphicsSettings.PresetOf("Default"));
        Assert.Null(GraphicsSettings.PresetOf("F"));
        Assert.Null(GraphicsSettings.PresetOf(null));
    }

    [Fact]
    public void Writes_the_graphics_section_keeping_the_rest_of_the_file()
    {
        var s = new GraphicsSettings(90, "K", DlssScaleMode.Custom, 0.75f, 0.667f, 0.58f, 0.5f, 0.333f, true);
        // no file: a new one with the section
        var made = GraphicsTools.Splice("", s);
        Assert.Equal(s, GraphicsTools.Parse(made));
        // a file without the section: added, comments and other keys kept
        const string plain = "{\n  // keep me\n  \"policy\": \"any\",\n  \"ui\": { \"look\": \"kiln\" }\n}\n";
        var added = GraphicsTools.Splice(plain, s);
        Assert.Contains("// keep me", added);
        Assert.Contains("\"look\": \"kiln\"", added);
        Assert.Equal(s, GraphicsTools.Parse(added));
        Assert.Equal(added, GraphicsTools.Splice(added, s));  // nothing to change: the same text
        // a partial section: values spliced in place (comments beside them kept), missing keys added; 0.50 stays 0.50
        const string partial = "{\n  \"graphics\": {\n    \"fpsCap\": 60, // mine\n    \"dlssPerformanceScale\": 0.50\n  }\n}\n";
        var spliced = GraphicsTools.Splice(partial, s);
        Assert.Contains("\"fpsCap\": 90, // mine", spliced);
        Assert.Contains("\"dlssPerformanceScale\": 0.50", spliced);
        Assert.Equal(s, GraphicsTools.Parse(spliced));
        Assert.Throws<SfEditException>(() => GraphicsTools.Splice("[1, 2]", s));
        // through the file
        var path = Path.Combine(_dir, "kiln.json");
        File.WriteAllText(path, plain);
        Assert.Null(GraphicsTools.Save(path, s));
        Assert.Equal(s, GraphicsTools.Read(path));
        Assert.Contains("// keep me", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_file_that_is_not_json_keeps_the_settings_in_use()
    {
        var path = Path.Combine(_dir, "kiln.json");
        File.WriteAllText(path, "{ \"graphics\": { \"fpsCap\": 72 ");  // half saved, or mistyped
        var warnings = new List<string>();
        Assert.Equal(GraphicsSettings.Default, GraphicsTools.Read(path, warnings.Add));  // at start: the defaults
        Assert.Contains("not valid JSON", Assert.Single(warnings));
        var mine = GraphicsSettings.Default with { FpsCap = 141, Readout = true };
        GraphicsTools.Use(mine);
        Assert.Equal(mine, GraphicsTools.Read(path));  // in a game: the cap stays on
        File.WriteAllText(path, "{ \"graphics\": { \"fpsCap\": 72 } }");
        Assert.Equal(72, GraphicsTools.Read(path).FpsCap);
    }

    [Fact]
    public void The_look_and_the_graphics_settings_write_kiln_json_under_one_lock()
    {
        // each reads the file, changes its own section and writes the file back: two at once would lose one's change
        Assert.Same(PlayerLook.FileLock, GraphicsTools.FileLock);
    }

    [Fact]
    public void A_change_on_the_page_is_not_undone_by_a_read_of_the_file_before_it_is_saved()
    {
        Install();
        GraphicsTools.PathOverride = Path.Combine(_dir, "kiln.json");
        File.WriteAllText(GraphicsTools.PathOverride, "{ \"graphics\": { \"fpsCap\": 72 } }");
        GraphicsTools.Start();
        Assert.Equal(72, GraphicsTools.Current.FpsCap);
        GraphicsTools.Change(GraphicsTools.Current with { FpsCap = 90 });
        // the launcher writes the file in the half second before the page's change is saved, and the game reads it
        File.WriteAllText(GraphicsTools.PathOverride, "{ \"graphics\": { \"fpsCap\": 30 } }");
        GraphicsTools.Reload(quiet: true);
        Assert.Equal(90, GraphicsTools.Current.FpsCap);
        Assert.Equal(90u, Set[^1].FpsCap);  // kiln.ui keeps the player's
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && GraphicsTools.Read(GraphicsTools.PathOverride).FpsCap != 90) Thread.Sleep(50);
        Assert.Equal(90, GraphicsTools.Read(GraphicsTools.PathOverride).FpsCap);
        // saved: a later edit of the file is read again
        until = DateTime.UtcNow.AddSeconds(5);
        do
        {
            File.WriteAllText(GraphicsTools.PathOverride, "{ \"graphics\": { \"fpsCap\": 30 } }");
            GraphicsTools.Reload(quiet: true);
            if (GraphicsTools.Current.FpsCap == 30) break;
            Thread.Sleep(50);
        } while (DateTime.UtcNow < until);
        Assert.Equal(30, GraphicsTools.Current.FpsCap);
        Assert.Equal(30u, Set[^1].FpsCap);
    }

    // ---- the native tail ----

    [Fact]
    public void The_graphics_tail_follows_set_texts()
    {
        Assert.Equal(1016, sizeof(UiNative.TableTexts));
        Assert.Equal(1016, (int)Marshal.OffsetOf<UiNative.TableGraphics>(nameof(UiNative.TableGraphics.GraphicsSet)));
        Assert.Equal(1024, (int)Marshal.OffsetOf<UiNative.TableGraphics>(nameof(UiNative.TableGraphics.GraphicsStatusFn)));
        Assert.Equal(1032, sizeof(UiNative.TableGraphics));
        Assert.Equal(48, sizeof(UiNative.Graphics));
        Assert.Equal(16, (int)Marshal.OffsetOf<UiNative.Graphics>(nameof(UiNative.Graphics.CustomScale)));
        Assert.Equal(36, (int)Marshal.OffsetOf<UiNative.Graphics>(nameof(UiNative.Graphics.Readout)));
        Assert.Equal(72, sizeof(UiNative.GraphicsStatus));
        Assert.Equal(20, (int)Marshal.OffsetOf<UiNative.GraphicsStatus>(nameof(UiNative.GraphicsStatus.RenderedFps)));
        Assert.Equal(40, (int)Marshal.OffsetOf<UiNative.GraphicsStatus>(nameof(UiNative.GraphicsStatus.DlssQuality)));
        Assert.Equal(56, (int)Marshal.OffsetOf<UiNative.GraphicsStatus>(nameof(UiNative.GraphicsStatus.DlssOutputWidth)));
        var h = File.ReadAllText(Path.Combine(RepoRoot(), "native", "core", "abi", "services", "kiln_ui.h"));
        Assert.Contains("KilnResult(KILN_CALL* graphics_status)(KilnUiGraphicsStatus* out);\n} KilnUiApi;", h, StringComparison.Ordinal);
        // a kiln.ui without the tail: nothing to call, nothing happens
        Assert.False(GraphicsTools.Available);
        GraphicsTools.Apply();
        Assert.Null(GraphicsTools.Status());
    }

    [Fact]
    public void Settings_go_to_kiln_ui_at_once_and_to_the_file_later()
    {
        Install();
        GraphicsTools.PathOverride = Path.Combine(_dir, "kiln.json");
        File.WriteAllText(GraphicsTools.PathOverride, "{ \"graphics\": { \"fpsCap\": 72, \"dlssPreset\": \"M\" } }");
        GraphicsTools.Start();
        Assert.Single(Set);
        Assert.Equal(72u, Set[0].FpsCap);
        Assert.Equal(13u, Set[0].DlssPreset);
        GraphicsTools.Change(GraphicsTools.Current with { FpsCap = 3, DlssScale = DlssScaleMode.Custom, RenderScale = 0.8f, Readout = true });
        Assert.Equal(2, Set.Count);
        var g = Set[1];
        Assert.Equal(20u, g.FpsCap);  // clamped
        Assert.Equal(2u, g.DlssScale);
        Assert.Equal(0.8f, g.CustomScale);
        Assert.Equal(GraphicsSettings.DlssQuality, g.ModeScale[0]);
        Assert.Equal(GraphicsSettings.DlssUltraPerformance, g.ModeScale[3]);
        Assert.Equal(1u, g.Readout);
        GraphicsTools.Change(GraphicsTools.Current);  // no change: kiln.ui is not told again
        Assert.Equal(2, Set.Count);
        // saved half a second after the last change
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && GraphicsTools.Read(GraphicsTools.PathOverride).FpsCap != 20) Thread.Sleep(50);
        Assert.Equal(GraphicsTools.Current, GraphicsTools.Read(GraphicsTools.PathOverride));
    }

    // ---- the page ----

    [Fact]
    public void The_page_says_what_the_tools_do()
    {
        var s = GraphicsSettings.Default with { FpsCap = 60, DlssPreset = "K" };
        Assert.Equal([KilnText.T("graphics.status.unavailable")], GraphicsPage.StatusLines(s, null));
        var st = new UiNative.GraphicsStatus
        {
            Flags = GraphicsPage.CapActive | GraphicsPage.Counting | GraphicsPage.DlssHooked | GraphicsPage.DlssActive | GraphicsPage.DlssPreset,
            FpsCap = 60, RenderedFps = 59.7f, OutputFps = 59.7f, FrameMs = 16.75f, DlssQuality = 2, DlssPreset = 11,
            RenderWidth = 1707, RenderHeight = 960, DlssOutputWidth = 2560, DlssOutputHeight = 1440,
        };
        var lines = GraphicsPage.StatusLines(s, st);
        Assert.Equal("Frame cap: 60 fps.", lines[0]);
        Assert.Equal("Rendered 60 fps, output 60 fps, 16.8 ms a frame.", lines[1]);
        Assert.Equal("DLSS Quality: 1707x960 to 2560x1440, preset K.", lines[2]);
        Assert.Equal(3, lines.Count);
        // DLSS's range for the mode limited the player's scale: said under the size in effect
        st.Flags |= GraphicsPage.DlssLimited;
        lines = GraphicsPage.StatusLines(s, st);
        Assert.Equal(4, lines.Count);
        Assert.Equal("Your render scale is outside what DLSS allows in Quality: it renders at the nearest size DLSS allows (above).", lines[3]);
        // OptiScaler: both step aside, by name
        st = new UiNative.GraphicsStatus { AsideCap = 1, AsideDlss = 1 | 8 };
        lines = GraphicsPage.StatusLines(s, st);
        Assert.Equal("Frame cap (60 fps): off, left to OptiScaler.", lines[0]);
        Assert.Equal("DLSS settings: left to OptiScaler, DLSSTweaks.", lines[1]);
        // a DLSS setting turned on in this session: from the next start
        st = new UiNative.GraphicsStatus { Flags = GraphicsPage.DlssRestart };
        lines = GraphicsPage.StatusLines(GraphicsSettings.Default with { DlssPreset = "J" }, st);
        Assert.Equal([KilnText.T("graphics.status.restart")], lines);
        // hooked, but no DLSS in use; or set at start and the game never set DLSS up (another GPU)
        Assert.Equal([KilnText.T("graphics.status.dlss_idle")], GraphicsPage.StatusLines(GraphicsSettings.Default, new UiNative.GraphicsStatus { Flags = GraphicsPage.DlssHooked }));
        Assert.Equal([KilnText.T("graphics.status.dlss_none")], GraphicsPage.StatusLines(GraphicsSettings.Default with { Readout = true }, new UiNative.GraphicsStatus()));
        Assert.Empty(GraphicsPage.StatusLines(GraphicsSettings.Default, new UiNative.GraphicsStatus()));
        Assert.Equal("DLAA", GraphicsPage.ModeName(5));
        Assert.Equal("Ultra Performance", GraphicsPage.ModeName(3));
        Assert.Equal("default", GraphicsPage.PresetName(0));
        Assert.Equal("M", GraphicsPage.PresetName(13));
    }

    private static string RepoRoot([CallerFilePath] string source = "")
    {
        var d = Path.GetDirectoryName(source);
        while (d is not null && !File.Exists(Path.Combine(d, "VERSION"))) d = Path.GetDirectoryName(d);
        return d ?? throw new InvalidOperationException("no repository above " + source);
    }

    [Fact]
    public void Graphics_is_a_section_of_the_kiln_menu()
    {
        Assert.Equal(ModManagerSection.Graphics, ModManagerLogic.Step(ModManagerSection.Overlay, 1));
        Assert.Equal(ModManagerSection.Graphics, ModManagerLogic.Step(ModManagerSection.Mods, -1));
        Assert.Equal("Graphics", KilnText.T("menu.sections.graphics"));
    }
}
