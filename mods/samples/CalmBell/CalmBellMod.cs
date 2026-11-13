// SPDX-License-Identifier: MIT
// The calm bell: an admin puts down a barrel with /bell. A player who uses it starts a calm: enemies hit half as hard
// for ten minutes, then the server's own value is back.
//   Offers    the "E" prompt on something the mod spawned, and a handler when a player accepts it
//   Settings  the game's own server settings (the ones in enshrouded_server.json), read, changed and put back
// What to take from it: check that a service can do it before relying on it (Offers.Available, Settings.Ready), use the
// call that returns a reason (TrySet) where a refusal is normal, and put back exactly what you changed, also when the
// mod unloads.
using Kiln;

namespace CalmBell;

public sealed class CalmBellMod : Mod
{
    private const string Barrel = "Prop_Deco_Steppeprops_Barrel_01";
    private const string Setting = "enemyDamageFactor";
    private static readonly TimeSpan CalmFor = TimeSpan.FromMinutes(10);

    private object? _before;        // the setting's value before the calm; null: no calm is running
    private DateTime _calmUntil;

    public override void OnLoad()
    {
        // Once a step: is the calm over?
        Context.Tick.Pre += (in TickInfo _) =>
        {
            if (_before is not null && DateTime.UtcNow >= _calmUntil) EndCalm("The calm is over.");
        };
    }

    public override void OnUnload() => EndCalm(null);  // never leave the server on a value this mod set

    [Command("bell", Description = "puts down a calm bell (a barrel) where you stand", Permission = CommandPermission.Admin)]
    private async Task Bell(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("stand where the bell should be");
        if (!Offers.Available) throw new CommandException("offers are not available on this server (kiln.gameplay or kiln.world is missing)");
        var at = Players.PositionOf(c.Source.Player) ?? throw new CommandException("your position is unknown");
        // A game word for the prompt (OfferVerbs): every player's game knows those. A text of your own
        // ("Ring the bell") shows only in a game that knows its id, so a sample for every client passes none.
        var bell = await Offers.SpawnWithOffer(Barrel, new WorldPosition(at.X + 1.5, at.Y, at.Z), null, OfferVerbs.Use, OnRung,
            new OfferOptions { RangeMeters = 4 });
        c.Reply($"A calm bell stands next to you ({bell}). The prompt appears a step or two after it was put down.");
    }

    [Command("bells", Description = "the offers on this server, every mod's", Permission = CommandPermission.Admin)]
    private void Bells(CommandContext c)
    {
        foreach (var (entity, owner, verb, accepts) in Offers.List())
            c.Reply($"  {entity} by {owner}: used {accepts} time(s), {Offers.Status(entity)}");
    }

    // Runs on the tick thread, as this mod, when a player accepts the offer.
    private void OnRung(OfferAccepted a)
    {
        if (_before is not null)
        {
            Chat.Tell(a.Player, $"The calm is already running ({(int)Math.Ceiling((_calmUntil - DateTime.UtcNow).TotalMinutes)} minute(s) left).");
            return;
        }
        // The settings are readable once the server has loaded its world.
        if (!Settings.Ready || !Settings.Has(Setting))
        {
            Chat.Tell(a.Player, "The bell is silent: this server's settings cannot be changed right now.");
            return;
        }
        var info = Settings.Info(Setting);
        var now = Convert.ToDouble(info.Value);
        var calm = Math.Max(info.Min, now / 2);  // never below what the game accepts
        // TrySet says why not instead of throwing: a refusal (out of range, read only on this build) is a normal answer.
        if (!Settings.TrySet(Setting, calm, out var change, out var error) || change is null)
        {
            Chat.Tell(a.Player, $"The bell is silent: {error}");
            return;
        }
        _before = change.Before;
        _calmUntil = DateTime.UtcNow + CalmFor;
        Chat.Broadcast($"{Players.Find(a.Player)?.Name ?? "Someone"} rang the calm bell: enemies hit half as hard for {CalmFor.TotalMinutes:0} minutes.");
        Log.Info($"calm-bell: {Setting} {change.Before} -> {change.After} until {_calmUntil:HH:mm} UTC{(info.Live ? "" : " (this setting is not live: the game applies it later)")}");
    }

    private void EndCalm(string? say)
    {
        if (_before is not { } before) return;
        _before = null;
        // Put back the value that was there, not a default: the server owner may have set their own.
        var error = "the settings are not ready";
        if (Settings.Ready && Settings.TrySet(Setting, before, out _, out error))
        {
            if (say is not null) Chat.Broadcast(say);
        }
        else
        {
            Log.Warn($"calm-bell: {Setting} could not be put back to {before}: {error}");
        }
    }
}
