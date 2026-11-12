// SPDX-License-Identifier: MIT
extern alias khooks;
using Xunit;
using HooksApi = khooks::Kiln.Hooks;

namespace Kiln.Hooks.Tests;

/// <summary>Every way a context ends revokes its managed links one by one (they are under the per-mod-id hooks owner,
/// which nothing revokes as a whole), and a context that has ended cannot add a link.</summary>
[Collection("kiln.hooks state")]
public sealed class RetireTests : IDisposable
{
    static int _seq;
    readonly string _id = $"retire-mod-{Interlocked.Increment(ref _seq)}";
    static readonly nuint Address = unchecked((nuint)0x140002000UL);
    readonly Func<string, ulong> _createOwner = HookCookies.CreateOwner;

    public RetireTests()
    {
        FakeRegistry.Install();
        HookCookies.CreateOwner = _ => 0x6000;
    }

    public void Dispose()
    {
        HookCookies.CreateOwner = _createOwner;
        FakeRegistry.Uninstall();
    }

    static nuint PassThrough(HookCall call) => call.CallOriginal();
    static void Nothing(ref Registers r) { }

    void AssertNothingLeft(ModContext mod)
    {
        Assert.Empty(FakeRegistry.Live);
        Assert.DoesNotContain(HooksApi.Detours.Values, r => r.Mod == mod);
        Assert.DoesNotContain(HooksApi.Mids.Values, r => r.Mod == mod);
        Assert.Equal(HookCookies.MadeCount(_id, HookCookies.Detour, Address), HookCookies.FreeCount(_id, HookCookies.Detour, Address));
        Assert.Equal(HookCookies.MadeCount(_id, HookCookies.Mid, Address), HookCookies.FreeCount(_id, HookCookies.Mid, Address));
    }

    [Fact]
    public void ThrowingSiblingCleanupsDoNotStopTheRevoke()
    {
        var mod = TestMods.Make(_id, advanced: true, owner: 61);
        TestMods.OnRetiring(mod, () => throw new InvalidOperationException("an earlier service's cleanup fails"));
        TestMods.As(mod, () =>
        {
            HooksApi.Detour(Address, PassThrough);  // Kiln.Hooks joins the context's cleanups here
            HooksApi.Mid(Address, Nothing);
        });
        TestMods.OnRetiring(mod, () => throw new InvalidOperationException("a later service's cleanup fails"));
        Assert.Equal(2, FakeRegistry.Live.Count);
        TestMods.Retire(mod);
        AssertNothingLeft(mod);
    }

    [Fact]
    public void RetiringTwiceIsHarmless()
    {
        var mod = TestMods.Make(_id, advanced: true, owner: 62);
        TestMods.As(mod, () => HooksApi.Detour(Address, PassThrough));
        TestMods.Retire(mod);
        TestMods.Retire(mod);
        AssertNothingLeft(mod);
        Assert.Equal(1, HookCookies.FreeCount(_id, HookCookies.Detour, Address));  // freed once
    }

    [Fact]
    public void ARetiredContextCannotInstall()
    {
        // e.g. a handler still in flight on a game thread, or a timer of the mod, after the mod unloaded
        var mod = TestMods.Make(_id, advanced: true, owner: 63);
        TestMods.As(mod, () => HooksApi.Detour(Address, PassThrough)).Remove();  // the context is tracked
        var fresh = TestMods.Make(_id + "-fresh", advanced: true, owner: 64);   // and one that never installed
        TestMods.Retire(mod);
        TestMods.Retire(fresh);
        var made = HookCookies.MadeCount(_id, HookCookies.Detour, Address);
        var slots = FakeRegistry.SlotsUsed;
        foreach (var m in new[] { mod, fresh })
        {
            var e = Assert.Throws<InvalidOperationException>(() => TestMods.As(m, () => HooksApi.Detour(Address, PassThrough)));
            Assert.Equal($"Hooks.Detour: mod '{m.Info.Id}' has unloaded; an unloaded mod cannot install hooks", e.Message);
            Assert.Throws<InvalidOperationException>(() => TestMods.As(m, () => HooksApi.Mid(Address, Nothing)));
        }
        Assert.Equal(slots, FakeRegistry.SlotsUsed);
        Assert.Equal(made, HookCookies.MadeCount(_id, HookCookies.Detour, Address));
        Assert.Equal(0, HookCookies.MadeCount(_id + "-fresh", HookCookies.Detour, Address));
        AssertNothingLeft(mod);
    }

