// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;
using Xunit;

public sealed class DevViewsTests
{
    private sealed class CountingView : DevView
    {
        public int Closes;
        public override int RefreshMs => 500;
        public override void Refresh() { }
        public override void Closed() => Closes++;
    }

    [Fact]
    public void ClosedViewsNeverRefreshAndOpenOnesRefreshAtTheirRate()
    {
        var v = new CountingView();
        for (var t = 0; t < 10_000; t += 16) PanelScheduler.Pump([(v, false)], t);
        Assert.Equal(0, v.Refreshes);
        for (var t = 10_000; t < 12_000; t += 16) PanelScheduler.Pump([(v, true)], t); // 2 s at 60 fps, 500 ms rate
        Assert.InRange(v.Refreshes, 4, 5);
        PanelScheduler.Pump([(v, false)], 12_000);
        Assert.Equal(1, v.Closes);
        PanelScheduler.Pump([(v, true)], 12_010); // reopening refreshes at once
        Assert.InRange(v.Refreshes, 5, 6);
    }

    [Fact]
    public void FilterUsesSetSyntax()
    {
        Assert.True(TextFilter.Matches("anything", ""));
        Assert.True(TextFilter.Matches("Kiln: hooks ready", "hooks,items"));
        Assert.False(TextFilter.Matches("Kiln: hooks ready", "items"));
        Assert.False(TextFilter.Matches("Kiln: hooks ready", "hooks,-ready"));
        Assert.True(TextFilter.Matches("Kiln: hooks ready", "-items"));
    }

    [Fact]
    public void BindingsMarkConflictsAndChangedKeys()
    {
        var f8 = KeyChord.Parse("F8");
        var rows = BindingsView.Build([
            ("B", "b.two", f8, KeyChord.None, "two", true),
            ("A", "a.one", f8, f8, "one", true),
            ("A", "a.free", KeyChord.None, KeyChord.None, "free", false)]);
        Assert.Equal(["a.free", "a.one", "b.two"], rows.Select(r => r.Id));
        Assert.Equal("b.two", rows[1].Conflict);
        Assert.Equal("a.one", rows[2].Conflict);
        Assert.True(rows[2].Changed);
        Assert.Null(rows[0].Conflict); // None never conflicts
        Assert.Contains("CONFLICT", BindingsView.Describe(rows, "b.two"));
    }

    [Fact]
    public void HookRatesComeFromDeltas()
    {
        var a = new HooksStats(1, 2, 3, 0, 100, 10);
        var b = new HooksStats(1, 2, 3, 0, 300, 10);
        var r = HooksView.Rates(a, b, 2);
        Assert.Equal(100, r.DetourCallsPerSecond);
        Assert.Equal(0, r.MidCallsPerSecond);
        Assert.Contains("(100.0/s)", HooksView.Describe(b, r));
    }

    [Fact]
    public void HooksViewIsSoftWhenUnavailable()
    {
        var v = new HooksView();
        v.Refresh(); // no native core in tests
        Assert.Contains("unavailable", v.Text);
    }

    [Fact]
    public void GameLogKeepsWholeLinesClassifiesAndCaps()
    {
        var v = new GameLogView();
        v.Append("[I] one\r\n[W] two\n[E] thr");
        Assert.Equal(2, v.Lines.Length);
        v.Append("ee\n");
        Assert.Equal([RowLevel.Info, RowLevel.Warn, RowLevel.Error], v.Lines.Select(l => l.Level));
        Assert.Equal("[E] three", v.Lines[2].Text);
        Assert.Equal(RowLevel.Warn, GameLogView.Classify("[W 00:00:13,444] [ecs] slow"));
        for (var i = 0; i < GameLogView.MaxLines + 10; i++) v.Append($"line {i}\n");
        Assert.Equal(GameLogView.MaxLines, v.Lines.Length);
        Assert.Single(GameLogView.Filtered([new LogLine(RowLevel.Info, "StateMachine x"), new LogLine(RowLevel.Info, "ok")], "", true));
    }

