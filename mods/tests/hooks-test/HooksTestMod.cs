// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Kiln;

namespace HooksTest;

/// <summary>
/// Player-free checks of kiln.hooks: install/remove, a priority chain, the original being called and its result kept,
/// a replaced return (driven by directly invoking the detoured function with a sentinel entity so the running game is
/// never affected), a mid hook, mod-shipped symbols, a reversible patch and its restore, and a per-call overhead
/// measurement. The persistent detour/mid/patch installed in <see cref="OnLoad"/> let a reload test exercise install
/// and owner-revoke on every reload. Prints "hooks-test: PASS|FAIL ..." lines and a summary.
/// </summary>
public sealed class HooksTestMod : Mod
{
    private const uint Sentinel = 0xFFFFFFF0;  // an entity id the world never contains
    private const nuint Magic = 0xC0FFEE;
    private const byte PatchByte = 0x90;

    private int _pass, _fail;
    private readonly List<HookHandle> _hooks = [];
    private long _d0Calls;         // genuine GetComponent calls seen by the pass-through detour
    private nuint _world;          // real server-world pointer, captured from a genuine call
    private int _chainHiSentinel, _chainLoSentinel;
    private long _midCalls;
    private nuint _midRcx, _midRdx, _midR8;  // registers the mid hook saw

    private void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"hooks-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    public override void OnLoad()
    {
        if (!Hooks.Available)
        {
            Check(false, "kiln.hooks unavailable on this build");
            Log.Info("hooks-test: done, 0 passed, 1 failed");
            return;
        }

        // Mod-shipped symbols resolve to the same address as the core symbol.
        try
        {
            var sig = Symbols.LoadShipped();
            var mine = Symbols.Get("hooks-test/GetComponent");
            var core = Symbols.Get("GetComponent");
            Check(sig.SymbolsOk == 1 && mine == core, $"shipped fragment resolves (hooks-test/GetComponent=0x{mine:X}, core=0x{core:X})");
        }
        catch (Exception e)
        {
            Check(false, $"shipped fragment: {e.Message}");
        }

        // Install a pass-through detour on GetComponent; capture the real world pointer and count calls.
        var before = Hooks.Stats().Handlers;
        _hooks.Add(Hooks.Detour("GetComponent", call =>
        {
            Interlocked.Increment(ref _d0Calls);
            if (_world == 0 && (uint)call[3] != Sentinel) _world = call[1];
            return call.CallOriginal();
        }, priority: 0));
        Check(Hooks.Stats().Handlers == before + 1, "install detour raises the live handler count");

        // A mid hook that reads registers.
        if (Symbols.TryGet("GetEntityTransform", out _))
            _hooks.Add(Hooks.Mid("GetEntityTransform", (ref Registers r) =>
            {
                Interlocked.Increment(ref _midCalls);
                _midRcx = r.Rcx; _midRdx = r.Rdx; _midR8 = r.R8;
            }));

        // Reversible byte patch, apply + restore, on a function that idles unused (SpawnWrapper). Microsecond window.
        if (Symbols.TryGet("SpawnWrapper", out var spawn))
        {
            var original = Memory.ReadBytes(spawn, 1)[0];
            var h = Memory.Patch(spawn, PatchByte);
            Check(Memory.ReadBytes(spawn, 1)[0] == PatchByte, "patch applied (byte changed in memory)");
            h.Remove();
            Check(Memory.ReadBytes(spawn, 1)[0] == original, "unpatch restores the original byte");
        }

        // Restore-on-unload proof: leave a patch installed on an int3 alignment byte before GetComponent (never
        // executed, so it is safe to hold patched across a whole reload test). On the next load, a correct
        // owner-revoke on the previous unload must have put the 0xCC back; if it did not, this byte reads 0x90.
        if (Symbols.TryGet("GetComponent", out var gcEntry))
        {
            var pad = gcEntry - 1;
            var padByte = Memory.ReadBytes(pad, 1)[0];
            if (padByte == 0xCC || padByte == PatchByte)
            {
                Check(padByte == 0xCC, "patch restored by the previous unload (padding byte is pristine)");
                _hooks.Add(Memory.Patch(pad, PatchByte));  // owner-revoke on unload restores it
            }
        }

        Context.Tick.Pre += OnPre;
        _ = RunAsync();
    }

    private void OnPre(in TickInfo tick) { }  // keeps a Pre subscription alive; work is driven by RunAsync

