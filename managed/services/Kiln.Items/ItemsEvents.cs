// SPDX-License-Identifier: MIT
// Kiln.Items on the event bus: ItemsGivenEvent, ItemsTakenEvent (docs/kiln/events.md).
namespace Kiln;

/// <summary>Base of <see cref="ItemsGivenEvent"/> and <see cref="ItemsTakenEvent"/>: a Kiln.Items transaction
/// finished with something moved (Done or Partial, not a dry run). Posted on the tick thread when the transaction
/// completes (Pre of the step after it ran), before the caller's task continues. Not cancelable (it already
/// happened). Items players move themselves do not raise it.</summary>
public abstract class ItemsTransactionEvent : KilnEvent
{
    private protected ItemsTransactionEvent(ItemsResult result, string origin)
    {
        Result = result;
        Origin = origin;
    }

    public ItemsResult Result { get; }

    /// <summary>The inventory owner (player, chest, NPC).</summary>
    public EntityId Target => Result.Entity;

    public ItemId Item => Result.Item;

    /// <summary>How many were given / taken.</summary>
    public int Count => Result.Done;

    /// <summary>Id of the mod that asked ("kiln" outside mod code).</summary>
    public string Origin { get; }

    /// <summary>The item from the catalog (null when unknown).</summary>
    public ItemInfo? Info => Items.Get(Item);
}

/// <summary>Kiln.Items gave items (see <see cref="ItemsTransactionEvent"/>).</summary>
public sealed class ItemsGivenEvent : ItemsTransactionEvent
{
    internal ItemsGivenEvent(ItemsResult result, string origin) : base(result, origin) { }
    public override string ToString() => $"given {Count} x {Item} to {Target} by {Origin}";
}

/// <summary>Kiln.Items took items (see <see cref="ItemsTransactionEvent"/>).</summary>
public sealed class ItemsTakenEvent : ItemsTransactionEvent
{
    internal ItemsTakenEvent(ItemsResult result, string origin) : base(result, origin) { }
    public override string ToString() => $"taken {Count} x {Item} from {Target} by {Origin}";
}

internal static class ItemsEvents
{
    internal static void Completed(bool take, ItemsResult result, ModContext? mod)
    {
        if (!result.Succeeded || result.DryRun || result.Done <= 0) return;
        if (take ? !Events.HasSubscribers<ItemsTakenEvent>() : !Events.HasSubscribers<ItemsGivenEvent>()) return;
        var origin = mod?.Info.Id ?? "kiln";
        KilnEvent ev = take ? new ItemsTakenEvent(result, origin) : new ItemsGivenEvent(result, origin);
        try
        {
            if (Game.IsTickThread) Events.Post(ev);
            else Events.Enqueue(ev);
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"kiln.items: posting {ev.GetType().Name} failed: {e.Message}");
        }
    }
}
