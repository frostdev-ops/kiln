// SPDX-License-Identifier: MIT
// The lab script's commands (admin; tools/labbot runs them as the bot: bot.command("e2e ...")). Each answers at once with
// one JSON line; work that takes steps writes its outcome to the evidence (e2e.jsonl) under the tag the script gave.
using System.Globalization;
using System.Text.Json.Nodes;
using Kiln;

namespace PetsE2eLab;

public sealed partial class PetsE2eLabMod
{

    [Command("e2e hello", Description = "is the e2e lab mod there", Permission = CommandPermission.Admin)]
    private void Hello(CommandContext c) => c.Reply(new JsonObject
    {
        ["mod"] = Me, ["pets"] = Capabilities.Is("kiln.pets"), ["records"] = Capabilities.Is("pets.records"), ["summon"] = Capabilities.Is("pets.summon"),
        ["follow"] = Capabilities.Is("pets.follow"), ["fight"] = Capabilities.Is("pets.fight"), ["downed"] = Capabilities.Is("pets.downed"),
        ["hud"] = Capabilities.Is("pets.hud.server"), ["why"] = $"summon: {Capabilities.Why("pets.summon")}; downed: {Capabilities.Why("pets.downed")}; hud: {Capabilities.Why("pets.hud.server")}",
        ["mule"] = _muleDefined, ["step"] = _step, ["game"] = R(_game),
    }.ToJsonString());

    [Command("e2e trace", Description = "every owner's and pet's position each step: on or off", Permission = CommandPermission.Admin)]
    private void TraceSwitch(CommandContext c, string on)
    {
        _trace = on == "on";
        c.Reply($"{{\"trace\": {(_trace ? "true" : "false")}}}");
    }

    [Command("e2e mark", Description = "a marker line in the evidence", Permission = CommandPermission.Admin)]
    private void Mark(CommandContext c, [Greedy] string text)
    {
        Write("mark", new JsonObject { ["text"] = text });
        c.Reply($"{{\"step\": {_step}}}");
    }

    [Command("e2e state", Description = "the caller's pets as JSON", Permission = CommandPermission.Admin)]
    private void State(CommandContext c)
    {
        var (who, key) = Caller(c);
        var o = new JsonObject
        {
            ["owner"] = key.ToString(), ["entity"] = who.Value, ["at"] = World.Position(who) is { } at ? Pos(at) : null,
            ["pets"] = new JsonArray(Pets.Of(key).Select(p => (JsonNode)Describe(p, who)).ToArray()), ["step"] = _step, ["game"] = R(_game),
            ["petsNow"] = PetsNow() is { } n ? R(n) : null,
        };
        // the whole state goes to the evidence; the reply names it (a long reply is cut into chat lines)
        o["seq"] = ++_stateSeq;
        Write("state", o);
        c.Reply($"{{\"stateSeq\": {_stateSeq}}}");
    }

    private long _stateSeq;

    [Command("e2e god", Description = "the caller invulnerable (on) or not (off)", Permission = CommandPermission.Admin)]
    private void God(CommandContext c, string on)
    {
        var (who, _) = Caller(c);
        Combat.SetInvulnerable(who, on == "on");
        c.Reply($"{{\"god\": {(on == "on" ? "true" : "false")}}}");
    }

    [Command("e2e enemy", Description = "an unowned early-game wolf at the caller + (dx, dz); mode: god (invulnerable), inert (AI off), both or mortal", Permission = CommandPermission.Admin)]
    private void Enemy(CommandContext c, string tag, string dx, string dz, string mode = "god")
    {
        var (who, _) = Caller(c);
        var at = World.Position(who) ?? throw new CommandException("the caller has no position");
        var t = Creatures.Templates.FirstOrDefault(x => string.Equals(x.Name, "enemy_wildbeast_wolf_earlygame", StringComparison.OrdinalIgnoreCase))
            ?? throw new CommandException("no template enemy_wildbeast_wolf_earlygame");
        var to = new WorldPosition(at.X + Num(dx), at.Y + 0.5, at.Z + Num(dz));
        var r = Spawn.TryEntity(t.Id, to, 0, new SpawnOptions { Tag = Me, Level = 1, Flags = SpawnFlags.Transient });
        if (!r.Ok) throw new CommandException($"spawn refused: {r.Reason}");
        var e = r.Entity.Id;
        _spawned.Add(e);
        // a new creature has its health (and enemy AI state) a step or more after its spawn: the setup waits for them
        Later("enemy", async () =>
        {
            var ready = await Until(() => Combat.TryGetHealth(e, out _), 5);
            if (mode.Contains("god", StringComparison.Ordinal) && ready) Combat.SetInvulnerable(e, true);
            // inert: its AI off, so it neither fights back nor walks off (it keeps its enemy AI state: still a target)
            if (mode.Contains("inert", StringComparison.Ordinal))
                try { Creatures.Of(e).AiEnabled = false; } catch (Exception x) { Info($"enemy {tag}: AI off: {x.Message}"); }
            string? faction = null;
            try { faction = Creatures.Of(e).Faction.ToString(); } catch (Exception x) { faction = $"error: {x.Message}"; }
            Write("enemy", new JsonObject
            {
                ["tag"] = tag, ["entity"] = e.Value, ["at"] = Pos(to), ["faction"] = faction, ["mode"] = mode, ["template"] = t.Name, ["ready"] = ready,
                ["health"] = Combat.TryGetHealth(e, out var h) ? h.ToString() : null,
            });
        });
        c.Reply(new JsonObject { ["tag"] = tag, ["entity"] = e.Value, ["at"] = Pos(to), ["mode"] = mode }.ToJsonString());
    }

