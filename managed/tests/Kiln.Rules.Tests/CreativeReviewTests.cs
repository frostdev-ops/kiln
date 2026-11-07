// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kiln.Rules.Tests;

public sealed class CreativeReviewTests
{
    private static readonly EntityId P = new(3);
    private static ItemInfo Item(bool gear = false) => new(new ItemId(7), "Material_Wood", null, gear ? 1 : 100,
        gear ? ItemCategory.Weapons : ItemCategory.Materials, ItemRarity.Common, gear, 2, 55, 0, 0, Guid.Empty);

    [Theory]
    [InlineData(int.MinValue)] [InlineData(-1)] [InlineData(0)] [InlineData(101)] [InlineData(int.MaxValue)]
    public void GiveRejectsInvalidCountsInsteadOfClamping(int count) => Assert.NotNull(CreativeTools.ValidateGive(Item(), count, null, null));

    [Fact]
    public void GiveValidatesServerItemAndGearBounds()
    {
        Assert.Null(CreativeTools.ValidateGive(Item(), 100, null, null));
        Assert.Null(CreativeTools.ValidateGive(Item(true), 1, 55, ItemRarity.Legendary));
        Assert.NotNull(CreativeTools.ValidateGive(Item(true), 2, null, null));
        Assert.NotNull(CreativeTools.ValidateGive(Item(true), 1, 1, null));
        Assert.NotNull(CreativeTools.ValidateGive(Item(true), 1, 56, null));
        Assert.NotNull(CreativeTools.ValidateGive(Item(true), 1, null, (ItemRarity)255));
        Assert.NotNull(CreativeTools.ValidateGive(Item(), 1, 1, ItemRarity.Common));
        Assert.NotNull(CreativeTools.ValidateGive(Item() with { Name = "Item_Debug" }, 1, null, null));
        Assert.NotNull(CreativeTools.ValidateGive(Item(true) with { LevelMin = 300, LevelMax = 500 }, 1, 255, null));
    }

    [Fact]
    public async Task NonAdminOrVanillaCannotEnableCreativeTakeItemsOrChangeTime()
    {
        Assert.False(Creative.Set(P, true).Ok); // no peer with the rules channel
        Assert.False(Creative.SetFlight(P, CharacterFlightMode.Unlimited).Ok);
        Assert.False(CreativeTools.HasPermission(P));
        Assert.False(CreativeTools.HasPermission(EntityId.None));
        Assert.False((await CreativeTools.Give(P, new ItemId(7))).Ok);
        Assert.False(CreativeTools.SetTime(P, 12).Ok);
        Assert.False(CreativeTools.SetCycle(P, TimeCycle.Fast).Ok);
        Assert.Null(CreativeTools.Found(P)); // no joined player's map, not an empty successful Journey read
    }

    [Fact]
    public void GiveAdmissionBoundsInflightRateAndRejectsOldWorldCompletion()
    {
        var g = new CreativeGiveGate();
        Assert.True(g.TryEnter(P, 1000, out var first));
        Assert.False(g.TryEnter(P, 2000, out _)); // outstanding transaction, irrespective of elapsed time
        g.Complete(P, first);
        Assert.False(g.TryEnter(P, 1100, out _));
        Assert.True(g.TryEnter(P, 1250, out var next));
        g.Clear();
        Assert.True(g.TryEnter(P, 2000, out var current));
        g.Complete(P, next);
        Assert.False(g.TryEnter(P, 3000, out _));
        g.Complete(P, current);
        Assert.True(g.TryEnter(P, 3000, out _));
    }

