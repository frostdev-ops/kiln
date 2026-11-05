// SPDX-License-Identifier: MIT
using Kiln;

namespace HookSample;

/// <summary>
/// Shows Kiln's Forge-lowest-tier (kiln.hooks): an inline detour on a frequently-called, harmless game function
/// (the entity <c>GetComponent</c>) that counts calls and forwards to the original; a mid hook that reads a register;
/// a reversible byte patch; and a mod-shipped symbol fragment. Requires <c>"advanced": true</c> in kiln.mod.json.
/// </summary>
public sealed class HookSampleMod : Mod
{
    private readonly List<HookHandle> _hooks = [];
    private long _getComponentCalls;
    private bool _midLogged, _midTriggered;
    private nuint _world;
    private ulong _loadedAt;

    public override void OnLoad()
    {
        if (!Hooks.Available)
        {
            Log.Warn("kiln.hooks is unavailable on this build; nothing to show");
            return;
        }

        // 1. A mod-shipped symbol fragment: hooksample/SpawnWrapper resolves against the running exe.
        try
        {
            var sig = Symbols.LoadShipped();
            Log.Info($"shipped symbols: {sig.SymbolsOk}/{sig.Symbols} resolved; " +
                     $"hooksample/SpawnWrapper at 0x{Symbols.Get("hooksample/SpawnWrapper"):X}");
        }
        catch (Exception e)
        {
            Log.Warn($"shipped symbols not loaded: {e.Message}");
        }

        // 2. Inline detour with a callable original: count every GetComponent call (and note the world pointer), then
        //    run the real function.
        _hooks.Add(Hooks.Detour("GetComponent", call =>
        {
            Interlocked.Increment(ref _getComponentCalls);
            if (_world == 0) _world = call[1];
            return call.CallOriginal();
        }));

        // 3. A mid hook that reads a register: log the registers at the GetEntityTransform entry the first time it
        //    runs (OnPre triggers it once we have a world pointer, since idle servers rarely call it).
        if (Symbols.TryGet("GetEntityTransform", out _))
        {
            _hooks.Add(Hooks.Mid("GetEntityTransform", (ref Registers r) =>
            {
                if (_midLogged) return;
                _midLogged = true;
                Log.Info($"mid hook: GetEntityTransform entry, rcx=0x{r.Rcx:X} rdx=0x{r.Rdx:X} r8=0x{r.R8:X}");
            }));
        }

        // 4. A reversible byte patch: flip the first byte of a function that idles unused (SpawnWrapper), confirm it
        //    changed, then restore it. (Kept to a microsecond window; the same restore path runs automatically when a
        //    patch's owner is revoked on unload.)
        if (Symbols.TryGet("SpawnWrapper", out var spawn))
        {
            var original = Memory.ReadBytes(spawn, 1)[0];
            using (Memory.Patch(spawn, 0x90))
            {
                var patched = Memory.ReadBytes(spawn, 1)[0];
                Log.Info($"patch demo: SpawnWrapper[0] 0x{original:X2} -> 0x{patched:X2}");
            }
            var restored = Memory.ReadBytes(spawn, 1)[0];
            Log.Info($"patch demo: restored to 0x{restored:X2} ({(restored == original ? "ok" : "MISMATCH")})");
        }

        Context.Tick.Pre += OnPre;
        Log.Info($"HookSample loaded: {_hooks.Count} hook(s) installed; {Hooks.Stats()}");
    }

    private unsafe void OnPre(in TickInfo tick)
    {
        if (_loadedAt == 0) _loadedAt = tick.Step;

        // Trigger the mid hook once, by calling GetEntityTransform ourselves for a non-existent entity (safe).
        if (!_midTriggered && _world != 0 && Symbols.TryGet("GetEntityTransform", out var get))
        {
            _midTriggered = true;
            var f = (delegate* unmanaged[Cdecl]<nuint, uint, void*, nuint>)get;
            byte* outT = stackalloc byte[64];
            for (int i = 0; i < 64; i++) outT[i] = 0;
            _ = f(_world, 0xFFFFFFF0u, outT);
        }

        if ((tick.Step - _loadedAt) == 0 || (tick.Step - _loadedAt) % 735 != 0) return;  // ~30 s
        var s = Hooks.Stats();
        Log.Info($"GetComponent called {Interlocked.Read(ref _getComponentCalls)} times so far " +
                 $"({s.DetourCalls} detour dispatches, {s.Handlers} handlers live)");
    }

    public override void OnUnload()
    {
        // Hooks and patches are revoked automatically with the mod; removing handles here is optional and idempotent.
        foreach (var h in _hooks) h.Remove();
        _hooks.Clear();
        Log.Info("HookSample unloaded");
    }
}
