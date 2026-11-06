// SPDX-License-Identifier: MIT
using Kiln;

namespace SampleFaulty;

public sealed class FaultyMod : Mod
{
    private string _trigger = "";

    public override void OnLoad()
    {
        // As a mod built against a newer Kiln fails: the runtime lacks a Kiln member it uses (here, a field of an enum).
        if (File.Exists(Path.Combine(Context.DataDirectory, "missing-member")))
            throw new MissingFieldException("Kiln.StatusLook", "Fire");
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
