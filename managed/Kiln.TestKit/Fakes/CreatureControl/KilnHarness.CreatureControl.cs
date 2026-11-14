// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessCreatureControl? _creatureControl;

    /// <summary>The kit's fake kiln.creature_control of this harness: what a test scripts of the bodies a mod leases, and
    /// what the mod asked. Since 0.2.0-dev.28.</summary>
    public HarnessCreatureControl CreatureControl => _creatureControl ??= new HarnessCreatureControl(this);
}

/// <summary>
/// The fake kiln.creature_control of a harness (<see cref="KilnHarness.CreatureControl"/>): Kiln.CreatureControl's own
/// code (<c>CreatureControl.Acquire</c>, <c>WaitActive</c>, <c>Inspect</c>, <c>Release</c>, <c>ControlChangedEvent</c>)
/// runs on it. Since 0.2.0-dev.28.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> Bodies are entities of the fake world (<see cref="KilnHarness.Entities"/>) in the engine's
/// counter range (from 1024; players are refused). One lease per body. A lease is Pending at its acquire and Active two
/// steps later (the step the broker removes the body's SteeringAgent in, and the Pre that checks it), with transition
/// sequence 1, 2, ... per body; while the body is busy (<see cref="SetBusy"/>: its current action keeps playing) it waits,
/// and after 5 s of simulation time (125 steps) it fails with Quiescence. A release ends drive permission at once; the
/// lease is Released four steps later (three steps of zeroed input and the restore's check), or Canceled at the next step
/// when it was never Active. A body destroyed in the fake world fails its leases BodyGone at the next step. Terminal
/// leases stay queryable for 10 minutes of simulation time. Switching the row <c>creature_control</c> off
/// (<c>Capabilities.Override</c>) makes new binds and admissions refuse as the native service does.</para>
/// <para><b>Not modelled</b> (offline): the game's components (no SteeringAgent is removed, no input zeroed), the roles
/// PetCombat, Vehicle and EngineGuard (refused RoleUnavailable, as this build's native service does), preemption,
/// quarantine, retirement and the retired-root table.</para>
/// </remarks>
public sealed class HarnessCreatureControl
{
    private readonly KilnHarness _harness;

    internal HarnessCreatureControl(KilnHarness harness) => _harness = harness;

    private CreatureControlModel Model => CreatureControlFake.ModelOf(_harness);

    /// <summary>Makes <paramref name="body"/>'s current action keep playing (true) or end (false): a lease on it stays
    /// Pending while it plays, and fails Quiescence after 5 s.</summary>
    public void SetBusy(EntityId body, bool busy = true) => Model.SetBusy(body.Value, busy);

    /// <summary>True while the broker holds <paramref name="body"/> (its AI-off layer and the SteeringAgent removal, in the
    /// game).</summary>
    public bool IsControlled(EntityId body) => Model.Controlled(body.Value);

    /// <summary>Every call mods made, in order.</summary>
    public IReadOnlyList<FakeControlCall> Calls => Model.Calls;
}