    private async Task RunAsync()
    {
        try
        {
            // Wait until the game has called GetComponent through our detour and we have the world pointer.
            for (var i = 0; i < 200 && (Interlocked.Read(ref _d0Calls) == 0 || _world == 0); i++)
                await Context.Scheduler.NextTick();
            Check(Interlocked.Read(ref _d0Calls) > 0, $"the original is called by the game ({Interlocked.Read(ref _d0Calls)} GetComponent calls forwarded)");
            Check(_world != 0, "captured the live server-world pointer from a hooked argument");

            // Baseline: invoking GetComponent ourselves for a non-existent entity returns a non-magic value.
            var baseline = CallGetComponent(Sentinel);
            Check(baseline != Magic, $"baseline direct call returns 0x{baseline:X} (not the sentinel magic)");

            // Replace the return for the sentinel only, at top priority, without calling the original; a lower-
            // priority pass-through proves the chain is cut for the sentinel but not for real calls.
            var hiBefore = _chainHiSentinel;
            var replace = Hooks.Detour("GetComponent", call =>
            {
                if ((uint)call[3] == Sentinel) { Interlocked.Increment(ref _chainHiSentinel); return Magic; }
                return call.CallOriginal();
            }, priority: 100);
            var lower = Hooks.Detour("GetComponent", call =>
            {
                if ((uint)call[3] == Sentinel) Interlocked.Increment(ref _chainLoSentinel);
                return call.CallOriginal();
            }, priority: 50);

            var realCallsBefore = Interlocked.Read(ref _d0Calls);
            var replaced = CallGetComponent(Sentinel);
            Check(replaced == Magic, $"a handler replaces the return value (got 0x{replaced:X})");
            Check(_chainHiSentinel == hiBefore + 1, "the top-priority handler ran for the sentinel call");
            Check(_chainLoSentinel == 0, "the replaced call short-circuits the rest of the chain and the original");

            // A couple of steps: real (non-sentinel) calls must still flow through every handler and the original.
            await Context.Scheduler.NextTick();
            await Context.Scheduler.NextTick();
            Check(Interlocked.Read(ref _d0Calls) > realCallsBefore, "non-sentinel calls still reach the whole chain and the original");

            // Remove the extra handlers; the pass-through detour stays. Confirm removal.
            var handlersWith = Hooks.Stats().Handlers;
            replace.Remove();
            lower.Remove();
            var handlersAfter = Hooks.Stats().Handlers;
            Check(handlersAfter == handlersWith - 2, $"removing handlers lowers the count ({handlersWith} -> {handlersAfter})");
            Check(CallGetComponent(Sentinel) != Magic, "after removal the replaced return is gone");

            // Mid hook: trigger GetEntityTransform ourselves (idle servers rarely call it) and confirm the handler
            // ran and read a real register (one of rcx/rdx/r8 holds the world pointer or the sentinel entity).
            if (Symbols.TryGet("GetEntityTransform", out _))
            {
                for (var i = 0; i < 5; i++) CallGetEntityTransform(Sentinel);
                Check(Interlocked.Read(ref _midCalls) > 0, $"mid hook fires ({Interlocked.Read(ref _midCalls)} calls)");
                Check(_midRcx == _world || _midRdx == _world || _midR8 == _world ||
                      (uint)_midRdx == Sentinel || (uint)_midR8 == Sentinel,
                      $"mid hook reads registers (rcx=0x{_midRcx:X} rdx=0x{_midRdx:X} r8=0x{_midR8:X})");
            }

            Measure();
            Log.Info($"hooks-test: stats {Hooks.Stats()}");
        }
        catch (Exception e)
        {
            Check(false, $"unexpected exception: {e}");
        }
        Log.Info($"hooks-test: done, {_pass} passed, {_fail} failed");
    }

    private unsafe nuint CallGetComponent(uint entity)
    {
        var gc = (delegate* unmanaged[Cdecl]<void*, nuint, uint, uint, nuint>)Symbols.Get("GetComponent");
        byte* scratch = stackalloc byte[32];
        for (int i = 0; i < 32; i++) scratch[i] = 0;
        return gc(scratch, _world, 0, entity);
    }

    private unsafe void CallGetEntityTransform(uint entity)
    {
        // Pattern analysis: rcx = world, rdx = entity, r8 = out transform. A missing entity returns without writing.
        var f = (delegate* unmanaged[Cdecl]<nuint, uint, void*, nuint>)Symbols.Get("GetEntityTransform");
        byte* outT = stackalloc byte[64];
        for (int i = 0; i < 64; i++) outT[i] = 0;
        _ = f(_world, entity, outT);
    }

    private unsafe void Measure()
    {
        const int K = 200_000;
        var gc = (delegate* unmanaged[Cdecl]<void*, nuint, uint, uint, nuint>)Symbols.Get("GetComponent");
        byte* scratch = stackalloc byte[32];
        for (int i = 0; i < 32; i++) scratch[i] = 0;

        for (int i = 0; i < 10_000; i++) _ = gc(scratch, _world, 0, Sentinel);  // warm up
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < K; i++) _ = gc(scratch, _world, 0, Sentinel);
        sw.Stop();
        var withManaged = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / K;  // ns/call through a managed handler

        // Remove every handler: the detour stays installed but the chain is empty, so dispatch calls the original
        // directly with no managed transition.
        foreach (var h in _hooks) h.Remove();
        _hooks.Clear();
        for (int i = 0; i < 10_000; i++) _ = gc(scratch, _world, 0, Sentinel);
        sw.Restart();
        for (int i = 0; i < K; i++) _ = gc(scratch, _world, 0, Sentinel);
        sw.Stop();
        var emptyChain = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / K;

        Log.Info($"hooks-test: per-call GetComponent ~{emptyChain:F0} ns (empty detour), ~{withManaged:F0} ns (through a managed handler); " +
                 $"managed dispatch overhead ~{withManaged - emptyChain:F0} ns/call");
        Check(withManaged > 0 && emptyChain > 0, "measured per-call overhead");
    }

    public override void OnUnload()
    {
        foreach (var h in _hooks) h.Remove();  // optional: owner-revoke also does this and restores the patch
        _hooks.Clear();
        Log.Info("hooks-test: unloading");
    }
}
