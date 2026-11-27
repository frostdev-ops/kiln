// SPDX-License-Identifier: MIT
// A ShroudForge mod's news (shroudforge.notifications.publish) in the Kiln menu when Kiln hosts ShroudForge: the
// notices its runtime keeps in state.json "events" (fork pin create_mod_api.rs + write_notification_event.rs) read
// back, drawn in the mod's details, and a mod's toggle_modloader() opening the page on the mod that just published.
// netjunk, 2026-11-26: Altar Cleanup's "Ask before cleaning up" scans, publishes what it found with "press the key
// again within 60 s", and opens ShroudForge's window; Kiln opened its page with none of that on it. No game.
using Kiln.FakeUi;
using Xunit;

namespace Kiln.ShroudForgeNewsTests;

[Collection(UiSerial.Name)]
public sealed class ShroudForgeNewsTests : IDisposable
{
    private readonly IKilnBackend _previous;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-sfnews-" + Guid.NewGuid().ToString("N"));
    private readonly UiDraw _ui = new();
    private string StatePath => Path.Combine(_root, "shroudforge", "state.json");

    public ShroudForgeNewsTests()
    {
        _previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        ShroudForgeUi.ResetForTests();
        ShroudForgeNews.ResetForTests();
        ShroudForgeNews.StatePath = () => StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        _ui.Frame.DpiScale = 1;
        _ui.Frame.Width = 1920;
        _ui.Frame.Height = 1080;
        ModManagerHost.Page.Section = ModManagerSection.Mods;
        ModManagerHost.Page.Filter = EngineFilter.All;
    }

