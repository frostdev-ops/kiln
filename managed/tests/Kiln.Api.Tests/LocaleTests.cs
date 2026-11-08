// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>Mod localisation: locales/&lt;language&gt;.json, Mod.Text, Game.Language, the English fallback.</summary>
public sealed class LocaleTests : KilnTest
{
    private string LocalesOf(string mod) => Path.Combine(Root, "mods", mod, "locales");

    private void Write(string mod, string file, string json)
    {
        Directory.CreateDirectory(LocalesOf(mod));
        File.WriteAllText(Path.Combine(LocalesOf(mod), file), json);
    }

    private ModContext ModWith(string id, params (string File, string Json)[] files)
    {
        foreach (var (file, json) in files) Write(id, file, json);
        return NewMod(id);
    }

    public override void Dispose()
    {
        GameLanguageSource.ResetForTests();
        base.Dispose();
    }

    private static void Language(string? tag) => GameLanguageSource.Override = () => tag;

    [Theory]
    [InlineData("de-de", "de-de,de,en,en-us")]
    [InlineData("de", "de,en,en-us")]
    [InlineData("pt_BR", "pt-br,pt,en,en-us")]
    [InlineData("es-mx", "es-mx,es-419,es,en,en-us")]
    [InlineData("es-es", "es-es,es,en,en-us")]
    [InlineData("fr-ca", "fr-ca,fr,en,en-us")]
    [InlineData("nb-no", "nb-no,nb,no,en,en-us")]
    [InlineData("no-no", "no-no,no,nb,en,en-us")]
    [InlineData("zh-TW", "zh-tw,zh,en,en-us")]
    [InlineData("en-us", "en-us,en")]
    [InlineData("en-gb", "en-gb,en,en-us")]
    [InlineData("", "en-us,en")]
    public void Languages_are_tried_from_the_closest_to_english(string language, string expected) =>
        Assert.Equal(expected.Split(','), ModLocale.LookupOrder(language));

    [Fact]
    public void A_dedicated_server_speaks_english()
    {
        Assert.Equal("en-us", Game.Language);
        var mod = ModWith("lcserver", ("en.json", """{ "hi": "Hello" }"""), ("de.json", """{ "hi": "Hallo" }"""));
        Assert.Equal("Hello", mod.Locale.Get("hi"));
        Assert.Equal("Hallo", mod.Locale.GetIn("de-de", "hi"));  // asked for a language explicitly
    }

    [Fact]
    public void The_game_language_picks_the_file_and_the_main_variety_comes_next()
    {
        var mod = ModWith("lcpick",
            ("en.json", """{ "hi": "Hello", "only.en": "English only", "bye": "Bye" }"""),
            ("de.json", """{ "hi": "Hallo", "bye": "Tschuess" }"""),
            ("de-AT.json", """{ "hi": "Servus" }"""));
        Language("de-at");
        Assert.Equal("de-at", Game.Language);
        Assert.Equal("Servus", mod.Locale.Get("hi"));
        Assert.Equal("Tschuess", mod.Locale.Get("bye"));       // de-at has no 'bye': its main variety does
        Assert.Equal("English only", mod.Locale.Get("only.en"));  // nobody translated it: English
        Language("de-de");
        Assert.Equal("Hallo", mod.Locale.Get("hi"));
        Language("ja-jp");
        Assert.Equal("Hello", mod.Locale.Get("hi"));           // no Japanese at all
        Language("en-us");
        Assert.Equal("Hello", mod.Locale.Get("hi"));
    }

