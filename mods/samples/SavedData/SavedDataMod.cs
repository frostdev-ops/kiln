// SPDX-License-Identifier: MIT
// Saved data: three places a mod keeps what must outlast a restart, and when to use which.
//   WorldData    one value for the whole world            the guestbook: how many joins, the last few names
//   PlayerData   one value per player, by Steam id        the visitor: visits, first and last time seen
//   EntityData   one value on one entity in the world     the label a player leaves on a chest
// All three hold plain classes (public properties, a parameterless constructor), give back the live object, and are
// written with the world's own save: nothing to flush, nothing to mark as changed. They belong to this world: another
// world has its own guestbook. /visits, /guestbook, /label <text>, /labels.
using Kiln;

namespace SavedData;

public sealed class Guestbook
{
    public int Joins { get; set; }
    public List<string> Recent { get; set; } = [];
}

public sealed class Visitor
{
    public string Name { get; set; } = "";
    public int Visits { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class Label
{
    public string Text { get; set; } = "";
    public ulong BySteamId { get; set; }
    public string By { get; set; } = "";
}

public sealed class SavedDataMod : Mod
{
    private const string GuestbookKey = "guestbook";
    private const string VisitorKey = "visitor";
    private const string LabelKey = "label";
    // A permission node of the mod's own: who may replace a label that someone else wrote. Admins by default; a server
    // owner grants it to a group or a player in kiln/config/permissions.json without touching the mod.
    private const string RelabelNode = "saved-data.relabel";
    private const double Reach = 6;  // metres to the chest that gets the label

    public override void OnLoad()
    {
        Permissions.Register(RelabelNode, PermissionLevel.Admin, "replace a label another player left on a chest");
        // On a dedicated server the world's data is readable here already. A game that hosts gets it when the world
        // has loaded: read saved data in handlers and commands, as below, not in fields set up in OnLoad.
        Log.Info($"saved-data: world data loaded as {WorldData.Loaded}; entity data {(EntityData.Available ? "available" : "not available (kiln.spawn is missing)")}");
    }

    [Subscribe]
    private void OnJoined(PlayerJoinedEvent e)
    {
        var now = DateTime.UtcNow;
        // Get creates the object on first use and returns the same live object afterwards: change it in place.
        var book = WorldData.Get<Guestbook>(GuestbookKey);
        book.Joins++;
        book.Recent.Remove(e.Player.Name);
        book.Recent.Insert(0, e.Player.Name);
        if (book.Recent.Count > 5) book.Recent.RemoveRange(5, book.Recent.Count - 5);

        // By Steam id, not by entity: the entity number is a connection slot and the next player gets it.
        if (e.Player.SteamId == 0) return;  // not known yet (a test connection): nothing to key the visit by
        var visitor = PlayerData.Get<Visitor>(e.Player.SteamId, VisitorKey);
        if (visitor.Visits == 0) visitor.FirstSeenUtc = now;
        visitor.Visits++;
        visitor.Name = e.Player.Name;
        var away = visitor.Visits > 1 ? now - visitor.LastSeenUtc : TimeSpan.Zero;
        visitor.LastSeenUtc = now;
        Chat.Tell(e.Player.Entity, visitor.Visits == 1
            ? $"Welcome, {e.Player.Name}. You are visitor number {book.Joins} of this world."
            : $"Welcome back, {e.Player.Name}: visit {visitor.Visits}, last seen {Ago(away)} ago.");
    }

    [Command("visits", Description = "how often you were here; an admin may name another player, online or not")]
    private string Visits(CommandContext c, string? player = null)
    {
        if (player is null)
        {
            if (!c.Source.IsPlayer || c.Source.SteamId == 0) throw new CommandException("name a player");
            // TryGet does not create anything: a player who was never counted has no entry.
            return PlayerData.TryGet<Visitor>(c.Source.SteamId, VisitorKey, out var own) ? Describe(own) : "no visit counted yet";
        }
        if (!c.Source.IsAdmin) throw new CommandException("only an admin may look up another player");
        // The player does not have to be online: PlayerData.Players lists everyone this mod has data for.
        foreach (var steamId in PlayerData.Players)
            if (PlayerData.TryGet<Visitor>(steamId, VisitorKey, out var v) && v.Name.Equals(player, StringComparison.OrdinalIgnoreCase))
                return Describe(v);
        return $"no visitor named {player}";
    }

    [Command("guestbook", Description = "the world's guestbook")]
    private string ShowGuestbook(CommandContext c)
    {
        if (!WorldData.TryGet<Guestbook>(GuestbookKey, out var book)) return "the guestbook is empty";
        return $"{book.Joins} join(s) in this world ({WorldData.WorldId}); last: {string.Join(", ", book.Recent)}";
    }

    [Command("label", Description = "leave a label on the chest next to you")]
    private string SetLabel(CommandContext c, [Greedy] string text)
    {
        if (!c.Source.IsPlayer) throw new CommandException("stand next to a chest");
        if (!EntityData.Available) throw new CommandException("labels need kiln.spawn, which is not available on this server");
        // The chests and other containers within reach, nearest first.
        var near = Nearby.Within(Items.FindInventories(), c.Source.Player, Reach);
        if (near.Count == 0) throw new CommandException($"no chest within {Reach:0} m");
        var chest = near[0];
        if (EntityData.TryGet<Label>(chest, LabelKey, out var old) && old.BySteamId != c.Source.SteamId && !c.Source.Has(RelabelNode))
            throw new CommandException($"{old.By} labelled this chest \"{old.Text}\"; replacing it needs {RelabelNode}");
        EntityData.Set(chest, LabelKey, new Label { Text = text, BySteamId = c.Source.SteamId, By = c.Source.Name });
        // A chest is saved by the game, so its label comes back after a restart. An entity the game never saves
        // (most enemies) keeps its data only until the server stops: IsPersistent tells which.
        return EntityData.IsPersistent(chest) ? $"labelled: \"{text}\"" : $"labelled: \"{text}\" (until the server stops: the game does not save this container)";
    }

    [Command("labels", Description = "every labelled chest", Permission = CommandPermission.Admin)]
    private void Labels(CommandContext c)
    {
        if (!EntityData.Available) throw new CommandException("labels need kiln.spawn, which is not available on this server");
        var n = 0;
        // Entities this mod has data on. Data of a chest that was destroyed goes with it.
        foreach (var entity in EntityData.Entities)
        {
            if (!EntityData.TryGet<Label>(entity, LabelKey, out var label)) continue;
            c.Reply($"  \"{label.Text}\" by {label.By} at {World.Position(entity)?.ToString() ?? "?"}");
            n++;
        }
        c.Reply($"{n} label(s)");
    }

    private static string Describe(Visitor v) =>
        $"{v.Name}: {v.Visits} visit(s), first {v.FirstSeenUtc:yyyy-MM-dd}, last {v.LastSeenUtc:yyyy-MM-dd HH:mm} UTC";

    private static string Ago(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} day(s)" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} hour(s)" : $"{Math.Max(1, (int)t.TotalMinutes)} minute(s)";
}
