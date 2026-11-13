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
    }

    /// <summary>The native services' Pre, after the players' scan: what each fake's game does at the start of a step.</summary>
    private void RecordingFakesBeforeStep(ulong step)
    {
        SpawnFake?.BeforeStep();
        ChatFake?.BeforeStep(step);
    }

    /// <summary>The harness ends, before the players and the world go.</summary>
    private void EndRecordingFakes()
    {
        SpawnFake.Ended();
    }
}