    [Fact]
    public void InboxBoundsMemoryPerPlayerAndRateAndPreservesAuthenticatedIdentity()
    {
        var q = new CreativeInbox();
        var ask = new CreativeRequest(CreativeAsk.Catalog, Item: uint.MaxValue);
        Assert.False(q.Enqueue(EntityId.None, ask, false, at: 0));
        Assert.False(q.Enqueue(P, new((CreativeAsk)255), false, at: 0));
        for (var p = 1; p <= 32; p++)
            for (var i = 0; i < 4; i++) Assert.True(q.Enqueue(new EntityId((uint)p), ask, false, at: 0));
        Assert.Equal(128, q.Count);
        Assert.False(q.Enqueue(new EntityId(33), ask, false, at: 0));
        Assert.True(q.TryDequeue(out var r));
        Assert.Equal(new EntityId(1), r.Player); // Item cannot select the receiving player
        Assert.Equal(ask, r.Req);
        q.Clear();
        for (var i = 0; i < 64; i++)
        {
            Assert.True(q.Enqueue(P, ask, false, at: 0));
            Assert.True(q.TryDequeue(out _));
        }
        Assert.False(q.Enqueue(P, ask, false, at: 999));
        Assert.True(q.Enqueue(P, ask, false, at: 1000));
        q.Clear();
        Assert.True(q.Enqueue(P, new(CreativeAsk.Found), false, at: 0));
        Assert.True(q.TryDequeue(out _));
        Assert.False(q.Enqueue(P, new(CreativeAsk.Found), false, at: 1999));
        Assert.True(q.Enqueue(P, new(CreativeAsk.Found), false, at: 2000));
    }