    [Fact]
    public void GameLogFollowsTheFile()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-devtools-log").FullName;
        try
        {
            var path = Path.Combine(dir, "enshrouded.log");
            File.WriteAllText(path, "a\nb\n");
            Assert.Equal(path, GameLogView.FindLog(dir));
            var v = new GameLogView { PathOverride = path };
            v.Refresh();
            Assert.Equal(2, v.Lines.Length);
            File.AppendAllText(path, "c\n");
            v.Refresh();
            Assert.Equal(["a", "b", "c"], v.Lines.Select(l => l.Text));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StatusRatesStateFiles()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-devtools-state").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "boot.json"), """{"kiln":"0.2","errors":[],"exe":{"verified":true}}""");
            File.WriteAllText(Path.Combine(dir, "emf.json"), """{"signatures":{"A":"0x1","B":null}}""");
            File.WriteAllText(Path.Combine(dir, "mods.json"), """{"mods":[{"id":"m","state":"failed","reason":"boom"}],"clr":{"error":"x"}}""");
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{");
            var v = new StatusView { DirOverride = dir };
            v.Refresh();
            var by = v.Files.ToDictionary(f => f.Name);
            Assert.Equal(RowLevel.Ok, by["boot.json"].Level);
            Assert.Contains(by["boot.json"].Lines, l => l.Text == "kiln: 0.2");
            Assert.Equal(RowLevel.Warn, by["emf.json"].Level);
            Assert.Contains("1 of 2 not found (B)", by["emf.json"].Lines[0].Text);
            Assert.Equal(RowLevel.Error, by["mods.json"].Level);
            Assert.Equal(RowLevel.Warn, by["bad.json"].Level);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ConsoleWritesCommandFilesAndReadsResults()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-devtools-console").FullName;
        try
        {
            var v = new ConsoleView { DirOverride = dir };
            var name = v.Submit("  help  ");
            Assert.Equal("help\n", File.ReadAllText(Path.Combine(dir, name + ".cmd")));
            Assert.Equal(["help"], v.History);
            v.Refresh();
            Assert.Equal(1, v.Pending);
            File.WriteAllText(Path.Combine(dir, name + ".result.json"),
                """{"ok":false,"results":[{"line":"help","ok":true,"error":null,"output":["a","b"]},{"line":"x","ok":false,"error":"unknown command"}]}""");
            v.Refresh();
            Assert.Equal(0, v.Pending);
            Assert.False(File.Exists(Path.Combine(dir, name + ".result.json")));
            Assert.Equal(["> help", "a", "b", "unknown command"], v.Output.Select(l => l.Text));
            Assert.Equal(RowLevel.Error, v.Output[^1].Level);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ConsoleResultParsingIsSoft()
    {
        Assert.Equal(RowLevel.Error, ConsoleView.ParseResult("{").Single().Level);
        Assert.Equal("(still running)", ConsoleView.ParseResult("""{"results":[{"running":true,"output":[]}]}""").Single().Text);
    }

    [Fact]
    public void ResourcesListThroughTheSeamAndDetailIsReplaceable()
    {
        var g = Guid.NewGuid();
        var oldList = ResourcesView.List;
        var oldDetail = ResourcesView.Detail;
        ResourcesView.List = h => [new AssetKey(g, h, 1), new AssetKey(g, h, 0)];
        ResourcesView.Detail = k => $"reflected {k.Guid} {k.Part}";
        try
        {
            var v = new ResourcesView();
            v.Request("0x1234abcd");
            v.Refresh();
            Assert.Equal(0x1234abcdu, v.TypeHash);
            Assert.Equal([0u, 1u], v.Rows.Select(r => r.Part));
            v.Select(v.Rows[1]);
            v.Refresh();
            Assert.Equal($"reflected {g} 1", v.DetailText);
            Assert.True(ResourcesView.TryHash("keen::ItemInfo", out var h) && h != 0);
            Assert.False(ResourcesView.TryHash("0xzz", out _));
        }
        finally { ResourcesView.List = oldList; ResourcesView.Detail = oldDetail; }
    }

    [Fact]
    public void InfoCommandsWorkWithoutAUi()
    {
        KilnDevTools.Start();
        foreach (var line in new[] { "devtools status", "devtools hooks", "devtools bindings", "devtools log 5" })
        {
            var r = Commands.Execute(CommandSource.Console, line);
            Assert.True(r.Ok, line + ": " + r.Error);
        }
    }

    [Fact]
    public void ServerDataTextFormsAreAdminOnly()
    {
        KilnDevTools.Start();
        var player = CommandSource.Remote("player", admin: false); // what a chat player is on a dedicated server
        foreach (var line in new[] { "devtools status", "devtools log", "devtools log 5", "devtools resources 0x1" })
            Assert.False(Commands.Execute(player, line).Ok, line);
        Assert.True(Commands.Execute(player, "devtools hooks").Ok);
    }

    [Fact]
    public void GameLogDecodesSplitUtf8StartsTailsOnALineAndHandlesTruncation()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-devtools-log2").FullName;
        try
        {
            var path = Path.Combine(dir, "enshrouded.log");
            var v = new GameLogView { PathOverride = path };
            // a long file: the first look starts at the 64 KiB tail and drops the cut first line
            File.WriteAllText(path, new string('x', 70_000) + "\nfirst\n");
            v.Refresh();
            Assert.Equal(["first"], v.Lines.Select(l => l.Text));
            // a 3-byte character split across two writes (and two reads)
            var euro = System.Text.Encoding.UTF8.GetBytes("\u20ac");
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) fs.Write(euro, 0, 1);
            v.Refresh();
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { fs.Write(euro, 1, 2); fs.Write("ok\n"u8); }
            v.Refresh();
            Assert.Equal("\u20acok", v.Lines[^1].Text);
            // truncated (rotated in place): follows the new content
            File.WriteAllText(path, "fresh\n");
            v.Refresh();
            Assert.Equal("fresh", v.Lines[^1].Text);
        }
        finally { Directory.Delete(dir, true); }
    }
}