    [Command("e2e attack", Description = "pet attacks entity (Pets.Command Attack); its AI target is recorded for 10 s", Permission = CommandPermission.Admin)]
    private void Attack(CommandContext c, string pet, string entity)
    {
        var (who, key) = Caller(c);
        var p = PetOf(key, pet);
        var target = Entity(entity);
        var req = PetRequest.New();
        Later("attack", async () =>
        {
            var r = await Pets.Command(PlayerRef.Of(who), p.Id, PetOrder.Attack, EntityRef.Of(target), req);
            var o = Result(r);
            o["pet"] = Short(p.Id);
            o["target"] = target.Value;
            Write("attack", o);
            if (r.Ok) _attackWatch = (p.Id, target, _game + 10);
        });
        c.Reply($"{{\"op\": \"{req.OperationId}\"}}");
    }

    [Command("e2e lethal", Description = "a lethal engine hit on the pet's body from a creature", Permission = CommandPermission.Admin)]
    private void Lethal(CommandContext c, string pet, string attacker)
    {
        var (_, key) = Caller(c);
        var p = PetOf(key, pet);
        if (p.Body.IsNone) throw new CommandException("the pet has no body");
        var from = Entity(attacker);
        var body = p.Body.Id;
        Combat.TryGetHealth(body, out var before);
        var task = Combat.Damage(body, 1_000_000, from);
        Write("lethal", new JsonObject { ["pet"] = Short(p.Id), ["body"] = body.Value, ["attacker"] = from.Value, ["healthBefore"] = before.ToString() });
        Later("lethal", async () =>
        {
            var h = await task;
            Write("hit", new JsonObject
            {
                ["pet"] = Short(p.Id), ["body"] = body.Value, ["state"] = h.State.ToString(), ["before"] = h.HealthBefore, ["after"] = h.HealthAfter,
                ["killingBlow"] = h.KillingBlow, ["steps"] = h.Steps, ["bodyExists"] = World.Exists(body),
            });
        });
        c.Reply($"{{\"body\": {body.Value}}}");
    }

    [Command("e2e kill", Description = "kills a creature with the caller as the attacker (the XP and loot control)", Permission = CommandPermission.Admin)]
    private void Kill(CommandContext c, string entity)
    {
        var (who, _) = Caller(c);
        var e = Entity(entity);
        Combat.SetInvulnerable(e, false);
        var task = Combat.Kill(e, who);
        Later("kill", async () =>
        {
            var h = await task;
            Write("kill", new JsonObject { ["entity"] = e.Value, ["state"] = h.State.ToString(), ["killingBlow"] = h.KillingBlow, ["after"] = h.HealthAfter });
        });
        c.Reply($"{{\"entity\": {e.Value}}}");
    }

    [Command("e2e xp", Description = "the caller's level and experience", Permission = CommandPermission.Admin)]
    private void Xp(CommandContext c)
    {
        var (who, _) = Caller(c);
        var s = Progression.Get(who);
        var o = new JsonObject
        {
            ["level"] = s?.Level, ["xp"] = s?.Experience, ["pending"] = s?.ExperiencePending, ["needed"] = s?.ExperienceNeeded, ["step"] = _step,
        };
        Write("xp", o.DeepClone().AsObject());
        c.Reply(o.ToJsonString());
    }

