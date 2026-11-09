// SPDX-License-Identifier: MIT
// UiWheel and UiDraw.ItemClicked against a fake kiln.ui v2 table: the definition that reaches the native side, the
// result paths (pick, cancel, Close, stale callbacks, the mod unloading), refusals and an older kiln.ui. No game.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Kiln.UiWheelTests;

/// <summary>A fake kiln.ui v2 table: its entries are unmanaged function pointers, so the state is static.</summary>
internal static unsafe class FakeKilnUi
{
    public static UiNative.Table2* Table;
    private static delegate* unmanaged[Cdecl]<int, void*, void> _fn;
    private static void* _user;
    public static string[] Labels = [];
    public static string Title = "";
    public static uint Hold, Count;
    public static ulong OpenOwner, ClosedHandle;
    public static int OpenCalls, CloseCalls, OpenResult;
    public static bool Active, RightDown;
    public const ulong Handle = 77;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Open(ulong owner, UiNative.WheelDef* d, delegate* unmanaged[Cdecl]<int, void*, void> fn, void* user, ulong* h)
    {
        OpenCalls++;
        if (OpenResult != 0) return OpenResult;
        if (Active) return 3;  // KILN_ERR_UNAVAILABLE: another wheel is open
        Labels = new string[d->Count];
        for (var i = 0; i < d->Count; i++) Labels[i] = Encoding.UTF8.GetString(d->Items[i].Ptr, (int)d->Items[i].Len);
        Title = Encoding.UTF8.GetString(d->Title.Ptr, (int)d->Title.Len);
        Hold = d->HoldKey;
        Count = d->Count;
        OpenOwner = owner;
        _fn = fn;
        _user = user;
        Active = true;
        *h = Handle;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Close(ulong handle)
    {
        CloseCalls++;
        ClosedHandle = handle;
        if (!Active) return 2;
        Active = false;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte IsActive() => Active ? (byte)1 : (byte)0;

    // Kiln.Ui's own bookkeeping (a mod retiring recounts what to draw) calls these
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NoOp(uint n) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Clicked(uint button) => button == 1 && RightDown ? (byte)1 : (byte)0;

    public sealed class Backend : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) { }
        public nint Service(string name, uint minVersion) =>
            name == "kiln.ui" && Table != null && Table->Base.Header.Version >= minVersion ? (nint)Table : 0;
    }

    public static void Install(uint version, bool full)
    {
        Free();
        Table = (UiNative.Table2*)NativeMemory.AllocZeroed((nuint)sizeof(UiNative.Table2));
        Table->Base.Header.Size = full ? (uint)sizeof(UiNative.Table2) : (uint)sizeof(UiNative.Table);
        Table->Base.Header.Version = version;
        Table->Base.SetDemand = &NoOp;
        Table->Base.SetCapture = &NoOp;
        Table->Base.SetHoverCapture = &NoOp;
        Table->IsItemClicked = &Clicked;
        Table->WheelOpen = &Open;
        Table->WheelClose = &Close;
        Table->WheelActive = &IsActive;
    }

    public static void Free()
    {
        if (Table != null) NativeMemory.Free(Table);
        Table = null;
    }

    public static void Reset()
    {
        OpenCalls = CloseCalls = OpenResult = 0;
        Active = RightDown = false;
        _fn = null;
        _user = null;
    }

    /// <summary>kiln.ui calls back from the render thread: the pick, or -1.</summary>
    public static void Native(int choice) => _fn(choice, _user);

    public static (nint Fn, nint User) Snapshot() => ((nint)_fn, (nint)_user);

    public static void NativeOf((nint Fn, nint User) callback, int choice) =>
        ((delegate* unmanaged[Cdecl]<int, void*, void>)callback.Fn)(choice, (void*)callback.User);
}

[Collection("bus")]
public sealed class WheelTests : IDisposable
{
    private readonly IKilnBackend _previous;

    public WheelTests()
    {
        _previous = Backend.Current;
        FakeKilnUi.Install(version: 2, full: true);
        Backend.Current = new FakeKilnUi.Backend();
        UiNative.ResetForTests();
        UiWheel.ResetForTests();
        FakeKilnUi.Reset();
    }

    public void Dispose()
    {
        UiWheel.ResetForTests();
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeKilnUi.Free();
    }

    // Ui.OpenWheel is a one-line wrapper of this; the name Ui resolves to the Kiln.Ui namespace in this assembly
    private static UiWheel? OpenWheel(IReadOnlyList<string> entries, Action<int>? onResult = null, UiWheelOptions? options = null) =>
        UiWheel.OpenCore(entries, onResult, options);

    private static void Native(int choice) => FakeKilnUi.Native(choice);

