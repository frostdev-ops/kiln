// SPDX-License-Identifier: MIT
// Package 5: GamePetWorld's bodies under kiln.pets' native safety module. A body's guard row is bound in the same tick as
// its spawn (Staging: invulnerable and harmless while it is prepared), activated with its final lease, read every Pre (a
// latched down or a lost protection is a down), and dropped only once the body is gone. The owner's player entity and the
// declared hostile pairs follow the owners' sessions. Targets are classified from kiln.creatures and kiln.players.
namespace Kiln;

internal sealed unsafe partial class GamePetWorld
{
    private readonly HashSet<(uint, uint)> _appliedPairs = [];

    private static PetsNative.Table* Native => PetsNative.T;

    private bool NativeBind(PetBody body, PetBodyRequest request, ControlBodyKey key, out string? reason)
    {
        reason = null;
        var t = Native;
        if (t == null)
        {
            reason = "ProtectionUnavailable: kiln.pets' native safety module is not loaded: no pet body is made without its guard";
            return false;
        }
        var owner = OnlineOwner(request.Owner);
        var b = new PetsNative.Binding
        {
            Size = (uint)sizeof(PetsNative.Binding), Flags = PetsNative.BindStaging, Body = PetsNative.Key(key),
            Pet = PetsNative.Id(request.Pet.Value),
            Owner = new PetsNative.CharacterKey128 { Account = request.Owner.Account, Character = PetsNative.Id(request.Owner.Id) },
            CharacterSession = request.OwnerSession, OwnerEntity = owner?.Entity.Id.Value ?? 0,
            HostileFactions = request.Definition.Targets.HostileMask, Pvp = (uint)request.Definition.Targets.Pvp,
        };
        var rc = t->Bind(0, &b);
        if (rc == PetsNative.Ok) return true;
        reason = rc switch
        {
            PetsNative.Unavailable => "ProtectionUnavailable: the native death guard is not installed (decisions.native_guards), or protection was withdrawn",
            PetsNative.BufferTooSmall => "Capacity: 256 guarded pet bodies",
            _ => $"ProtectionRefused: kiln.pets refused the guard row ({rc})",
        };
        return false;
    }

    private bool NativeActivate(PetBody body, out string? reason)
    {
        reason = null;
        var t = Native;
        if (t == null || Tag(body).Control is not { } c) return true;
        var rc = t->Activate(0, PetsNative.Key(c.Key));
        if (rc == PetsNative.Ok) return true;
        reason = $"ProtectionLost: the guard row is gone ({rc})";
        return false;
    }

    private void NativeRetire(PetBody body)
    {
        var t = Native;
        if (t == null || Tag(body).Control is not { } c) return;
        t->RetireBinding(0, PetsNative.Key(c.Key));
    }

    private PetsNative.State? NativeState(PetBody body)
    {
        var t = Native;
        if (t == null || body.Tag is not BodyTag { Control: { } c }) return null;
        var s = new PetsNative.State { Size = (uint)sizeof(PetsNative.State) };
        return t->InspectBody(PetsNative.Key(c.Key), &s) == PetsNative.Ok ? s : null;
    }

    private partial bool NativeDownPending(PetBody body) => NativeState(body) is { } s && (s.Flags & PetsNative.DownPending) != 0;

    private partial bool ProtectionLost(PetBody body)
    {
        if (Tag(body).Control is null) return false;
        var s = NativeState(body);
        // a live pet body without its guard row has lost its protection (the module's world ended, the row was dropped)
        return s is null || (s.Value.Flags & PetsNative.ProtectionLost) != 0;
    }

    /// <summary>The owner's player entity follows the owner's session (heals of the owner, declared pairs).</summary>
    internal void SyncOwnerEntity(PetBody body, EntityId owner)
    {
        var t = Native;
        if (t == null || Tag(body).Control is not { } c) return;
        t->SetOwnerEntity(0, PetsNative.Key(c.Key), owner.Value);
    }

    public int? OwnerHealth(PetOwner owner)
    {
        try { return Combat.GetHealth(owner.Entity.Id) is { } h ? h.Current : null; }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { return null; }
    }

    public PetTargetInfo Classify(EntityRef target)
    {
        try
        {
            if (target.IsNone) return default;
            var alive = target.IsAlive;
            if (!alive) return new PetTargetInfo(false, false, false, CharacterKey.None, Faction.None, false);
            if (target.Id.IsPlayer)
                return new PetTargetInfo(true, true, true, CharacterData.KeyOf(target.Id), Faction.Player, false);
            var info = Creatures.Of(target.Id).Inspect();
            if (info is null) return new PetTargetInfo(true, true, false, CharacterKey.None, Faction.None, false);
            return new PetTargetInfo(true, info.Health > 0 && info.State != EnemyState.Dead, false, CharacterKey.None, info.Faction, info.HasAi);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return default;
        }
    }

    public void SyncHostility(IReadOnlyCollection<(CharacterKey A, CharacterKey B)> pairs)
    {
        var t = Native;
        if (t == null) return;
        var online = OnlineOwners().ToDictionary(o => o.Key, o => o.Entity.Id.Value);
        var want = new HashSet<(uint, uint)>();
        foreach (var (a, b) in pairs)
            if (online.TryGetValue(a, out var ea) && online.TryGetValue(b, out var eb) && ea != eb) want.Add(ea < eb ? (ea, eb) : (eb, ea));
        foreach (var gone in _appliedPairs.Where(p => !want.Contains(p)).ToList())
        {
            t->SetHostile(0, gone.Item1, gone.Item2, 0);
            _appliedPairs.Remove(gone);
        }
        foreach (var add in want.Where(p => !_appliedPairs.Contains(p)).ToList())
            if (t->SetHostile(0, add.Item1, add.Item2, 1) == PetsNative.Ok) _appliedPairs.Add(add);
        // every live body's owner entity, as the sessions are now
        foreach (var body in _live.Values)
            if (body.Tag is BodyTag tag && online.TryGetValue(tag.Request.Owner, out var e)) SyncOwnerEntity(body, new EntityId(e));
    }
}
