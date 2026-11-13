using Kiln;

namespace KilnEvents1;

/// <summary>An event of this mod's own. Another mod that depends on this one subscribes to it like to any of Kiln's,
/// and because it is cancelable and has a settable property, that mod can stop the greeting or change its words.</summary>
public sealed class GreetingEvent(Player player, string text) : CancelableEvent
{
    public Player Player { get; } = player;

    /// <summary>What the player will be told; handlers may change it.</summary>
    public string Text { get; set; } = text;
}

public sealed class KilnEvents1Mod : Mod
{
    private EventSubscription? _left;

    public override void OnLoad()
    {
        // In code: for a subscription that comes and goes, or one with a filter (for hits on one creature only:
        // Events.On<EntityHurtEvent>(handler, EventFilter.ForEntity(creature)), with the Combat service).
        // Monitor runs last and is for looking, not for changing.
        _left = Events.On<PlayerLeftEvent>(e => Log.Info($"{e.Player.Name} left"), EventPriority.Monitor);
    }

    // By attribute: Kiln subscribes every [Subscribe] method of the mod class when the mod loads and removes it when
    // the mod unloads. The parameter's type is the event. Handlers run on the server's tick thread.
    [Subscribe]
    private void OnJoined(PlayerJoinedEvent e)
    {
        // Post runs every handler now, in priority order, and gives the event back: read what they left.
        var greeting = Events.Post(new GreetingEvent(e.Player, $"Welcome, {e.Player.Name}!"));
        if (greeting.Canceled)
        {
            Log.Info($"no greeting for {e.Player.Name}: canceled by {greeting.CanceledBy}");
            return;
        }
        // Ask before you use a service: a server without Kiln.Chat has no chat to tell.
        if (Chat.Available) Chat.Tell(e.Player.Entity, greeting.Text);
    }

    // A handler of the mod's own event, early (High runs before Normal): it cancels the greeting for a player whose
    // Steam id is not known. A canceled event is not shown to later handlers...
    [Subscribe(EventPriority.High)]
    private void OnlyKnownPlayers(GreetingEvent e)
    {
        if (e.Player.SteamId == 0) e.Cancel();
    }

    // ...unless they ask for it.
    [Subscribe(EventPriority.Monitor, ReceiveCanceled = true)]
    private void Watch(GreetingEvent e) => Log.Info($"greeting for {e.Player.Name}: {(e.Canceled ? "canceled" : e.Text)}");

    public override void OnUnload() => _left?.Dispose();  // Kiln removes a mod's subscriptions anyway; this says it
}