    [Fact]
    public void A_missing_key_shows_as_the_key_and_is_logged_once()
    {
        var mod = ModWith("lcmiss", ("en.json", """{ "a": "A" }"""));
        Assert.Equal("nope.key", mod.Locale.Get("nope.key"));
        Assert.Equal("nope.key", mod.Locale.Get("nope.key"));
        Assert.Equal(1, Backend.Lines.Count(l => l.Contains("text 'nope.key' of mod lcmiss")));
        Assert.True(Backend.Logged("showing the key"));
        Assert.False(mod.Locale.Has("nope.key"));
        Assert.True(mod.Locale.Has("a"));
        Assert.True(mod.Locale.TryGet("a", out var a) && a == "A");
        Assert.False(mod.Locale.TryGet("nope.key", out _));
    }

    [Fact]
    public void A_mod_without_locales_still_answers_with_keys()
    {
        var mod = NewMod("lcnone");
        Assert.Empty(mod.Locale.Languages);
        Assert.Equal("some.key", mod.Locale.Get("some.key"));
        Assert.Contains(Backend.Lines, l => l.Contains("has no locales/*.json"));
    }

    [Fact]
    public void Nested_objects_make_dotted_keys_and_comments_are_fine()
    {
        var mod = ModWith("lcnest", ("en.json", """
            // the shop
            {
              "shop": { "buy": "Buy", "sell": { "all": "Sell all", }, },
              "n": 5,
              "flag": true,
              "list": [1, 2],
            }
            """));
        Assert.Equal("Buy", mod.Locale.Get("shop.buy"));
        Assert.Equal("Sell all", mod.Locale.Get("shop.sell.all"));
        Assert.Equal("5", mod.Locale.Get("n"));
        Assert.Equal("True", mod.Locale.Get("flag"));
        Assert.DoesNotContain("list", mod.Locale.Keys);
        Assert.True(Backend.Logged("'list' is not a text"));
        Assert.Equal(["flag", "n", "shop.buy", "shop.sell.all"], mod.Locale.Keys);
    }

    [Fact]
    public void Placeholders_take_positions_names_formats_and_escapes()
    {
        var mod = ModWith("lcfill", ("en.json", """
            {
              "pos": "{0} of {1}",
              "named": "Hello {name}, you have {count:N0} coins",
              "brace": "{{literal}} {0}",
              "missing": "{0} and {1} and {who}",
              "fixed": "no placeholders",
              "once": "{0}{0}",
              "open": "a { b"
            }
            """));
        var l = mod.Locale;
        Assert.Equal("3 of 9", l.Get("pos", 3, 9));
        Assert.Equal("Hello Ada, you have 12,345 coins", l.Get("named", new { name = "Ada", count = 12345 }));
        Assert.Equal("Hello Bo, you have 7 coins", l.Get("named", new Dictionary<string, object?> { ["NAME"] = "Bo", ["count"] = 7 }));
        Assert.Equal("{literal} x", l.Get("brace", "x"));
        Assert.Equal("1 and {1} and {who}", l.Get("missing", 1));  // what has no value stays as written
        Assert.Equal("no placeholders", l.Get("fixed", "ignored"));
        Assert.Equal("zz", l.Get("once", "z"));
        Assert.Equal("a { b", l.Get("open"));
        Assert.Equal("{name}, {count:N0}", ModLocale.Fill("{name}, {count:N0}", []));
    }

