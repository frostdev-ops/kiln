using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        GraphicsStateFile.ResetForTests();
        VendorFiles.ResetForTests();
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
        Assert.Equal(40, (int)Marshal.OffsetOf<UiNative.Graphics>(nameof(UiNative.Graphics.Upscaler)));
        Assert.Equal(44, (int)Marshal.OffsetOf<UiNative.Graphics>(nameof(UiNative.Graphics.AmdUpscaler)));
        Assert.Equal(248, sizeof(UiNative.GraphicsStatus));
        Assert.Equal((int)UiNative.GraphicsStatusSizeV2, sizeof(UiNative.GraphicsStatus));
        Assert.Equal(20, (int)Marshal.OffsetOf<UiNative.GraphicsStatus>(nameof(UiNative.GraphicsStatus.RenderedFps)));
        Assert.Equal(40, (int)Marshal.OffsetOf<UiNative.GraphicsStatus>(nameof(UiNative.GraphicsStatus.DlssQuality)));
        Assert.Equal(56, (int)Marshal.OffsetOf<UiNative.GraphicsStatus>(nameof(UiNative.GraphicsStatus.DlssOutputWidth)));
        // phase B's fields (kiln_ui.h; ui.cpp pins the same offsets)
        foreach (var (field, at) in new[]
                 {
                     (nameof(UiNative.GraphicsStatus.Upscaler), 72), (nameof(UiNative.GraphicsStatus.AmdUpscaler), 76),
                     (nameof(UiNative.GraphicsStatus.UpscaleRunning), 80), (nameof(UiNative.GraphicsStatus.UpscaleReason), 84),
                     (nameof(UiNative.GraphicsStatus.UpscaleFlags), 88), (nameof(UiNative.GraphicsStatus.AsideUpscale), 92),
                     (nameof(UiNative.GraphicsStatus.UpscaleVersion), 96), (nameof(UiNative.GraphicsStatus.UpscaleRenderWidth), 100),
                     (nameof(UiNative.GraphicsStatus.UpscaleRenderHeight), 104), (nameof(UiNative.GraphicsStatus.UpscaleOutputWidth), 108),
                     (nameof(UiNative.GraphicsStatus.UpscaleOutputHeight), 112), (nameof(UiNative.GraphicsStatus.Reserved2), 116),
                     (nameof(UiNative.GraphicsStatus.UpscaleDetail), 120),
                 })
            Assert.Equal(at, (int)Marshal.OffsetOf<UiNative.GraphicsStatus>(field));
        var h = File.ReadAllText(Path.Combine(RepoRoot(), "native", "core", "abi", "services", "kiln_ui.h"));
        Assert.Contains("KilnResult(KILN_CALL* graphics_status)(KilnUiGraphicsStatus* out);\n", h, StringComparison.Ordinal);
        // what follows the graphics tail: chat_completion's slot (PR #73) at 1032, then subscribe_pointer_press (0.2.0-dev.30)
        Assert.Equal(1032, (int)Marshal.OffsetOf<UiNative.TableChat>(nameof(UiNative.TableChat.ChatCompletion)));
        Assert.Equal(1040, (int)Marshal.OffsetOf<UiNative.TablePointer>(nameof(UiNative.TablePointer.SubscribePointerPress)));
        Assert.Equal(1048, sizeof(UiNative.TablePointer));
        Assert.Contains("KilnResult(KILN_CALL* subscribe_pointer_press)(KilnOwner owner, KilnUiPointerFn fn, void* user, KilnHandle* out);\n",
            h, StringComparison.Ordinal);
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

    [Fact]
    public void A_preset_reads_as_its_letter_whatever_bits_it_has()
    {
        // the game's own hint as the NGX SDK code in the game fills it in: 0x8000000B is K (session 29.18 showed "preset 2147483659")
        Assert.Equal("K", GraphicsPage.PresetName(0x8000000Bu));
        Assert.Equal("K", GraphicsPage.PresetKey(0x8000000Bu));
        Assert.Equal("default", GraphicsPage.PresetName(0x80000000u));
        Assert.Equal("default", GraphicsPage.PresetKey(0x80000000u));
        // a plain value
        Assert.Equal("K", GraphicsPage.PresetName(11));
        Assert.Equal("L", GraphicsPage.PresetKey(12));
        Assert.Equal("default", GraphicsPage.PresetKey(0));
        Assert.Equal("O", GraphicsPage.PresetName(15));
        // a value with no letter: readable, never the number
        Assert.Equal("?", GraphicsPage.PresetName(16));
        Assert.Equal("?", GraphicsPage.PresetName(99));
        Assert.Equal("?", GraphicsPage.PresetName(0xFFFFFFFFu));
        Assert.Equal("unknown", GraphicsPage.PresetKey(99));
        Assert.Equal("unknown", GraphicsPage.PresetKey(0x8000001Fu));
        // the Graphics page's line
        var st = new UiNative.GraphicsStatus
        {
            Flags = GraphicsPage.DlssHooked | GraphicsPage.DlssActive, DlssQuality = 2, DlssPreset = 0x8000000Bu,
            RenderWidth = 1280, RenderHeight = 720, DlssOutputWidth = 1920, DlssOutputHeight = 1080,
        };
        Assert.Equal(["DLSS Quality: 1280x720 to 1920x1080, preset K."], GraphicsPage.StatusLines(GraphicsSettings.Default with { Readout = true }, st));
        st.DlssPreset = 99;
        Assert.Equal(["DLSS Quality: 1280x720 to 1920x1080, preset ?."], GraphicsPage.StatusLines(GraphicsSettings.Default with { Readout = true }, st));
    }

    [Fact]
    public void The_state_file_names_the_preset_by_its_letter()
    {
        var path = GraphicsStateFile.PathOverride = Path.Combine(_dir, "state", "graphics.json");
        VendorFiles.RootOverride = _dir;
        var s = GraphicsSettings.Default with { Readout = true };
        string Preset(uint raw)
        {
            var st = Phase(1, 0, UpscalerNow.Hooked);
            st.Flags = GraphicsPage.DlssHooked | GraphicsPage.DlssActive;
            st.DlssQuality = 2;
            st.DlssPreset = raw;
            GraphicsStateFile.Update(s, st, UpscalerStatus.FromNative(st));
            return (string)JsonNode.Parse(File.ReadAllText(path))!["dlss"]!["preset"]!;
        }
        Assert.Equal("K", Preset(0x8000000Bu));
        Assert.Equal("M", Preset(13));
        Assert.Equal("default", Preset(0));
        Assert.Equal("unknown", Preset(99));
    }

    // ---- phase B: the upscaler behind the game's FSR 3 option ----

    [Fact]
    public void Reads_and_writes_the_upscaler_choice()
    {
        Assert.Equal(UpscalerChoice.Game, GraphicsSettings.Default.Upscaler);
        Assert.Equal(AmdUpscalerMode.Auto, GraphicsSettings.Default.AmdUpscaler);
        var s = GraphicsTools.Parse("{ \"graphics\": { \"upscaler\": \"amd\", \"amdUpscaler\": \"fsr3\" } }");
        Assert.Equal(UpscalerChoice.Amd, s.Upscaler);
        Assert.Equal(AmdUpscalerMode.Fsr3, s.AmdUpscaler);
        Assert.Equal(UpscalerChoice.Xess, GraphicsTools.Parse("{ \"graphics\": { \"upscaler\": \"xess\" } }").Upscaler);
        var warnings = new List<string>();
        var bad = GraphicsTools.Parse("{ \"graphics\": { \"upscaler\": \"DLSS\", \"amdUpscaler\": 4 } }", warnings.Add);
        Assert.Equal(GraphicsSettings.Default, bad);
        Assert.Equal(["graphics.upscaler is not game, amd or xess: game", "graphics.amdUpscaler is not auto, fsr3 or fsr4: auto"], warnings);
        // FSR 4 as an explicit, experimental choice ("auto" runs FSR 3.1 until FSR 4 is confirmed in this game)
        Assert.Equal(AmdUpscalerMode.Fsr4, GraphicsTools.Parse("{ \"graphics\": { \"upscaler\": \"amd\", \"amdUpscaler\": \"fsr4\" } }").AmdUpscaler);
        Assert.Equal("AMD FSR 4 (experimental) behind the game's FSR 3",
            GraphicsTools.Describe(GraphicsSettings.Default with { Upscaler = UpscalerChoice.Amd, AmdUpscaler = AmdUpscalerMode.Fsr4 }));
        var four = GraphicsSettings.Default with { Upscaler = UpscalerChoice.Amd, AmdUpscaler = AmdUpscalerMode.Fsr4 };
        Assert.Contains("\"amdUpscaler\": \"fsr4\"", GraphicsTools.Splice("", four));
        Assert.Equal(four, GraphicsTools.Parse(GraphicsTools.Splice("", four)));
        // written with the rest of the section; comments stay; an older section gains the two keys
        var mine = GraphicsSettings.Default with { FpsCap = 90, Upscaler = UpscalerChoice.Xess, AmdUpscaler = AmdUpscalerMode.Fsr3 };
        const string old = "{\n  \"graphics\": {\n    \"fpsCap\": 60 // mine\n  }\n}\n";
        var text = GraphicsTools.Splice(old, mine);
        Assert.Contains("\"fpsCap\": 90, // mine", text);
        Assert.Contains("\"upscaler\": \"xess\"", text);
        Assert.Contains("\"amdUpscaler\": \"fsr3\"", text);
        Assert.Equal(mine, GraphicsTools.Parse(text));
        Assert.Equal(mine, GraphicsTools.Parse(GraphicsTools.Splice("", mine)));
        Assert.Contains("Intel XeSS behind the game's FSR 3", GraphicsTools.Describe(mine));
        Assert.Equal("AMD FSR (auto) behind the game's FSR 3", GraphicsTools.Describe(GraphicsSettings.Default with { Upscaler = UpscalerChoice.Amd }));
        Assert.Equal("all off", GraphicsTools.Describe(GraphicsSettings.Default with { AmdUpscaler = AmdUpscalerMode.Fsr3 }));
    }

    [Fact]
    public void The_choice_goes_to_kiln_ui_in_graphics_set()
    {
        Install();
        GraphicsTools.PathOverride = Path.Combine(_dir, "kiln.json");
        GraphicsStateFile.PathOverride = Path.Combine(_dir, "state", "graphics.json");
        File.WriteAllText(GraphicsTools.PathOverride, "{ \"graphics\": { \"upscaler\": \"amd\" } }");
        GraphicsTools.Start();
        Assert.Equal(1u, Set[^1].Upscaler);
        Assert.Equal(0u, Set[^1].AmdUpscaler);
        GraphicsTools.Change(GraphicsTools.Current with { AmdUpscaler = AmdUpscalerMode.Fsr3 });
        Assert.Equal(1u, Set[^1].AmdUpscaler);
        GraphicsTools.Change(GraphicsTools.Current with { Upscaler = UpscalerChoice.Xess });
        Assert.Equal(2u, Set[^1].Upscaler);
        // kiln.json is written half a second later, with both keys
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && GraphicsTools.Read(GraphicsTools.PathOverride).Upscaler != UpscalerChoice.Xess) Thread.Sleep(50);
        var saved = GraphicsTools.Read(GraphicsTools.PathOverride);
        Assert.Equal((UpscalerChoice.Xess, AmdUpscalerMode.Fsr3), (saved.Upscaler, saved.AmdUpscaler));
    }

    [Fact]
    public void Graphics_set_carries_the_upscaler_and_amd_s_version_at_offsets_40_and_44_for_every_choice()
    {
        // kiln.ui takes the choice from graphics_set at every call (every 2 seconds and on every change): left at 0, it would
        // turn the player's choice back to the game's own FSR 3 at the next FSR set-up
        foreach (var (name, choice, native) in new[] { ("game", UpscalerChoice.Game, 0u), ("amd", UpscalerChoice.Amd, 1u), ("xess", UpscalerChoice.Xess, 2u) })
            foreach (var (amdName, amd, amdNative) in new[] { ("auto", AmdUpscalerMode.Auto, 0u), ("fsr3", AmdUpscalerMode.Fsr3, 1u), ("fsr4", AmdUpscalerMode.Fsr4, 2u) })
            {
                var s = GraphicsTools.Parse($"{{ \"graphics\": {{ \"upscaler\": \"{name}\", \"amdUpscaler\": \"{amdName}\", \"fpsCap\": 60 }} }}");
                Assert.Equal((choice, amd), (s.Upscaler, s.AmdUpscaler));
                var g = GraphicsTools.ToNative(s);
                var bytes = new byte[sizeof(UiNative.Graphics)];
                fixed (byte* b = bytes) *(UiNative.Graphics*)b = g;
                Assert.Equal(native, BitConverter.ToUInt32(bytes, 40));
                Assert.Equal(amdNative, BitConverter.ToUInt32(bytes, 44));
                Assert.Equal(60u, BitConverter.ToUInt32(bytes, 4));  // phase A's fields as before
                Assert.Equal((uint)sizeof(UiNative.Graphics), BitConverter.ToUInt32(bytes, 0));
            }
        // and through the table: at start, on a change, and on every later apply
        Install();
        GraphicsTools.PathOverride = Path.Combine(_dir, "kiln.json");
        GraphicsStateFile.PathOverride = Path.Combine(_dir, "state", "graphics.json");
        File.WriteAllText(GraphicsTools.PathOverride, "{ \"graphics\": { \"upscaler\": \"xess\", \"amdUpscaler\": \"fsr3\" } }");
        GraphicsTools.Start();
        Assert.Equal((2u, 1u), (Set[^1].Upscaler, Set[^1].AmdUpscaler));
        GraphicsTools.Apply();
        Assert.Equal((2u, 1u), (Set[^1].Upscaler, Set[^1].AmdUpscaler));
        GraphicsTools.Change(GraphicsTools.Current with { FpsCap = 90 });  // another setting: the choice stays in the struct
        Assert.Equal((2u, 1u, 90u), (Set[^1].Upscaler, Set[^1].AmdUpscaler, Set[^1].FpsCap));
    }

    private static IEnumerable<(string Name, uint Value)> HeaderEnum(string header, string prefix)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(header,
                     @"\b" + prefix + @"([A-Z0-9_]+)\s*=\s*(?:(\d+)u?|1u\s*<<\s*(\d+))\s*,"))
            yield return (m.Groups[1].Value, m.Groups[2].Success ? uint.Parse(m.Groups[2].Value) : 1u << int.Parse(m.Groups[3].Value));
    }

    private static string CamelOf(string upper)
    {
        var parts = upper.ToLowerInvariant().Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    [Fact]
    public void The_upscaler_codes_are_kiln_ui_h_s()
    {
        var h = File.ReadAllText(Path.Combine(RepoRoot(), "native", "core", "abi", "services", "kiln_ui.h"));
        // every reason by its number and its name in camelCase; none missing either way
        var why = HeaderEnum(h, "KILN_UI_UPSCALE_WHY_").ToList();
        Assert.Equal(15, why.Count);
        foreach (var (name, value) in why)
        {
            Assert.Equal(name.Replace("_", ""), ((UpscalerReason)value).ToString().ToUpperInvariant());
            if (value == 0) continue;
            Assert.Equal(CamelOf(name), UpscalerCodes.Reason((UpscalerReason)value));
        }
        Assert.Equal(why.Count - 1, UpscalerCodes.Reasons.Length);
        var run = HeaderEnum(h, "KILN_UI_UPSCALE_RUN_").ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal(new Dictionary<string, uint> { ["OFF"] = 0, ["NOT_FSR"] = 1, ["GAME_FSR3"] = 2, ["AMD_FSR4"] = 3, ["AMD_FSR3"] = 4, ["XESS"] = 5 }, run);
        Assert.Equal((uint)UpscalerRunning.AmdFsr31, run["AMD_FSR3"]);
        Assert.Equal((uint)UpscalerRunning.NotFsr, run["NOT_FSR"]);
        var flags = HeaderEnum(h, "KILN_UI_UPSCALE_").Where(x => !x.Name.StartsWith("RUN_", StringComparison.Ordinal) && !x.Name.StartsWith("WHY_", StringComparison.Ordinal))
            .ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal(new Dictionary<string, uint>
        {
            ["HOOKED"] = UpscalerNow.Hooked, ["FSR_SEEN"] = UpscalerNow.FsrSeen, ["FILES_OK"] = UpscalerNow.FilesOk,
            ["DEVICE_OK"] = UpscalerNow.DeviceOk, ["FELL_BACK"] = UpscalerNow.FellBack, ["AMD_PINNED"] = UpscalerNow.AmdPinned,
        }, flags);
        var aside = HeaderEnum(h, "KILN_UI_GFX_ASIDE_").ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal(aside.Count, GraphicsPage.AsideKeys.Length);
        foreach (var (bit, key) in GraphicsPage.AsideKeys) Assert.Equal(bit, aside[key.ToUpperInvariant()]);
        Assert.Equal(1u, HeaderEnum(h, "KILN_UI_UPSCALER_").Single(x => x.Name == "AMD").Value);
        Assert.Equal(1u, HeaderEnum(h, "KILN_UI_AMD_UPSCALER_").Single(x => x.Name == "FSR3").Value);
        Assert.Equal((uint)AmdUpscalerMode.Fsr4, HeaderEnum(h, "KILN_UI_AMD_UPSCALER_").Single(x => x.Name == "FSR4").Value);
    }

    [Fact]
    public void Every_reason_has_a_text_in_kiln_s_english_and_a_row_in_the_docs()
    {
        using var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "runtime", "locales", "en.json")));
        var reasons = en.RootElement.GetProperty("graphics").GetProperty("upscaler").GetProperty("reason");
        var docs = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "kiln", "graphics.md"));
        foreach (var (_, code) in UpscalerCodes.Reasons.Append((UpscalerReason.None, "unknown")))
        {
            var text = reasons.GetProperty(UpscalerCodes.Key(code)).GetString()!;
            Assert.DoesNotContain("DLSS", text, StringComparison.Ordinal);
            // the docs' table has the code with the same English
            Assert.Contains($"| `{code}` |", docs, StringComparison.Ordinal);
            Assert.Contains(text.Replace("{upscaler}", "AMD FSR", StringComparison.Ordinal).Replace("{tools}", "OptiScaler", StringComparison.Ordinal)
                .Replace("{number}", "15", StringComparison.Ordinal), docs, StringComparison.Ordinal);
        }
        Assert.Equal(UpscalerCodes.Reasons.Length + 1, reasons.EnumerateObject().Count());
    }

    private static UiNative.GraphicsStatus Phase(uint running, uint reason, uint flags = 0, uint aside = 0, uint version = 0, string detail = "")
    {
        var st = new UiNative.GraphicsStatus
        {
            Size = UiNative.GraphicsStatusSizeV2, UpscaleRunning = running, UpscaleReason = reason, UpscaleFlags = flags, AsideUpscale = aside,
            UpscaleVersion = version, UpscaleRenderWidth = 1707, UpscaleRenderHeight = 960, UpscaleOutputWidth = 2560, UpscaleOutputHeight = 1440,
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes(detail);
        for (var i = 0; i < bytes.Length && i < UiNative.UpscaleDetailBytes - 1; i++) st.UpscaleDetail[i] = bytes[i];
        return st;
    }

    [Fact]
    public void The_status_is_read_from_kiln_ui_s_phase_b_fields()
    {
        // phase A's 72 bytes: nothing said about the upscaler
        Assert.Null(UpscalerStatus.FromNative(new UiNative.GraphicsStatus { Size = UiNative.GraphicsStatusSizeV1 }));
        var now = UpscalerStatus.FromNative(Phase(3, 0, UpscalerNow.Hooked | UpscalerNow.FsrSeen | UpscalerNow.FilesOk, version: (4u << 22) | (1u << 12) | 2u))!;
        Assert.Equal(UpscalerRunning.AmdFsr4, now.Running);
        Assert.Equal(UpscalerReason.None, now.Reason);
        Assert.Equal("4.1.2", now.VersionText);
        Assert.Equal((1707u, 960u, 2560u, 1440u), (now.RenderWidth, now.RenderHeight, now.OutputWidth, now.OutputHeight));
        var bad = UpscalerStatus.FromNative(Phase(2, 5, detail: "VK_KHR_shader_float16_int8 is missing"))!;
        Assert.Equal((UpscalerRunning.GameFsr3, UpscalerReason.Device), (bad.Running, bad.Reason));
        Assert.Equal("VK_KHR_shader_float16_int8 is missing", bad.Detail);
        // a number from a newer kiln-ui.dll
        var newer = UpscalerStatus.FromNative(Phase(9, 40))!;
        Assert.Equal(UpscalerRunning.Unknown, newer.Running);
        Assert.Equal("unknown", UpscalerCodes.Reason(newer.Reason));
        Assert.Equal(40u, newer.Code);
        // a detail of 127 bytes without its NUL in the last place still ends
        Assert.Equal(127, UpscalerStatus.FromNative(Phase(2, 3, detail: new string('x', 200)))!.Detail.Length);
    }

    [Fact]
    public void A_choice_that_needs_a_restart_gets_one_toast()
    {
        var amd = GraphicsSettings.Default with { Upscaler = UpscalerChoice.Amd };
        var restart = UpscalerStatus.FromNative(Phase(2, 1));
        var t0 = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var n = new GraphicsPage.RestartNotice();
        Assert.Null(n.Due(amd, restart, t0));  // not armed: the player did not change it here
        n.Arm(UpscalerChoice.Amd, t0);
        Assert.Null(n.Due(amd, UpscalerStatus.FromNative(Phase(2, 0)), t0.AddMilliseconds(16)));  // the status has not caught up yet
        Assert.Equal("Restart the game to use AMD FSR. Until then the game's own FSR 3 runs.", n.Due(amd, restart, t0.AddMilliseconds(33)));
        Assert.Null(n.Due(amd, restart, t0.AddMilliseconds(50)));  // once
        n.Arm(UpscalerChoice.Amd, t0.AddSeconds(10));
        Assert.Null(n.Due(amd, restart, t0.AddSeconds(10.1)));  // once per choice and session
        var xess = amd with { Upscaler = UpscalerChoice.Xess };
        n.Arm(UpscalerChoice.Xess, t0.AddSeconds(20));
        Assert.Null(n.Due(xess, UpscalerStatus.FromNative(Phase(3, 0)), t0.AddSeconds(24)));  // applied at once: no restart, dropped
        Assert.Null(n.Due(xess, restart, t0.AddSeconds(24.1)));
        n.Arm(UpscalerChoice.Xess, t0.AddSeconds(30));
        Assert.Equal("Restart the game to use Intel XeSS. Until then the game's own FSR 3 runs.", n.Due(xess, restart, t0.AddSeconds(30.5)));
    }

    [Fact]
    public void The_page_says_what_runs_behind_fsr_3_and_why_the_choice_does_not()
    {
        var amd = GraphicsSettings.Default with { Upscaler = UpscalerChoice.Amd };
        // the game's own chosen and the swap off: nothing said (phase A's lines alone)
        Assert.Empty(GraphicsPage.StatusLines(GraphicsSettings.Default, Phase(0, 0)));
        Assert.Empty(GraphicsPage.UpscalerLines(GraphicsSettings.Default, UpscalerStatus.FromNative(Phase(0, 0)), available: true));
        var running = GraphicsPage.StatusLines(amd, Phase(3, 0, UpscalerNow.Hooked | UpscalerNow.FsrSeen, version: (4u << 22) | (1u << 12) | 2u));
        Assert.Equal(["AMD FSR 4 runs in place of the game's FSR 3.", "Version 4.1.2, rendering at 1707x960 for 2560x1440."], running);
        // FSR 3.1 by AMD's selection (FSR 4 chosen), and pinned (FSR 3.1, or auto: FSR 3.1 until FSR 4 is confirmed)
        Assert.Equal(["AMD FSR 3.1 runs in place of the game's FSR 3.", "Version 3.1.6, rendering at 1707x960 for 2560x1440.",
            "AMD's selection chose FSR 3.1: FSR 4 does not run on this graphics card or driver."],
            GraphicsPage.StatusLines(amd with { AmdUpscaler = AmdUpscalerMode.Fsr4 }, Phase(4, 0, UpscalerNow.FsrSeen, version: (3u << 22) | (1u << 12) | 6u)));
        Assert.Equal(2, GraphicsPage.StatusLines(amd, Phase(4, 0, UpscalerNow.FsrSeen | UpscalerNow.AmdPinned, version: (3u << 22) | (1u << 12) | 6u)).Count);
        Assert.Equal(2, GraphicsPage.StatusLines(amd with { AmdUpscaler = AmdUpscalerMode.Fsr3 },
            Phase(4, 0, UpscalerNow.FsrSeen | UpscalerNow.AmdPinned, version: (3u << 22) | (1u << 12) | 6u)).Count);
        // files missing: the game's FSR 3 and why
        Assert.Equal(["The game's own FSR 3 runs.", "It renders at 1707x960 for 2560x1440.", "AMD FSR's files are not downloaded yet, so the game's own FSR 3 runs."],
            GraphicsPage.StatusLines(amd, Phase(2, 2, UpscalerNow.FsrSeen)));
        // OptiScaler: Kiln steps aside, by name
        Assert.Equal(["Kiln's upscaler is not in use behind the game's FSR 3 now.", "Kiln steps aside for OptiScaler, so it does not change the game's FSR 3."],
            GraphicsPage.StatusLines(amd with { Upscaler = UpscalerChoice.Xess }, Phase(0, 9, aside: 1)));
        // not FSR 3 in the game's settings; chosen while the game runs
        Assert.Equal(KilnText.T("graphics.upscaler.running.not_fsr"), GraphicsPage.StatusLines(amd, Phase(1, 0))[0]);
        var restartLines = GraphicsPage.StatusLines(GraphicsSettings.Default with { Upscaler = UpscalerChoice.Xess }, Phase(0, 1));
        Assert.Equal("Intel XeSS runs from the next start of the game: Kiln sets it up when the game starts.", restartLines[^1]);
        // ... and the restart leads, plainly (29.19 beta 2: a player missed the reason line and took the game's FSR 3 for AMD FSR)
        Assert.Equal("Restart the game to use Intel XeSS. Until then the game's own FSR 3 runs.", restartLines[0]);
        Assert.Equal("Restart the game to use AMD FSR. Until then the game's own FSR 3 runs.",
            GraphicsPage.RestartLine(amd with { AmdUpscaler = AmdUpscalerMode.Fsr3 }, UpscalerStatus.FromNative(Phase(2, 1))));
        Assert.Null(GraphicsPage.RestartLine(GraphicsSettings.Default, UpscalerStatus.FromNative(Phase(2, 1))));
        Assert.Null(GraphicsPage.RestartLine(amd, UpscalerStatus.FromNative(Phase(2, 2))));
        Assert.Null(GraphicsPage.RestartLine(amd, null));
        Assert.DoesNotContain(GraphicsPage.StatusLines(amd, Phase(2, 2, UpscalerNow.FsrSeen)), l => l.StartsWith("Restart the game", StringComparison.Ordinal));
        // a newer reason; a kiln-ui.dll without phase B
        Assert.Equal("AMD FSR does not run (reason 40 from a newer Kiln), so the game's own FSR 3 runs.", GraphicsPage.StatusLines(amd, Phase(2, 40))[^1]);
        Assert.Equal([KilnText.T("graphics.upscaler.running.unknown")],
            GraphicsPage.StatusLines(amd, new UiNative.GraphicsStatus { Size = UiNative.GraphicsStatusSizeV1 }));
        // the English detail, for a muted line
        Assert.Equal("Details: VK_KHR_shader_float16_int8 is missing",
            GraphicsPage.UpscalerDetail(UpscalerStatus.FromNative(Phase(2, 5, detail: "VK_KHR_shader_float16_int8 is missing"))));
        Assert.Null(GraphicsPage.UpscalerDetail(UpscalerStatus.FromNative(Phase(3, 0, detail: "x"))));
    }

    [Fact]
    public void The_vendor_files_are_the_launcher_s_list_and_their_state_is_read_from_kiln_vendor()
    {
        var list = VendorFiles.Components;
        Assert.Equal(["amd-fsr", "intel-xess"], list.Select(c => c.Id));
        var amd = VendorFiles.Of("amd")!;
        Assert.Equal("2.4.0", amd.Version);
        Assert.Equal(["amd_fidelityfx_loader_vk.dll", "amd_fidelityfx_upscaler_vk.dll", "amd_fidelityfx_framegeneration_vk.dll"], amd.Files.Select(f => f.Name));
        Assert.Equal(72_981_808, amd.Files.Sum(f => f.Size));
        Assert.Equal("kiln/vendor/intel/3.0.2", VendorFiles.Folder(VendorFiles.Of("intel")!));
        using (var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "launcher", "src", "data", "vendor-components.json"))))
            Assert.Equal(m.RootElement.GetProperty("components").GetArrayLength(), list.Count);

        VendorFiles.RootOverride = _dir;
        Assert.Equal(VendorFilesState.Missing, VendorFiles.Look(amd));
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "vendor", "amd", "2.4.0")).FullName;
        foreach (var (name, size) in amd.Files)
        {
            using var f = File.Create(Path.Combine(dir, name));
            f.SetLength(size);
        }
        // every file, but no receipt yet (a download that did not finish: the game does not use the folder)
        Assert.Equal(VendorFilesState.Changed, VendorFiles.Look(amd));
        var receipt = Path.Combine(dir, VendorFiles.Receipt);
        File.WriteAllText(receipt, """{"format": 1, "component": "intel-xess"}""");
        Assert.Equal(VendorFilesState.Changed, VendorFiles.Look(amd));
        File.WriteAllText(receipt, """{"format": 2, "component": "amd-fsr"}""");
        Assert.Equal(VendorFilesState.Changed, VendorFiles.Look(amd));
        File.WriteAllText(receipt, "{ half");
        Assert.Equal(VendorFilesState.Changed, VendorFiles.Look(amd));
        File.WriteAllText(receipt, """{"format": 1, "component": "amd-fsr", "vendor": "amd", "version": "2.4.0"}""");
        Assert.Equal(VendorFilesState.Present, VendorFiles.Look(amd));
        // the launcher's receipt as kiln-manage writes it (schema/examples/vendor-receipt.json, Intel's)
        var intel = VendorFiles.Of("intel")!;
        var idir = Directory.CreateDirectory(Path.Combine(_dir, "vendor", "intel", "3.0.2")).FullName;
        foreach (var (name, size) in intel.Files)
        {
            using var f = File.Create(Path.Combine(idir, name));
            f.SetLength(size);
        }
        File.Copy(Path.Combine(RepoRoot(), "schema", "examples", "vendor-receipt.json"), Path.Combine(idir, VendorFiles.Receipt));
        Assert.Equal(VendorFilesState.Present, VendorFiles.Look(intel));
        Directory.Delete(idir, recursive: true);
        using (var f = File.OpenWrite(Path.Combine(dir, amd.Files[1].Name))) f.SetLength(10);
        Assert.Equal(VendorFilesState.Changed, VendorFiles.Look(amd));
        File.Delete(Path.Combine(dir, amd.Files[1].Name));
        Assert.Equal(VendorFilesState.Changed, VendorFiles.Look(amd));

        // the page's line for the chosen one, none for the game's own
        Assert.Null(GraphicsPage.FilesLine(GraphicsSettings.Default));
        Assert.Equal("Some of AMD FSR's files in kiln/vendor/amd/2.4.0 are missing or changed. Download them again in the Kiln launcher (Settings, Graphics) while the game is closed. Until then the game's own FSR 3 runs.",
            GraphicsPage.FilesLine(GraphicsSettings.Default with { Upscaler = UpscalerChoice.Amd }));
        Assert.Equal("Intel XeSS's files are not in this game yet. Download them in the Kiln launcher (Settings, Graphics) while the game is closed. Until then the game's own FSR 3 runs.",
            GraphicsPage.FilesLine(GraphicsSettings.Default with { Upscaler = UpscalerChoice.Xess }));
    }

    [Fact]
    public void The_state_file_is_written_when_it_changes_and_only_then()
    {
        var path = GraphicsStateFile.PathOverride = Path.Combine(_dir, "state", "graphics.json");
        VendorFiles.RootOverride = _dir;
        var s = GraphicsSettings.Default with { FpsCap = 60, Upscaler = UpscalerChoice.Amd };
        var st = Phase(2, 2, UpscalerNow.Hooked | UpscalerNow.FsrSeen);
        st.Flags = GraphicsPage.CapActive | GraphicsPage.Counting;
        st.RenderedFps = 59.9f;
        Assert.True(GraphicsStateFile.Update(s, st, UpscalerStatus.FromNative(st)));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(1, (int)json["format"]!);
        Assert.Equal(Environment.ProcessId, (int)json["pid"]!);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", (string)json["written"]!);
        var up = json["upscaler"]!.AsObject();
        Assert.Equal("amd", (string)up["chosen"]!);
        Assert.Equal("auto", (string)up["amdUpscaler"]!);
        Assert.Equal("gameFsr3", (string)up["running"]!);
        Assert.Equal("filesMissing", (string)up["reason"]!);
        Assert.True((bool)up["hooked"]! && (bool)up["fsrSeen"]! && !(bool)up["filesOk"]!);
        Assert.Equal("missing", (string)json["files"]!["amd"]!["state"]!);
        Assert.Equal("2.4.0", (string)json["files"]!["amd"]!["version"]!);
        Assert.Equal(60, (int)json["frameCap"]!["fps"]!);
        Assert.True((bool)json["frameCap"]!["active"]!);
        Assert.False((bool)json["dlss"]!["hooked"]!);
        Assert.DoesNotContain("59.9", File.ReadAllText(path));  // no frame rates: they change all the time
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        // the same again (another frame rate): not written
        st.RenderedFps = 30;
        File.WriteAllText(path, "sentinel");
        Assert.False(GraphicsStateFile.Update(s, st, UpscalerStatus.FromNative(st)));
        Assert.Equal("sentinel", File.ReadAllText(path));
        // AMD FSR 4 now: written again
        var on = Phase(3, 0, UpscalerNow.Hooked | UpscalerNow.FsrSeen | UpscalerNow.FilesOk | UpscalerNow.DeviceOk, version: (4u << 22) | (1u << 12) | 2u);
        Assert.True(GraphicsStateFile.Update(s, on, UpscalerStatus.FromNative(on)));
        up = JsonNode.Parse(File.ReadAllText(path))!["upscaler"]!.AsObject();
        Assert.Equal("amdFsr4", (string)up["running"]!);
        Assert.Null(up["reason"]);
        Assert.Equal("4.1.2", (string)up["version"]!);
        // aside bits by name; a kiln-ui.dll without phase B
        var aside = Phase(0, 9, aside: 1 | 128, detail: "dxgi.dll");
        Assert.True(GraphicsStateFile.Update(s, aside, UpscalerStatus.FromNative(aside)));
        up = JsonNode.Parse(File.ReadAllText(path))!["upscaler"]!.AsObject();
        Assert.Equal(["optiscaler", "upscaler"], up["aside"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("dxgi.dll", (string)up["detail"]!);
        Assert.True(GraphicsStateFile.Update(s, null, null));
        up = JsonNode.Parse(File.ReadAllText(path))!["upscaler"]!.AsObject();
        Assert.Equal(("unknown", "unavailable"), ((string)up["running"]!, (string)up["reason"]!));
        Assert.Null(JsonNode.Parse(File.ReadAllText(path))!["frameCap"]);
    }

    [Fact]
    public void The_state_example_follows_the_schema_s_names()
    {
        // schema/examples/state-graphics.json is what this code writes (the launcher's check-schemas validates it)
        var example = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "schema", "examples", "state-graphics.json")))!.AsObject();
        GraphicsStateFile.PathOverride = Path.Combine(_dir, "graphics.json");
        VendorFiles.RootOverride = _dir;
        var on = Phase(3, 0, UpscalerNow.Hooked | UpscalerNow.FsrSeen | UpscalerNow.FilesOk | UpscalerNow.DeviceOk, version: (4u << 22) | (1u << 12) | 2u);
        on.Flags = GraphicsPage.CapActive;
        GraphicsStateFile.Update(GraphicsSettings.Default with { FpsCap = 141, Upscaler = UpscalerChoice.Amd }, on, UpscalerStatus.FromNative(on));
        var mine = JsonNode.Parse(File.ReadAllText(GraphicsStateFile.PathOverride))!.AsObject();
        static IEnumerable<string> Keys(JsonNode? n, string at) => n is JsonObject o
            ? o.SelectMany(kv => Keys(kv.Value, at + "/" + kv.Key).Prepend(at + "/" + kv.Key)) : [];
        Assert.Equal(Keys(example, "").Order(StringComparer.Ordinal), Keys(mine, "").Order(StringComparer.Ordinal));
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
