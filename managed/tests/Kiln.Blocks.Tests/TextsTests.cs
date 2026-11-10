// SPDX-License-Identifier: MIT
// Texts.Get (Kiln.Content): the loca table of a Kiln catalog, read lazily and bounded. Synthetic catalogs with invented texts.
using System.Text;
using System.Text.Json;
using Xunit;

namespace Kiln.Blocks.Tests;

[Collection("Texts")]
public sealed class TextsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-texts-" + Guid.NewGuid().ToString("N"));
    // hermetic: this machine may have a real catalog under HOME; no place but the test's own may hold one
    private static readonly string[] Places =
        ["KILN_CATALOG", "KILN_DATA_DIR", "XDG_DATA_HOME", "HOME", "WINE_HOST_HOME", "WINE_HOST_XDG_DATA_HOME", "WINEHOMEDIR", "LOCALAPPDATA"];
    private readonly Dictionary<string, string?> _saved = Places.ToDictionary(n => n, Environment.GetEnvironmentVariable);

    public TextsTests()
    {
        foreach (var n in Places) Environment.SetEnvironmentVariable(n, null);
        Directory.CreateDirectory(_dir);
        Texts.Reset();
    }

    public void Dispose()
    {
        foreach (var (n, v) in _saved) Environment.SetEnvironmentVariable(n, v);
        Texts.BeforeLoadForTests = null;
        Texts.Reset();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly Guid Tag = Guid.Parse("4d8d0e3a-1b0f-4783-808c-1203bdd118ca");

    private static string Id(uint id) => $"0x{id:x8}";

    private static byte[] Catalog(params (uint Id, string Text)[] loca) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        format = 1,
        game = new { steamBuild = "26430625", svn = "1287369", side = "client" },
        sections = new
        {
            items = new[] { new { name = "x", id = "0x00000001", text = "not loca" } },
            loca = loca.Select(l => new { id = Id(l.Id), text = l.Text }).ToArray(),
            perks = new[] { new { name = "p", displayName = "not loca either" } },
        },
    }));

    [Fact]
    public void A_tag_id_is_the_fnv_of_the_guid_bytes_in_the_games_order()
    {
        // FNV-1a-32 of the 16 bytes of Guid.ToByteArray(), checked against an independent computation
        var h = 0x811c9dc5u;
        foreach (var b in Tag.ToByteArray()) h = (h ^ b) * 0x01000193u;
        Assert.Equal(h, Texts.TagOf(Tag));
        Assert.NotEqual(Texts.TagOf(Tag), Texts.TagOf(Guid.Parse("4d8d0e3a-1b0f-4783-808c-1203bdd118cb")));
    }

    [Fact]
    public void Parse_reads_only_the_loca_section_and_the_build()
    {
        var table = Texts.Parse(Catalog((7, "Wood Logs"), (9, "<b>Bold</b> text")), out var build);
        Assert.NotNull(table);
        Assert.Equal(2, table!.Count);
        Assert.Equal("Wood Logs", table[7]);
        Assert.Equal("26430625 (svn 1287369)", build);
    }

    [Fact]
    public void Parse_has_no_table_without_a_loca_section()
    {
        var server = Encoding.UTF8.GetBytes("""{"format":1,"sections":{"items":[{"id":"0x00000001","text":"x"}]}}""");
        Assert.Null(Texts.Parse(server, out _));
    }

    [Fact]
    public void Parse_refuses_what_is_not_a_catalog()
    {
        Assert.ThrowsAny<JsonException>(() => Texts.Parse("[1,2]"u8, out _));
        Assert.ThrowsAny<JsonException>(() => Texts.Parse("{ not json"u8, out _));
        Assert.ThrowsAny<JsonException>(() => Texts.Parse("{\"sections\":{\"loca\":[]}} trailing"u8, out _));
        Assert.ThrowsAny<JsonException>(() => Texts.Parse("{\"sections\":{\"loca\":[{}]}"u8, out _));
    }

    [Fact]
    public void Malformed_entries_do_not_hide_later_valid_entries_or_supply_a_wrapped_id()
    {
        var json = """{"sections":{"loca":[null,5,[],{"id":"0x100000001","text":"overflow"},{"id":"0x00000007","text":"seven"},"bad",{"id":"0x00000008","text":"eight"}]}}""";
        var table = Texts.Parse(Encoding.UTF8.GetBytes(json), out _)!;
        Assert.Equal(2, table.Count);
        Assert.Equal("seven", table[7]);
        Assert.Equal("eight", table[8]);
    }

    [Fact]
    public void Parse_skips_bad_entries_and_keeps_the_good_ones()
    {
        var json = """{"sections":{"loca":[{"id":"bad","text":"x"},{"id":"0x00000005"},{"text":"no id"},{"id":"0x00000000","text":"zero"},{"id":"0x0000000a","text":"kept"},{"id":"0x0000000b","text":5}]}}""";
        var t = Texts.Parse(Encoding.UTF8.GetBytes(json), out _)!;
        Assert.Single(t);
        Assert.Equal("kept", t[10]);
    }

    [Fact]
    public void Parse_skips_a_text_longer_than_64_KiB()
    {
        var t = Texts.Parse(Catalog((1, new string('x', 70_000)), (2, new string('y', 1000))), out _)!;
        Assert.False(t.ContainsKey(1));
        Assert.Equal(1000, t[2].Length);
    }

    [Fact]
    public void Get_reads_the_catalog_the_environment_names_once_and_by_every_spelling_of_the_tag()
    {
        var file = Path.Combine(_dir, "client.json");
        File.WriteAllBytes(file, Catalog((Texts.TagOf(Tag), "Shock <b>Resistance</b>"), (0x12345678, "by id")));
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);   // a folder: client.json inside it
        Assert.True(Texts.Available);
        Assert.Equal("Shock <b>Resistance</b>", Texts.Get(Tag));
        Assert.Equal("Shock Resistance", Texts.GetPlain(Tag));
        Assert.Equal("by id", Texts.Get(0x12345678u));
        Assert.Equal("by id", Texts.Get("0x12345678"));
        Assert.Equal("by id", Texts.Get("305419896"));
        Assert.Equal("Shock <b>Resistance</b>", Texts.Get(Tag.ToString()));
        Assert.Null(Texts.Get("not a tag"));
        Assert.Null(Texts.Get(Guid.Empty));
        Assert.Null(Texts.Get(0u));
        Assert.Null(Texts.Get(0xdeadbeefu));
        var st = Texts.Status;
        Assert.True(st.Loaded);
        Assert.Equal(2, st.Count);
        Assert.Equal(file, st.Source);
        Assert.Equal("26430625 (svn 1287369)", st.Build);
        // read once: changing the file does not change the table
        File.WriteAllBytes(file, Catalog((1, "changed")));
        Assert.Equal("by id", Texts.Get(0x12345678u));
    }

    [Fact]
    public void A_file_path_in_the_environment_works_too()
    {
        var file = Path.Combine(_dir, "mine.json");
        File.WriteAllBytes(file, Catalog((3, "three")));
        Environment.SetEnvironmentVariable("KILN_CATALOG", file);
        Assert.Equal("three", Texts.Get(3u));
    }

    [Fact]
    public void No_catalog_means_no_texts_with_a_reason_and_no_exception()
    {
        Environment.SetEnvironmentVariable("KILN_CATALOG", Path.Combine(_dir, "nowhere"));
        Assert.Null(Texts.Get(7u));
        Assert.False(Texts.Available);
        var st = Texts.Status;
        Assert.False(st.Loaded);
        Assert.Contains("no client catalog found", st.Reason);
        Assert.Contains("nowhere", st.Reason);
    }

    [Fact]
    public void A_server_catalog_without_loca_says_so()
    {
        File.WriteAllText(Path.Combine(_dir, "client.json"), """{"format":1,"sections":{"items":[]}}""");
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);
        Assert.Null(Texts.Get(7u));
        Assert.Contains("no 'loca' section", Texts.Status.Reason);
    }

    [Fact]
    public void A_broken_catalog_is_no_texts_not_an_exception()
    {
        File.WriteAllText(Path.Combine(_dir, "client.json"), "{ not json");
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);
        Assert.Null(Texts.Get(7u));
        Assert.False(Texts.Status.Loaded);
    }

    [Fact]
    public void The_search_order_starts_with_the_environment_and_includes_wine_s_names_for_the_linux_home()
    {
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);
        var files = Texts.CatalogFiles().ToList();
        Assert.Equal(Path.Combine(_dir, "client.json"), files[0]);
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(files, f => Assert.EndsWith(".json", f));
    }

    [Fact]
    public async Task Preload_loads_in_the_background()
    {
        File.WriteAllBytes(Path.Combine(_dir, "client.json"), Catalog((3, "three")));
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);
        Assert.True(await Texts.Preload());
        Assert.Equal("three", Texts.Get(3u));
    }

    [Fact]
    public async Task Preload_retries_a_failed_single_flight_after_the_cooldown_and_after_a_successful_Get()
    {
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);
        var first = Texts.Preload();
        Assert.False(await first);
        File.WriteAllBytes(Path.Combine(_dir, "client.json"), Catalog((3, "three")));
        // Move only the failure timestamp beyond the real 60-second cooldown, without waiting in a unit test.
        typeof(Texts).GetField("_failedAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, System.Diagnostics.Stopwatch.GetTimestamp() - 61 * System.Diagnostics.Stopwatch.Frequency);
        Task<bool> retry;
        var gate = typeof(Texts).GetField("Lock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        lock (gate)
        {
            retry = Texts.Preload();
            Assert.Same(retry, Texts.Preload());  // the worker cannot enter Table until this lock is released
        }
        Assert.NotSame(first, retry);
        Assert.True(await retry);
        Assert.Equal("three", Texts.Get(3u));

        Texts.Reset();
        File.Delete(Path.Combine(_dir, "client.json"));
        Assert.False(await Texts.Preload());
        File.WriteAllBytes(Path.Combine(_dir, "client.json"), Catalog((3, "three")));
        typeof(Texts).GetField("_failedAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, System.Diagnostics.Stopwatch.GetTimestamp() - 61 * System.Diagnostics.Stopwatch.Frequency);
        Assert.Equal("three", Texts.Get(3u));
        Assert.True(await Texts.Preload());
    }

    [Fact]
    public async Task Get_does_not_wait_for_a_preload_that_is_still_parsing()
    {
        File.WriteAllBytes(Path.Combine(_dir, "client.json"), Catalog((3, "three")));
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);
        using var parsing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Texts.BeforeLoadForTests = () => { parsing.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var preload = Texts.Preload();
        Assert.True(parsing.Wait(TimeSpan.FromSeconds(5)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(Texts.Get(3u));                 // the parse is held up: Get answers at once, without a table
        Assert.False(Texts.Available);
        Assert.Contains("still loading", Texts.Status.Reason);   // not "not loaded yet": the load is on its way
        Assert.Same(preload, Texts.Preload());      // one load at a time
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
        release.Set();
        Assert.True(await preload);
        Assert.Equal("three", Texts.Get(3u));
    }

    [Fact]
    public void A_catalog_without_loca_is_read_again_only_when_it_changed()
    {
        var file = Path.Combine(_dir, "client.json");
        File.WriteAllText(file, """{"format":1,"sections":{"items":[]}}""");
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);
        var loads = Texts.LoadsForTests;
        Assert.Null(Texts.Get(3u));
        Assert.Equal(loads + 1, Texts.LoadsForTests);
        var failedAt = typeof(Texts).GetField("_failedAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        void PastCooldown() => failedAt.SetValue(null, System.Diagnostics.Stopwatch.GetTimestamp() - 61 * System.Diagnostics.Stopwatch.Frequency);
        PastCooldown();
        Assert.Null(Texts.Get(3u));                 // past the minute, but the file is the same: not read again
        Assert.Equal(loads + 1, Texts.LoadsForTests);
        Assert.Contains("no 'loca' section", Texts.Status.Reason);
        File.WriteAllBytes(file, Catalog((3, "three")));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Null(Texts.Get(3u));                 // changed, but the minute since the last check has not passed
        PastCooldown();
        Assert.Equal("three", Texts.Get(3u));
        Assert.Equal(loads + 2, Texts.LoadsForTests);
    }

    [Fact]
    public void A_catalog_that_could_not_be_opened_is_tried_again_after_the_minute_though_it_did_not_change()
    {
        var file = Path.Combine(_dir, "client.json");
        File.WriteAllBytes(file, Catalog((3, "three")));
        Environment.SetEnvironmentVariable("KILN_CATALOG", _dir);
        var failedAt = typeof(Texts).GetField("_failedAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var loads = Texts.LoadsForTests;
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // another process has it open
            Assert.Null(Texts.Get(3u));
        Assert.Equal(loads + 1, Texts.LoadsForTests);
        Assert.False(Texts.Status.Loaded);
        failedAt.SetValue(null, System.Diagnostics.Stopwatch.GetTimestamp() - 61 * System.Diagnostics.Stopwatch.Frequency);
        Assert.Equal("three", Texts.Get(3u));        // same size and time as before: a read error is not a reason to give up
        Assert.Equal(loads + 2, Texts.LoadsForTests);
    }
}

[CollectionDefinition("Texts", DisableParallelization = true)]
public sealed class TextsCollection { }
