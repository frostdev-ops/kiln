using Xunit;

namespace Kiln.KilnTextTests;

// Kiln's own in-game texts (KilnText, docs/kiln/translating-game.md): English built in, the files of <kiln>/locales, the
// lookup (the language, its main variety, English), placeholders, plurals, the setting (auto follows the game's language,
// a tag forces one) and the live switch.
[Collection(Kiln.FakeUi.UiSerial.Name)]
public sealed class KilnTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-text-" + Guid.NewGuid().ToString("N"));

    public KilnTextTests()
    {
        Directory.CreateDirectory(_dir);
        KilnText.ResetForTests();
        KilnText.Directory = _dir;
        GameLanguageSource.Override = () => "en-us";
    }

    public void Dispose()
    {
        KilnText.ResetForTests();
        GameLanguageSource.ResetForTests();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private void File(string name, string json) => System.IO.File.WriteAllText(Path.Combine(_dir, name), json);

    [Fact]
    public void English_is_built_in_without_any_file()
    {
        Assert.Equal("Mods", KilnText.T("native.menu.mods"));
        Assert.Equal("English", KilnText.NameOf("en"));
        Assert.Equal(["en"], KilnText.Languages());
        Assert.Equal("Kiln 0.2.0 - 3 Mods Loaded", KilnText.Plural("native.footer.loaded", 3, ("version", "0.2.0")));
        Assert.Equal("Kiln 0.2.0 - 1 Mod Loaded", KilnText.Plural("native.footer.loaded", 1, ("version", "0.2.0")));
    }

    [Fact]
    public void A_missing_key_is_the_key_and_is_reported_once()
    {
        Assert.Equal("no.such.key", KilnText.T("no.such.key"));
        Assert.True(KilnText.WasReported("no.such.key"));
        Assert.False(KilnText.Has("no.such.key"));
        Assert.True(KilnText.Has("native.menu.mods"));
    }

    [Fact]
    public void A_forced_language_reads_its_file_then_its_main_variety_then_english()
    {
        File("zh-CN.json", """{ "language": { "name": "简体中文" }, "native": { "menu": { "mods": "模组" } } }""");
        File("zh.json", """{ "native": { "fallback": { "close": "按 {key} 关闭。" } } }""");
        KilnText.Configure("zh-CN");
        Assert.Equal("zh-cn", KilnText.Language);
        Assert.Equal("模组", KilnText.T("native.menu.mods"));                                  // the file of zh-cn
        Assert.Equal("按 F1 关闭。", KilnText.T("native.fallback.close", ("key", "F1")));      // its main variety
        Assert.StartsWith("Kiln's managed host", KilnText.T("native.fallback.body"), StringComparison.Ordinal);  // English
        Assert.Equal("简体中文", KilnText.NameOf("zh-cn"));
        Assert.Equal(["en", "zh", "zh-cn"], KilnText.Languages());
    }

    [Fact]
    public void Auto_follows_the_game_language_and_switches_live()
    {
        File("zh-CN.json", """{ "native": { "menu": { "mods": "模组" } } }""");
        KilnText.Configure("auto");
        var changes = 0;
        KilnText.Changed += () => changes++;
        Assert.Equal("Mods", KilnText.T("native.menu.mods"));
        var g0 = KilnText.Generation;
        KilnText.Refresh("zh-cn");                     // the player switched the game to Chinese
        Assert.Equal("模组", KilnText.T("native.menu.mods"));
        Assert.NotEqual(g0, KilnText.Generation);
        Assert.Equal(1, changes);
        KilnText.Refresh("zh-cn");                     // nothing changed: no rebuild, no event
        Assert.Equal(1, changes);
        KilnText.Configure("en");                      // forced English while the game stays Chinese
        Assert.Equal("Mods", KilnText.T("native.menu.mods"));
        KilnText.Refresh("zh-cn");
        Assert.Equal("Mods", KilnText.T("native.menu.mods"));
        KilnText.Configure("AUTO");
        Assert.Equal("模组", KilnText.T("native.menu.mods"));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Placeholders_positional_named_formats_and_braces()
    {
        File("de.json", """
            // comments and trailing commas are fine, as in a mod's locales
            { "t": { "pos": "{1} vor {0}", "named": "{name} hat {count:N0} Mods", "brace": "{{wörtlich}} {name}", "keep": "{missing}", }, }
            """);
        KilnText.Configure("de");
        Assert.Equal("b vor a", KilnText.T("t.pos", "a", "b"));
        Assert.Equal("Ada hat 12,345 Mods", KilnText.T("t.named", ("name", "Ada"), ("count", 12345)));
        Assert.Equal("{wörtlich} x", KilnText.T("t.brace", ("name", "x")));
        Assert.Equal("{missing}", KilnText.T("t.keep", ("name", "x")));  // a placeholder without a value stays as written
    }

    [Fact]
    public void A_translation_replaces_a_plural_group_whole()
    {
        // Chinese has no singular: its .other answers 1 too, and the English .one never mixes in
        File("zh-CN.json", """{ "native": { "footer": { "loaded": { "other": "Kiln {version} - 已加载 {count} 个模组" } } } }""");
        KilnText.Configure("zh-CN");
        Assert.Equal("Kiln 1.0 - 已加载 1 个模组", KilnText.Plural("native.footer.loaded", 1, ("version", "1.0")));
        Assert.Equal("Kiln 1.0 - 已加载 5 个模组", KilnText.Plural("native.footer.loaded", 5, ("version", "1.0")));
        Assert.False(KilnText.Has("native.footer.loaded.one"));
        // French: 0 and 1 are singular
        File("fr.json", """{ "native": { "footer": { "loaded": { "one": "{count} mod", "other": "{count} mods" } } } }""");
        KilnText.Reload();
        KilnText.Configure("fr-FR");
        Assert.Equal("0 mod", KilnText.Plural("native.footer.loaded", 0));
        Assert.Equal("2 mods", KilnText.Plural("native.footer.loaded", 2));
        Assert.Equal("other", KilnText.PluralForm("ja-jp", 1));
        Assert.Equal("one", KilnText.PluralForm("en-us", 1));
        Assert.Equal("other", KilnText.PluralForm("en-us", 0));
        Assert.Equal("one", KilnText.PluralForm("pt-br", 0));
        Assert.Equal("other", KilnText.PluralForm("pt-pt", 0));
    }

    [Fact]
    public void A_broken_file_is_skipped_and_english_stays()
    {
        File("zh-CN.json", "{ not json");
        File("de.json", "[1, 2]");
        KilnText.Configure("zh-CN");
        Assert.Equal("Mods", KilnText.T("native.menu.mods"));
        KilnText.Configure("de");
        Assert.Equal("Mods", KilnText.T("native.menu.mods"));
    }

    [Fact]
    public void The_native_keys_go_to_kiln_ui_in_the_language_in_use()
    {
        File("zh-CN.json", """{ "native": { "menu": { "mods": "模组" } }, "other": { "x": "y" } }""");
        KilnText.Configure("zh-CN");
        var native = KilnText.Native();
        Assert.All(native, p => Assert.StartsWith(KilnText.NativePrefix, p.Key, StringComparison.Ordinal));
        Assert.Contains(native, p => p.Key == "native.menu.mods" && p.Value == "模组");
        Assert.Contains(native, p => p.Key == "native.fallback.body");  // English where the translation has none
        Assert.DoesNotContain(native, p => p.Key == "other.x");
    }

    [Fact]
    public void Until_the_overlay_has_the_glyphs_its_texts_are_english_and_the_games_are_not()
    {
        File("zh-CN.json", """{ "native": { "menu": { "mods": "模组" } }, "settings": { "look": { "look": "外观" } } }""");
        KilnText.Glyphs(false);                      // kiln.ui is still reading the game's Chinese font
        KilnText.Configure("zh-CN");
        Assert.True(KilnText.Gated);
        Assert.Equal("Look", KilnText.T("settings.look.look"));                          // the overlay: English, no boxes
        Assert.Contains(KilnText.Native(), p => p.Key == "native.menu.mods" && p.Value == "模组");  // the game draws these
        var changes = 0;
        KilnText.Changed += () => changes++;
        KilnText.Glyphs(true);                       // the font joined the atlas
        Assert.False(KilnText.Gated);
        Assert.Equal("外观", KilnText.T("settings.look.look"));
        Assert.Equal(1, changes);
        KilnText.Glyphs(false);                      // a Latin language never waits
        KilnText.Configure("de");
        Assert.False(KilnText.Gated);
        Assert.True(KilnText.NeedsGlyphs("ja-jp") && KilnText.NeedsGlyphs("ko") && !KilnText.NeedsGlyphs("ru-ru"));
    }

    [Fact]
    public void Tags_are_written_the_canonical_way()
    {
        Assert.Equal("zh-CN", KilnText.Canonical("zh_cn"));
        Assert.Equal("es-419", KilnText.Canonical("ES-419"));
        Assert.Equal("zh-Hans", KilnText.Canonical("zh-hans"));
        Assert.Equal("en", KilnText.Canonical(" EN "));
    }
}
