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

    private const string Keen = """{ "name": "Keen", "stat": "crit-chance", "stacks": false, "group": "edge", "tiers": [ { "min": 0.02, "max": 0.05 } ] }""";

    public void Dispose()
    {
        ModsBackend.Current = _previous;
        StartProgress.Use(0);
        if (_table != null) NativeMemory.Free(_table);
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); }
            catch (IOException) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnStep(StartProgress.Str id, StartProgress.Str label, StartProgress.Str detail, ulong done, ulong total)
    {
        Calls.Add((Text(id), Text(label), Text(detail), done, total));
        return 0;  // the fake core takes every step
    }

    private static string Text(StartProgress.Str s) => s.Ptr == null ? "" : System.Text.Encoding.UTF8.GetString(s.Ptr, (int)s.Len);

    private void FakeCore()
    {
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
        Assert.Equal(4UL, BlocksStartup.CountDataBlocks(good.Directory));  // a: 2, e: 2 (a duplicate key is read twice by the compile too)
        Assert.Equal(0UL, BlocksStartup.CountDataBlocks(Path.Combine(good.Directory, "missing")));
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