    public void Dispose()
    {
        ModManagerHost.Page.CancelRequest();
        ModManagerHost.Page.Filter = EngineFilter.All;
        ModManagerHost.Page.Update(CatalogSnapshot.Empty with { Version = -2 }, false);
        ShroudForgeNews.ResetForTests();
        ShroudForgeUi.ResetForTests();
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeUiTable.Free();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    // What ShroudForge's runtime writes for notifications.publish{id, title, message, level}: the state file's "events"
    // section, keyed "<mod>-<id>.json", the notice's id namespaced "<mod>.<id>", updatedAt in Unix seconds.
    private static string Event(string mod, string id, string title, string message, long at, string level = "info") =>
        $$"""
        "{{mod}}-{{id}}.json": {"id":"{{mod}}.{{id}}","modId":"{{mod}}","title":"{{title}}","message":"{{message}}","level":"{{level}}","actionUrl":null,"updatedAt":{{at}}}
        """;

    private void State(params string[] events) =>
        File.WriteAllText(StatePath, "{\"schemaVersion\":1,\"runtime\":{\"running\":true},\"events\":{" + string.Join(",", events) + "}}");

    [Fact]
    public void Reads_the_notices_ShroudForge_keeps_in_its_state_file_newest_first()
    {
        State(
            Event("altar-cleanup", "undo", "Undo", "Nothing to undo", 50),
            Event("altar-cleanup", "result", "Altar Cleanup: 487 blocks found", "Press Keypad 1 again within 60 seconds to clean up.", 100),
            Event("glider", "hello", "Glider", "Ready", 70, "warn"),
            // not what the runtime writes: left out, the rest still read
            "\"broken-1.json\": {\"id\":\"x.y\",\"modId\":\"x\",\"message\":\"no title\",\"level\":\"info\",\"updatedAt\":1}",
            "\"broken-2.json\": {\"id\":\"x.y\",\"modId\":3,\"title\":\"t\",\"message\":\"m\",\"level\":\"info\",\"updatedAt\":1}",
            "\"broken-3.json\": \"text\"");
        var news = ShroudForgeNews.Read(StatePath);
        Assert.Equal(["altar-cleanup.result", "glider.hello", "altar-cleanup.undo"], news.Select(n => n.Id));
        var top = news[0];
        Assert.Equal(("altar-cleanup", "Altar Cleanup: 487 blocks found", "Press Keypad 1 again within 60 seconds to clean up.", "info", 100L),
            (top.ModId, top.Title, top.Message, top.Level, top.UpdatedAt));
        Assert.Equal(["altar-cleanup.result", "altar-cleanup.undo"], ShroudForgeNews.ForMod(news, "altar-cleanup").Select(n => n.Id));
    }

    [Fact]
    public void A_missing_or_unreadable_state_file_has_no_news()
    {
        Assert.Empty(ShroudForgeNews.Read(StatePath));
        File.WriteAllText(StatePath, "{\"events\":");
        Assert.Empty(ShroudForgeNews.Read(StatePath));
        File.WriteAllText(StatePath, "{\"events\":[]}");
        Assert.Empty(ShroudForgeNews.Read(StatePath));
    }

    [Fact]
    public void A_notice_whose_words_are_not_UTF8_is_left_out_and_the_read_never_throws()
    {
        // a damaged state file (ShroudForge's serde_json never writes one): the read runs on the thread that holds F9 and
        // toggle_modloader(), which an exception from here would end for the rest of the game
        var bytes = new List<byte>();
        bytes.AddRange(System.Text.Encoding.UTF8.GetBytes("{\"events\":{\"a-b.json\":{\"id\":\"a.b\",\"modId\":\"a\",\"title\":\""));
        bytes.AddRange([0xC3, 0x28]);
        bytes.AddRange(System.Text.Encoding.UTF8.GetBytes("\",\"message\":\"m\",\"level\":\"info\",\"updatedAt\":5}," + Event("glider", "hello", "Glider", "Ready", 7) + "}}"));
        File.WriteAllBytes(StatePath, bytes.ToArray());
        Assert.Equal(["glider.hello"], ShroudForgeNews.Read(StatePath).Select(n => n.Id));
        Assert.Equal(["glider.hello"], ShroudForgeNews.Reload().Select(n => n.Id));
    }

    [Fact]
    public void ShroudForges_own_mod_events_without_words_are_left_out()
    {
        // what ShroudForge's Modloader window writes when a mod is installed, updated or removed (sync_mod_events ->
        // write_event_notice, fork pin): no title, no message, a kind its window words; Kiln has no words for it, and an
        // empty entry would take one of the three places in the mod's details (or make an empty toast)
        State(
            "\"event-mod-install-altar-cleanup-1.0.0.json\": {\"id\":\"mod-install-altar-cleanup-1.0.0\",\"modId\":\"altar-cleanup\",\"ecosystem\":null,\"title\":\"\",\"message\":\"\",\"level\":\"info\",\"kind\":\"mod.install.success\",\"values\":{\"name\":\"Altar Cleanup\",\"version\":\"1.0.0\",\"previousVersion\":null},\"changelog\":[],\"actions\":[{\"id\":\"open-mod\",\"kind\":\"open-mod\",\"target\":\"altar-cleanup\"}],\"updatedAt\":200}",
            Event("altar-cleanup", "result", "", "487 blocks found", 100),
            Event("altar-cleanup", "title-only", "Done", "", 90));
        Assert.Equal(["altar-cleanup.result", "altar-cleanup.title-only"], ShroudForgeNews.Read(StatePath).Select(n => n.Id));
    }

    [Fact]
    public void The_state_file_is_read_again_only_when_it_changed()
    {
        // read once a second for the whole game: an unchanged file (ShroudForge replaces it by rename at each write) is
        // not opened and parsed again
        State(Event("altar-cleanup", "result", "Found", "Press again", 100));
        var written = File.GetLastWriteTimeUtc(StatePath);
        Assert.Equal(["Found"], ShroudForgeNews.Reload().Select(n => n.Title));
        State(Event("altar-cleanup", "result", "Gone!", "Press again", 100));  // the same length
        File.SetLastWriteTimeUtc(StatePath, written);
        Assert.Equal(["Found"], ShroudForgeNews.Reload().Select(n => n.Title));
        File.SetLastWriteTimeUtc(StatePath, written.AddSeconds(1));
        Assert.Equal(["Gone!"], ShroudForgeNews.Reload().Select(n => n.Title));
        File.Delete(StatePath);
        Assert.Empty(ShroudForgeNews.Reload());
    }

    [Fact]
    public void A_read_that_fails_now_is_tried_again_at_the_next_look()
    {
        // a scanner or backup tool holding the file for a moment: that is no reason to keep "no news" until ShroudForge
        // writes the file again
        if (OperatingSystem.IsWindows()) return;
        State(Event("altar-cleanup", "result", "Found", "Press again", 100));
        File.SetUnixFileMode(StatePath, UnixFileMode.None);
        try
        {
            try
            {
                using (File.OpenRead(StatePath)) { }
                return;  // root reads it all the same: nothing to show here
            }
            catch (UnauthorizedAccessException) { }
            Assert.Empty(ShroudForgeNews.Reload());
        }
        finally { File.SetUnixFileMode(StatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        Assert.Equal(["altar-cleanup.result"], ShroudForgeNews.Reload().Select(n => n.Id));  // the same size and time
    }

    [Fact]
    public void The_fresh_mod_is_the_one_whose_notice_came_in_the_last_seconds()
    {
        State(Event("glider", "hello", "Glider", "Ready", 95), Event("altar-cleanup", "result", "Found", "Press again", 100));
        var news = ShroudForgeNews.Read(StatePath);
        Assert.Equal("altar-cleanup", ShroudForgeNews.Fresh(news, nowSeconds: 101, sinceSeconds: 99));
        Assert.Null(ShroudForgeNews.Fresh(news, nowSeconds: 200, sinceSeconds: 198));  // an old notice opens nothing
        Assert.Null(ShroudForgeNews.Fresh(news, nowSeconds: 50, sinceSeconds: 48));  // nor one dated ahead of the clock
    }

    private string Package(string id, string settings)
    {
        var folder = Path.Combine(_root, "mods", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","capabilities":["runtime"]}""");
        File.WriteAllText(Path.Combine(folder, "extended.mod.json"), "{\n  \"schemaVersion\": 1,\n  \"enabled\": true,\n  \"settings\": " + settings + "\n}\n");
        ShroudForgeUi.Load(folder);
        return folder;
    }

    private const string KeySettings = """{ "cleanupKey": { "value": "SDL:89", "label": "Clean up altar zone", "control": "keybind", "applyAt": "live" } }""";

    private static CatalogMod Mod(string folder, string id, SfiRow? sfi = null) =>
        new() { Engine = "shroudforge", Id = id, Name = id, Folder = folder, State = ModRunState.Loaded, Active = true, Sfi = sfi };

    private static ModManagerPage Page(params CatalogMod[] mods) => new()
    {
        Source = () => new CatalogSnapshot
        {
            Version = 1, Mods = mods, Engines = [new CatalogEngine { Kind = "shroudforge", Name = "ShroudForge", Count = mods.Length }],
        },
        Filter = new EngineFilter("shroudforge"), Selected = ModManagerLogic.KeyOf(mods[0]),
    };

    private void Frame(ModManagerPage page)
    {
        _ui.Frame.Number++;
        page.Draw(_ui, new PageRect(0, 0, 1800, 900), 1);
    }

    [Fact]
    public void The_details_of_a_hosted_mod_show_its_newest_notices_and_where_its_keys_work()
    {
        State(Event("altar-cleanup", "result", "Altar Cleanup: 487 blocks found", "Press Keypad 1 again within 60 seconds to clean up.", 100));
        var folder = Package("altar-cleanup", KeySettings);
        ShroudForgeNews.Reload();
        var page = Page(Mod(folder, "altar-cleanup"));
        FakeUiTable.Texts.Clear();
        Frame(page);
        var drawn = string.Join("\n", FakeUiTable.Texts);
        Assert.Contains("Altar Cleanup: 487 blocks found", drawn);
        Assert.Contains("Press Keypad 1 again within 60 seconds to clean up.", drawn);
        Assert.Contains(KilnText.T("shroudforge.news.keys_after_menu", ("key", ShroudForgeMenu.KeyName)), drawn);
    }

    [Fact]
    public void Another_mods_notices_are_not_drawn_in_these_details()
    {
        State(Event("glider", "hello", "Glider news", "Ready to fly", 100));
        var folder = Package("altar-cleanup", KeySettings);
        ShroudForgeNews.Reload();
        var page = Page(Mod(folder, "altar-cleanup"));
        FakeUiTable.Texts.Clear();
        Frame(page);
        var drawn = string.Join("\n", FakeUiTable.Texts);
        Assert.DoesNotContain("Glider news", drawn);
        Assert.DoesNotContain(KilnText.T("shroudforge.news.label"), drawn);
    }

    [Fact]
    public void A_kiln_backend_row_never_reads_ShroudForges_state_file()
    {
        var asked = 0;
        ShroudForgeNews.StatePath = () => { asked++; return StatePath; };
        State(Event("altar-cleanup", "result", "Found", "Press again", 100));
        var folder = Package("altar-cleanup", KeySettings);
        var page = Page(Mod(folder, "altar-cleanup", new SfiRow(SfiBranchKind.Kiln, true, [])));
        FakeUiTable.Texts.Clear();
        Frame(page);
        Frame(page);
        Assert.Equal(0, asked);
        Assert.DoesNotContain("Press again", string.Join("\n", FakeUiTable.Texts));
    }

    private static CatalogSnapshot TwoMods(long version) => new()
    {
        Version = version,
        Mods = [new CatalogMod { Engine = "shroudforge", Id = "glider", Name = "Glider" }, new CatalogMod { Engine = "shroudforge", Id = "altar-cleanup", Name = "Altar Cleanup" }],
        Engines = [new CatalogEngine { Kind = "shroudforge", Name = "ShroudForge", Count = 2 }],
    };

    [Fact]
    public void A_request_for_a_mod_selects_its_row_once_the_list_has_it()
    {
        var page = new ModManagerPage();
        page.Request(new EngineFilter("shroudforge"), "altar-cleanup");
        page.TakeRequest();
        page.Update(CatalogSnapshot.Empty, false);  // the menu opened before the list was read: the wish waits
        page.Update(TwoMods(3), false);
        Assert.Equal("altar-cleanup", page.Rows.Single(r => r.Key == page.Selected).Mod.Id);
        page.Selected = page.Rows[0].Key;  // the player moves on: the request is spent
        page.Update(TwoMods(4), false);
        Assert.Equal("glider", page.Rows.Single(r => r.Key == page.Selected).Mod.Id);
    }

    [Fact]
    public void A_mod_toggle_opens_the_page_on_the_mod_whose_notice_just_came()
    {
        Assert.False(FakeUiTable.MenuOpen);
        // the mod asks for the window first and publishes a moment later: the page follows the notice once it lands
        var reads = 0;
        IReadOnlyList<SfNews> Read() => ++reads < 3 ? [] : [new SfNews("altar-cleanup.result", "altar-cleanup", "Found", "Press again", "info", 1000)];
        ShroudForgeMenu.ModToggled(Read, () => 1001, tries: 5, waitMs: 0);
        Assert.True(FakeUiTable.MenuOpen);
        var page = ModManagerHost.Page;
        page.TakeRequest();
        page.Update(TwoMods(5), false);
        Assert.Equal(("shroudforge", "altar-cleanup"), (page.Filter.Kind, page.Rows.Single(r => r.Key == page.Selected).Mod.Id));
    }

    [Fact]
    public void A_mod_toggle_that_closes_the_page_waits_for_no_notice()
    {
        ShroudForgeMenu.Toggle();  // open (F9)
        Assert.True(FakeUiTable.MenuOpen);
        var reads = 0;
        ShroudForgeMenu.ModToggled(() => { reads++; return []; }, () => 1001, tries: 5, waitMs: 0);
        Assert.False(FakeUiTable.MenuOpen);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void A_mod_toggle_opens_the_page_even_when_the_news_cannot_be_read()
    {
        // the menu thread: an exception from ShroudForge's file would end F9 and toggle_modloader() for the whole game
        ShroudForgeMenu.ModToggled(() => throw new InvalidOperationException("damaged state file"), () => 1001, tries: 3, waitMs: 0);
        Assert.True(FakeUiTable.MenuOpen);
        Assert.Equal("shroudforge", ModManagerHost.Page.PendingRequest);
    }

    [Fact]
    public void A_mod_toggle_does_not_pull_the_player_back_from_another_section()
    {
        // the page opened, the player went to Keys before the notice landed (the 1.6 s the toggle waits for it)
        var page = ModManagerHost.Page;
        var reads = 0;
        IReadOnlyList<SfNews> Read()
        {
            if (++reads == 1)
            {
                page.TakeRequest();
                page.Section = ModManagerSection.Keys;
                return [];
            }
            return [new SfNews("altar-cleanup.result", "altar-cleanup", "Found", "Press again", "info", 1000)];
        }
        ShroudForgeMenu.ModToggled(Read, () => 1001, tries: 3, waitMs: 0);
        Assert.Equal(2, reads);
        Assert.Null(page.PendingRequest);
        Assert.Equal(ModManagerSection.Keys, page.Section);
    }

    // Kiln's toasts: a notice a hosted mod publishes while the game runs shows over the game at once, as ShroudForge's
    // window would show it in its feed (netjunk 2026-11-26, Kiln 31.18: "the first press does nothing", the scan's
    // result and "press the cleanup key again within 60 seconds" were only in kiln.log)
    private static SfNews N(string id, long at, string title = "Altar Cleanup", string message = "Press again", string level = "info") =>
        new(id, id.Split('.')[0], title, message, level, at);

    [Fact]
    public void Each_notice_published_after_the_start_is_toasted_once_and_again_when_it_changes()
    {
        var toasts = new SfNewsToasts(startSeconds: 1000);
        Assert.Empty(toasts.Poll([N("altar-cleanup.result", 900)], 0));  // from an earlier game: in the page only
        Assert.Equal(["altar-cleanup.result"], toasts.Poll([N("altar-cleanup.result", 1005), N("glider.hello", 990)], 1).Select(n => n.Id));
        Assert.Empty(toasts.Poll([N("altar-cleanup.result", 1005)], 2));  // the same notice
        Assert.Equal([1010L], toasts.Poll([N("altar-cleanup.result", 1010)], 3).Select(n => n.UpdatedAt));  // republished
        var many = Enumerable.Range(0, 10).Select(i => N("m" + i + ".x", 1100 + i)).ToList();
        // a burst does not fill the screen: the newest, in the room the two toasts above leave until they are gone
        Assert.Equal(["m9.x"], toasts.Poll(many, 4).Select(n => n.Id));
        var more = Enumerable.Range(0, 10).Select(i => N("n" + i + ".x", 1200 + i)).ToList();
        Assert.Equal(["n9.x", "n8.x", "n7.x"], toasts.Poll(more.OrderByDescending(n => n.UpdatedAt).ToList(), 15).Select(n => n.Id));
    }

    [Fact]
    public void At_most_three_notice_toasts_are_on_screen_at_once()
    {
        // a mod that publishes again and again (a counter, a status) would keep three toasts coming each second: kiln.ui
        // keeps 8 and drops the oldest, so Kiln's own messages (a server's refusal) would be pushed off the screen. A
        // notice that finds no room is only in the mod's details.
        var toasts = new SfNewsToasts(startSeconds: 0);
        var shown = 0;
        for (var second = 0; second < 30; second++)
            shown += toasts.Poll([N("counter.status", 100 + second, message: "Count " + second)], second).Count;
        Assert.Equal(9, shown);  // 3 at seconds 0-2, 10-12 and 20-22: each toast stays 10 s
        Assert.Equal(3, SfNewsToasts.MaxOnScreen);
        Assert.Equal(10, SfNewsToasts.Seconds);
    }

    [Fact]
    public void A_failing_news_poll_never_ends_the_menu_thread()
    {
        // ShroudForge's menu thread polls the news at each wake; an exception here would end F9 and toggle_modloader()
        var toasts = new SfNewsToasts(startSeconds: 0);
        ShroudForgeMenu.PollNews(toasts, () => throw new InvalidOperationException("damaged state file"), (_, _, _) => Assert.Fail("no toast"), 1);
        ShroudForgeMenu.PollNews(toasts, () => [N("altar-cleanup.result", 5)], (_, _, _) => throw new IOException("toast"), 2);
        var made = new List<(string, ToastKind, double)>();
        ShroudForgeMenu.PollNews(toasts, () => [N("glider.hello", 6, "Glider", "Ready", "error")], (t, k, s) => made.Add((t, k, s)), 3);
        Assert.Equal([("Glider: Ready", ToastKind.Error, SfNewsToasts.Seconds)], made);
    }

    [Fact]
    public void A_toast_says_the_title_and_the_message_in_the_look_of_its_level()
    {
        Assert.Equal(("Altar Cleanup: 487 blocks found. Press Keypad 1 again within 60 seconds to clean up.", ToastKind.Info),
            SfNewsToasts.Text(N("altar-cleanup.result", 1, "Altar Cleanup", "487 blocks found. Press Keypad 1 again within 60 seconds to clean up.")));
        Assert.Equal(("Only a message", ToastKind.Warning), SfNewsToasts.Text(N("a.b", 1, "", "Only a message", "warn")));
        Assert.Equal(("Failed: no altar here", ToastKind.Error), SfNewsToasts.Text(N("a.b", 1, "Failed", "no altar here", "error")));
        Assert.Equal(("Done", ToastKind.Success), SfNewsToasts.Text(N("a.b", 1, "Done", "", "success")));
    }

    [Fact]
    public void A_toast_keeps_a_long_or_odd_notice_to_a_few_lines_of_plain_words()
    {
        // kiln.ui's toasts do not wrap, and a notice may hold 2,000 characters: one line that long runs off the screen,
        // and a message of line breaks would cover it; control and invisible format characters (bidi overrides) are the
        // mod's, not words. The mod's details show the whole notice.
        var words = string.Join(" ", Enumerable.Range(0, 400).Select(i => "word" + i));
        var (text, _) = SfNewsToasts.Text(N("a.b", 1, "Altar\tCleanup\u202E", "Line one\r\nLine two\u0007\n\n\n" + words));
        var lines = text.Split('\n');
        Assert.InRange(lines.Length, 2, 4);
        Assert.All(lines, l => Assert.InRange(l.Length, 1, 96));
        Assert.StartsWith("Altar Cleanup: Line one Line two word0 word1", text);
        Assert.EndsWith("...", text);
        Assert.DoesNotContain(text, c => c is < ' ' and not '\n' or '\u202E' or '\u007F');
        // a word longer than a line (or words with no spaces between them) is cut at the line's end
        var (one, _) = SfNewsToasts.Text(N("a.b", 1, "", new string('x', 150)));
        Assert.Equal([96, 54], one.Split('\n').Select(l => l.Length));
        // a short notice stays one line, as it came
        Assert.Equal("Found: 487 blocks", SfNewsToasts.Text(N("a.b", 1, "  Found ", " 487   blocks ")).Text);
    }
}
