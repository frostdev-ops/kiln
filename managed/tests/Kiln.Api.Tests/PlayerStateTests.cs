// SPDX-License-Identifier: MIT
using Kiln;
using Xunit;

namespace Kiln.Api.Tests;

public class PlayerStateTests
{
    [Fact]
    public void A_state_id_is_its_source_and_its_raw_value()
    {
        Assert.Equal(0x0125, (ushort)PlayerStateId.HangGliding);
        Assert.Equal(PlayerStateSource.Actor, PlayerStates.SourceOf(PlayerStateId.HangGliding));
        Assert.Equal(PlayerStateSource.Locomotion, PlayerStates.SourceOf(PlayerStateId.LocomotionFalling));
        Assert.Equal(PlayerStateSource.Derived, PlayerStates.SourceOf(PlayerStateId.Airborne));
        Assert.Equal(PlayerStateSource.Input, PlayerStates.SourceOf(PlayerStateId.InputJump));
    }

    [Fact]
    public void The_plain_names_are_the_games_states()
    {
        Assert.Equal(PlayerStateId.HangGliding, PlayerStateId.Gliding);
        Assert.Equal(PlayerStateId.InFog, PlayerStateId.InShroud);
        Assert.Equal(PlayerStateId.InSneakMode, PlayerStateId.Sneaking);
        Assert.Equal(PlayerStateId.UnderWater, PlayerStateId.Underwater);
        Assert.Equal(PlayerStateId.LocomotionSwimming, PlayerStateId.Swimming);
        Assert.Equal(PlayerStateId.IsSprinting, PlayerStateId.Sprinting);
    }

    [Fact]
    public void An_item_compares_with_an_id()
    {
        var mushroom = new PlayerItem(0x23d06ec5);
        Assert.True(mushroom == 0x23d06ec5u);
        Assert.True(0x23d06ec5u == mushroom);
        Assert.False(mushroom != 0x23d06ec5u);
        Assert.True(mushroom == new PlayerItem(0x23d06ec5));
        Assert.False(mushroom == 1u);
        Assert.True(new PlayerItem(0).IsNone);
    }

    [Fact]
    public void No_item_matches_no_name()
    {
        Assert.False(new PlayerItem(0) == "");
        Assert.False(new PlayerItem(0) == (string?)null);
        Assert.Equal("(none)", new PlayerItem(0).ToString());
    }

    [Fact]
    public void A_buff_compares_with_a_GameBuffs_constant()
    {
        var b = new PlayerBuff(0xc6a35bfe, "Food");
        Assert.True(b.Is("0xc6a35bfe"));
        Assert.True(b.Is(0xc6a35bfeu));
        Assert.False(b.Is("0xc6a35bff"));
        Assert.False(b.Is("Food"));
    }

    [Fact]
    public void Without_the_service_a_state_is_unknown_not_false()
    {
        Assert.False(PlayerStates.Available);
        Assert.Null(PlayerStates.Read(new EntityId(1)));
        Assert.Null(PlayerStates.Check(new EntityId(1), PlayerStateId.Gliding));
        Assert.False(PlayerStates.Is(new EntityId(1), PlayerStateId.Gliding));
        Assert.Empty(PlayerStates.BuffsOf(new EntityId(1)));
        Assert.Null(PlayerStates.EquipmentOf(new EntityId(1)));
    }

    [Fact]
    public void Equipment_slots_are_in_the_games_field_order()
    {
        // keen::ecs::EquipmentSlotData: checked against the running game by the watch, here against this list
        Assert.Equal(0, (int)PlayerEquipSlot.VanityHead);
        Assert.Equal(5, (int)PlayerEquipSlot.ArmourHead);
        Assert.Equal(18, (int)PlayerEquipSlot.Glider);
        Assert.Equal(19, (int)PlayerEquipSlot.Backpack);
    }
}
