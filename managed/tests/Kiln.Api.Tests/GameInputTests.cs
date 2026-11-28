// SPDX-License-Identifier: MIT
// Kiln.Client's game input (GameInput.cs) against a fake kiln.client.game_input v1 table: the binding is
// kiln_client_game_input.h byte for byte, the public surface is the settled one, the refusals (a dedicated server, the row
// off, no mod, bad arguments, the native answers) throw what the spec says, the reads never throw, a handler sees its
// sample and its Press/Release reach the native answer (the later call wins, only inside its own call), handlers end with
// their handle, their mod's unload or five faults, and Repeat sends the clamped interval, evaluates its condition (the
// action's own held bit by default) and ends when the world does.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.Api.Tests;

public sealed unsafe class GameInputTests : IDisposable
{
    private readonly IKilnBackend _previous = Backend.Current;
    private readonly BuildingInputTests.FakeBackend _backend = new();
    private readonly ModContext _mod;
    private readonly ModContext? _outerMod = ModContext.CurrentMod;

    public GameInputTests()
    {
        Backend.Current = _backend;
        FakeBuildingTable.Reset();
        FakeGameInputTable.Reset();
        _backend.Tables["kiln.client"] = FakeBuildingTable.Create(80);
        _backend.Tables[GameInputNative.Service] = FakeGameInputTable.Create();
        _backend.Features.Add(GameInput.Capability);
        _mod = HostAccess.CreateContext(new ModInfo("gi-test", "gi-test", "1.0.0", Path.GetTempPath()), 4343, Path.GetTempPath());
        FakeBuildingTable.InGame(player: 17, session: 3);
        ModContext.CurrentMod = _mod;
    }

    public void Dispose()
    {
        ModContext.CurrentMod = _outerMod;
        Backend.Current = _previous;
    }

    private static int Offset<T>(T* start, void* field) where T : unmanaged => (int)((byte*)field - (byte*)start);

