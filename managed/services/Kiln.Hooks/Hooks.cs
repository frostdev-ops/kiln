// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>A detour call in progress. Read and change the integer/pointer arguments, call <see cref="CallOriginal"/>
/// to run the rest of the chain and the real function (optionally after changing the args), or return a value without
/// calling it to replace the function entirely. Only the first four integer/pointer arguments (rcx, rdx, r8, r9) are
/// visible; a target with more integer arguments, or floating-point arguments, needs a mid hook instead.</summary>
public readonly unsafe ref struct HookCall
{
    private readonly HooksNative.HookCallRaw* _p;
    internal HookCall(HooksNative.HookCallRaw* p) => _p = p;

    /// <summary>Number of valid argument slots (4 on x64).</summary>
    public int ArgCount => (int)_p->ArgCount;

    /// <summary>Argument <paramref name="i"/> (0..3): rcx, rdx, r8, r9.</summary>
    public nuint this[int i]
    {
        get => i switch { 0 => _p->Arg0, 1 => _p->Arg1, 2 => _p->Arg2, 3 => _p->Arg3,
                          _ => throw new IndexOutOfRangeException() };
        set
        {
            switch (i)
            {
                case 0: _p->Arg0 = value; break;
                case 1: _p->Arg1 = value; break;
                case 2: _p->Arg2 = value; break;
                case 3: _p->Arg3 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    /// <summary>Runs the rest of the detour chain and then the real function, with the current argument values, and
    /// returns what it produced.</summary>
    public nuint CallOriginal() => HooksNative.T->CallOriginal(_p);

    /// <summary>Reinterprets argument <paramref name="i"/> as an unmanaged value (e.g. a pointer dereferenced by the
    /// caller). The pointer is only valid for the duration of the handler.</summary>
    public T ReadArgAs<T>(int i) where T : unmanaged => *(T*)this[i];
}

/// <summary>A detour handler: returns the value the hooked function should yield.</summary>
public delegate nuint DetourHandler(HookCall call);

/// <summary>A mid-hook handler: reads and writes the registers in place.</summary>
public delegate void MidHandler(ref Registers regs);

/// <summary>Full CPU register context at a mid hook. Writing a field changes the register when the game resumes.
/// Layout mirrors the engine's; xmm registers are exposed as raw bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Registers
{
    private fixed byte _xmm[256];
    /// <summary>General registers. <see cref="Rsp"/> is the hooked code's rsp and is read-only.</summary>
    public nuint Rflags, R15, R14, R13, R12, R11, R10, R9, R8, Rdi, Rsi, Rdx, Rcx, Rbx, Rax, Rbp, Rsp;
    /// <summary>safetyhook's rule: starts at <c>Rsp - 8</c> (it points at <see cref="Rip"/>); the game resumes with
    /// <c>rsp = TrampolineRsp; ret</c>. At a function's first instruction, <c>Rax = value; TrampolineRsp = Rsp</c>
    /// returns to the caller without running the function.</summary>
    public nuint TrampolineRsp;
    /// <summary>Where execution goes on (the next hook, or the overwritten instructions).</summary>
    public nuint Rip;

    /// <summary>xmm register <paramref name="n"/> (0..15) as a 16-byte span.</summary>
    public Span<byte> Xmm(int n)
    {
        if ((uint)n > 15) throw new IndexOutOfRangeException();
        fixed (byte* p = _xmm) return new Span<byte>(p + n * 16, 16);
    }

    /// <summary>The low 8 bytes of xmm<paramref name="n"/> as a double (a float argument/return).</summary>
    public double XmmD(int n) => BitConverter.ToDouble(Xmm(n));
    public float XmmF(int n) => BitConverter.ToSingle(Xmm(n));
}

/// <summary>A handle to an installed hook or patch; removing it is idempotent.</summary>
public sealed class HookHandle : IDisposable
{
    private long _cookie;
    internal ulong Native;
    // Which install of the cookie this handle made: a cookie is reused (see HookCookies), so a stale handle must not
    // remove a later install that took the same cookie. 0 for patches (no cookie).
    internal readonly long Install;
    internal HookHandle(long cookie, ulong native, long install = 0) { _cookie = cookie; Native = native; Install = install; }

    /// <summary>Removes this hook/patch. The detour stays installed if other handlers remain on the target; this
    /// owner's handler simply leaves the chain.</summary>
    public void Remove() => Hooks.Remove(this, ref _cookie);
    public void Dispose() => Remove();
}

/// <summary>
/// Forge's lowest tier for Kiln: install inline detours (with a callable original and a priority chain, like a Mixin
/// @Inject head/tail with cancellation) and mid-function hooks (full register access) on arbitrary game code, read
/// and patch memory, and look symbols up. Only mods whose <c>kiln.mod.json</c> says <c>"advanced": true</c> may use
/// it. Handlers run on whatever thread the target runs on (often the tick thread; job threads too), so keep them
/// short and thread-safe and do not call tick-thread-only APIs from them.
/// </summary>
public static unsafe class Hooks
{
    // One install of a handler. A class, so a dispatch that is still running the old install of a reused cookie keeps
    // its own registration (handler, mod, fault count) and a removal can tell two installs of one cookie apart.
    internal abstract class Reg(ModContext mod, StrongBox<long> inFlight, long cookie, long install)
    {
        public readonly ModContext Mod = mod;
        public readonly StrongBox<long> InFlight = inFlight;
        public readonly long Cookie = cookie;
        public readonly long Install = install;
        public ulong Native;  // the native link handle, once installed
        public int Faults;    // per install: a handler that takes a reused cookie starts at 0
    }
    internal sealed class DetourReg(DetourHandler handler, ModContext mod, StrongBox<long> inFlight, long cookie, long install)
        : Reg(mod, inFlight, cookie, install)
    {
        public readonly DetourHandler Handler = handler;
    }
    internal sealed class MidReg(MidHandler handler, ModContext mod, StrongBox<long> inFlight, long cookie, long install)
        : Reg(mod, inFlight, cookie, install)
    {
        public readonly MidHandler Handler = handler;
    }

    internal static readonly ConcurrentDictionary<long, DetourReg> Detours = new();
    internal static readonly ConcurrentDictionary<long, MidReg> Mids = new();
    private static long _nextInstall;

    /// <summary>True when kiln.hooks is loaded on this build.</summary>
    public static bool Available => HooksNative.T != null;

    // ---- detours ----
    /// <summary>Detours the function at <paramref name="symbol"/> (a sigdb name — core, or "&lt;modid&gt;/name" from a
    /// shipped fragment). Higher <paramref name="priority"/> runs first.</summary>
    public static HookHandle Detour(string symbol, DetourHandler handler, int priority = 0)
        => Detour(Symbols.Get(symbol), handler, priority, symbol);

    /// <summary>Detours the function at <paramref name="address"/> (absolute).</summary>
    public static HookHandle Detour(nuint address, DetourHandler handler, int priority = 0)
        => Detour(address, handler, priority, null);

    private static HookHandle Detour(nuint address, DetourHandler handler, int priority, string? label)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var t = HooksNative.Require("Hooks.Detour");
        var mod = HooksGate.RequireAdvanced("Hooks.Detour");
        var where = label ?? "0x" + address.ToString("X");
        var h = Install("Hooks.Detour", where, mod, HookCookies.Detour, address,
            (cookie, inFlight, install) => new DetourReg(handler, mod, inFlight, cookie, install),
            (owner, cookie, native) => t->Detour(owner, address, priority, &DetourDispatch, (void*)(nint)cookie, native));
        mod.Log.Info($"hooks: detour on {where} (priority {priority})");
        return h;
    }

    // ---- mid hooks ----
    /// <summary>Installs a mid-function hook at <paramref name="symbol"/>.</summary>
    public static HookHandle Mid(string symbol, MidHandler handler, int priority = 0)
        => Mid(Symbols.Get(symbol), handler, priority, symbol);

    /// <summary>Installs a mid-function hook at <paramref name="address"/> (absolute).</summary>
    public static HookHandle Mid(nuint address, MidHandler handler, int priority = 0)
        => Mid(address, handler, priority, null);

    private static HookHandle Mid(nuint address, MidHandler handler, int priority, string? label)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var t = HooksNative.Require("Hooks.Mid");
        var mod = HooksGate.RequireAdvanced("Hooks.Mid");
        var where = label ?? "0x" + address.ToString("X");
        var h = Install("Hooks.Mid", where, mod, HookCookies.Mid, address,
            (cookie, inFlight, install) => new MidReg(handler, mod, inFlight, cookie, install),
            (owner, cookie, native) => t->Mid(owner, address, priority, &MidDispatch, (void*)(nint)cookie, native));
        mod.Log.Info($"hooks: mid hook on {where} (priority {priority})");
        return h;
    }

    internal delegate int NativeInstall(ulong owner, long cookie, ulong* native);

    // Installs one link for a live context. Runs under HooksGate.Lifecycle, which Forget (the context's retire) takes
    // too, so a retire either waits for the install and then finds its registration, or ran before it. The context is
    // checked again after the native install: a retire that began meanwhile may have taken its list of cleanups before
    // Track joined it (the first hook of a context), and then nothing would revoke this link; it is undone here instead.
    // A refused install leaves no link, no registration and no taken cookie.
    private static HookHandle Install(string api, string where, ModContext mod, byte kind, nuint address,
        Func<long, StrongBox<long>, long, Reg> make, NativeInstall install)
    {
        lock (HooksGate.Lifecycle)
        {
            if (!mod.Alive) throw HooksGate.Retired(api, mod);
            var inFlight = HooksGate.Current(mod).InFlight;  // joins the context's cleanups (Track)
            // The cookie is the native link's user pointer. The same mod re-installing on the same address gets a cookie
            // a revoked link of it already carries, so the registry revives that link instead of taking a new slot.
            var cookie = HookCookies.Take(mod.Info.Id, kind, address);
            var reg = make(cookie, inFlight, Interlocked.Increment(ref _nextInstall));
            Add(reg);
            ulong native = 0;
            int rc = install(HookCookies.Owner(mod), cookie, &native);
            if (rc != HooksNative.Ok)
            {
                Drop(reg);
                HookCookies.Return(cookie);  // nothing native carries it
                throw new InvalidOperationException($"{api}({where}): {HooksNative.Describe(rc)}");
            }
            reg.Native = native;
            if (!mod.Alive)
            {
                Drop(reg);
                if (HooksNative.T != null) HooksNative.T->Revoke(native);
                HookCookies.Return(cookie);
                HooksGate.Untrack(mod);
                throw HooksGate.Retired(api, mod);
            }
            return new HookHandle(cookie, native, reg.Install);
        }
    }

    private static void Add(Reg reg)
    {
        if (reg is DetourReg d) Detours[reg.Cookie] = d;
        else Mids[reg.Cookie] = (MidReg)reg;
    }

    private static void Drop(Reg reg)
    {
        if (reg is DetourReg d) Detours.TryRemove(KeyValuePair.Create(reg.Cookie, d));
        else Mids.TryRemove(KeyValuePair.Create(reg.Cookie, (MidReg)reg));
    }

    // ---- fixed dispatchers (default load context: stable function pointers) ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint DetourDispatch(HooksNative.HookCallRaw* call, void* user)
    {
        DetourReg? reg = null;
        try
        {
            if (Detours.TryGetValue((long)user, out reg))
            {
                var box = reg.InFlight;
                Interlocked.Increment(ref box.Value);
                // Run the handler as its mod: on a game job thread ModContext.CurrentMod (thread-static) is otherwise
                // null, and every gated call inside the handler (Memory.TryRead, Symbols.Get, ...) would throw into
                // this dispatcher, which swallows it: the handler silently stopped at its first Memory read.
                var outer = ModContext.CurrentMod;
                ModContext.CurrentMod = reg.Mod;
                try { return reg.Handler(new HookCall(call)); }
                finally
                {
                    ModContext.CurrentMod = outer;
                    Interlocked.Decrement(ref box.Value);
                }
            }
        }
        catch (Exception e)
        {
            // a managed handler must never throw into native code; fall through to the original
            ReportFault(reg, "detour", e);
        }
        return HooksNative.T->CallOriginal(call);  // mod gone or faulted: pass through
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MidDispatch(HooksNative.RegsRaw* regs, void* user)
    {
        MidReg? reg = null;
        try
        {
            if (Mids.TryGetValue((long)user, out reg))
            {
                var box = reg.InFlight;
                Interlocked.Increment(ref box.Value);
                var outer = ModContext.CurrentMod;
                ModContext.CurrentMod = reg.Mod;  // see DetourDispatch
                try { reg.Handler(ref Unsafe.AsRef<Registers>(regs)); }
                finally
                {
                    ModContext.CurrentMod = outer;
                    Interlocked.Decrement(ref box.Value);
                }
            }
        }
        catch (Exception e)
        {
            // swallow: the game resumes with whatever the handler managed to set
            ReportFault(reg, "mid hook", e);
        }
    }

    // A throwing handler used to vanish without a trace (the dispatcher must swallow it). Log the first few faults of
    // each install to its mod's log, from whatever thread the hook runs on (the logger is thread-safe). Counted on the
    // registration the faulting call ran, so a later install under the same cookie starts at 0.
    private static void ReportFault(Reg? reg, string kind, Exception e)
    {
        try
        {
            if (reg is null) return;
            var n = Interlocked.Increment(ref reg.Faults);
            if (n > 3) return;
            reg.Mod.Log.Error($"hooks: {kind} handler threw{(n == 3 ? " (3rd time; further faults are not logged)" : "")}: " +
                              $"{e.GetType().Name}: {e.Message}");
        }
        catch
        {
            // never throw from the fault path
        }
    }

    internal static void Remove(HookHandle handle, ref long cookie)
    {
        // Patches have no cookie (0) but do have a native handle, so never gate the native revoke on the cookie.
        var c = Interlocked.Exchange(ref cookie, 0);
        var native = Interlocked.Exchange(ref handle.Native, 0);
        if (c == 0 && native == 0) return;  // already removed
        // Only this handle's own install: after its mod retired, the cookie may belong to a newer install.
        Reg? reg = null;
        if (c != 0)
        {
            if (Detours.TryGetValue(c, out var d) && d.Install == handle.Install && Detours.TryRemove(KeyValuePair.Create(c, d))) reg = d;
            else if (Mids.TryGetValue(c, out var m) && m.Install == handle.Install && Mids.TryRemove(KeyValuePair.Create(c, m))) reg = m;
        }
        // A stale handle's native id is refused by the registry once its link was revived under a new one.
        if (native != 0 && HooksNative.T != null) HooksNative.T->Revoke(native);
        // Free the cookie only once no live link carries it, so two live links never dispatch to one registration.
        if (reg is not null) HookCookies.Return(reg.Cookie);
    }

    // Drops every registration of a retiring mod, so no further dispatch can reach its (collectible) handlers, revokes
    // their links and frees their cookies for the mod's next load. Patches and anything under the mod's own owner went
    // with RevokeOwner before this.
    internal static void Forget(ModContext mod)
    {
        lock (HooksGate.Lifecycle) ForgetLocked(mod);
    }

    private static void ForgetLocked(ModContext mod)
    {
        var gone = new List<Reg>();
        foreach (var kv in Detours)
            if (kv.Value.Mod == mod && Detours.TryRemove(kv)) gone.Add(kv.Value);
        foreach (var kv in Mids)
            if (kv.Value.Mod == mod && Mids.TryRemove(kv)) gone.Add(kv.Value);
        var t = HooksNative.T;
        foreach (var r in gone)
            if (t != null && r.Native != 0) t->Revoke(r.Native);
        foreach (var r in gone) HookCookies.Return(r.Cookie);
    }

    /// <summary>Hook/patch counts for diagnostics.</summary>
    public static HooksStats Stats()
    {
        var t = HooksNative.Require("Hooks.Stats");
        HooksNative.Stats s;
        s.Size = (uint)sizeof(HooksNative.Stats);
        if (t->GetStats(&s) != HooksNative.Ok) return default;
        return new HooksStats((int)s.DetourTargets, (int)s.MidTargets, (int)s.Handlers, (int)s.Patches, s.DetourCalls,
            s.MidCalls);
    }
}

/// <summary>Snapshot of what kiln.hooks has installed.</summary>
public readonly record struct HooksStats(int DetourTargets, int MidTargets, int Handlers, int Patches,
    ulong DetourCalls, ulong MidCalls);