    [Fact]
    public void Numbers_are_written_the_same_in_every_culture()
    {
        var mod = ModWith("lcculture", ("en.json", """{ "x": "{0:0.00}" }"""));
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.50", mod.Locale.Get("x", 1.5));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Files_with_bad_names_or_content_are_skipped_with_a_warning()
    {
        var mod = ModWith("lcbad",
            ("en.json", """{ "a": "A" }"""),
            ("not a tag.json", """{ "a": "x" }"""),
            ("de.json", "{ oops"),
            ("fr.json", "[1, 2]"));
        Assert.Equal(["en"], mod.Locale.Languages);
        Assert.True(Backend.Logged("not a tag.json"));
        Assert.True(Backend.Logged("de.json"));
        Assert.True(Backend.Logged("fr.json"));
        Language("de-de");
        Assert.Equal("A", mod.Locale.Get("a"));
    }

    [Fact]
    public void Two_files_for_one_language_keep_the_first()
    {
        var mod = ModWith("lcdup", ("pt-BR.json", """{ "a": "first" }"""), ("pt_br.json", """{ "a": "second" }"""));
        Assert.Equal(["pt-br"], mod.Locale.Languages);
        Language("pt-br");
        Assert.Equal("first", mod.Locale.Get("a"));
        Assert.True(Backend.Logged("has a file already"));
    }

    [Fact]
    public void Missing_lists_what_a_translator_still_owes()
    {
        var mod = ModWith("lcowe",
            ("en.json", """{ "a": "A", "b": "B", "c": { "d": "D" } }"""),
            ("de.json", """{ "a": "Ah", "extra": "x" }"""));
        Assert.Equal(["b", "c.d"], mod.Locale.Missing("de-de"));  // the tag of the file or its variety: the file's own keys count
        Assert.Equal(["a", "b", "c.d"], mod.Locale.Missing("fr"));
        Assert.Empty(mod.Locale.Missing("en"));
        Assert.Equal(["a", "b", "c.d", "extra"], mod.Locale.Keys);
    }

    [Fact]
    public void Reload_reads_the_files_again()
    {
        var mod = ModWith("lcreload", ("en.json", """{ "a": "one" }"""));
        Assert.Equal("one", mod.Locale.Get("a"));
        Write("lcreload", "en.json", """{ "a": "two" }""");
        Assert.Equal("one", mod.Locale.Get("a"));  // read when first needed
        mod.Locale.Reload();
        Assert.Equal("two", mod.Locale.Get("a"));
        Write("lcreload", "de.json", """{ "a": "zwei" }""");
        mod.Locale.Reload();
        Assert.Equal(["de", "en"], mod.Locale.Languages);
    }

    [Fact]
    public void A_mod_that_never_loaded_from_disk_has_an_empty_locale()
    {
        var ctx = HostAccess.CreateContext(new ModInfo("lcmem", "m", "1.0.0", ""), 999, Path.Combine(Root, "data", "lcmem"));
        Assert.Equal("", ctx.Locale.Directory);
        Assert.Equal("k", ctx.Locale.Get("k"));
        ctx.Retire();
    }

    private sealed class TextMod : Mod
    {
        public string Hi() => Text("hi");
        public string Price(int n) => Text("price", n);
        public string Named() => Text("named", new { who = "Ada" });
        public string InGerman() => TextIn("de", "hi");
    }

    [Fact]
    public void Mod_Text_answers_for_the_calling_mod_in_the_game_language()
    {
        var ctx = ModWith("lctext", ("en.json", """{ "hi": "Hello", "price": "{0} gold", "named": "Hi {who}" }"""), ("de.json", """{ "hi": "Hallo" }"""));
        var mod = new TextMod();
        HostAccess.Bind(mod, ctx);
        Assert.Equal("Hello", mod.Hi());
        Assert.Equal("12 gold", mod.Price(12));
        Assert.Equal("Hi Ada", mod.Named());
        Assert.Equal("Hallo", mod.InGerman());
        Language("de-de");
        Assert.Equal("Hallo", mod.Hi());
        Assert.Equal("12 gold", mod.Price(12));  // not translated: English
    }

    [Fact]
    public void Texts_can_be_read_from_any_thread()
    {
        var mod = ModWith("lcthreads", ("en.json", """{ "a": "A", "b": "B {0}" }"""), ("de.json", """{ "a": "Ah" }"""));
        Language("de-de");
        var results = new string[8];
        Parallel.For(0, results.Length, i => results[i] = mod.Locale.Get("a") + mod.Locale.Get("b", i) + mod.Locale.Get("zz"));
        Assert.All(results.Select((r, i) => (r, i)), x => Assert.Equal($"AhB {x.i}zz", x.r));
    }
}
