// SPDX-License-Identifier: MIT
using Kiln;

namespace TkEvents;

/// <summary>An event of the mod's own.</summary>
public sealed class PingEvent(string text) : KilnEvent
{
    public string Text { get; } = text;
}

/// <summary>Events, the scheduler and threads, driven by messages the test sends: "ping" posts a PingEvent (a
/// [Subscribe] method and an Events.On handler log it), "await" logs three times across NextTick awaits, "worker" tries
/// tick-thread API from a thread of its own and posts work back, "tick" logs the next TickInfo.</summary>
public sealed class TkEventsMod : Mod
{
    private bool _logTick;

    public override void OnLoad()
    {
        Events.On<PingEvent>(e => Log.Info($"on: {e.Text}"));
        Context.Messages.On("ping", m => Events.Post(new PingEvent(m.PayloadAs<string>() ?? "")));
        Context.Messages.On("await", m => _ = AwaitLoop(3));
        Context.Messages.On("worker", _ => Worker());
        Context.Messages.On("tick", _ => _logTick = true);
        Context.Tick.Pre += OnPre;
        Log.Info($"loaded; tick thread {Game.IsTickThread}");
    }

    [Subscribe]
    private void OnPing(PingEvent e) => Log.Info($"subscribe: {e.Text}");

    private void OnPre(in TickInfo t)
    {
        if (!_logTick) return;
        _logTick = false;
        Log.Info($"tick step {t.Step} clock {t.Clock} delta {t.ClockDelta} wall {t.WallDeltaMs:0.0}");
    }

    private async Task AwaitLoop(int n)
    {
        for (var i = 0; i < n; i++)
        {
            await Context.Scheduler.NextTick();
            Log.Info($"await {i} on tick thread {Game.IsTickThread}");
        }
    }

    private void Worker()
    {
        var t = new Thread(() =>
        {
            try
            {
                Events.Post(new PingEvent("from a worker"));
                Log.Info("worker: Events.Post ran");
            }
            catch (InvalidOperationException e)
            {
                Log.Info($"worker: Events.Post refused: {e.Message}");
            }
            Log.Info($"worker: tick thread {Game.IsTickThread}");
            Context.Scheduler.Post(() => Log.Info($"posted work ran; tick thread {Game.IsTickThread}"));
        });
        t.Start();
        t.Join();
    }
}