    [Fact]
    public void ReplyCompletionsWaitForTickAndCannotCrossWorlds()
    {
        var ran = 0;
        CreativeNet.Post(() => ran++, CreativeNet.Session);
        Assert.Equal(0, ran);
        typeof(CreativeNet).GetMethod("Drain", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        Assert.Equal(1, ran);
        var old = CreativeNet.Session;
        CreativeNet.Post(() => ran++, old);
        CreativeNet.WorldClosed();
        CreativeNet.Post(() => ran++, old);
        typeof(CreativeNet).GetMethod("Drain", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void UnsafeCraftAndConsumptionFallbacksAreDeniedWithoutMaskingOtherRules()
    {
        var requested = new EffectiveRules { Player = P, FreeCrafting = true, KeepConsumables = true, NoFallDamage = true,
            Denied = ["invulnerable"] };
        var supported = RulesCrafting.Supported(requested, hooks: 0);   // no native hook: fail closed
        Assert.False(supported.FreeCrafting);
        Assert.False(supported.KeepConsumables);
        Assert.True(supported.NoFallDamage);
        Assert.Equal(new[] { "invulnerable", "freeCraftingUnavailable", "keepConsumablesUnavailable" }, supported.Denied);
        Assert.Equal((0UL, 0UL), RulesCrafting.Counters);
        Assert.Same(supported, RulesCrafting.Supported(supported, 0));
        var limited = RulesMath.Combine(P, [(null, new PlayerRules { FreeCrafting = true, KeepConsumables = true })],
            RulesLimits.Default with { AllowFreeCrafting = false, AllowKeepConsumables = false });
        Assert.Same(limited, RulesCrafting.Supported(limited, 0));
    }

    [Fact]
    public void NativeCraftAndConsumeHooksEnableTheRulesOneByOne()
    {
        var requested = new EffectiveRules { Player = P, FreeCrafting = true, KeepConsumables = true };
        Assert.Same(requested, RulesCrafting.Supported(requested, RulesNative.HookCraft | RulesNative.HookConsume));
        var craftOnly = RulesCrafting.Supported(requested, RulesNative.HookCraft);
        Assert.True(craftOnly.FreeCrafting);
        Assert.False(craftOnly.KeepConsumables);
        Assert.Equal(new[] { "keepConsumablesUnavailable" }, craftOnly.Denied);
        // food and potions alone (the ammunition sites missing on a build) still keep what they can
        Assert.True(RulesCrafting.Supported(requested, RulesNative.HookCraft | RulesNative.HookConsumeBuff).KeepConsumables);
        var native = requested.Native.Flags;
        Assert.Equal(RulesNative.FreeCrafting | RulesNative.KeepConsumables, native & (RulesNative.FreeCrafting | RulesNative.KeepConsumables));
        Assert.Equal(0u, new EffectiveRules { Player = P }.Native.Flags & (RulesNative.FreeCrafting | RulesNative.KeepConsumables));
    }

    [Fact]
    public void AllLargeCataloguePagesAreReachableAndSearchHandlesUnicodeWhitespace()
    {
        var items = Enumerable.Range(1, 4213).Select(i => new CreativeItem((uint)(0x80000000L + i), $"Item_{i}", $"Item {i}",
            ItemCategory.Materials, ItemRarity.Common, false, 0, 0, 100)).ToArray();
        var pages = Enumerable.Range(0, (items.Length + CreativeMenu.PageSize - 1) / CreativeMenu.PageSize)
            .Select(p => CreativeMenu.Page(items, p).ToArray()).ToArray();
        Assert.All(pages, p => Assert.InRange(p.Length, 1, CreativeMenu.PageSize));
        Assert.Equal(items, pages.SelectMany(p => p));
        Assert.True(CreativeCatalog.Matches("\tWood\u2003logs\n", "Wood Logs", "Wood_Log"));
    }

    [Fact]
    public void JourneyCannotTreatUnknownKnowledgeAsFoundAndNetworkCallbacksLeaveRenderStateAlone()
    {
        Assert.False(CreativeCatalog.FoundMatches(true, null, 7));
        Assert.False(CreativeCatalog.FoundMatches(true, new HashSet<uint>(), 7));
        Assert.True(CreativeCatalog.FoundMatches(true, new HashSet<uint> { 7 }, 7));
        Assert.True(CreativeCatalog.FoundMatches(false, null, 7));
        var cache = typeof(CreativeMenu).GetField("_shownFor", BindingFlags.Static | BindingFlags.NonPublic)!;
        var before = cache.GetValue(null);
        CreativeMenu.OnCatalog(new CreativeCatalogMsg(0, 0, []));
        CreativeMenu.OnFound(new CreativeFoundMsg(false, []));
        Assert.Equal(before, cache.GetValue(null));
        Assert.Equal(typeof(uint), typeof(CreativeMenu).GetField("_selected", BindingFlags.Static | BindingFlags.NonPublic)!.FieldType);
        CreativeMenu.OnDisconnected();
    }

    [Theory]
    [InlineData(-1)] [InlineData(16381)] [InlineData(int.MaxValue)]
    public void InvalidJourneyCountsAreRejected(int count)
    {
        var w = new NetWriter().Bool(true).Int32(count);
        Assert.Throws<FormatException>(() => { var r = new NetReader(w.ToArray()); CreativeNet.DecodeFound(ref r); });
    }

    [Theory]
    [InlineData(-1, 4000, 1)] [InlineData(0, -1, 1)] [InlineData(0, 16385, 1)]
    [InlineData(3999, 4000, 2)] [InlineData(0, 4000, 151)]
    public void InvalidCataloguePagesAreRejectedBeforeAllocatingRows(int start, int total, int count)
    {
        var w = new NetWriter().Int32(start).Int32(total).UInt16((ushort)count);
        Assert.Throws<FormatException>(() => { var r = new NetReader(w.ToArray()); CreativeNet.DecodeCatalog(ref r); });
    }

    [Fact]
    public void FastSpeedCannotOverflowTimeSpanAddition() => Assert.Equal(1000, Time.FastSpeed(TimeSpan.MaxValue, TimeSpan.MaxValue));
}

public sealed unsafe class TimeAbiReviewTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public uint Size, Version;
        public nint Get, Set, Weather;
        public delegate* unmanaged[Cdecl]<float, int> Scale;
        public delegate* unmanaged[Cdecl]<float> Hold;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static float Held() => 1;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Set(float _) => 0;

    [Fact]
    public void OldTimeTableAndMissingPointersFailSoftWhileNewTailRemainsCompatible()
    {
        var field = typeof(Time).GetField("_table", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        Table t = new() { Size = 32, Version = 1, Scale = &Set, Hold = &Held };
        try
        {
            field.SetValue(null, Pointer.Box(&t, field.FieldType));
            Assert.True(Time.Available); // the old get/set/weather prefix still loads
            Assert.False(Time.CanSetSpeed);
            Assert.Null(Time.HeldSpeed);
            Assert.Throws<NotSupportedException>(() => Time.SetSpeed(0));
            t.Size = (uint)sizeof(Table);
            Assert.True(Time.CanSetSpeed);
            Assert.Equal(TimeCycle.Normal, Time.Cycle);
            t.Scale = null;
            Assert.False(Time.CanSetSpeed);
            t.Scale = &Set;
            t.Hold = null;
            Assert.False(Time.CanSetSpeed);
        }
        finally { field.SetValue(null, previous); }
    }
}
