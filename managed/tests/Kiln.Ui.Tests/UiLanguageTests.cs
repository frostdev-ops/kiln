using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.KilnTextTests;

// The language of Kiln's own UI (UiLanguage): kiln.json ui.language read and written with the look, the texts kiln.ui
// draws handed over (set_texts) at start and at each change, the Language entry's choices.
[Collection(Kiln.FakeUi.UiSerial.Name)]
public sealed unsafe class UiLanguageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-lang-" + Guid.NewGuid().ToString("N"));
    private static readonly List<(string Language, Dictionary<string, string> Texts)> Pushed = [];

    public UiLanguageTests()
    {
        Directory.CreateDirectory(_dir);
        Pushed.Clear();
        KilnText.ResetForTests();
        KilnText.Directory = _dir;
        GameLanguageSource.Override = () => "en-us";
        Directory.CreateDirectory(Path.Combine(_dir, "cfg"));
        PlayerLook.PathOverride = Path.Combine(_dir, "cfg", "kiln.json");
    }

    public void Dispose()
    {
        UiLanguage.ResetForTests();
        PlayerLook.ResetForTests();
        LookPage.ResetForTests();
        UiNative.ResetForTests();
        KilnText.ResetForTests();
        GameLanguageSource.ResetForTests();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetTexts(UiNative.Texts* t)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < t->Count; i++) d[t->Keys[i].ToString()] = t->Values[i].ToString();
        Pushed.Add((t->Language.ToString(), d));
        return t->Size == (uint)sizeof(UiNative.Texts) ? 0 : 3;
    }

    [Fact]
    public void Texts_go_to_kiln_ui_at_start_and_at_each_change()
    {
        File.WriteAllText(Path.Combine(_dir, "zh-CN.json"), """{ "native": { "menu": { "mods": "模组" }, "footer": { "loaded": { "other": "已加载 {count} 个模组" } } } }""");
        UiNative.SetTextsOverride = &FakeSetTexts;
        UiLanguage.Start();
        Assert.Single(Pushed);
        Assert.Equal("en-us", Pushed[0].Language);
        Assert.Equal("Mods", Pushed[0].Texts["native.menu.mods"]);
        Assert.All(Pushed[0].Texts.Keys, k => Assert.StartsWith("native.", k, StringComparison.Ordinal));
        PlayerLook.Change(PlayerLook.Current with { Language = "zh-cn" });  // Kiln's menu: shown at once
        Assert.Equal(2, Pushed.Count);
        Assert.Equal("zh-cn", Pushed[1].Language);
        Assert.Equal("模组", Pushed[1].Texts["native.menu.mods"]);
        // Chinese has no singular form: kiln.ui's ".one for 1" gets the .other text
        Assert.Equal("已加载 {count} 个模组", Pushed[1].Texts["native.footer.loaded.one"]);
        Assert.Equal("已加载 {count} 个模组", Pushed[1].Texts["native.footer.loaded.other"]);
        PlayerLook.Change(PlayerLook.Current with { Language = "auto" });  // the game's language again (English here)
        Assert.Equal(3, Pushed.Count);
        Assert.Equal("Mods", Pushed[2].Texts["native.menu.mods"]);
        UiLanguage.Frame();  // the game's language did not change: nothing new
        Assert.Equal(3, Pushed.Count);
    }

    [Fact]
    public void The_setting_is_read_written_and_kept_out_of_files_that_never_chose()
    {
        Assert.Equal("auto", PlayerLook.Parse("{}").Language);
        Assert.Equal("zh-CN", PlayerLook.Parse("""{ "ui": { "language": "zh_cn" } }""").Language);
        Assert.Equal("auto", PlayerLook.Parse("""{ "ui": { "language": "  AUTO " } }""").Language);
        Assert.Equal("auto", PlayerLook.Parse("""{ "ui": { "language": 3 } }""").Language);
        // a file that never chose a language keeps its lines as they were when the look changes
        var plain = "{\n  \"ui\": { \"look\": \"game\" }\n}\n";
        var kiln = LookSettings.Default with { Look = "kiln" };
        Assert.DoesNotContain("language", PlayerLook.Splice(plain, kiln), StringComparison.Ordinal);
        // a chosen language is written, and read back
        var zh = PlayerLook.Splice(plain, kiln with { Language = "zh-CN" });
        Assert.Contains("\"language\": \"zh-CN\"", zh, StringComparison.Ordinal);
        Assert.Equal("zh-CN", PlayerLook.Parse(zh).Language);
        // back to auto in a file that has the key: written as auto
        Assert.Equal("auto", PlayerLook.Parse(PlayerLook.Splice(zh, kiln)).Language);
        Assert.Contains("\"language\": \"auto\"", PlayerLook.Splice(zh, kiln), StringComparison.Ordinal);
    }

    [Fact]
    public void The_language_entry_lists_auto_english_and_each_file_in_its_own_name()
    {
        File.WriteAllText(Path.Combine(_dir, "zh-CN.json"), """{ "language": { "name": "简体中文" } }""");
        File.WriteAllText(Path.Combine(_dir, "de.json"), "{}");
        var (labels, ids) = LanguagePage.Choices("auto");
        Assert.Equal(["auto", "en", "de", "zh-CN"], ids);
        Assert.Equal("Automatic (the game's language: English)", labels[0]);
        Assert.Equal(["English", "de", "简体中文"], labels[1..]);
        // the player's setting while no file has it stays listed
        (labels, ids) = LanguagePage.Choices("ko-KR");
        Assert.Equal("ko-KR", ids[^1]);
        Assert.Equal("ko-KR (Kiln has no texts in this language)", labels[^1]);
    }

    [Fact]
    public void The_overlay_has_a_languages_glyphs_when_kiln_ui_says_so_for_that_language()
    {
        Assert.True(UiLanguage.GlyphsReady("de-de", (UiNative.GlyphsLoading, 0)));           // Latin needs nothing
        Assert.False(UiLanguage.GlyphsReady("zh-cn", (UiNative.GlyphsLoading, 20)));
        Assert.True(UiLanguage.GlyphsReady("zh-cn", (UiNative.GlyphsReady, 20)));
        Assert.False(UiLanguage.GlyphsReady("zh-cn", (UiNative.GlyphsReady, 11)));             // Japanese glyphs, still
        Assert.False(UiLanguage.GlyphsReady("ja", (UiNative.GlyphsFailed, 11)));
        Assert.True(UiLanguage.GlyphsReady("ko-kr", null));                                    // a kiln.ui that does not say
        Assert.Equal(21, UiLanguage.LanguageId("zh-Hant"));
        Assert.Equal(21, UiLanguage.LanguageId("zh-HK"));
        Assert.Equal(20, UiLanguage.LanguageId("zh"));
        Assert.Equal(13, UiLanguage.LanguageId("ko"));
        Assert.Equal(0, UiLanguage.LanguageId("en-us"));
        // the same numbers as kiln.ui's (font_group.h) and the game's keen::LanguageId (GameLanguage)
        Assert.Equal("zh-cn", GameLanguage.KeyOf(20));
        Assert.Equal("zh-tw", GameLanguage.KeyOf(21));
        Assert.Equal("ja-jp", GameLanguage.KeyOf(11));
        Assert.Equal("ko-kr", GameLanguage.KeyOf(13));
    }

    [Fact]
    public void Kiln_ui_s_layout_of_the_texts_tail()
    {
        Assert.Equal(40, sizeof(UiNative.Texts));
        Assert.Equal(8, (int)Marshal.OffsetOf<UiNative.Texts>(nameof(UiNative.Texts.Language)));
        Assert.Equal(24, (int)Marshal.OffsetOf<UiNative.Texts>(nameof(UiNative.Texts.Keys)));
        Assert.Equal(1008, (int)Marshal.OffsetOf<UiNative.TableTexts>(nameof(UiNative.TableTexts.SetTexts)));
        var h = File.ReadAllText(Path.Combine(RepoRoot(), "native", "core", "abi", "services", "kiln_ui.h"));
        Assert.Contains("KilnResult(KILN_CALL* set_texts)(const KilnUiTexts* texts);\n} KilnUiApi;", h, StringComparison.Ordinal);
    }

    private static string RepoRoot([CallerFilePath] string source = "")
    {
        var d = Path.GetDirectoryName(source);
        while (d is not null && !File.Exists(Path.Combine(d, "VERSION"))) d = Path.GetDirectoryName(d);
        return d!;
    }
}
