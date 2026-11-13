using Kiln;

namespace KilnSavedData1;

/// <summary>What is kept for the world: any class with public properties and a parameterless constructor.</summary>
public sealed class WorldNotes
{
    public int Joins { get; set; }
    public string? Motto { get; set; }
}

/// <summary>What is kept for one player.</summary>
public sealed class PlayerNotes
{
    public int Visits { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class KilnSavedData1Mod : Mod
{
    // Keys are this mod's own: another mod's "notes" is another value.
    private const string Key = "notes";
    // The capability: Kiln.Saves' service is here (the /caps command lists it).
    private const string SavedData = "kiln.saves";

    [Subscribe]
    private void OnJoined(PlayerJoinedEvent e)
    {
        // A server can run without kiln.saves: ask before every use, and do nothing (or say so) when it is not there.
        if (!Capabilities.Is(SavedData)) return;
        // Get returns the live object, made on first use. Change it in place: Kiln writes it with the world's own save
        // (nothing to flush or mark), into Kiln's folder beside the world, and a restart gives back what belongs to
        // the save on disk.
        WorldData.Get<WorldNotes>(Key).Joins++;

        // Per player by Steam id: the entity number is a connection slot, and the next player gets it.
        if (e.Player.SteamId == 0) return;
        var notes = PlayerData.Get<PlayerNotes>(e.Player.SteamId, Key);
        notes.Visits++;
        notes.LastSeenUtc = DateTime.UtcNow;
    }

    [Command("notes", Description = "what this mod keeps for the world and for you")]
    private string Notes(CommandContext c)
    {
        if (!Capabilities.Is(SavedData)) throw new CommandException($"saved data is not available on this server ({Capabilities.Why(SavedData)})");
        // TryGet creates nothing: there is no value until something was stored.
        var world = WorldData.TryGet<WorldNotes>(Key, out var w) ? $"{w.Joins} join(s), motto: {w.Motto ?? "none"}" : "nothing yet";
        var own = c.Source.SteamId != 0 && PlayerData.TryGet<PlayerNotes>(c.Source.SteamId, Key, out var p)
            ? $"{p.Visits} visit(s), last {p.LastSeenUtc:yyyy-MM-dd HH:mm} UTC"
            : "nothing yet";
        return $"world: {world}; you: {own}";
    }

    [Command("motto", Description = "sets the world's motto", Permission = CommandPermission.Admin)]
    private string Motto(CommandContext c, [Greedy] string text)
    {
        if (!Capabilities.Is(SavedData)) throw new CommandException($"saved data is not available on this server ({Capabilities.Why(SavedData)})");
        WorldData.Get<WorldNotes>(Key).Motto = text;
        return "saved with the world's next save";
    }
}
