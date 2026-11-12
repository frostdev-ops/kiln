// SPDX-License-Identifier: MIT
// The capability rows of item instances and InstanceData, read back with the public Capabilities.Get: saves.instance_data
// in each state it can be in (driven through the harness, the fake item source and the runtime's internals),
// saves.instance_data.carried, and items.instances (its decision tested without the native table, and its real row in the
// state this process has: no kiln.items). Capabilities.Reset is never called: it would drop the rows Kiln.Items reported
// once. The attributes are read by reflection, so a later edit that drops one fails.
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Kiln;

[Collection("instance data")]
public sealed class InstanceCapabilityTests : IDisposable
{
    private readonly InstanceHarness _h = new();
    private static readonly FieldInfo State = typeof(SavesRuntime).GetField("_state", BindingFlags.NonPublic | BindingFlags.Static)!;
    private const string Row = "saves.instance_data";

    public void Dispose() => _h.Dispose();

    private static Capability Refreshed()
    {
        InstanceCapabilities.Refresh();   // what the pump does about once a second
        return Capabilities.Get(Row);
    }

    [Fact]
    public void AvailableWhenTheItemSourceIsOnAndTheWorldsDataIsOpen()
    {
        var row = Refreshed();
        Assert.Equal((CapabilityState.Available, CapabilityReason.None), (row.State, row.Reason));
        Assert.True(Capabilities.Is(Row));
    }

    [Fact]
    public void UnavailableWithKilnSavesReasonWhenKilnSavesIsNotThere()
    {
        var saves = Capabilities.Get(SavesNative.Service);
        Assert.Equal(CapabilityState.Unavailable, saves.State);        // no native kiln.saves in a test process
        State.SetValue(null, 2);
        try
        {
            var row = Refreshed();
            Assert.Equal((CapabilityState.Unavailable, saves.Reason, saves.ReasonText), (row.State, row.Reason, row.ReasonText));
        }
        finally
        {
            State.SetValue(null, 1);
        }
        Assert.Equal(CapabilityState.Available, Refreshed().State);
    }