    private static ModContext NewMod() => HostAccess.CreateContext(
        new ModInfo("wheel-" + Guid.NewGuid().ToString("N"), "Wheel test", "1.0.0", Path.GetTempPath()), 42, Path.GetTempPath());

    private static void As(ModContext mod, Action action)
    {
        var previous = HostAccess.Current;
        HostAccess.Current = mod;
        try { action(); }
        finally { HostAccess.Current = previous; }
    }

    // ---- the definition ----

    [Fact]
    public unsafe void TheTableLayoutMatchesTheHeader()
    {
        // kiln_ui.h: the v2 slots come right after add_swapchain_usage: three unused pre-overlay slots, then four
        var slot = (nuint)sizeof(void*);
        Assert.Equal((nuint)sizeof(UiNative.Table) + 3 * slot, (nuint)((byte*)&FakeKilnUi.Table->IsItemClicked - (byte*)FakeKilnUi.Table));
        Assert.Equal((nuint)sizeof(UiNative.Table) + 7 * slot, (nuint)sizeof(UiNative.Table2));
        Assert.Equal(40, sizeof(UiNative.WheelDef));  // KilnUiWheel: size, flags, title, items, count, hold_key
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void EntryCountsOutsideOneToSixtyFourAreRefused(int n)
    {
        var entries = Enumerable.Range(0, n).Select(i => "e" + i).ToArray();
        Assert.NotNull(UiWheel.Check(entries, null));
        Assert.Throws<ArgumentException>(() => OpenWheel(entries));
        Assert.Equal(0, FakeKilnUi.OpenCalls);
    }

    [Fact]
    public void BadDefinitionsThrowAndGoodOnesPass()
    {
        Assert.Null(UiWheel.Check(["a"], null));
        Assert.Null(UiWheel.Check(Enumerable.Repeat("x", 64).ToArray(), new UiWheelOptions { HoldKey = UiWheel.HoldShift }));
        Assert.NotNull(UiWheel.Check(null, null));
        Assert.NotNull(UiWheel.Check(["a", null!], null));
        foreach (var vk in new uint[] { 1, 2, 4, 5, 6, 0xA0, 0xA1, 0xA4, 0xA5, 0x100, uint.MaxValue })
        {
            Assert.False(UiWheel.IsValidHoldKey(vk), $"0x{vk:X}");
            Assert.NotNull(UiWheel.Check(["a"], new UiWheelOptions { HoldKey = vk }));
        }
        foreach (var vk in new uint[] { 0, 0x10, 0x11, 0x12, 'Q', 0x20, 0x70, 0xFF })
            Assert.True(UiWheel.IsValidHoldKey(vk), $"0x{vk:X}");
        Assert.Throws<ArgumentException>(() => OpenWheel(["a"], null, new UiWheelOptions { HoldKey = 1 }));
        Assert.Throws<ArgumentException>(() => OpenWheel(["a", null!]));
        Assert.Equal(0, FakeKilnUi.OpenCalls);
    }

    [Fact]
    public void PagesAreEightEntriesEach()
    {
        Assert.Equal(0, UiWheel.PageCount(0));
        Assert.Equal(1, UiWheel.PageCount(1));
        Assert.Equal(1, UiWheel.PageCount(8));
        Assert.Equal(2, UiWheel.PageCount(9));
        Assert.Equal(3, UiWheel.PageCount(20));
        Assert.Equal(8, UiWheel.PageCount(64));
        Assert.Equal(8, UiWheel.PageEntries);
        Assert.Equal(64, UiWheel.MaxEntries);
    }

    [Fact]
    public void TheNativeSideGetsTheCopiedDefinition()
    {
        var mod = NewMod();
        try
        {
            UiWheel? w = null;
            As(mod, () => w = OpenWheel(["Wolf", "Bear (level 3)", "Föx €"], null,
                new UiWheelOptions { Title = "Pets — tame", HoldKey = UiWheel.HoldShift }));
            Assert.NotNull(w);
            Assert.True(w!.IsOpen);
            Assert.Equal(1, FakeKilnUi.OpenCalls);
            Assert.Equal(["Wolf", "Bear (level 3)", "Föx €"], FakeKilnUi.Labels);
            Assert.Equal("Pets — tame", FakeKilnUi.Title);
            Assert.Equal(0x10u, FakeKilnUi.Hold);
            Assert.Equal(3u, FakeKilnUi.Count);
            Assert.Equal(42ul, FakeKilnUi.OpenOwner);  // the calling mod's native owner
            Assert.Equal(["Wolf", "Bear (level 3)", "Föx €"], w.Entries);
            Assert.True(UiWheel.IsAnyOpen);
            w.Close();
        }
        finally { mod.Retire(); }
    }

    [Fact]
    public void UiOpenWheelIsTheSameCall()
    {
        var wrapper = typeof(UiWheel).Assembly.GetType("Kiln.Ui")!.GetMethod("OpenWheel")!;
        var w = (UiWheel?)wrapper.Invoke(null, [new[] { "a", "b" }, null, new UiWheelOptions { Title = "T" }]);
        Assert.NotNull(w);
        Assert.Equal(["a", "b"], FakeKilnUi.Labels);
        Assert.Equal("T", FakeKilnUi.Title);
        w!.Close();
    }

    [Fact]
    public void NoTitleAndNoHoldKeyAreEmptyAndZero()
    {
        var w = OpenWheel(["only"]);
        Assert.NotNull(w);
        Assert.Equal("", FakeKilnUi.Title);
        Assert.Equal(0u, FakeKilnUi.Hold);
        Assert.Equal(["only"], FakeKilnUi.Labels);
        w!.Close();
    }

    [Fact]
    public void SixtyFourEntriesAllArrive()
    {
        var entries = Enumerable.Range(0, 64).Select(i => $"entry {i}").ToArray();
        var w = OpenWheel(entries);
        Assert.NotNull(w);
        Assert.Equal(entries, FakeKilnUi.Labels);
        w!.Close();
    }

    // ---- results ----

    [Fact]
    public async Task APickRunsTheCallbackOnceAndCompletesResult()
    {
        var picks = new List<int>();
        var w = OpenWheel(["a", "b", "c"], picks.Add)!;
        Assert.False(w.Result.IsCompleted);
        Native(2);
        Assert.Equal([2], picks);
        Assert.False(w.IsOpen);
        Assert.Equal(2, await w.Result);
        Native(1);  // a second call from the native side does nothing
        Assert.Equal([2], picks);
        Assert.False(FakeKilnUi.Active == false && w.IsOpen);
    }

    [Fact]
    public async Task ACancelIsMinusOne()
    {
        var picks = new List<int>();
        var w = OpenWheel(["a", "b"], picks.Add)!;
        Native(-1);
        Assert.Equal([UiWheel.Cancelled], picks);
        Assert.Equal(-1, await w.Result);
    }

    [Fact]
    public async Task AnIndexOutsideTheEntriesIsACancel()
    {
        var picks = new List<int>();
        var w = OpenWheel(["a", "b"], picks.Add)!;
        Native(5);
        Assert.Equal([UiWheel.Cancelled], picks);
        Assert.Equal(-1, await w.Result);
    }

    [Fact]
    public async Task CloseEndsItWithoutTheCallback()
    {
        var picks = new List<int>();
        var w = OpenWheel(["a", "b"], picks.Add)!;
        w.Close();
        Assert.False(w.IsOpen);
        Assert.Equal(1, FakeKilnUi.CloseCalls);
        Assert.Equal(FakeKilnUi.Handle, FakeKilnUi.ClosedHandle);
        Assert.Equal(-1, await w.Result);
        Assert.Empty(picks);
        w.Close();
        w.Dispose();
        Assert.Equal(1, FakeKilnUi.CloseCalls);  // nothing more to close
        // a callback that was already on its way is ignored
        Native(1);
        Assert.Empty(picks);
        Assert.Equal(-1, await w.Result);
    }

    [Fact]
    public async Task DisposeIsClose()
    {
        UiWheel w;
        using (w = OpenWheel(["a"])!) Assert.True(w.IsOpen);
        Assert.False(w.IsOpen);
        Assert.Equal(-1, await w.Result);
        Assert.False(FakeKilnUi.Active);
    }

    [Fact]
    public void ACallbackFromAnEarlierWheelIsIgnored()
    {
        var first = new List<int>();
        var second = new List<int>();
        var a = OpenWheel(["a", "b"], first.Add)!;
        var old = FakeKilnUi.Snapshot();
        a.Close();
        var b = OpenWheel(["c", "d"], second.Add)!;
        FakeKilnUi.NativeOf(old, 1);  // the first wheel's serial
        Assert.Empty(first);
        Assert.Empty(second);
        Assert.True(b.IsOpen);
        Native(0);
        Assert.Equal([0], second);
    }

    [Fact]
    public void AWheelCanOpenFromItsOwnCallback()
    {
        UiWheel? next = null;
        var w = OpenWheel(["a", "b"], pick => next = OpenWheel(["sub " + pick], null))!;
        FakeKilnUi.Active = false;  // kiln.ui closes the wheel before it calls back
        Native(1);
        Assert.NotNull(next);
        Assert.Equal(["sub 1"], FakeKilnUi.Labels);
        Assert.True(next!.IsOpen);
        Assert.False(w.IsOpen);
        next.Close();
    }

    [Fact]
    public void ACallbackRunsAsItsModAndAThrowDoesNotLoseTheResult()
    {
        var mod = NewMod();
        try
        {
            ModContext? seen = null;
            UiWheel? w = null;
            As(mod, () => w = OpenWheel(["a", "b"], _ =>
            {
                seen = HostAccess.Current;
                throw new InvalidOperationException("mod bug");
            }));
            Native(1);
            Assert.Same(mod, seen);
            Assert.Equal(1, w!.Result.Result);  // still completed
        }
        finally { mod.Retire(); }
    }

    // ---- one at a time, refusals, no overlay ----

    [Fact]
    public void OnlyOneWheelAtATime()
    {
        var w = OpenWheel(["a"])!;
        Assert.Null(OpenWheel(["b"]));
        Assert.Equal(1, FakeKilnUi.OpenCalls);  // refused before kiln.ui was asked
        w.Close();
        var again = OpenWheel(["b"]);
        Assert.NotNull(again);
        again!.Close();
    }

    [Fact]
    public void AnotherModsWheelRefusesToo()
    {
        FakeKilnUi.Active = true;  // some other mod's wheel, opened natively
        Assert.Null(OpenWheel(["a"]));
        Assert.Equal(1, FakeKilnUi.OpenCalls);
        Assert.True(UiWheel.IsAnyOpen);
        FakeKilnUi.Active = false;
        var w = OpenWheel(["a"]);
        Assert.NotNull(w);  // the refusal left nothing behind
        w!.Close();
    }

    [Theory]
    [InlineData(3)]  // KILN_ERR_UNAVAILABLE: the Kiln menu is open, or no window yet
    [InlineData(1)]  // KILN_ERR_INVALID_ARGUMENT
    public void WhenKilnUiRefusesNothingIsLeftBehind(int rc)
    {
        FakeKilnUi.OpenResult = rc;
        Assert.Null(OpenWheel(["a"]));
        FakeKilnUi.OpenResult = 0;
        var w = OpenWheel(["a"]);
        Assert.NotNull(w);
        w!.Close();
    }

    [Fact]
    public void WithoutAV2KilnUiThereIsNoWheel()
    {
        FakeKilnUi.Install(version: 1, full: false);  // an older core: the table ends before the v2 slots
        UiNative.ResetForTests();
        Assert.False(UiWheel.Available);
        Assert.Null(OpenWheel(["a"]));
        Assert.False(UiWheel.IsAnyOpen);
        Assert.Equal(0, FakeKilnUi.OpenCalls);
        Assert.True(UiNative.Available);  // the rest of Kiln.Ui keeps working
        var ui = new UiDraw();
        Assert.False(ui.ItemClicked(UiMouseButton.Right));
    }

    [Fact]
    public void WithoutKilnUiThereIsNoWheelAndNothingThrows()
    {
        Backend.Current = _previous;
        UiNative.ResetForTests();
        Assert.False(UiWheel.Available);
        Assert.Null(OpenWheel(["a"]));
        Assert.False(UiWheel.IsAnyOpen);
    }

    // ---- the owning mod unloading ----

    [Fact]
    public async Task AModThatUnloadsWithAWheelOpenClosesIt()
    {
        var mod = NewMod();
        var picks = new List<int>();
        UiWheel? w = null;
        As(mod, () => w = OpenWheel(["a", "b"], picks.Add));
        Assert.True(w!.IsOpen);
        mod.Retire();
        Assert.False(w.IsOpen);
        Assert.Equal(1, FakeKilnUi.CloseCalls);
        Assert.Equal(-1, await w.Result);
        Assert.Empty(picks);
        Assert.False(FakeKilnUi.Active);
    }

    [Fact]
    public void AnotherModsUnloadLeavesTheWheelAlone()
    {
        var owner = NewMod();
        var other = NewMod();
        UiWheel? w = null;
        As(owner, () => w = OpenWheel(["a"]));
        As(other, () => { });
        other.Retire();
        Assert.True(w!.IsOpen);
        Assert.Equal(0, FakeKilnUi.CloseCalls);
        owner.Retire();
        Assert.False(w.IsOpen);
    }

    [Fact]
    public void ARetiredModCannotOpenAWheel()
    {
        var mod = NewMod();
        mod.Retire();
        As(mod, () => Assert.Throws<InvalidOperationException>(() => OpenWheel(["a"])));
        Assert.Equal(0, FakeKilnUi.OpenCalls);
        Assert.False(UiWheel.IsAnyOpen);
    }

    // ---- right clicks ----

    [Fact]
    public void ItemClickedAsksKilnUiForTheButton()
    {
        var ui = new UiDraw();
        Assert.False(ui.ItemClicked(UiMouseButton.Right));
        FakeKilnUi.RightDown = true;
        Assert.True(ui.ItemClicked(UiMouseButton.Right));
        Assert.False(ui.ItemClicked());  // left by default
        Assert.False(ui.ItemClicked(UiMouseButton.Middle));
    }
}
