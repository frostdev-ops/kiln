// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;
using static Kiln.Blocks.Tests.RawGateTests;

namespace Kiln.Blocks.Tests;

/// <summary>kiln.abilities' raw entry points: a program blob (<see cref="Abilities.Programs.Register(byte[])"/>) and
/// the raw bytes of a wrapped call (<see cref="NativeCall.Bytes"/>). Kiln's own paths behind them (the buff program,
/// asset programs, the typed accessors and <see cref="DamageCall"/>) stay open to ordinary mods.</summary>
[Collection("raw gates")]
public sealed unsafe class AbilitiesGateTests
{
    [Fact]
    public void NewlyGatedMembersDeclareTheAdvancedContract()
    {
        var registers = typeof(Abilities.Programs).GetMethods().Where(m => m.Name == "Register").ToArray();
        Assert.Equal(2, registers.Length);
        foreach (var method in registers.Append(typeof(NativeCall).GetMethod("Bytes")!))
            Assert.True(method.IsDefined(typeof(AdvancedApiAttribute), inherit: false), $"{method} lacks AdvancedApi");
    }

    [Fact]
    public void ProgramsRegisterIsForAdvancedModsOnly()
    {
        var blob = new byte[16];
        As(Plain, () =>
        {
            AssertGated("Abilities.Programs.Register", "plain-mod", () => Abilities.Programs.Register(blob));
            AssertGated("Abilities.Programs.Register", "plain-mod", () => Abilities.Programs.Register(blob, out _));
        });
        As(Coremod, () =>
        {
            var e = Assert.Throws<NotSupportedException>(() => Abilities.Programs.Register(blob));
            Assert.Contains("kiln.abilities is not available", e.Message);
            Assert.Throws<NotSupportedException>(() => Abilities.Programs.Register(blob, out _));
        });
        Assert.Throws<InvalidOperationException>(() => As(null, () => Abilities.Programs.Register(blob)));
    }

    [Fact]
    public void KilnsOwnProgramsStayOpenToOrdinaryMods()
    {
        // ApplyBuff registers Kiln's buff program for whichever mod uses buffs: it reaches the native table (here: none),
        // never the gate
        var buff = As(Plain, () => Abilities.ApplyBuff(new EntityId(5), Guid.NewGuid()));
        var e = Assert.IsType<NotSupportedException>(buff.Exception?.InnerException);
        Assert.Equal("Abilities.Programs.Register: kiln.abilities is not available on this build", e.Message);
        // Kiln.Blocks ships generated programs as assets and asks kiln.abilities to pick them up: open, no gate
        var refresh = Assert.Throws<NotSupportedException>(() => As(Plain, () => Abilities.Programs.Refresh()));
        Assert.Equal("Abilities.Programs.Refresh: kiln.abilities is not available on this build", refresh.Message);
    }

    // ---- NativeCall.Bytes, invoked as the core invokes a wrap: WrapDispatch(call, user) on the game's job thread ----

    // A wrapped call with 15 arguments: argument 6 is variable 0 (a float, AttackMod), argument 14 variable 1 (a 52-byte
    // DamageSetup); every other argument is a raw value.
    sealed class FakeCall : IDisposable
    {
        public readonly AbilitiesNative.NativeCall* Call;
        readonly uint* _args;
        readonly byte* _layout, _data;

        public FakeCall(uint hash)
        {
            _args = (uint*)NativeMemory.AllocZeroed(15 * sizeof(uint));
            _args[6] = 0xFFFF0000;
            _args[14] = 0xFFFF0001;
            _layout = (byte*)NativeMemory.AllocZeroed(2 * 24);
            *(ushort*)(_layout + 12) = 4;        // variable 0: 4 bytes at 0
            *(ushort*)(_layout + 14) = 0;
            *(ushort*)(_layout + 24 + 12) = 52;  // variable 1: 52 bytes at 4
            *(ushort*)(_layout + 24 + 14) = 4;
            _data = (byte*)NativeMemory.AllocZeroed(56);
            *(float*)_data = 1.5f;
            Call = (AbilitiesNative.NativeCall*)NativeMemory.AllocZeroed((nuint)sizeof(AbilitiesNative.NativeCall));
            Call->Size = (uint)sizeof(AbilitiesNative.NativeCall);
            Call->Hash = hash;
            Call->Argc = 15;
            Call->Args = _args;
            Call->Layout = _layout;
            Call->LayoutCount = 2;
            Call->Data = _data;
            Call->DataSize = 56;
            Call->OutError = 0xFF;
        }

        public float AttackMod => *(float*)_data;
        public float Cut => *(float*)(_data + 4);

        public void Dispose()
        {
            NativeMemory.Free(Call);
            NativeMemory.Free(_data);
            NativeMemory.Free(_layout);
            NativeMemory.Free(_args);
        }
    }

    static readonly delegate* unmanaged[Cdecl]<AbilitiesNative.NativeCall*, void*, void> WrapDispatch =
        (delegate* unmanaged[Cdecl]<AbilitiesNative.NativeCall*, void*, void>)typeof(AbilitiesDispatch)
            .GetMethod("WrapDispatch", BindingFlags.NonPublic | BindingFlags.Static)!.MethodHandle.GetFunctionPointer();

    static uint _hash = 0x7e570000;