    [Fact]
    public void The_binding_is_kiln_client_game_input_h_byte_for_byte()
    {
        Assert.Equal(72, sizeof(GameInputNative.Sample));
        Assert.Equal(40, sizeof(GameInputNative.Table));
        var s = default(GameInputNative.Sample);
        Assert.Equal(4, Offset(&s, &s.Player));
        Assert.Equal(8, Offset(&s, &s.ClientSession));
        Assert.Equal(12, Offset(&s, &s.Step));
        Assert.Equal(16, Offset(&s, &s.Held));
        Assert.Equal(20, Offset(&s, &s.Pressed));
        Assert.Equal(24, Offset(&s, &s.Press));
        Assert.Equal(28, Offset(&s, &s.Release));
        Assert.Equal(32, Offset(&s, &s.HeldKind));
        Assert.Equal(36, Offset(&s, &s.HeldItem));
        Assert.Equal(40, Offset(&s, &s.Flags));
        Assert.Equal(44, Offset(&s, &s.Gate));
        Assert.Equal(48, Offset(&s, &s.Digital));
        Assert.Equal(56, Offset(&s, &s.Sequence));
        Assert.Equal(64, Offset(&s, &s.AgeNs));
        var t = default(GameInputNative.Table);
        Assert.Equal(8, Offset(&t, &t.Last));
        Assert.Equal(16, Offset(&t, &t.Subscribe));
        Assert.Equal(24, Offset(&t, &t.Repeat));
        Assert.Equal(32, Offset(&t, &t.Unsubscribe));
        // KILN_GAME_ACTION_* and KILN_GAME_HELD_* (Kiln.ActionKind's numbers)
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Enum.GetValues<GameAction>().Select(a => (int)a));
        Assert.Equal(new[] { "PrimaryAttack", "SecondaryAttack", "Block", "Dodge", "Jump", "Interact" }, Enum.GetNames<GameAction>());
        Assert.Equal((0, 1, 2), ((int)ActionKind.Melee, (int)ActionKind.Bow, (int)ActionKind.Tool));
    }

    [Fact]
    public void The_public_surface_is_the_settled_one()
    {
        var input = typeof(GameInput);
        Assert.True(input.IsAbstract && input.IsSealed);  // static
        Assert.Equal(new[] { "Available", "IsHeld", "OnSample", "Repeat", "WasPressed" },
            input.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m is not System.Reflection.MethodInfo { IsSpecialName: true }).Select(m => m.Name).Order());
        var sample = typeof(GameInputSample);
        Assert.True(sample.IsByRefLike);
        Assert.Equal(new[] { "BowFullyDrawn", "HeldKind", "IsHeld", "Player", "Press", "Release" },
            sample.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m is not System.Reflection.MethodInfo { IsSpecialName: true } && m is not System.Reflection.ConstructorInfo)
                .Select(m => m.Name).Order());
        Assert.Null(sample.GetProperty("Player")!.SetMethod);
        Assert.Equal(typeof(ActionKind?), sample.GetProperty("HeldKind")!.PropertyType);
        var invoke = typeof(GameInputHandler).GetMethod("Invoke")!;
        Assert.True(invoke.GetParameters()[0].ParameterType.IsByRef);
        var repeat = input.GetMethod("Repeat")!;
        Assert.Equal(typeof(IDisposable), repeat.ReturnType);
        Assert.True(repeat.GetParameters()[2].IsOptional);
    }

    [Fact]
    public void Available_needs_the_table_the_row_a_client_and_a_local_player()
    {
        Assert.True(GameInput.Available);
        _backend.Features.Remove(GameInput.Capability);
        Assert.False(GameInput.Available);
        _backend.Features.Add(GameInput.Capability);
        FakeBuildingTable.Server();
        Assert.False(GameInput.Available);
    }

    [Fact]
    public void A_dedicated_server_refuses_OnSample_and_Repeat()
    {
        FakeBuildingTable.Server();
        var e = Assert.Throws<InvalidOperationException>(() => GameInput.OnSample((ref GameInputSample s) => { }));
        Assert.Contains("dedicated server", e.Message);
        Assert.Throws<InvalidOperationException>(() => GameInput.Repeat(GameAction.PrimaryAttack, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, FakeGameInputTable.Subscribes + FakeGameInputTable.Repeats);
        Assert.False(GameInput.IsHeld(GameAction.Jump));  // reads never throw
    }

    [Fact]
    public void The_row_off_refuses_and_reads_are_false()
    {
        _backend.Features.Remove(GameInput.Capability);
        Assert.Throws<InvalidOperationException>(() => GameInput.OnSample((ref GameInputSample s) => { }));
        Assert.False(GameInput.WasPressed(GameAction.PrimaryAttack));
        Assert.Equal(0, FakeGameInputTable.Subscribes);
    }

    [Fact]
    public void No_table_refuses_and_reads_are_false()
    {
        _backend.Tables.Remove(GameInputNative.Service);  // before any call: the binding looks the table up once per backend
        Assert.False(GameInput.Available);
        var e = Assert.Throws<InvalidOperationException>(() => GameInput.Repeat(GameAction.Block, TimeSpan.FromSeconds(1)));
        Assert.Contains("kiln.client.game_input is not here", e.Message);
        Assert.False(GameInput.IsHeld(GameAction.PrimaryAttack));
    }

    [Fact]
    public void Arguments_and_native_refusals()
    {
        Assert.Throws<ArgumentNullException>(() => GameInput.OnSample(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameInput.Repeat((GameAction)6, TimeSpan.FromMilliseconds(100)));
        FakeGameInputTable.Rc = 5;  // BUFFER_TOO_SMALL
        Assert.Contains("64 subscriptions", Assert.Throws<InvalidOperationException>(() => GameInput.OnSample((ref GameInputSample s) => { })).Message);
        FakeGameInputTable.Rc = 3;  // UNAVAILABLE (a refused install)
        Assert.Throws<InvalidOperationException>(() => GameInput.Repeat(GameAction.Jump, TimeSpan.FromMilliseconds(100)));
        FakeGameInputTable.Rc = 6;  // REVOKED
        Assert.Contains("unloading", Assert.Throws<InvalidOperationException>(() => GameInput.OnSample((ref GameInputSample s) => { })).Message);
        FakeGameInputTable.Rc = 0;
        ModContext.CurrentMod = null;
        Assert.Throws<InvalidOperationException>(() => GameInput.OnSample(StaticHandler));  // no mod in reach
    }

    private static void StaticHandler(ref GameInputSample sample) { }

    [Fact]
    public void IsHeld_and_WasPressed_read_the_last_fresh_sample()
    {
        FakeGameInputTable.Last = new GameInputNative.Sample { Size = 72, Held = 1u << 0 | 1u << 2, Pressed = 1u << 2, Sequence = 9, ClientSession = 3 };
        Assert.True(GameInput.IsHeld(GameAction.PrimaryAttack));
        Assert.True(GameInput.IsHeld(GameAction.Block));
        Assert.False(GameInput.IsHeld(GameAction.Jump));
        Assert.True(GameInput.WasPressed(GameAction.Block));
        Assert.False(GameInput.WasPressed(GameAction.PrimaryAttack));
        Assert.False(GameInput.IsHeld((GameAction)42));
        FakeGameInputTable.Reason = GameInputNative.ReasonStale;
        Assert.False(GameInput.IsHeld(GameAction.PrimaryAttack));
        FakeGameInputTable.Reason = GameInputNative.ReasonNoSample;
        Assert.False(GameInput.WasPressed(GameAction.Block));
        Assert.Equal(72u, FakeGameInputTable.LastSize);
    }

    [Fact]
    public void A_handler_sees_its_sample_and_answers_with_Press_and_Release()
    {
        var seen = new List<(EntityId, ActionKind?, bool, bool, bool)>();
        ModContext? during = null;
        using var h = GameInput.OnSample((ref GameInputSample s) =>
        {
            seen.Add((s.Player, s.HeldKind, s.BowFullyDrawn, s.IsHeld(GameAction.PrimaryAttack), s.IsHeld(GameAction.Jump)));
            during = ModContext.CurrentMod;
            s.Press(GameAction.Jump);
            s.Release(GameAction.PrimaryAttack);
            s.Press(GameAction.PrimaryAttack);  // the later call wins
            s.Release(GameAction.Block);
        });
        Assert.Equal(1, FakeGameInputTable.Subscribes);
        Assert.Equal(4343ul, FakeGameInputTable.LastOwner);
        var sample = FakeGameInputTable.Sample(held: 1u << 0, kind: 1, flags: 1);
        FakeGameInputTable.Raise(0, &sample);
        Assert.Equal([(new EntityId(17), (ActionKind?)ActionKind.Bow, true, true, false)], seen);
        Assert.Same(_mod, during);
        Assert.Equal(1u << 4 | 1u << 0, sample.Press);
        Assert.Equal(1u << 2, sample.Release);
        var none = FakeGameInputTable.Sample(held: 0, kind: -1, flags: 0);
        FakeGameInputTable.Raise(0, &none);
        Assert.Null(seen[^1].Item2);
        Assert.False(seen[^1].Item3);
    }

    [Fact]
    public void Press_outside_its_handler_throws_and_a_default_sample_reads_nothing()
    {
        var s = default(GameInputSample);
        Assert.Equal(EntityId.None, s.Player);
        Assert.Null(s.HeldKind);
        Assert.False(s.BowFullyDrawn || s.IsHeld(GameAction.PrimaryAttack));
        try
        {
            s.Press(GameAction.PrimaryAttack);
            Assert.Fail("Press outside a handler did not throw");
        }
        catch (InvalidOperationException)
        {
        }
        Exception? inner = null;
        using var h = GameInput.OnSample((ref GameInputSample x) =>
        {
            try
            {
                x.Press((GameAction)9);
            }
            catch (Exception e)
            {
                inner = e;
            }
        });
        var sample = FakeGameInputTable.Sample(held: 0, kind: -1, flags: 0);
        FakeGameInputTable.Raise(0, &sample);
        Assert.IsType<ArgumentOutOfRangeException>(inner);
        Assert.Equal(0u, sample.Press);
    }

    [Fact]
    public void A_handler_ends_with_its_handle_its_mods_unload_or_five_faults()
    {
        var calls = 0;
        var h = GameInput.OnSample((ref GameInputSample s) => calls++);
        var sample = FakeGameInputTable.Sample(held: 0, kind: -1, flags: 0);
        FakeGameInputTable.Raise(0, &sample);
        h.Dispose();
        h.Dispose();
        Assert.Equal([FakeGameInputTable.HandleOf(0)], FakeGameInputTable.Unsubscribed);
        FakeGameInputTable.RaiseStale(0, &sample);  // a call that raced the dispose: ignored, never a freed handle
        Assert.Equal(1, calls);

        var other = HostAccess.CreateContext(new ModInfo("gi-other", "gi-other", "1.0.0", Path.GetTempPath()), 4444, Path.GetTempPath());
        ModContext.CurrentMod = other;
        GameInput.OnSample((ref GameInputSample s) => calls++);
        GameInput.Repeat(GameAction.Jump, TimeSpan.FromMilliseconds(100));
        ModContext.CurrentMod = _mod;
        other.Retire();
        Assert.Equal(3, FakeGameInputTable.Unsubscribed.Count);

        var throws = 0;
        GameInput.OnSample((ref GameInputSample s) =>
        {
            throws++;
            throw new InvalidOperationException("a mod bug");
        });
        var index = FakeGameInputTable.Count - 1;
        for (var i = 0; i < 6; i++) FakeGameInputTable.Raise(index, &sample);
        Assert.Equal(5, throws);
        Assert.Contains(FakeGameInputTable.HandleOf(index), FakeGameInputTable.Unsubscribed);
    }

    [Fact]
    public void Repeat_sends_the_clamped_interval_and_the_action()
    {
        using var a = GameInput.Repeat(GameAction.Block, TimeSpan.FromMilliseconds(10));
        Assert.Equal((2u, 50u, 4343ul), (FakeGameInputTable.LastAction, FakeGameInputTable.LastInterval, FakeGameInputTable.LastOwner));
        using var b = GameInput.Repeat(GameAction.PrimaryAttack, TimeSpan.FromSeconds(2.5));
        Assert.Equal((0u, 2500u), (FakeGameInputTable.LastAction, FakeGameInputTable.LastInterval));
        using var c = GameInput.Repeat(GameAction.Jump, TimeSpan.FromMilliseconds(-5));
        Assert.Equal(50u, FakeGameInputTable.LastInterval);
        using var d = GameInput.Repeat(GameAction.Jump, TimeSpan.FromDays(100));
        Assert.Equal(uint.MaxValue, FakeGameInputTable.LastInterval);
        Assert.Equal(4, FakeGameInputTable.Repeats);
    }

    [Fact]
    public void Repeats_condition_is_the_action_held_by_default_or_whileHeld()
    {
        using var byDefault = GameInput.Repeat(GameAction.PrimaryAttack, TimeSpan.FromMilliseconds(100));
        var held = FakeGameInputTable.Sample(held: 1u << 0, kind: 0, flags: 0);
        var free = FakeGameInputTable.Sample(held: 1u << 4, kind: 0, flags: 0);
        Assert.True(FakeGameInputTable.Ask(0, &held));
        Assert.False(FakeGameInputTable.Ask(0, &free));

        var on = true;
        ModContext? during = null;
        using var custom = GameInput.Repeat(GameAction.Block, TimeSpan.FromMilliseconds(100), () =>
        {
            during = ModContext.CurrentMod;
            return on;
        });
        Assert.True(FakeGameInputTable.Ask(1, &free));
        Assert.Same(_mod, during);
        on = false;
        Assert.False(FakeGameInputTable.Ask(1, &held));

        var faults = 0;
        GameInput.Repeat(GameAction.Jump, TimeSpan.FromMilliseconds(100), () =>
        {
            faults++;
            throw new InvalidOperationException("a mod bug");
        });
        for (var i = 0; i < 5; i++) Assert.False(FakeGameInputTable.Ask(2, &held));
        Assert.Equal(5, faults);
        Assert.Contains(FakeGameInputTable.HandleOf(2), FakeGameInputTable.Unsubscribed);
    }

    [Fact]
    public void A_repeat_ends_with_the_world_of_its_first_sample()
    {
        GameInput.Repeat(GameAction.PrimaryAttack, TimeSpan.FromMilliseconds(100));
        var first = FakeGameInputTable.Sample(held: 1, kind: 0, flags: 0, session: 3);
        Assert.True(FakeGameInputTable.Ask(0, &first));
        Assert.Empty(FakeGameInputTable.Unsubscribed);
        var next = FakeGameInputTable.Sample(held: 1, kind: 0, flags: 0, session: 4);
        Assert.False(FakeGameInputTable.Ask(0, &next));
        Assert.Equal([FakeGameInputTable.HandleOf(0)], FakeGameInputTable.Unsubscribed);
    }
}

/// <summary>A fake kiln.client.game_input v1 table (kiln_client_game_input.h). Subscriptions are kept so a test can call
/// them as the native dispatch does (on the test's thread, which stands for the game's main thread).</summary>
internal static unsafe class FakeGameInputTable
{
    public static GameInputNative.Sample Last;
    public static uint Reason, LastSize, LastAction, LastInterval;
    public static ulong LastOwner;
    public static int Rc, Subscribes, Repeats;
    public static readonly List<ulong> Unsubscribed = [];
    private static readonly List<(ulong Handle, nint Fn, nint Cond, nint User)> Subs = [];
    private static ulong _next;

    public static int Count => Subs.Count;
    public static ulong HandleOf(int index) => Subs[index].Handle;

    public static void Reset()
    {
        Last = default;
        Reason = LastSize = LastAction = LastInterval = 0;
        LastOwner = 0;
        Rc = Subscribes = Repeats = 0;
        Unsubscribed.Clear();
        Subs.Clear();
    }

    public static GameInputNative.Sample Sample(uint held, int kind, uint flags, uint session = 3) =>
        new() { Size = 72, Player = 17, ClientSession = session, Held = held, HeldKind = kind, Flags = flags, Sequence = 1 };

    public static nint Create()
    {
        var t = (GameInputNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(GameInputNative.Table));
        t->Header.Size = (uint)sizeof(GameInputNative.Table);
        t->Header.Version = 1;
        t->Last = &LastFn;
        t->Subscribe = &SubscribeFn;
        t->Repeat = &RepeatFn;
        t->Unsubscribe = &UnsubscribeFn;
        return (nint)t;
    }

    // The native dispatch: a live subscription's fn, as kiln.client.game_input calls it.
    public static void Raise(int index, GameInputNative.Sample* s)
    {
        var sub = Subs[index];
        if (Unsubscribed.Contains(sub.Handle)) return;
        ((delegate* unmanaged[Cdecl]<GameInputNative.Sample*, void*, void>)sub.Fn)(s, (void*)sub.User);
    }

    // A call that was already on its way when the handle was disposed (the handle's GCHandle may be gone): the managed
    // side must ignore it without touching freed memory. The fake passes a null user, as a freed handle resolves to none.
    public static void RaiseStale(int index, GameInputNative.Sample* s) =>
        ((delegate* unmanaged[Cdecl]<GameInputNative.Sample*, void*, void>)Subs[index].Fn)(s, null);

    public static bool Ask(int index, GameInputNative.Sample* s)
    {
        var sub = Subs[index];
        if (Unsubscribed.Contains(sub.Handle)) return false;
        return ((delegate* unmanaged[Cdecl]<GameInputNative.Sample*, void*, byte>)sub.Cond)(s, (void*)sub.User) != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LastFn(GameInputNative.Sample* out_, uint* reason)
    {
        LastSize = out_->Size;
        if (out_->Size < 72) return 1;
        *out_ = Last;
        out_->Size = 72;
        if (reason != null) *reason = Reason;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SubscribeFn(ulong owner, delegate* unmanaged[Cdecl]<GameInputNative.Sample*, void*, void> fn, void* user, ulong* out_)
    {
        LastOwner = owner;
        *out_ = 0;
        if (Rc != 0) return Rc;
        Subscribes++;
        *out_ = ++_next;
        Subs.Add((*out_, (nint)fn, 0, (nint)user));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RepeatFn(ulong owner, uint action, uint interval, delegate* unmanaged[Cdecl]<GameInputNative.Sample*, void*, byte> cond,
                                void* user, ulong* out_)
    {
        LastOwner = owner;
        LastAction = action;
        LastInterval = interval;
        *out_ = 0;
        if (Rc != 0) return Rc;
        Repeats++;
        *out_ = ++_next;
        Subs.Add((*out_, 0, (nint)cond, (nint)user));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int UnsubscribeFn(ulong handle)
    {
        if (Unsubscribed.Contains(handle) || !Subs.Exists(s => s.Handle == handle)) return 2;
        Unsubscribed.Add(handle);
        return 0;
    }
}
