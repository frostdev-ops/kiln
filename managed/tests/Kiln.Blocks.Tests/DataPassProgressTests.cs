// SPDX-License-Identifier: MIT
// The data pass of blocks/*.json (BlocksStartup.CompileLoaded) and progress.json's step "content": counting the blocks changes
// nothing of what is compiled, marked done or logged (a mod whose files are malformed costs only what it cost before), and the
// step ends, with "game" after it, also when a mod's files stop the pass.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln;
using Xunit;

namespace DataPassProgress;

public sealed unsafe class DataPassProgressTests : IDisposable
{
    private readonly List<string> _dirs = [];
    private readonly IModsBackend? _previous = ModsBackend.Current;
    private static readonly List<(string Id, string Label, string Detail, ulong Done, ulong Total)> Calls = [];
    private StartProgress.Table* _table;
    private static readonly Func<ModEntry, ulong> Real = BlocksStartup.Counter;

    private const string Keen = """{ "name": "Keen", "stat": "crit-chance", "stacks": false, "group": "edge", "tiers": [ { "min": 0.02, "max": 0.05 } ] }""";

    public void Dispose()
    {
        BlocksStartup.Counter = Real;
        BlocksStartup.ResetStartForTests();
        ModsBackend.Current = _previous;
        StartProgress.Use(0);
        if (_table != null) NativeMemory.Free(_table);
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); }
            catch (IOException) { }
    }

    // The core's rules (native/core/src/progress_file.h, Model): only the running step or the next one of the plan; ready once "game"
    // has begun and the side's mark has come (a mark that came first is remembered); nothing after ready.
    private static readonly string[] Plan = ["mods", "content", "game"];
    private static string _running = "mods";
    private static bool _marked, _ready;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnStep(StartProgress.Str id, StartProgress.Str label, StartProgress.Str detail, ulong done, ulong total)
    {
        if (_ready) return StartProgress.Unavailable;
        var s = Text(id);
        if (s != _running && Array.IndexOf(Plan, s) != Array.IndexOf(Plan, _running) + 1) return StartProgress.Refused;
        _running = s;
        Calls.Add((s, Text(label), Text(detail), done, total));
        if (_running == "game" && _marked) _ready = true;
        return StartProgress.Ok;
    }

    private static string Text(StartProgress.Str s) => s.Ptr == null ? "" : System.Text.Encoding.UTF8.GetString(s.Ptr, (int)s.Len);

    private void FakeCore(bool marked = false)
    {
        (_running, _marked, _ready) = ("mods", marked, false);
        BlocksStartup.ResetStartForTests();
        _table = (StartProgress.Table*)NativeMemory.AllocZeroed((nuint)sizeof(StartProgress.Table));
        _table->Size = (uint)sizeof(StartProgress.Table);
        _table->Version = 2;
        _table->Step = &OnStep;
        StartProgress.Use((nint)_table);
        Calls.Clear();
    }

    private ModEntry Mod(string id, params (string Name, string Json)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"kiln-data-pass-{Guid.NewGuid():N}");
        _dirs.Add(dir);
        Directory.CreateDirectory(Path.Combine(dir, "blocks"));
        foreach (var (name, json) in files) File.WriteAllText(Path.Combine(dir, "blocks", name), json);
        return new ModEntry(id, id, "1", dir, ModSide.Server, ModState.Loaded, null, [], 0, new ModInfo(id, id, "1", dir));
    }

    private static void Pass()
    {
        try { BlocksStartup.CompileLoaded(); }
        catch (InvalidOperationException) { }  // a malformed mod stops the pass where it stands, as it always did
    }

    private static string Uid(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    [Fact]
    public void A_mod_with_a_blocks_file_of_the_wrong_shape_does_not_stop_the_mods_before_it()
    {
        var a = Uid("pass-a");
        var b = Uid("pass-b");
        ModsBackend.Current = new ListedMods(Mod(a, ("a.json", $$"""{ "format": 1, "affixes": { "keen": {{Keen}} } }""")), Mod(b, ("b.json", "[]")));
        Pass();
        Assert.NotNull(Blocks.Find("keen", a));
    }

    [Fact]
    public void Counting_survives_every_kind_of_bad_file_and_counts_only_blocks()
    {
        var good = Mod(Uid("count"), ("a.json", $$"""{ "format": 1, "comment": "x", "affixes": { "keen": {{Keen}}, "keen2": {{Keen}} } }"""),
            ("b.json", "[]"), ("c.json", "{ not json"), ("d.json", """{ "affixes": [] }"""), ("e.json", """{ "affixes": { "k": {}, "k": {} } }"""));
        Assert.Equal(4UL, BlockJson.CountEntries(good.Directory));  // a: 2, e: 2 (a duplicate key is read twice by the compile too)
        Assert.Equal(0UL, BlockJson.CountEntries(Path.Combine(good.Directory, "missing")));
    }

    [Fact]
    public void The_content_step_counts_up_to_its_total_then_game_begins()
    {
        FakeCore();
        var a = Uid("prog");
        ModsBackend.Current = new ListedMods(Mod(a, ("a.json", $$"""{ "format": 1, "affixes": { "keen": {{Keen}}, "edge": {{Keen.Replace("Keen", "Edge")}} } }""")));
        Pass();
        var content = Calls.Where(c => c.Id == "content").ToList();
        Assert.Equal([(0UL, 2UL), (1UL, 2UL), (2UL, 2UL)], content.Select(c => (c.Done, c.Total)));
        Assert.Equal("game", Calls[^1].Id);
    }

    [Fact]
    public void A_pass_a_malformed_mod_stops_still_ends_the_content_step()
    {
        FakeCore();
        ModsBackend.Current = new ListedMods(Mod(Uid("bad"), ("b.json", "[]")));
        Pass();
        Assert.Equal("game", Calls[^1].Id);
    }

    [Fact]
    public void A_section_the_compiler_does_not_know_is_not_counted()
    {
        var m = Mod(Uid("typo"), ("a.json", """{ "format": 1, "typo": { "x": {} }, "affixes": { "keen": {} } }"""));
        Assert.Equal(1UL, BlockJson.CountEntries(m.Directory));
    }

    [Fact]
    public void A_count_that_runs_out_of_memory_leaves_no_total_and_the_mods_still_compile()
    {
        FakeCore();
        BlocksStartup.Counter = _ => throw new OutOfMemoryException();
        var a = Uid("oom");
        ModsBackend.Current = new ListedMods(Mod(a, ("a.json", $$"""{ "format": 1, "affixes": { "keen": {{Keen}} } }""")));
        Pass();
        Assert.NotNull(Blocks.Find("keen", a));
        Assert.All(Calls.Where(c => c.Id == "content"), c => Assert.Equal(0UL, c.Total));  // no total
        Assert.Equal("game", _running);
    }

    [Fact]
    public void A_mod_whose_code_read_its_data_files_is_counted_from_them_not_read_again()
    {
        FakeCore();
        var a = Uid("cached");
        var entry = Mod(a, ("a.json", $$"""{ "format": 1, "affixes": { "keen": {{Keen}}, "edge": {{Keen.Replace("Keen", "Edge")}} } }"""));
        var blocks = Blocks.ModFor(a, () => 0);
        BlockJson.Read(blocks, entry.Directory);  // what a mod's OnLoad does when its code names a data block
        File.WriteAllText(Path.Combine(entry.Directory, "blocks", "a.json"), "{ \"format\": 1 }");  // the files no longer say so
        ModsBackend.Current = new ListedMods(entry);
        Pass();
        Assert.Equal(2UL, Calls.First(c => c.Id == "content").Total);  // from the cached entries
    }

    [Fact]
    public void Game_begins_only_after_every_mod_of_the_start_was_attempted_also_the_one_after_a_failing_mod()
    {
        FakeCore(marked: true);  // the menu (or ServerStarted) came first: ready at the moment "game" begins
        string a = Uid("abc-a"), b = Uid("abc-b"), c = Uid("abc-c");
        ModsBackend.Current = new ListedMods(Mod(a, ("a.json", $$"""{ "format": 1, "affixes": { "keen": {{Keen}} } }""")), Mod(b, ("b.json", "[]")),
            Mod(c, ("c.json", $$"""{ "format": 1, "affixes": { "keen": {{Keen}} } }""")));
        StartProgress.Step("content");  // Kiln.Host, after the mods loaded
        Pass();  // a compiles, b throws: c waits for the next pass, as it always did
        Assert.NotNull(Blocks.Find("keen", a));
        Assert.Null(Blocks.Find("keen", c));
        Assert.Equal("content", _running);
        Assert.False(_ready);
        Pass();  // the next service tick
        Assert.NotNull(Blocks.Find("keen", c));
        Assert.Equal("game", _running);
        Assert.True(_ready);
        Assert.Equal(2UL, Calls.Last(x => x.Id == "content").Done);  // a's and c's block, inside the recorded content
    }

    private sealed class ListedMods(params ModEntry[] entries) : IModsBackend
    {
        public IReadOnlyList<ModEntry> All => entries;
        public ModSide Side => ModSide.Server;
        public ModEntry? Get(string id) => Array.Find(entries, e => e.Id == id);
        public ModStats Stats(string id) => default;
        public bool Send(ModContext? sender, string targetId, string method, object? payload) => false;
        public Task<ModOperationResult> Request(ModOperation op, string arg) => throw new NotSupportedException();
        public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) => throw new NotSupportedException();
    }
}