    // Registers the handler as `mod` would (the decision is taken there), then runs one call through the dispatcher on
    // a thread where no mod is current, as on a game job thread.
    static void Dispatch(ModContext? mod, NativeWrapHandler handler, FakeCall? call = null)
    {
        var hash = call is null ? Interlocked.Increment(ref _hash) : call.Call->Hash;
        var entry = As(mod, () => AbilitiesDispatch.Insert(hash, handler, 0, mod));
        var own = call ?? new FakeCall(hash);
        try
        {
            var t = new Thread(() => WrapDispatch(own.Call, null));
            t.Start();
            t.Join();
        }
        finally
        {
            AbilitiesDispatch.RemoveWrap(entry);
            if (call is null) own.Dispose();
        }
    }

    [Fact]
    public void NativeCallBytesIsForAdvancedModsOnlyAsTheDispatcherRunsIt()
    {
        Exception? plain = null, outside = null;
        int advancedLength = -1;
        ModContext? currentInHandler = null;
        Dispatch(Plain, (ref NativeCall c) =>
        {
            currentInHandler = HostAccess.Current;
            try { c.Bytes(6); } catch (Exception e) { plain = e; }
        });
        Dispatch(Coremod, (ref NativeCall c) => advancedLength = c.Bytes(6).Length);
        Dispatch(null, (ref NativeCall c) => { try { c.Bytes(6); } catch (Exception e) { outside = e; } });

        Assert.Same(Plain, currentInHandler);  // the dispatcher runs each handler as its mod
        var refused = Assert.IsType<InvalidOperationException>(plain);
        Assert.StartsWith("NativeCall.Bytes: mod 'plain-mod' is not advanced.", refused.Message);
        Assert.Contains("add \"advanced\": true to its kiln.mod.json to allow it.", refused.Message);
        Assert.Equal(4, advancedLength);
        Assert.Equal("NativeCall.Bytes must be called from a mod (on the tick thread or in OnLoad)",
            Assert.IsType<InvalidOperationException>(outside).Message);
    }

    [Fact]
    public void TheDecisionIsTheRegisteringModsNotTheCallingThreads()
    {
        // an ordinary mod's handler stays refused even if an advanced mod is current when the call comes in, and the
        // other way round: the answer was decided where the handler was registered
        Exception? plain = null;
        int advanced = -1;
        Dispatch(Plain, (ref NativeCall c) =>
        {
            var outer = HostAccess.Current;
            HostAccess.Current = Coremod;
            try { c.Bytes(6); } catch (Exception e) { plain = e; }
            finally { HostAccess.Current = outer; }
        });
        Dispatch(Coremod, (ref NativeCall c) =>
        {
            var outer = HostAccess.Current;
            HostAccess.Current = null;
            try { advanced = c.Bytes(6).Length; }
            finally { HostAccess.Current = outer; }
        });
        Assert.IsType<InvalidOperationException>(plain);
        Assert.Equal(4, advanced);
    }

    [Fact]
    public void TypedAccessorsAndDamageCallWorkForAnOrdinaryMod()
    {
        float read = 0, attack = 0;
        bool wrote = false;
        Dispatch(Plain, (ref NativeCall c) =>
        {
            read = c.GetFloat(6);
            wrote = c.SetFloat(6, 1.5f);
        });
        Assert.Equal(1.5f, read);
        Assert.True(wrote);

        // OnDamage's own wrap, as an ordinary mod registers it
        using var call = new FakeCall(Abilities.DamageHash);
        Dispatch(Plain, Abilities.DamageWrap((ref DamageCall d) =>
        {
            attack = d.AttackMod;
            d.Damage = d.Damage with { Cut = 9f };
        }), call);
        Assert.Equal(1.5f, attack);
        Assert.Equal(9f, call.Cut);
    }

    [Fact]
    public void TheBytesCheckCostsAFieldRead()
    {
        // measured in the handler, as a hot wrap runs it; the best of 5 alternating rounds of each (a Release build gives
        // the real numbers)
        double gated = double.MaxValue, core = double.MaxValue, lookup = double.MaxValue;
        Dispatch(Coremod, (ref NativeCall c) =>
        {
            for (var round = 0; round < 5; round++)
            {
                core = Math.Min(core, Time(ref c, CoreLoop));
                gated = Math.Min(gated, Time(ref c, GatedLoop));
                lookup = Math.Min(lookup, Time(ref c, LookupLoop));
            }
        });
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"NativeCall.Bytes: {gated:F2} ns/call gated (field check), {core:F2} ns/call ungated core, " +
            $"{lookup:F2} ns/call with a per-call CurrentMod check instead");
        Assert.True(gated > 0 && core > 0);
    }

    const int Loops = 10_000_000;
    delegate long Loop(ref NativeCall c);

    static double Time(ref NativeCall c, Loop loop)
    {
        var sw = Stopwatch.StartNew();
        GC.KeepAlive(loop(ref c));
        return sw.Elapsed.TotalNanoseconds / Loops;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long CoreLoop(ref NativeCall c)
    {
        long sink = 0;
        for (var i = 0; i < Loops; i++) sink += c.BytesCore(6).Length;
        return sink;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long GatedLoop(ref NativeCall c)
    {
        long sink = 0;
        for (var i = 0; i < Loops; i++) sink += c.Bytes(6).Length;
        return sink;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long LookupLoop(ref NativeCall c)
    {
        long sink = 0;
        for (var i = 0; i < Loops; i++) sink += HostAccess.Current is { Info.Advanced: true } ? c.BytesCore(6).Length : 0;
        return sink;
    }
}
