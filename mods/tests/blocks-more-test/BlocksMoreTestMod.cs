// SPDX-License-Identifier: MIT
// Status and perk icons of a mod's own, tools of every kind, piercing deliveries, props with their own collision and sounds as blocks, player-free on a
// dedicated server. What only a client can show (an icon drawn, a sound heard, a prop's collision felt) is in the docs' manual test.
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksMoreTest;

public sealed partial class BlocksMoreTestMod : Mod
{
    private const string Me = "blocks-more-test";
    private int _pass, _fail;

    private void Info(string s) => Log.Info($"{Me}: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"{Me}: PASS {what}"); }
        else { _fail++; Log.Error($"{Me}: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    // A block that must be refused: the error names the block, the field and what is wrong.
    private void Refused(string what, Action define, params string[] words)
    {
        try
        {
            define();
            Check(false, $"{what}: defined, should be refused");
        }
        catch (BlockError e)
        {
            Check(words.All(w => e.Message.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{what}: \"{e.Message}\"");
        }
    }

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        DefineIcons();
        DefineTools();
        DefinePierce();
        DefineProps();
        DefineSounds();
        DefineLookVfx();
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(150);
            Mistakes();
            Icons();
            Tools();
            Pierce();
            Props();
            Sounds();
            LookVfx();
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    }

    private void Mistakes()
    {
        var (_, errors) = Blocks.Report()[Me];
        void Named(string block, params string[] words)
        {
            var e = errors.FirstOrDefault(x => x.Contains(block, StringComparison.Ordinal));
            Check(e != null && words.All(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{block}: \"{e}\"");
        }
        foreach (var (block, words) in BrokenJson) Named(block, words);
    }

    private static readonly (string Block, string[] Words)[] BrokenJson =
    [
        ("status-png-missing", ["icon", "no-such.png", "not in the mod's folder"]),
        ("perk-png-bad", ["icon", "not a PNG"]),
        ("tool-bad-class", ["harvests", "shovel", "rake"]),
        ("tool-torch-power", ["tool.power"]),
    ];
}
