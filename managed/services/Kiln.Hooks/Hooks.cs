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
    internal HookHandle(long cookie, ulong native) { _cookie = cookie; Native = native; }

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
    internal readonly record struct DetourReg(DetourHandler Handler, ModContext? Mod,
        System.Runtime.CompilerServices.StrongBox<long>? InFlight);
    internal readonly record struct MidReg(MidHandler Handler, ModContext? Mod,
        System.Runtime.CompilerServices.StrongBox<long>? InFlight);

    private static readonly ConcurrentDictionary<long, DetourReg> Detours = new();
    private static readonly ConcurrentDictionary<long, MidReg> Mids = new();
    private static long _nextCookie;

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
        var cookie = Interlocked.Increment(ref _nextCookie);
        Detours[cookie] = new DetourReg(handler, mod, HooksGate.Current(mod).InFlight);
        ulong native = 0;
        int rc = t->Detour(mod.Owner, address, priority, &DetourDispatch, (void*)(nint)cookie, &native);
        if (rc != HooksNative.Ok)
        {
            Detours.TryRemove(cookie, out _);
            throw new InvalidOperationException($"Hooks.Detour({label ?? address.ToString("X")}): {HooksNative.Describe(rc)}");
        }
        HooksGate.Track(mod);
        mod.Log.Info($"hooks: detour on {label ?? "0x" + address.ToString("X")} (priority {priority})");
        return new HookHandle(cookie, native);
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
        var cookie = Interlocked.Increment(ref _nextCookie);
        Mids[cookie] = new MidReg(handler, mod, HooksGate.Current(mod).InFlight);
        ulong native = 0;
        int rc = t->Mid(mod.Owner, address, priority, &MidDispatch, (void*)(nint)cookie, &native);
        if (rc != HooksNative.Ok)
        {
            Mids.TryRemove(cookie, out _);
            throw new InvalidOperationException($"Hooks.Mid({label ?? address.ToString("X")}): {HooksNative.Describe(rc)}");
        }
        HooksGate.Track(mod);
        mod.Log.Info($"hooks: mid hook on {label ?? "0x" + address.ToString("X")} (priority {priority})");
        return new HookHandle(cookie, native);
    }

    // ---- fixed dispatchers (default load context: stable function pointers) ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint DetourDispatch(HooksNative.HookCallRaw* call, void* user)
    {
        var cookie = (long)user;
        try
        {
            if (Detours.TryGetValue(cookie, out var reg))
            {
                var box = reg.InFlight;
                if (box is not null) Interlocked.Increment(ref box.Value);
                // Run the handler as its mod: on a game job thread ModContext.CurrentMod (thread-static) is otherwise
                // null, and every gated call inside the handler (Memory.TryRead, Symbols.Get, ...) would throw into
                // this dispatcher, which swallows it: the handler silently stopped at its first Memory read.
                var outer = ModContext.CurrentMod;
                ModContext.CurrentMod = reg.Mod;
                try { return reg.Handler(new HookCall(call)); }
                finally
                {
                    ModContext.CurrentMod = outer;
                    if (box is not null) Interlocked.Decrement(ref box.Value);
                }
            }
        }
        catch (Exception e)
        {
            // a managed handler must never throw into native code; fall through to the original
            ReportFault(cookie, "detour", e);
        }
        return HooksNative.T->CallOriginal(call);  // mod gone or faulted: pass through
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MidDispatch(HooksNative.RegsRaw* regs, void* user)
    {
        var cookie = (long)user;
        try
        {
            if (Mids.TryGetValue(cookie, out var reg))
            {
                var box = reg.InFlight;
                if (box is not null) Interlocked.Increment(ref box.Value);
                var outer = ModContext.CurrentMod;
                ModContext.CurrentMod = reg.Mod;  // see DetourDispatch
                try { reg.Handler(ref Unsafe.AsRef<Registers>(regs)); }
                finally
                {
                    ModContext.CurrentMod = outer;
                    if (box is not null) Interlocked.Decrement(ref box.Value);
                }
            }
        }
        catch (Exception e)
        {
            // swallow: the game resumes with whatever the handler managed to set
            ReportFault(cookie, "mid hook", e);
        }
    }

    // A throwing handler used to vanish without a trace (the dispatcher must swallow it). Log the first few faults of
    // each handler to its mod's log, from whatever thread the hook runs on (the logger is thread-safe).
    private static readonly ConcurrentDictionary<long, int> Faults = new();

    private static void ReportFault(long cookie, string kind, Exception e)
    {
        try
        {
            var n = Faults.AddOrUpdate(cookie, 1, (_, v) => v + 1);
            if (n > 3) return;
            var mod = Detours.TryGetValue(cookie, out var d) ? d.Mod : Mids.TryGetValue(cookie, out var m) ? m.Mod : null;
            var text = $"hooks: {kind} handler threw{(n == 3 ? " (3rd time; further faults are not logged)" : "")}: " +
                       $"{e.GetType().Name}: {e.Message}";
            if (mod is not null) mod.Log.Error(text);
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
        if (c != 0)
        {
            Detours.TryRemove(c, out _);
            Mids.TryRemove(c, out _);
        }
        if (native != 0 && HooksNative.T != null) HooksNative.T->Revoke(native);
    }

    // Drops every cookie that belongs to a retiring mod, so no further dispatch can reach its (collectible) handlers.
    internal static void Forget(ModContext mod)
    {
        foreach (var kv in Detours)
            if (kv.Value.Mod == mod) Detours.TryRemove(kv.Key, out _);
        foreach (var kv in Mids)
            if (kv.Value.Mod == mod) Mids.TryRemove(kv.Key, out _);
        foreach (var kv in Faults)
            if (!Detours.ContainsKey(kv.Key) && !Mids.ContainsKey(kv.Key)) Faults.TryRemove(kv.Key, out _);
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
