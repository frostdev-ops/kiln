// SPDX-License-Identifier: MIT
// Kiln.ActionSpeed (kiln.combat.speed, mod API 0.2.28): the kinds' numbers, per-mod values and their product, clamping,
// authority, unload, the wire.
using Kiln;
using Xunit;

namespace Kiln.Contract.Tests;

public sealed class ActionKindTests
{
    [Fact]
    public void The_kinds_keep_the_native_and_wire_numbers()
    {
        // kiln_action_speed.h KILN_ACTION_MELEE/BOW/TOOL and the kiln:speed wire carry these numbers; append only.
        Assert.Equal(0, (int)ActionKind.Melee);
        Assert.Equal(1, (int)ActionKind.Bow);
        Assert.Equal(2, (int)ActionKind.Tool);
        Assert.Equal(["Melee", "Bow", "Tool"], Enum.GetNames<ActionKind>());
    }
}
