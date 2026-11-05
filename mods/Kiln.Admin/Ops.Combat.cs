// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal static partial class Ops
{
    // Deaths with killer from kiln.combat (replaces the health-polling grave markers when available).
    private static readonly List<JsonObject> CombatDeaths = [];
    private static EventSubscription? _deathSub;

    static partial void RegisterCombat(Bridge bridge, Logger log)
    {
        if (!Combat.Available) return;
        // {entity} -> {entity, health, healthMax, dead, god}
        bridge.Register("health", req =>
        {
            var e = new EntityId(Entity(req));
            var h = Combat.GetHealth(e) ?? throw new AdminError("That entity has no health.");
            return new JsonObject { ["entity"] = e.Value, ["health"] = h.Current, ["healthMax"] = h.Max, ["dead"] = h.Dead, ["god"] = h.Invulnerable };
        });
        // {entity, amount?} -> heal a player to full (or by amount)
        bridge.Register("heal", req =>
        {
            var p = RequirePlayer(req);
            var h = Combat.GetHealth(p.Entity) ?? throw new AdminError("That player has no health.");
            var amount = req["amount"] is JsonValue v && v.TryGetValue<double>(out var a) ? (int)Math.Clamp(a, 0, int.MaxValue) : h.Max;
            var healed = Combat.Heal(p.Entity, amount);
            return new JsonObject { ["entity"] = p.Entity.Value, ["healed"] = healed, ["health"] = Combat.GetHealth(p.Entity)?.Current };
        });
        // {entity, on} -> god mode for a player (held by Kiln.Admin; ends when Kiln.Admin unloads)
        bridge.Register("god", req =>
        {
            var p = RequirePlayer(req);
            var on = req["on"]?.GetValue<bool>() ?? !Combat.IsInvulnerable(p.Entity);
            Combat.SetInvulnerable(p.Entity, on);
            return new JsonObject { ["entity"] = p.Entity.Value, ["god"] = Combat.IsInvulnerable(p.Entity) };
        });
        // {entity} -> kill a player or an enemy (a killing blow, no credit)
        bridge.RegisterAsync("kill", async req =>
        {
            var e = new EntityId(Entity(req));
            if (Combat.GetHealth(e) is null) throw new AdminError("That entity has no health.");
            var r = await Combat.Kill(e);
            return new JsonObject { ["entity"] = e.Value, ["killed"] = r.KillingBlow, ["state"] = r.State.ToString() };
        });
        // {entity, amount, attacker?} -> damage as a hit
        bridge.RegisterAsync("damage", async req =>
        {
            var e = new EntityId(Entity(req));
            var amount = (int)Math.Clamp(Number(req, "amount"), 1, 1_000_000);
            var attacker = req["attacker"] is JsonValue ? new EntityId(Entity(req, "attacker")) : EntityId.None;
            var r = await Combat.Damage(e, amount, attacker, new DamageOptions { Reaction = HitReaction.Light });
            return new JsonObject { ["entity"] = e.Value, ["state"] = r.State.ToString(), ["health"] = r.HealthAfter, ["killed"] = r.KillingBlow };
        });
        _deathSub ??= Events.On<EntityDeathEvent>(d =>
        {
            if (!d.IsPlayer) return;
            var o = new JsonObject
            {
                ["entity"] = d.Victim.Value, ["killer"] = d.Killer.Value, ["origin"] = d.Origin.ToString(), ["pos"] = Pos(d.Position),
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            // Names now: the killer may be gone (despawned, logged out) by the time the dashboard reads this.
            if (Players.Available && Players.Find(d.Victim)?.Name is { Length: > 0 } victim) o["victimName"] = victim;
            if (d.Killer.IsNone) o["killerKind"] = "none";
            else if (Players.Available && Players.Find(d.Killer) is { } kp)
            {
                o["killerKind"] = "player";
                o["killerName"] = kp.Name;
            }
            else
            {
                var t = new JsonObject();
                TemplateOf(d.Killer, t);
                o["killerKind"] = "creature";
                if (t["name"] is JsonNode n) o["killerName"] = n.DeepClone();
                if (t["guid"] is JsonNode g) o["killerGuid"] = g.DeepClone();
            }
            CombatDeaths.Add(o);
            if (CombatDeaths.Count > 50) CombatDeaths.RemoveAt(0);
        });
    }

    static partial void CombatOf(EntityId entity, JsonObject player)
    {
        if (Combat.Available) player["god"] = Combat.IsInvulnerable(entity);
    }

    static partial void StateCombat(JsonObject state)
    {
        if (CombatDeaths.Count == 0) return;
        var arr = new JsonArray();
        foreach (var d in CombatDeaths) arr.Add(d.DeepClone());
        state["combatDeaths"] = arr;
    }
}
