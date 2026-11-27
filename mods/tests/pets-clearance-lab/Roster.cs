// SPDX-License-Identifier: MIT
// The one step that needs an owner: a real pet (Pets.Define, Grant, Summon for a connected player) of the plan's first
// creature, its body's trace beside its owner for rosterSeconds, then Dismiss and Release. The owner is whatever player is
// connected: LABBOT's signed-in bot (dev32/labbot) once it exists; never a game client. The player is put at the control
// lane's start; moving it (through a fixture, around a corner) is the bot driver's part. Without a player it is skipped.
// The pet uses the profile the session measured when tools/pets-clearance put one in kiln/data/kiln.pets/clearance.json
// (else it only catches up, which the trace shows as well).
using System.Diagnostics;
using System.Text.Json.Nodes;
using Kiln;

namespace PetsClearanceLab;

public sealed partial class PetsClearanceLabMod
{
    private async Task Roster(Site site)
    {
        var players = Players.All.Where(p => !p.Entity.IsNone).ToList();
        if (players.Count == 0 || _plan.Creatures.Count == 0)
        {
            Info("roster: skipped: no player is connected (the owner step waits for LABBOT's bot)");
            Evidence.Write("roster", new JsonObject { ["skipped"] = "no player is connected (LABBOT's bot)" });
            return;
        }
        var player = players[0];
        var key = CharacterData.KeyOf(player.Entity);
        var who = PlayerRef.Of(player.Entity);
        var c = _plan.Creatures[0];
        var o = new JsonObject { ["creature"] = c.Id, ["scale"] = c.Scale, ["player"] = player.Entity.Value };
        if (key.IsNone || key.IsAccountOnly)
        {
            o["skipped"] = "the player's character is not known";
            Evidence.Write("roster", o);
            return;
        }
        var start = site.Control ?? site.Lanes[0];
        try { await Teleport.To(player.Entity, start.Start, new TeleportOptions { FindGround = false }); }
        catch (Exception e) { Info($"roster: teleporting the player: {e.Message}"); }
        await Wait(1);
        var def = new PetDefinition
        {
            Id = $"{Me}:roster", Creature = c.Id, Level = new PetLevelPolicy { Scale = c.Scale },
            Movement = new PetMovementPolicy { FollowDistance = 2 },
        };
        var d = Pets.Define(def);
        if (!d.Ok) { o["refused"] = d.Reason; Evidence.Write("roster", o); return; }
        var g = await Pets.Grant(key, def.Id, PetRequest.New());
        if (!g.Ok || g.Pet is not { } snap) { o["refused"] = g.Reason; Evidence.Write("roster", o); return; }
        var s = await Pets.Summon(who, snap.Id, PetRequest.New());
        o["summon"] = s.Ok ? "ok" : s.Reason;
        var trace = new JsonArray();
        var wall = Stopwatch.StartNew();
        var t0 = Now;
        while (s.Ok && Now - t0 < _plan.RosterSeconds && wall.Elapsed.TotalSeconds < _plan.RosterSeconds * 4 + 10)
        {
            await Wait(0.25);
            var pet = Pets.Get(snap.Id);
            var pp = pet?.Body is { IsNone: false } b ? Pos(b.Id) : default;
            var op = player.Entity is var pe && World.Position(pe) is { } at ? at : default;
            trace.Add(new JsonArray(Out.R(Now - t0), Out.R(op.X), Out.R(op.Y), Out.R(op.Z), Out.R(pp.X), Out.R(pp.Y), Out.R(pp.Z), pet?.Presence.ToString()));
        }
        o["trace"] = trace;
        try { await Pets.Dismiss(who, snap.Id, PetRequest.New()); } catch (Exception e) { Info($"roster: dismiss: {e.Message}"); }
        try { await Pets.Release(who, snap.Id, PetRequest.New()); } catch (Exception e) { Info($"roster: release: {e.Message}"); }
        Evidence.Write("roster", o);
        Info($"roster: {c.Id} followed player {player.Entity} for {Now - t0:F0} s of game time (summon {o["summon"]})");
    }
}
