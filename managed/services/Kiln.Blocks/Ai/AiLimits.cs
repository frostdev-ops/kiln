// SPDX-License-Identifier: MIT
namespace Kiln;

// Tick-owned budgets include pending spawns, so concurrent async actions cannot all pass the same population check.
internal sealed class AiLimits
{
    internal const int PerCreature = 16, PerMod = 128, Total = 256, MaxDepth = 3;
    private readonly HashSet<Lease> _leases = [];
    private readonly Dictionary<uint, int> _actions = [];
    private ulong _step = ulong.MaxValue;
    private int _actionCount, _births;

    internal sealed class Lease(AiLimits budget, string mod, uint parent, ulong lifetime) : IDisposable
    {
        internal readonly string Mod = mod;
        internal readonly uint Parent = parent;
        internal readonly ulong Lifetime = lifetime;
        internal bool Active = true;
        public void Dispose() { Active = false; budget._leases.Remove(this); }
    }

    internal void Begin(ulong step)
    {
        if (_step == step) return;
        _step = step;
        _actions.Clear();
        _actionCount = _births = 0;
    }

    internal bool Action(uint entity)
    {
        var n = _actions.GetValueOrDefault(entity);
        if (n >= 4 || _actionCount >= 64) return false;
        _actions[entity] = n + 1;
        _actionCount++;
        return true;
    }

    internal Lease? Reserve(string mod, uint parent, int depth, ulong lifetime = 0)
    {
        if (depth >= MaxDepth || _births >= 16 || _leases.Count >= Total ||
            _leases.Count(x => x.Mod == mod) >= PerMod || _leases.Count(x => x.Parent == parent && x.Lifetime == lifetime) >= PerCreature) return null;
        var lease = new Lease(this, mod, parent, lifetime);
        _leases.Add(lease);
        _births++;
        return lease;
    }

    internal void Release(string mod)
    {
        foreach (var lease in _leases.Where(x => x.Mod == mod).ToList()) lease.Dispose();
    }
}
