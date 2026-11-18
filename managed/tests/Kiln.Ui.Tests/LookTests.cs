using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Kiln.LookTests;

// (The namespace Kiln.Ui of this test assembly hides the class Kiln.Ui here: the tests call its internals, UiThemes and
// PlayerLook, which Ui.Themes and Ui.LoadFont hand on to.)
// Looks and themes (0.2.0-dev.29.16, docs/kiln/ui.md "Looks and themes"): kiln.json's "ui" section (read, clamped, written
// back keeping the file's comments), the native tail's layout, themes (definitions, the offered list, the player's pick,
// a mod unloading), the guard against a look a mod's callback leaves on, Kiln's pages following the player's palette, fonts.
[Collection(Kiln.FakeUi.UiSerial.Name)]
public unsafe class LookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-look-" + Guid.NewGuid().ToString("N"));
    private readonly IKilnBackend _previous = Backend.Current;
    private UiNative.TableLooks* _table;

    public LookTests()
    {
        Directory.CreateDirectory(_dir);
        Calls.Clear();
        _nextHandle = 1;
    }

    public void Dispose()
    {
        PlayerLook.ResetForTests();
        UiThemes.ResetForTests();
        LookPage.ResetForTests();
        KilnLook.Override = null;
        UiNative.ResetForTests();
        Backend.Current = _previous;
        Kiln.FakeUi.FakeUiTable.Free();
        if (_table != null) NativeMemory.Free(_table);
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    // ---- kiln.json ----

    [Fact]
    public void Reads_the_ui_section_with_comments_clamps_and_defaults()
    {
        Assert.Equal(LookSettings.Default, PlayerLook.Parse("{}"));
        Assert.Equal(LookSettings.Default, PlayerLook.Parse("{\"ui\": 3}"));
        var s = PlayerLook.Parse("""
            // Kiln's settings
            {
              "policy": "any",
              "ui": { "look": "Translucent", "background": 0.05, "opacity": 0.8, "scale": 3, }, // trailing comma
            }
            """);
        Assert.Equal(new LookSettings("translucent", "", 0.1f, 0.8f, 1.5f), s);
        var warnings = new List<string>();
        Assert.Equal("game", PlayerLook.Parse("{\"ui\": {\"look\": \"neon\"}}", warnings.Add).Look);
        Assert.Contains(warnings, w => w.Contains("neon", StringComparison.Ordinal));
        Assert.Equal(LookSettings.Default, PlayerLook.Parse("{ not json", warnings.Add));
        var th = PlayerLook.Parse("{\"ui\": {\"look\": \"theme\", \"theme\": \"frostmod:frost\"}}");
        Assert.Equal(new UiPlayerLook(UiLook.Theme, "frostmod:frost", 0.55f, 1f, 1f), th.ToPublic());
        Assert.Null(PlayerLook.Parse("{\"ui\": {\"look\": \"game\", \"theme\": \"x:y\"}}").ToPublic().ThemeId);
    }

    [Fact]
    public void Writes_the_ui_section_keeping_the_rest_of_the_file()
    {
        var s = new LookSettings("translucent", "", 0.4f, 0.9f, 1.25f);
        // no file: a new one with the section
        var empty = PlayerLook.Splice("", s);
        Assert.Equal(s, PlayerLook.Parse(empty));
        // a file without the section: added, comments and other keys kept
        const string plain = "{\n  // keep me\n  \"policy\": \"any\",\n  \"mods\": { \"fault_limit\": 5 }\n}\n";
        var added = PlayerLook.Splice(plain, s);
        Assert.Contains("// keep me", added);
        Assert.Contains("\"fault_limit\": 5", added);
        Assert.Equal(s, PlayerLook.Parse(added));
        Assert.Equal(added, PlayerLook.Splice(added, s));  // nothing to change: the same text
        // a partial section: values spliced in place, missing keys added
        const string partial = "{\n  \"ui\": {\n    \"look\": \"kiln\", // mine\n    \"opacity\": 0.5\n  }\n}\n";
        var spliced = PlayerLook.Splice(partial, s);
        Assert.Contains("// mine", spliced);
        Assert.Equal(s, PlayerLook.Parse(spliced));
        // "ui" that is not an object is replaced
        Assert.Equal(s, PlayerLook.Parse(PlayerLook.Splice("{\"ui\": true}", s)));
        Assert.Throws<SfEditException>(() => PlayerLook.Splice("[1, 2]", s));
        // through the file
        var path = Path.Combine(_dir, "kiln.json");
        File.WriteAllText(path, plain);
        Assert.Null(PlayerLook.Save(path, s));
        Assert.Equal(s, PlayerLook.Read(path));
        Assert.Contains("// keep me", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void The_launcher_s_settings_say_what_Kiln_reads()
    {
        // launcher/src/data/kiln-json.snapshot.json: the launcher's Settings form and its checks of kiln.json
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "launcher", "src", "data", "kiln-json.snapshot.json")));
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!);
        Assert.Contains(doc.RootElement.GetProperty("sections").EnumerateArray(), e => e.GetProperty("path").GetString() == "ui");
        var look = entries["ui.look"];
        Assert.Equal(LookSettings.Default.Look, look.GetProperty("default").GetString());
        Assert.Equal(LookSettings.Looks, look.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToArray());
        Assert.Equal("string", entries["ui.theme"].GetProperty("type").GetString());
        void Range(string key, float def, float min, float max)
        {
            var e = entries[key];
            Assert.Equal(def, e.GetProperty("default").GetSingle());
            Assert.Equal(min, e.GetProperty("min").GetSingle());
            Assert.Equal(max, e.GetProperty("max").GetSingle());
            Assert.False(e.GetProperty("requiresRestart").GetBoolean());  // Kiln reads the file again while the game runs
        }
        Range("ui.background", LookSettings.Default.Background, 0.1f, 1f);
        Range("ui.opacity", LookSettings.Default.Opacity, 0.3f, 1f);
        Range("ui.scale", LookSettings.Default.Scale, 0.75f, 1.5f);
        var clamped = new LookSettings("game", "", 0f, 0f, 0f).Clamped();
        Assert.Equal((0.1f, 0.3f, 0.75f), (clamped.Background, clamped.Opacity, clamped.Scale));
        clamped = new LookSettings("game", "", 9f, 9f, 9f).Clamped();
        Assert.Equal((1f, 1f, 1.5f), (clamped.Background, clamped.Opacity, clamped.Scale));
        // the neutral stand-in is the same in kiln.ui (look_palette.h look::neutral) and Kiln.Ui
        var h = File.ReadAllText(Path.Combine(RepoRoot(), "native", "services", "ui", "look_palette.h"));
        var body = h[h.IndexOf("inline Palette neutral()", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("return p;", StringComparison.Ordinal)];
        var native = System.Text.RegularExpressions.Regex.Matches(body, @"= 0x([0-9a-f]{8})u;").Select(m => Convert.ToUInt32(m.Groups[1].Value, 16)).ToArray();
        var n = UiPalette.Neutral;
        Assert.Equal(new[] { n.Text, n.TextMuted, n.WindowBackground, n.PanelBackground, n.FrameBackground, n.Border, n.Accent, n.Button,
            n.Header, n.Attention, n.Warning, n.Ok }.Select(c => c.Rgba).ToArray(), native);
    }

    private static string RepoRoot([CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "native", "services", "ui", "look_palette.h")) &&
                    Directory.Exists(Path.Combine(d.FullName, "launcher", "src", "data")))
                    return d.FullName;
        throw new InvalidOperationException("repository root not found above the test assembly or its source");
    }

    // ---- the native tail ----

    [Fact]
    public void The_looks_tail_follows_image_state()
    {
        Assert.Equal(928, sizeof(UiNative.TableImageState));
        Assert.Equal(928, (int)Marshal.OffsetOf<UiNative.TableLooks>(nameof(UiNative.TableLooks.ThemeCreate)));
        Assert.Equal(952, (int)Marshal.OffsetOf<UiNative.TableLooks>(nameof(UiNative.TableLooks.SetLook)));
        Assert.Equal(984, (int)Marshal.OffsetOf<UiNative.TableLooks>(nameof(UiNative.TableLooks.PushFontHandle)));
        Assert.Equal(992, sizeof(UiNative.TableLooks));
        Assert.Equal(360, sizeof(UiNative.ThemeDef));
        Assert.Equal(16, (int)Marshal.OffsetOf<UiNative.ThemeDef>(nameof(UiNative.ThemeDef.ColorMask)));
        Assert.Equal(344, (int)Marshal.OffsetOf<UiNative.ThemeDef>(nameof(UiNative.ThemeDef.Background)));
        Assert.Equal(352, (int)Marshal.OffsetOf<UiNative.ThemeDef>(nameof(UiNative.ThemeDef.Font)));
        Assert.Equal(32, sizeof(UiNative.Look));
        Assert.Equal(104, sizeof(UiNative.LookInfo));
        Assert.Equal(96, (int)Marshal.OffsetOf<UiNative.LookInfo>(nameof(UiNative.LookInfo.RoleCount)));
        // Kiln's own numbers: roles in kiln_ui.h's order, colour slots 20.. appended
        Assert.Equal(12, Enum.GetValues<UiThemeRole>().Length);
        Assert.Equal(11u, (uint)UiThemeRole.Ok);
        Assert.Equal(20u, (uint)UiColorSlot.TitleBg);
        Assert.Equal(27u, (uint)UiColorSlot.TextSelectedBg);
        Assert.Equal(2u, (uint)UiStyle.Player);
        // a table that ends before the tail has none
        Install(full: false);
        Assert.True(UiNative.Looks == null);
        Assert.False(PlayerLook.Available);
    }

    [Fact]
    public void A_spec_becomes_kiln_ui_s_definition()
    {
        var spec = new UiThemeSpec
        {
            Base = UiStyle.Kiln,
            Roles = { [UiThemeRole.Accent] = Color.FromRgba(0x7fd4ffff) },
            Colors = { [UiColorSlot.TitleBg] = Color.FromRgba(0x10203040) },
            Vars = { [UiStyleVar.ItemSpacing] = (10, 8) },
            Background = 0.8f,
        };
        var d = UiThemes.ToNative(spec);
        Assert.Equal((uint)sizeof(UiNative.ThemeDef), d.Size);
        Assert.Equal(0u, d.Base);
        Assert.Equal(1u << 6, d.RoleMask);
        Assert.Equal(0x7fd4ffffu, d.Roles[6]);
        Assert.Equal(1ul << 20, d.ColorMask);
        Assert.Equal(0x10203040u, d.Colors[20]);
        Assert.Equal(1u << 2, d.VarMask);
        Assert.Equal(10f, d.Vars[4]);
        Assert.Equal(8f, d.Vars[5]);
        Assert.Equal(0.8f, d.Background);
        Assert.Equal(-1f, d.Rounding);
        Assert.Equal(0ul, d.Font);
        Assert.Equal(1u, UiThemes.ToNative(new UiThemeSpec()).Base);
    }

    // ---- themes and the player's look ----

    [Fact]
    public void Themes_are_offered_picked_by_the_player_only_and_go_with_their_mod()
    {
        Install(full: true);
        Assert.Throws<ArgumentException>(() => UiThemes.Define("Frost Bad", new UiThemeSpec()));
        Assert.Throws<ArgumentException>(() => UiThemes.Define("frost", new UiThemeSpec { Base = UiStyle.Player }));
        Assert.Throws<ArgumentException>(() => UiThemes.Define("frost", new UiThemeSpec { Background = 2 }));
        var quiet = UiThemes.Define("quiet", new UiThemeSpec());
        var frost = UiThemes.Define("frost", new UiThemeSpec { Name = "Frost", OfferToPlayer = true });
        Assert.Equal("kiln:frost", frost.Id);
        Assert.NotEqual(0ul, frost.Handle);
        Assert.True(frost.OfferedToPlayer);
        Assert.False(quiet.OfferedToPlayer);
        Assert.Equal(new[] { frost }, UiThemes.Offered());
        Assert.Contains("create", Calls);
        // defining the same name again changes it in place: the same object, a new kiln.ui theme, the old one freed
        var first = frost.Handle;
        Assert.Same(frost, UiThemes.Define("frost", new UiThemeSpec { Name = "Frost 2", OfferToPlayer = true }));
        Assert.NotEqual(first, frost.Handle);
        Assert.Contains($"free {first}", Calls);
        // the player picks it (kiln.json): kiln.ui gets its handle; a theme nobody offers draws the game's look
        var path = Path.Combine(_dir, "kiln.json");
        File.WriteAllText(path, "{\"ui\": {\"look\": \"theme\", \"theme\": \"kiln:frost\", \"opacity\": 0.7}}");
        PlayerLook.PathOverride = path;
        PlayerLook.Start();
        Assert.Equal((uint)UiLook.Theme, _lastLook.Kind);
        Assert.Equal(frost.Handle, _lastLook.Theme);
        Assert.Equal(0.7f, _lastLook.Opacity);
        Assert.Equal(new UiPlayerLook(UiLook.Theme, "kiln:frost", 0.55f, 0.7f, 1f), PlayerLook.Current.ToPublic());
        PlayerLook.Change(PlayerLook.Current with { Theme = "kiln:quiet" });
        Assert.Equal((uint)UiLook.Game, _lastLook.Kind);  // not offered: the game's look
        PlayerLook.Change(PlayerLook.Current with { Theme = "kiln:frost" });
        Assert.Equal(frost.Handle, _lastLook.Theme);
        // the theme goes: kiln.ui is told, the setting stays
        frost.Dispose();
        Assert.True(frost.IsDisposed);
        Assert.Equal((uint)UiLook.Game, _lastLook.Kind);
        Assert.Equal("kiln:frost", PlayerLook.Current.Theme);
        Assert.Empty(UiThemes.Offered());
        // it comes back (a mod loading later): the player's pick applies again
        var again = UiThemes.Define("frost", new UiThemeSpec { OfferToPlayer = true });
        Assert.Equal((uint)UiLook.Theme, _lastLook.Kind);
        Assert.Equal(again.Handle, _lastLook.Theme);
    }

    [Fact]
    public void The_menu_lists_the_looks_and_the_offered_themes()
    {
        Install(full: true);
        var frost = UiThemes.Define("frost", new UiThemeSpec { Name = "Frost", OfferToPlayer = true });
        var cur = LookSettings.Default;
        var (labels, ids) = LookPage.Choices(cur, UiThemes.Offered());
        Assert.Equal(new[] { "game", "translucent", "kiln", "theme:kiln:frost" }, ids);
        Assert.Equal("Frost (Kiln)", labels[3]);
        Assert.Equal(new LookSettings("theme", "kiln:frost", 0.55f, 1f, 1f), LookPage.Pick(cur, "theme:kiln:frost"));
        Assert.Equal("translucent", LookPage.Pick(cur, "translucent").Look);
        Assert.Equal("theme:kiln:frost", LookPage.IdOf(LookPage.Pick(cur, "theme:kiln:frost")));
        // the player's theme while no mod offers it keeps its place in the list
        var gone = cur with { Look = "theme", Theme = "other:night" };
        var (l2, i2) = LookPage.Choices(gone, UiThemes.Offered());
        Assert.Equal("theme:other:night", i2[^1]);
        Assert.Contains("not loaded", l2[^1]);
        frost.Dispose();
    }

    [Fact]
    public void A_look_a_callback_leaves_on_is_taken_off()
    {
        Install(full: true);
        var ui = UiEngine.DrawContext;
        ui.Frame = new UiNative.Frame { Number = 42 };
        _pushes = 0;
        var theme = UiThemes.Define("own", new UiThemeSpec());
        Assert.Equal(0, ui.LookDepth);
        var outer = ui.LookDepth;
        ui.PushLookRaw(null, UiStyle.Player);
        var inner = ui.LookDepth;
        _ = ui.Theme(theme);  // a callback that never disposes its scope
        _ = ui.Look(UiStyle.Kiln);
        Assert.Equal(3, ui.LookDepth);
        Assert.Equal(3, _pushes);
        ui.RestoreLooks(inner);
        Assert.Equal(1, ui.LookDepth);
        Assert.Equal(1, _pushes);
        ui.RestoreLooks(outer);
        Assert.Equal(0, _pushes);
        // a scope disposed after Kiln took its look off does not pop another one
        var late = ui.Look(UiStyle.Game);
        ui.RestoreLooks(0);
        ui.PushLookRaw(null, UiStyle.Player);
        late.Dispose();
        Assert.Equal(1, _pushes);
        ui.RestoreLooks(0);
        // a disposed theme: the player's look instead
        theme.Dispose();
        using (ui.Theme(theme)) Assert.Equal(1, _pushes);
        Assert.Equal(0, _pushes);
        Assert.Equal((uint)UiStyle.Player, _lastStyle);
    }

    [Fact]
    public void Kiln_s_pages_follow_the_player_s_palette()
    {
        var ember = new UiPalette(Color.FromRgba(0xf4ece6ff), Color.FromRgba(0x94857aff), Color.FromRgba(0x0f0d0cf0), Color.FromRgba(0x2a2421a9),
            Color.FromRgba(0x1f1b19ff), Color.FromRgba(0xb83f05ff), Color.FromRgba(0xff6a1aff), Color.FromRgba(0xb83f05d9),
            Color.FromRgba(0xb83f05ff), Color.FromRgba(0xf9c74fff), Color.FromRgba(0xeb5046ff), Color.FromRgba(0x6ed26eff));
        KilnLook.Override = ember;
        Assert.Equal(Color.FromRgba(0xb83f05ff), GameColors.Gold);
        Assert.Equal(Color.FromRgba(0xff6a1aff), GameColors.GoldBright);
        Assert.Equal(Color.FromRgba(0x2a2421ff), GameColors.Tile);
        Assert.Equal(169, GameColors.PanelAlpha);
        Assert.Equal(Color.FromRgba(0x94857aff), GameColors.Muted);
        Assert.Equal(1f, GameColors.Backdrop);
        Assert.Equal(ember.Accent, ember[UiThemeRole.Accent]);
        // translucent: the backdrop thins with the windows' opacity
        KilnLook.Override = ember with { WindowBackground = ember.WindowBackground.WithAlpha(115) };
        Assert.Equal(0.5f, GameColors.Backdrop, 2);
        // without kiln.ui's looks (an older kiln-ui.dll): the neutral stand-in, never ember
        KilnLook.Override = null;
        Assert.Equal(UiPalette.Neutral, KilnLook.Player);
        Assert.NotEqual(ember.Accent, GameColors.GoldBright);
    }

    [Fact]
    public void Fonts_load_through_kiln_ui_and_go_with_their_mod()
    {
        Install(full: true);
        Assert.Null(UiThemes.LoadFont([0x89, (byte)'P', (byte)'N', (byte)'G']));  // kiln.ui refused it
        var f = UiThemes.LoadFont([0, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.NotNull(f);
        Assert.Equal(12, f!.Bytes);
        Assert.Null(LoadFontFile(Path.Combine(_dir, "missing.ttf")));
        var theme = UiThemes.Define("lettered", new UiThemeSpec { Font = f });
        Assert.Equal(f.Handle, UiThemes.ToNative(new UiThemeSpec { Font = f }).Font);
        var h = f.Handle;
        f.Dispose();
        Assert.Contains($"font-free {h}", Calls);
        f.Dispose();  // twice: nothing more
        Assert.Single(Calls, c => c == $"font-free {h}");
        theme.Dispose();
    }

    private static UiFontFile? LoadFontFile(string path) =>
        (UiFontFile?)typeof(UiTheme).Assembly.GetType("Kiln.Ui")!.GetMethod("LoadFont", [typeof(string)])!.Invoke(null, [path]);

    // ---- a fake kiln.ui with the looks tail ----

    private static readonly List<string> Calls = [];
    private static ulong _nextHandle = 1;
    private static UiNative.Look _lastLook;
    private static int _pushes;
    private static uint _lastStyle;

    private void Install(bool full)
    {
        Kiln.FakeUi.FakeUiTable.Install();
        Backend.Current = new Kiln.FakeUi.FakeUiTable.Backend();
        UiNative.ResetForTests();
        ref var b = ref Kiln.FakeUi.FakeUiTable.Table->Base.Base.Base;
        b.PushStyle = &PushStyle;
        b.PopStyle = &PopStyle;
        if (!full) return;
        _table = (UiNative.TableLooks*)NativeMemory.AllocZeroed((nuint)sizeof(UiNative.TableLooks));
        _table->ThemeCreate = &ThemeCreate;
        _table->ThemeFree = &ThemeFree;
        _table->PushTheme = &PushTheme;
        _table->SetLook = &SetLook;
        _table->FontLoad = &FontLoad;
        _table->FontFree = &FontFree;
        UiNative.LooksOverride = _table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ThemeCreate(ulong owner, UiNative.ThemeDef* def, ulong* h)
    {
        Calls.Add("create");
        *h = _nextHandle++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ThemeFree(ulong h)
    {
        Calls.Add($"free {h}");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte PushTheme(ulong h)
    {
        _pushes++;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PushStyle(uint s)
    {
        _lastStyle = s;
        _pushes++;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PopStyle() => _pushes--;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetLook(UiNative.Look* l)
    {
        _lastLook = *l;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong FontLoad(ulong owner, void* data, nuint len) => len >= 12 && ((byte*)data)[1] == 1 ? _nextHandle++ : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FontFree(ulong h) => Calls.Add($"font-free {h}");
}
