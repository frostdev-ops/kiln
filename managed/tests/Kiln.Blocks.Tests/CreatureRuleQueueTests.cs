// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.BlockTests;

public sealed unsafe class CreatureRuleQueueTests
{
    // Fake the native lifetime-aware drain, including destruction/reuse by an earlier managed callback.
    private static readonly Queue<(uint Id, uint Entity, ulong Life)> Pending = [];
    private static readonly Dictionary<uint, ulong> Lives = [];
    private static uint _maxCapacity;
    private static int _dropped;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Drain(CreaturesNative.RuleWork* output, uint capacity)
    {
        _maxCapacity = Math.Max(_maxCapacity, capacity);
        uint n = 0;
        while (n < capacity && Pending.TryDequeue(out var work))
        {
            if (!Lives.TryGetValue(work.Entity, out var life) || life != work.Life)
            {
                _dropped++;
                continue;
            }
            output[n++] = new() { Kind = CreaturesNative.WorkAction, Entity = work.Entity, Id = work.Id };
        }
        return n;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actions_validate_lifetimes_after_each_callback(bool replace)
    {
        var handle = new ArsenalHandle(Guid.NewGuid(), Creatures.OwnerKey.Named("queue-test"), "queue-test");
        var ran = new List<uint>();
        var first = Act.Custom(npc =>
        {
            ran.Add(1);
            if (replace) Lives[2]++;  // DestroyEntity(2), followed by a new entity at exactly the same ID/address.
        });
        var second = Act.Custom(npc => ran.Add(2));
        var third = Act.Custom(npc => ran.Add(3));
        var firstId = RulePump.Bind(first, handle)["id"]!.GetValue<uint>();
        var secondId = RulePump.Bind(second, handle)["id"]!.GetValue<uint>();
        var thirdId = RulePump.Bind(third, handle)["id"]!.GetValue<uint>();
        Lives.Clear();
        Lives[1] = Lives[2] = Lives[3] = 1;
        Pending.Clear();
        Pending.Enqueue((firstId, 1, 1));
        Pending.Enqueue((secondId, 2, 1));
        Pending.Enqueue((thirdId, 3, 1));
        _maxCapacity = 0;
        _dropped = 0;
        var table = new CreaturesNative.Table { RulesDrain = &Drain };
        *(uint*)&table = (uint)sizeof(CreaturesNative.Table);
        var field = typeof(CreaturesNative).GetField("_t", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try
        {
            field.SetValue(null, Pointer.Box(&table, typeof(CreaturesNative.Table*)));
            RulePump.Pump();
            Assert.Equal(replace ? new uint[] { 1, 3 } : [1, 2, 3], ran);
            Assert.Equal(replace ? 1 : 0, _dropped);
            Assert.Equal(1u, _maxCapacity);
            Assert.Equal(replace ? 0 : 1, RulePump.RunsOf(second));
            Assert.Empty(Pending);
        }
        finally
        {
            RulePump.Release(handle);
            field.SetValue(null, previous);
        }
    }
}
