// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Abilities (impacts, programs, natives, sequences, buffs, abilities) by the contract.</summary>
public sealed class AbilitiesContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Abilities).Assembly);
        Assert.False(Abilities.Available);
        Assert.Null(Abilities.Status);
        AssertOffline("kiln.abilities");
        foreach (var name in (string[])["abilities.attributes", "abilities.instances", "abilities.natives", "abilities.programs", "abilities.sequences"])
            AssertOffOffline(name, "needs kiln.abilities");
        Assert.Equal(CapabilityState.Unavailable, Capabilities.Get("abilities.buffs").State);
        Assert.NotEqual(CapabilityReason.Unknown, Capabilities.Get("abilities.buff_list").Reason);
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.abilities", () => Abilities.PlaySequence(new EntityId(5000), Guid.NewGuid()));
        AssertUnavailable("kiln.abilities", () => Abilities.Programs.Refresh());
        AssertUnavailable("kiln.abilities", () => Abilities.Natives.Observe("applyDamage"));
    }

    [Fact]
    public void A_refused_cast_is_a_kiln_result()
    {
        IKilnResult refused = CastResult.Refused("on cooldown", TimeSpan.FromSeconds(3));
        Assert.False(refused.Ok);
        Assert.Equal("on cooldown", refused.Reason);
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        var play = Method(typeof(Abilities), "PlaySequence", typeof(EntityId), typeof(Guid), typeof(SequenceOptions));
        AssertHas<TickThreadOnlyAttribute>(play);
        AssertRequires(play, "kiln.abilities", "abilities.sequences");
        AssertHas<AnyThreadAttribute>(Property(typeof(Abilities), "Status"));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(Abilities), "Cast", typeof(EntityId), typeof(string), typeof(CastOrigin)));
        AssertRequires(Method(typeof(Abilities.Programs), "Refresh"), "kiln.abilities");
    }
}

/// <summary>Kiln.Abilities' writes on a fake kiln.abilities table: an attribute added posts the same change event as
/// Kiln.Combat's attribute writes, an impact variable written posts <see cref="ImpactVariableChangedEvent"/>, each only for
/// a change.</summary>
public sealed unsafe class AbilitiesChangeTests : FakeTableTest
{
    private static int _value;
    private static float _var;
    private static uint _program;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAttrGet(uint e, uint component, uint attribute, int* v)
    {
        *v = _value;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAttrAdd(uint e, uint component, uint attribute, int delta)
    {
        _value += delta;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeVars(uint id, AbilitiesNative.Var* o, uint cap, uint* count)
    {
        *count = 1;
        if (o == null || cap < 1) return 0;
        o->Index = 0;
        o->ValueSize = 4;
        var name = Encoding.UTF8.GetBytes("Damage1");
        for (var i = 0; i < name.Length; i++) o->Name[i] = name[i];
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeRead(uint id, uint index, void* dst, uint cap, uint* len)
    {
        *(float*)dst = _var;
        *len = 4;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeWrite(uint id, uint index, void* src, uint len)
    {
        _var = *(float*)src;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeInfo(uint id, uint* self, uint* program, uint* flags)
    {
        *self = 300;
        *program = _program;
        *flags = 1;
        return 0;
    }

    public AbilitiesChangeTests()
    {
        _value = 10;
        _var = 1;
        _program = 0xabc;
        var t = Alloc<AbilitiesNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(AbilitiesNative.Table), Version = 1 };
        t->AttrGet = &FakeAttrGet;
        t->AttrAdd = &FakeAttrAdd;
        t->InstanceVars = &FakeVars;
        t->InstanceRead = &FakeRead;
        t->InstanceWrite = &FakeWrite;
        t->InstanceInfo = &FakeInfo;
        Serve("kiln.abilities", t, typeof(AbilitiesNative), "_t");
    }

    [Fact]
    public void Adding_to_an_attribute_posts_the_combat_change_and_adding_nothing_posts_nothing()
    {
        var seen = new List<CombatChangedEvent>();
        using var sub = Events.On<CombatChangedEvent>(seen.Add);
        var e = new EntityId(300);
        Assert.True(Abilities.Attributes.Add(e, AbilityResource.Mana, 5));
        Assert.True(Abilities.Attributes.Add(e, AbilityResource.Mana, 0));
        var ev = Assert.Single(seen);
        Assert.Equal(("Attribute.Mana", 10, 15, "writer"), (ev.What, ev.Before, ev.After, ev.Origin));
    }

    [Fact]
    public void Writing_an_impact_variable_posts_one_event_and_writing_the_same_value_none()
    {
        var seen = new List<ImpactVariableChangedEvent>();
        using var sub = Events.On<ImpactVariableChangedEvent>(seen.Add);
        var instance = new ImpactInstance(7);
        var v = instance.Find("Damage1")!;
        Assert.True(instance.Write(v, BitConverter.GetBytes(2f)));
        Assert.True(instance.Write(v, BitConverter.GetBytes(2f)));
        var ev = Assert.Single(seen);
        Assert.Equal((7u, 0xabcu, new EntityId(300), "Damage1", "writer"), (ev.ImpactId, ev.ProgramId, ev.Self, ev.Variable, ev.Origin));
    }

    [Fact]
    public void A_change_that_waited_is_dropped_when_the_instance_id_names_another_by_then()
    {
        var seen = 0;
        var instance = new ImpactInstance(7);
        var v = instance.Find("Damage1")!;
        using var sub = Events.On<ImpactVariableChangedEvent>(_ =>
        {
            if (seen++ == 0) instance.Write(v, BitConverter.GetBytes(3f));  // waits for the next step
        });
        instance.Write(v, BitConverter.GetBytes(2f));
        Assert.Equal(1, ImpactChanges.Notices.Waiting);
        _program = 0xdef;  // the instance ended and its id went to an instance of another program
        Step();
        Assert.Equal(1, seen);
    }
}