    [Command("e2e give", Description = "items to the caller", Permission = CommandPermission.Admin)]
    private void Give(CommandContext c, string item, int count)
    {
        var (who, _) = Caller(c);
        var info = Item(item);
        var task = Items.Give(who, info.Id, count);
        Later("give", async () =>
        {
            var r = await task;
            Write("give", new JsonObject { ["item"] = item, ["count"] = count, ["status"] = r.Status.ToString(), ["done"] = r.Done });
        });
        c.Reply($"{{\"item\": {info.Id.Value}}}");
    }

    [Command("e2e count", Description = "how many of an item the caller holds", Permission = CommandPermission.Admin)]
    private void Count(CommandContext c, string item)
    {
        var (who, _) = Caller(c);
        var info = Item(item);
        var n = Items.Read(who)?.Count(info.Id);
        Write("count", new JsonObject { ["item"] = item, ["count"] = n });
        c.Reply($"{{\"item\": \"{item}\", \"count\": {(n is { } v ? v.ToString(CultureInfo.InvariantCulture) : "null")}}}");
    }

    [Command("e2e revive", Description = "Pets.Revive as the caller; the answer and its receipts go to the evidence", Permission = CommandPermission.Admin)]
    private void Revive(CommandContext c, string pet, string tag)
    {
        var (who, key) = Caller(c);
        var p = PetOf(key, pet);
        var req = PetRequest.New();
        var t0 = _wall.Elapsed.TotalSeconds;
        var g0 = _game;
        Later("revive", async () =>
        {
            var r = await Pets.Revive(PlayerRef.Of(who), p.Id, req);
            var o = Result(r);
            o["tag"] = tag;
            o["pet"] = Short(p.Id);
            o["wallSeconds"] = R(_wall.Elapsed.TotalSeconds - t0);
            o["gameSeconds"] = R(_game - g0);
            Write("revive", o);
        });
        c.Reply($"{{\"op\": \"{req.OperationId}\"}}");
    }

    [Command("e2e cancel", Description = "Pets.CancelRevive of a revive operation", Permission = CommandPermission.Admin)]
    private void CancelRevive(CommandContext c, string pet, string op, string tag)
    {
        var (who, key) = Caller(c);
        var p = PetOf(key, pet);
        var id = Guid.Parse(op);
        Later("cancel", async () =>
        {
            var r = await Pets.CancelRevive(PlayerRef.Of(who), p.Id, id, PetRequest.New());
            var o = Result(r);
            o["tag"] = tag;
            Write("cancel", o);
        });
        c.Reply("{}");
    }

    [Command("e2e save", Description = "a world save (Saves.Request)", Permission = CommandPermission.Admin)]
    private void Save(CommandContext c, string tag)
    {
        var task = Saves.Request();
        Write("saverequest", new JsonObject { ["tag"] = tag });
        Later("save", async () =>
        {
            var r = await task;
            Write("save", new JsonObject { ["tag"] = tag, ["success"] = r.Success, ["revision"] = r.Revision, ["error"] = r.Error });
        });
        c.Reply("{}");
    }

    [Command("e2e downsave", Description = "in one step: a lethal hit on the pet's body and a world save request", Permission = CommandPermission.Admin)]
    private void DownSave(CommandContext c, string pet, string attacker, string tag)
    {
        var (_, key) = Caller(c);
        var p = PetOf(key, pet);
        if (p.Body.IsNone) throw new CommandException("the pet has no body");
        var from = Entity(attacker);
        _nextPre.Enqueue(() =>
        {
            var body = p.Body.Id;
            var hit = Combat.Damage(body, 1_000_000, from);
            var save = Saves.Request();
            var now = Pets.Get(p.Id);
            Write("downsave", new JsonObject
            {
                ["tag"] = tag, ["pet"] = Short(p.Id), ["body"] = body.Value, ["life"] = now?.Life.ToString(), ["presence"] = now?.Presence.ToString(),
                ["rev"] = now?.StateRevision,
            });
            Later("downsave", async () =>
            {
                var h = await hit;
                Write("hit", new JsonObject { ["tag"] = tag, ["pet"] = Short(p.Id), ["state"] = h.State.ToString(), ["after"] = h.HealthAfter, ["steps"] = h.Steps });
                var r = await save;
                Write("save", new JsonObject { ["tag"] = tag, ["success"] = r.Success, ["revision"] = r.Revision, ["error"] = r.Error });
            });
        });
        c.Reply("{}");
    }

    [Command("e2e destroy", Description = "removes an entity this mod spawned", Permission = CommandPermission.Admin)]
    private void Destroy(CommandContext c, string entity)
    {
        var e = Entity(entity);
        if (!_spawned.Contains(e)) throw new CommandException("not one of this mod's");
        Spawn.Destroy(e);
        _spawned.Remove(e);
        c.Reply("{}");
    }
}
