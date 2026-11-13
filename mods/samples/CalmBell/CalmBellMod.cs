// SPDX-License-Identifier: MIT
// The calm bell: an admin puts down a barrel with /bell. A player who uses it starts a calm: enemies hit half as hard
// for ten minutes, then the server's own value is back.
//   Offers    the "E" prompt on something the mod spawned, and a handler when a player accepts it
//   Settings  the game's own server settings (the ones in enshrouded_server.json), read, changed and put back
// What to take from it: check that a service can do it before relying on it (Offers.Available, Settings.Ready), use the
// call that returns a reason (TrySet) where a refusal is normal, and when you change something for a while, put back
// exactly what you changed, only if it is still yours to put back, and keep trying until it worked.
using Kiln;

namespace CalmBell;

public sealed class CalmBellMod : Mod
{
    private const string Barrel = "Prop_Deco_Steppeprops_Barrel_01";
    private const string Setting = "enemyDamageFactor";
    private static readonly TimeSpan CalmFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    // A calm that is running: what the setting was, what this mod made it, and when it ends. Both values are kept:
    // the first to put back, the second to see whether the setting is still the mod's own.
    private object? _before;
    private object? _mine;
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
        // Each service the command needs, asked for by name: the answer for a server without one is a sentence.
        if (!Offers.Available) throw new CommandException("offers are not available on this server (kiln.gameplay or kiln.world is missing)");
        if (!Spawn.Available) throw new CommandException("nothing can be put down on this server (kiln.spawn is missing)");
        if (!Players.Available) throw new CommandException("your position cannot be read on this server (kiln.players is missing)");
        WorldPosition at;
        try
        {
            at = Players.PositionOf(c.Source.Player) ?? throw new CommandException("your position is unknown");
        }
        catch (NotSupportedException)
        {
            // kiln.players is there, but this game build's positions are not known to it
            throw new CommandException("your position cannot be read on this game build");
        }
        // A game word for the prompt (OfferVerbs): every player's game knows those. A text of your own
        // ("Ring the bell") shows only in a game that knows its id, so a sample for every client passes none.
        var bell = await Offers.SpawnWithOffer(Barrel, new WorldPosition(at.X + 1.5, at.Y, at.Z), null, OfferVerbs.Use, OnRung,
            new OfferOptions { RangeMeters = 4 });
        c.Reply($"A calm bell stands next to you ({bell}). The prompt appears a step or two after it was put down.");
    }

    [Command("bells", Description = "the offers on this server, every mod's", Permission = CommandPermission.Admin)]
    private void Bells(CommandContext c)
    {
        if (!Offers.Available) throw new CommandException("offers are not available on this server");
        foreach (var (entity, owner, verb, accepts) in Offers.List())
        {
            // The list is every mod's; Offers.Status answers for this mod's own offers only.
            var status = owner == Context.Info.Id ? $", {Offers.Status(entity)}" : "";
            c.Reply($"  {entity} by {owner}: used {accepts} time(s){status}");
        }
    }

    // What the bell says goes through chat when the server has it, and into the log either way.
    private void Tell(EntityId player, string text)
    {
        Log.Info($"calm-bell: {text}");
        if (Chat.Available) Chat.Tell(player, text);
    }

    private void Announce(string text)
    {
        Log.Info($"calm-bell: {text}");
        if (Chat.Available) Chat.Broadcast(text);
    }

    // Runs on the tick thread, as this mod, when a player accepts the offer.
    private void OnRung(OfferAccepted a)
    {
        if (_before is not null)
        {
            Tell(a.Player, $"The calm is already running ({(int)Math.Ceiling((_calmUntil - DateTime.UtcNow).TotalMinutes)} minute(s) left).");
            return;
        }
        // The settings are readable once the server has loaded its world.
        if (!Settings.Ready || !Settings.Has(Setting))
        {
            Tell(a.Player, "The bell is silent: this server's settings cannot be changed right now.");
            return;
        }
        var info = Settings.Info(Setting);
        if (info.Kind != SettingKind.Float)
        {
            Tell(a.Player, $"The bell is silent: {Setting} is not a factor on this game build.");
            return;
        }
        var now = Convert.ToDouble(info.Value);
        var calm = Math.Max(info.Min, now / 2);  // never below what the game accepts
        if (calm >= now)
        {
            // Say what is true: at the game's lowest factor there is nothing to halve.
            Tell(a.Player, $"The bell rings, but enemies already hit as softly as the game allows (factor {now:0.##}).");
            return;
        }
        // TrySet says why not instead of throwing: a refusal (out of range, read only on this build) is a normal answer.
        if (!Settings.TrySet(Setting, calm, out var change, out var error) || change is null)
        {
            Tell(a.Player, $"The bell is silent: {error}");
            return;
        }
        _before = change.Before;
        _mine = change.After;
        _calmUntil = DateTime.UtcNow + CalmFor;
        Announce($"{(Players.Available ? Players.Find(a.Player)?.Name : null) ?? "Someone"} rang the calm bell: enemies hit with factor {change.After:0.##} instead of {change.Before:0.##} for {CalmFor.TotalMinutes:0} minutes.");
        if (!info.Live) Log.Info($"calm-bell: {Setting} is not a live setting on this game build: the game applies the change later");
    }

    // The calm ends: by its time, or because the mod unloads (`say` null). The value from before goes back only while
    // the setting still holds what this mod set. If an admin or another mod changed it meanwhile, theirs stands.
    private void EndCalm(string? say)
    {
        if (_before is not { } before) return;
        if (!Settings.Ready)
        {
            // Cannot be put back now (the world is closing or not loaded): keep the calm's record and try again.
            _calmUntil = DateTime.UtcNow + RetryAfter;
            if (say is null) Log.Warn($"calm-bell: unloading while {Setting} could not be put back to {before} (the settings are not ready)");
            return;
        }
        if (!Equals(Settings.Get(Setting), _mine))
        {
            Log.Info($"calm-bell: {Setting} was changed by someone else during the calm: it is left as it is");
            _before = _mine = null;
            if (say is not null) Announce(say);
            return;
        }
        // anyValue: the value from before is put back even when it lies outside the documented range (another mod or
        // the server's own file may have set it so). The record is cleared only when that worked.
        if (Settings.TrySet(Setting, before, out _, out var error, anyValue: true))
        {
            _before = _mine = null;
            if (say is not null) Announce(say);
            return;
        }
        Log.Warn($"calm-bell: {Setting} could not be put back to {before} ({error}); trying again in {RetryAfter.TotalSeconds:0} s");
        _calmUntil = DateTime.UtcNow + RetryAfter;
    }
}