    [Fact]
    public void UnavailableNamingItemsInstancesWhenTheItemSourceIsOffAndBackWhenItIsOn()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Items).TypeHandle);
        _h.World.Off = true;
        var row = Refreshed();
        Assert.Equal(CapabilityState.Unavailable, row.State);
        Assert.Equal(Capabilities.Get(Items.Caps.Instances).Reason, row.Reason);   // the reason of items.instances' own row
        Assert.Equal("needs items.instances: off in the test", row.ReasonText);
        _h.World.Off = false;                                                    // the cause goes: the same owner reports again
        Assert.Equal((CapabilityState.Available, CapabilityReason.None, ""), (Refreshed() is var back ? (back.State, back.Reason, back.ReasonText) : default));
    }

    [Fact]
    public void UnavailableNamingKilnSpawnWhenHolderIdentityIsOff()
    {
        _h.World.Off = true;
        _h.World.OffCapability = "kiln.spawn";
        _h.World.OffReason = "item holders need entity inspection, template metadata and holder search";
        var row = Refreshed();
        Assert.Equal(CapabilityState.Unavailable, row.State);
        Assert.Equal("needs kiln.spawn: item holders need entity inspection, template metadata and holder search", row.ReasonText);
        // kiln.spawn's own reason when its row says it is off, else the table is there without what is needed
        var spawn = Capabilities.Get("kiln.spawn");
        Assert.Equal(spawn is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } ? spawn.Reason : CapabilityReason.ServiceOlder, row.Reason);
    }

    [Fact]
    public void UnavailableNamingItemsInstancesWhenThereIsNoItemSourceAtAll()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Items).TypeHandle);
        Store.Instances.World = null;   // the setter refreshes the row
        var row = Capabilities.Get(Row);
        // items.instances' own reason when its row says it is off (here: no kiln.items), else service-missing
        var items = Capabilities.Get(Items.Caps.Instances);
        var expected = items is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } ? items.Reason : CapabilityReason.ServiceMissing;
        Assert.Equal((CapabilityState.Unavailable, expected), (row.State, row.Reason));
        Assert.StartsWith("needs items.instances: ", row.ReasonText);
        Store.Instances.World = _h.World;
        Assert.Equal(CapabilityState.Available, Capabilities.Get(Row).State);
    }

    private static WorldStore Store => SavesRuntime.Store;

    [Fact]
    public void PendingWhileTheWorldsDataIsNotOpenAndAgainAfterAHostedWorldClosed()
    {
        SavesRuntime.LabCloseWorld();                    // a hosted world's close: the next one is not loaded yet
        var row = Capabilities.Get(Row);
        Assert.Equal((CapabilityState.Pending, CapabilityReason.Session, InstanceCapabilities.WaitingText), (row.State, row.Reason, row.ReasonText));
        Assert.True(row.SessionDependent);
        SavesRuntime.LabWorldLoaded("caps-world-two", 1, 0, newWorld: false);
        Assert.Equal(CapabilityState.Available, Capabilities.Get(Row).State);
        SavesRuntime.LabCloseWorld();                    // and the next close
        Assert.Equal(CapabilityState.Pending, Capabilities.Get(Row).State);
        SavesRuntime.LabWorldLoaded("caps-world-three", 1, 0, newWorld: false);
        Assert.Equal(CapabilityState.Available, Capabilities.Get(Row).State);
    }

    [Fact]
    public void CarriedIsUnavailableByPolicyWithItsSentence()
    {
        var row = Capabilities.Get(InstanceData.CarriedCapability);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Policy), (row.State, row.Reason));
        Assert.Equal(InstanceData.CarriedReason, row.ReasonText);
        Assert.Contains("Persist.World", row.ReasonText);
    }

    // ---- items.instances (Kiln.Items has no test project; its decision is pure and tested here) ----

    [Fact]
    public void ItemsInstancesIsLoweredWithoutATableWithoutV4AndWhenTheFlagIsClear()
    {
        Assert.Equal((Items.InstancesVerdict.NoTable, "kiln.items is not loaded"), Items.DecideInstances(null, false, false, ""));
        Assert.Equal((Items.InstancesVerdict.NoV4, "kiln.items v3 has no item instances (update Kiln)"), Items.DecideInstances(3, false, false, ""));
        Assert.Equal((Items.InstancesVerdict.Off, "stamp table full"), Items.DecideInstances(4, true, false, "stamp table full"));
        Assert.Equal(Items.InstancesVerdict.Off, Items.DecideInstances(4, true, false, "").Verdict);
        Assert.Equal((Items.InstancesVerdict.On, ""), Items.DecideInstances(4, true, true, "ignored when on"));   // nothing lowered
    }

    [Fact]
    public void ItemsInstancesRowInThisProcessIsLoweredWithKilnItemsReason()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Items).TypeHandle);   // Kiln.Items' start reports its rows
        var items = Capabilities.Get(Items.Caps.Service);
        var row = Capabilities.Get(Items.Caps.Instances);
        Assert.Equal(CapabilityState.Unavailable, items.State);           // no native kiln.items in a test process
        Assert.Equal((CapabilityState.Unavailable, items.Reason), (row.State, row.Reason));
        Assert.StartsWith("kiln.items is unavailable", row.ReasonText);
    }

    // ---- the attributes ----

    [Fact]
    public void TheTenMembersNameTheirCapability()
    {
        static string? Needs(Type t, string name) =>
            t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name)
                .Select(m => m.GetCustomAttribute<RequiresCapabilityAttribute>()?.Name).Distinct().Single();
        foreach (var (type, name) in new[] { (typeof(Items), "Held"), (typeof(Items), "Equipped"), (typeof(ItemInstanceExtensions), "Location") })
            Assert.True(Needs(type, name) == "items.instances", $"{type.Name}.{name}");
        foreach (var (type, name) in new[]
                 {
                     (typeof(InstanceData), "All"), (typeof(InstanceData), "Parked"), (typeof(ItemInstanceDataExtensions), "Get"),
                     (typeof(ItemInstanceDataExtensions), "TryGet"), (typeof(ItemInstanceDataExtensions), "Has"),
                     (typeof(ItemInstanceDataExtensions), "Set"), (typeof(ItemInstanceDataExtensions), "Remove"),
                 })
            Assert.True(Needs(type, name) == "saves.instance_data", $"{type.Name}.{name}");
        Assert.Null(Needs(typeof(InstanceData), "Key"));               // declaring a key needs nothing
        Assert.Null(Needs(typeof(Items), "LabFollow"));
    }
}
