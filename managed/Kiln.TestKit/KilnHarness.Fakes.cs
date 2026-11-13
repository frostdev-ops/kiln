// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

// The recording fakes of dev.27 (Fakes/: kiln.spawn, ...): their life on the harness's, called from KilnHarness.World.cs.
public sealed partial class KilnHarness
{
    /// <summary>After the world, the players and the saves: each fake's state for this harness.</summary>
    private void StartRecordingFakes()
    {
        StartSpawn();
        SpawnFake.Starting();
        StartChat();
        StartItems();
        StartCombat();
        StartTime();
    }

    /// <summary>The native services' Pre, after the players' scan: what each fake's game does at the start of a step.</summary>
    private void RecordingFakesBeforeStep(ulong step)
    {
        SpawnFake?.BeforeStep();
        ChatFake?.BeforeStep(step);
        ItemsFake?.BeforeStep(step);
        CombatFake?.BeforeStep(step);
        TimeFake?.BeforeStep();
    }

    /// <summary>The budget of the process's first service tick (<see cref="StartServicesOnce"/>).</summary>
    private static readonly TimeSpan ServicesStartBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The first service tick of the process starts Kiln's services (ServiceTick.AutoStart: every service assembly beside
    /// the kit is loaded and its start type run). With the recording fakes, those starts find their tables and do their
    /// work; on a loaded machine that alone can take longer than a short <see cref="KilnHarnessOptions.CallTimeout"/>.
    /// The first harness of the process runs that tick here, on its tick thread at a menu step (as
    /// <c>BootServiceTick</c> does for a later harness), with a budget of its own, so a test's own call budget measures
    /// the test's calls. Later harnesses find the services started.
    /// </summary>
    private void StartServicesOnce()
    {
        if (ServiceTick.Step != 0) return;  // a service tick ran in this process already: the services are started
        var previous = _tick.Watchdog;
        _tick.Watchdog = new Watchdog { Budget = ServicesStartBudget > _options.CallTimeout ? ServicesStartBudget : _options.CallTimeout, Subject = "the services' start" };
        try
        {
            _tick.Invoke(() =>
            {
                ServiceTick.Run(HostAccess.Tick(MenuStepBase + ++_bootTicks, 0, 0, 0, 0));
                return 0;
            });
        }
        finally
        {
            _tick.Watchdog = previous;
        }
    }

    /// <summary>The harness ends, before the players and the world go.</summary>
    private void EndRecordingFakes()
    {
        SpawnFake.Ended();
        ItemsFake?.Ended();
    }
}
