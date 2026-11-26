// SPDX-License-Identifier: MIT
// The revive channel (19d §4.5, §5.1): package 6. Until it lands a downed pet stays downed (release is the way out).
namespace Kiln;

internal sealed partial class PetRoster
{
    private void CancelChannel(PetState p, PetChangeCause cause)
    {
        if (p.ReviveOperation is null) return;
        p.ReviveOperation = null;
        p.ReviveElapsed = p.ReviveDuration = 0;
    }

    private void StartRevive(Op op, PetState p) => Complete(op, false, "ReviveUnavailable: Kiln's revive channel is not in this Kiln yet", p);

    private void AdvanceRevive(Op op, PetState p) { }

    private void CancelRevive(Op op, PetState p) => Complete(op, false, "NoChannel: no revive channel runs", p);
}