    [Fact]
    public void RetireDuringInstallDrainsOtherHandlersAndUnloadsShippedSymbols()
    {
        var mod = TestMods.Make(_id, advanced: true, owner: 66);
        var sigdb = Path.Combine(mod.Info.Directory, "sigdb");
        Directory.CreateDirectory(sigdb);
        File.WriteAllText(Path.Combine(sigdb, "server.json"), "{}");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var revoking = new ManualResetEventSlim();
        Thread? handler = null, retire = null;
        try
        {
            TestMods.As(mod, () =>
            {
                Symbols.LoadShipped();
                HooksApi.Detour(Address, call =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    return call.CallOriginal();
                });
            });
            Assert.True(FakeRegistry.HasSymbols(_id));
            var link = Assert.Single(FakeRegistry.Live);
            handler = new Thread(() => FakeRegistry.CallDetour(link));
            handler.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            var sawRevoke = false;
            FakeRegistry.DuringRevokeOwner = revoking.Set;
            FakeRegistry.DuringInstall = () =>
            {
                FakeRegistry.DuringInstall = null;
                retire = new Thread(() => TestMods.Retire(mod));
                retire.Start();
                sawRevoke = revoking.Wait(TimeSpan.FromSeconds(5));
            };
            Assert.Throws<InvalidOperationException>(() => TestMods.As(mod,
                () => HooksApi.Detour(Address + 16, PassThrough)));
            Assert.True(sawRevoke);
            Assert.True(SpinWait.SpinUntil(() => !HooksApi.Detours.Values.Any(r => r.Mod == mod), 5000));
            // Forget has run, but the second handler still owns the original tracker's InFlight count.
            Assert.False(retire!.Join(100), "retire skipped the in-flight handler drain");
            Assert.True(FakeRegistry.HasSymbols(_id));  // still needed by the handler
            release.Set();
            Assert.True(handler.Join(5000));
            Assert.True(retire.Join(5000));
            Assert.False(FakeRegistry.HasSymbols(_id), "retire left the shipped symbols loaded");
            AssertNothingLeft(mod);
        }
        finally
        {
            release.Set();
            handler?.Join(10000);
            retire?.Join(10000);
            TestMods.Retire(mod);
            Directory.Delete(mod.Info.Directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]   // the context had installed before
    [InlineData(false)]  // its first install
    public void ARetireDuringTheNativeInstallLeavesNoLiveLink(bool trackedBefore)
    {
        var mod = TestMods.Make(_id, advanced: true, owner: 65);
        if (trackedBefore) TestMods.As(mod, () => HooksApi.Detour(Address, PassThrough)).Remove();
        Thread? retire = null;
        FakeRegistry.DuringInstall = () =>
        {
            FakeRegistry.DuringInstall = null;
            // the unload runs on the tick thread while another thread of the mod is inside Hooks.Detour; wait until it
            // has marked the context retired (its Kiln.Hooks cleanup then waits for this install to finish)
            retire = new Thread(() => TestMods.Retire(mod));
            retire.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (TestMods.IsAlive(mod) && sw.ElapsedMilliseconds < 5000) Thread.Yield();
        };
        var e = Assert.Throws<InvalidOperationException>(() => TestMods.As(mod, () => HooksApi.Detour(Address, PassThrough)));
        Assert.Contains("has unloaded", e.Message);
        Assert.True(retire!.Join(5000));
        AssertNothingLeft(mod);
    }
}
