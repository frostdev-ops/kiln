// SPDX-License-Identifier: MIT
using Kiln;

namespace Hello;

/// <summary>Logs once a minute, counts game log lines, and shows await on the tick thread.</summary>
public sealed class HelloMod : Mod
{
    private int _gameLines;
    private ulong _loadedAt;

    public override void OnLoad()
    {
        Log.Info($"hello from C# (Kiln {Game.KilnVersion}, tick thread: {Game.IsTickThread})");
        Context.Tick.Pre += OnPre;
        Context.GameLog.Line += (_, _) => _gameLines++;
        _ = GreetLaterAsync();
    }

    private void OnPre(in TickInfo tick)
    {
        if (_loadedAt == 0) _loadedAt = tick.Step;
        if ((tick.Step - _loadedAt) % 1470 != 0 || tick.Step == _loadedAt) return;
        Log.Info($"step {tick.Step}: clock +{tick.ClockDelta}, wall {tick.WallDeltaMs:F1} ms, {_gameLines} game log lines");
    }

    private async Task GreetLaterAsync()
    {
        for (var i = 0; i < 25; i++) await Context.Scheduler.NextTick();
        Log.Info($"25 steps later, still on the tick thread: {Game.IsTickThread}; " +
                 (Example.Available ? $"kiln.example counted {Example.Steps} steps" : "kiln.example unavailable"));
    }

    public override void OnUnload() => Log.Info("goodbye");
}
