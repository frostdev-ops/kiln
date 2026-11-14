// SPDX-License-Identifier: MIT
// Lab test (managed part): Kiln.Testing.LabSystems, the lab parallel switch, in a window before the native part
// (native/systems_test.cpp) uses the switch itself (from 260 steps after the server world's first Pre).
using Kiln;
using Kiln.Testing;

namespace SystemsTest;

public sealed class SystemsTestMod : Mod
{
    private const ulong From = 60, Latest = 200;  // world steps: the window must end before the native part's
    private ulong _start;
    private int _pass, _fail;
    private bool _done;

    private void Check(string name, bool ok, object? detail = null)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : ": " + detail)}");
    }

    public override void OnLoad() => Context.Tick.Post += OnPost;

    private void OnPost(in TickInfo tick)
    {
        if (_done || tick.Step < From) return;
        if (_start == 0)
        {
            _start = tick.Step;
            if (_start > Latest)
            {
                Check("LabSystems window", false, $"the first step seen is {_start}, after the native part may use the switch");
                Finish();
                return;
            }
        }
        try
        {
            switch (tick.Step - _start)
            {
                case 0:
                    Check("LabSystems.Enabled", LabSystems.Enabled, "kiln/control/systems.parallel is there");
                    Check("LabSystems refuses a window outside 1..3000 steps",
                        Throws(() => LabSystems.ForceParallel(0)) && Throws(() => LabSystems.ForceParallel(3001)));
                    Check("LabSystems.ForceParallel(40)", LabSystems.PlayersMask == 0 && LabSystems.ForceParallel(40),
                        $"mask {LabSystems.PlayersMask:x4} before");
                    break;
                case 2:
                    Check("LabSystems: forced from the next step", LabSystems.Forced && LabSystems.PlayersMask == 0x8000 &&
                        LabSystems.StepsLeft is > 30 and <= 40, $"mask {LabSystems.PlayersMask:x4}, {LabSystems.StepsLeft} steps left");
                    Check("LabSystems.StopParallel", LabSystems.StopParallel());
                    break;
                case 4:
                    Check("LabSystems: let go in the next step", !LabSystems.Forced && LabSystems.PlayersMask == 0,
                        $"mask {LabSystems.PlayersMask:x4}");
                    Finish();
                    break;
            }
        }
        catch (Exception e)
        {
            Check($"step {tick.Step - _start}", false, e.Message);
            Finish();
        }
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }

    private void Finish()
    {
        _done = true;
        Log.Info($"systems-test (managed): {_pass} PASS, {_fail} FAIL");
    }
}
