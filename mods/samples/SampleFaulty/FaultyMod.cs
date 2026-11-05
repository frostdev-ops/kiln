// SPDX-License-Identifier: MIT
using Kiln;

namespace SampleFaulty;

public sealed class FaultyMod : Mod
{
    private string _trigger = "";

    public override void OnLoad()
    {
        _trigger = Path.Combine(Context.DataDirectory, "fault");
        Directory.CreateDirectory(Context.DataDirectory);
        Context.Tick.Pre += OnPre;
        Log.Info($"sample-faulty: create {_trigger} to make every tick throw");
    }

    private void OnPre(in TickInfo tick)
    {
        if (tick.Step % 25 == 0 && File.Exists(_trigger))
            throw new InvalidOperationException($"sample-faulty: deliberate fault at step {tick.Step}");
    }
}
