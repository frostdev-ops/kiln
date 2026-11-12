// SPDX-License-Identifier: MIT
extern alias khooks;
using Xunit;
using HooksApi = khooks::Kiln.Hooks;

namespace Kiln.Hooks.Tests;

/// <summary>Managed hooks reuse their link slot: HooksApi.Detour / HooksApi.Mid hand the registry a user pointer (cookie)
/// that a revoked link of the same mod on the same address already carries, so the registry revives that link. Driven
/// against <see cref="FakeRegistry"/>, which keeps the core's revive rule.</summary>
[Collection("kiln.hooks state")]
public sealed class CookieTests : IDisposable
{
    static int _seq;
    readonly string _id = $"cookie-mod-{Interlocked.Increment(ref _seq)}";  // a key of its own per test
    static readonly nuint Address = unchecked((nuint)0x140001000UL);

    public CookieTests() => FakeRegistry.Install();
    public void Dispose() => FakeRegistry.Uninstall();

    static nuint PassThrough(HookCall call) => call.CallOriginal();
    static void Nothing(ref Registers r) { }

    [Fact]
    public void InstallAndRemoveManyTimesTakesOneCookieAndOneSlot()
    {
        var mod = TestMods.Make(_id, advanced: true, owner: 41);
        TestMods.As(mod, () =>
        {
            for (var i = 0; i < 500; i++) HooksApi.Detour(Address, PassThrough).Remove();
            for (var i = 0; i < 500; i++) HooksApi.Mid(Address, Nothing).Remove();
        });
        var links = FakeRegistry.All;
        Assert.Equal(2, links.Count);  // one detour link, one mid link, each revived 499 times
        Assert.Empty(FakeRegistry.Live);
        Assert.Equal(1, HookCookies.FreeCount(_id, HookCookies.Detour, Address));
        Assert.Equal(1, HookCookies.FreeCount(_id, HookCookies.Mid, Address));
    }

    [Fact]
    public void TwoLiveInstallsOfOneHandlerGetTwoCookies()
    {
        var mod = TestMods.Make(_id, advanced: true, owner: 42);
        TestMods.As(mod, () =>
        {
            for (var round = 0; round < 3; round++)
            {
                var a = HooksApi.Detour(Address, PassThrough);
                var b = HooksApi.Detour(Address, PassThrough);
                var live = FakeRegistry.Live;
                Assert.Equal(2, live.Count);
                Assert.NotEqual(live[0].User, live[1].User);
                a.Remove();
                b.Remove();
            }
        });
        Assert.Equal(2, FakeRegistry.SlotsUsed);
    }

    [Fact]
    public void ARemovedInstallsCookiePassesThroughUntilTakenAgain()
    {
        var mod = TestMods.Make(_id, advanced: true, owner: 43);
        int first = 0, second = 0;
        TestMods.As(mod, () =>
        {
            var h = HooksApi.Detour(Address, call => { first++; return 7; });
            var link = Assert.Single(FakeRegistry.Live);
            Assert.Equal((nuint)7, FakeRegistry.CallDetour(link));
            h.Remove();
            // a dispatch that still arrives through the dead link (a call in flight) finds no registration
            Assert.Equal(FakeRegistry.Original, FakeRegistry.CallDetour(link));
            HooksApi.Detour(Address, call => { second++; return 8; });
            Assert.Same(link, Assert.Single(FakeRegistry.Live));  // revived: same slot
            Assert.Equal((nuint)8, FakeRegistry.CallDetour(link));
        });
        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void FaultsDoNotCarryOverToTheNextInstallOfACookie()
    {
        var mod = TestMods.Make(_id, advanced: true, owner: 44);
        TestMods.As(mod, () =>
        {
            var h = HooksApi.Detour(Address, call => throw new InvalidOperationException("boom"));
            var link = Assert.Single(FakeRegistry.Live);
            for (var i = 0; i < 5; i++) Assert.Equal(FakeRegistry.Original, FakeRegistry.CallDetour(link));
            Assert.Equal(5, HooksApi.Detours[(long)link.User].Faults);
            h.Remove();
            HooksApi.Detour(Address, PassThrough);
            Assert.Equal(link.User, Assert.Single(FakeRegistry.Live).User);
            Assert.Equal(0, HooksApi.Detours[(long)link.User].Faults);
        });
    }

    [Fact]
    public void ANewContextOfTheSameModReusesTheCookieAfterRetire()
    {
        var old = TestMods.Make(_id, advanced: true, owner: 45);
        var stale = TestMods.As(old, () => HooksApi.Detour(Address, PassThrough));
        TestMods.As(old, () => HooksApi.Mid(Address, Nothing));
        var before = FakeRegistry.Live.ToDictionary(l => l.Kind, l => l.User);
        TestMods.Retire(old);
        Assert.Empty(FakeRegistry.Live);
        // nothing of the retired context stays reachable from Kiln.Hooks
        Assert.DoesNotContain(HooksApi.Detours.Values, r => r.Mod == old);
        Assert.DoesNotContain(HooksApi.Mids.Values, r => r.Mod == old);

        var reloaded = TestMods.Make(_id, advanced: true, owner: 46);  // the host gives every load a new owner
        var calls = 0;
        TestMods.As(reloaded, () =>
        {
            HooksApi.Detour(Address, call => { calls++; return call.CallOriginal(); });
            HooksApi.Mid(Address, Nothing);
        });
        var after = FakeRegistry.Live.ToDictionary(l => l.Kind, l => l.User);
        Assert.Equal(before, after);

        // the old context's handle no longer reaches the new install that took its cookie
        stale.Remove();
        var detour = FakeRegistry.Live.Single(l => l.Kind == HookCookies.Detour);
        FakeRegistry.CallDetour(detour);
        Assert.Equal(1, calls);
        TestMods.Retire(reloaded);
    }
}
