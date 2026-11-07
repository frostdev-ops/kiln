// SPDX-License-Identifier: MIT
// FreeCrafting and KeepConsumables on the server: a player's craft request is answered with the recipe's output and its
// inputs stay (Kiln.Gameplay's CraftEvent canceled, Kiln.Items gives the recipe result); a used consumable is given
// back (Kiln.Players' PlayerConsumedEvent). Kiln listens only once some player has had one of the rules.
namespace Kiln;

internal static class RulesCrafting
{
    private static EventSubscription? _craft, _consumed;
    private static ulong _crafts, _kept;

    /// <summary>Crafts made free and consumables given back this session.</summary>
    public static (ulong Crafts, ulong Kept) Counters => (_crafts, _kept);

    /// <summary>Called when a player's rules change (tick thread).</summary>
    public static void Changed(EffectiveRules rules)
    {
        if (rules.FreeCrafting && _craft is null) _craft = Listen("FreeCrafting", () =>
        {
            if (!Gameplay.Available || !Gameplay.Enable()) return null;
            return Events.On<CraftEvent>(OnCraft, EventPriority.Low);
        });
        if (rules.KeepConsumables && _consumed is null) _consumed = Listen("KeepConsumables", () =>
            Events.On<PlayerConsumedEvent>(OnConsumed, EventPriority.Monitor));
    }

    private static EventSubscription? Listen(string rule, Func<EventSubscription?> subscribe)
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's subscription, whichever mod's rule set led here
        try
        {
            var s = subscribe();
            RulesRuntime.Log(s is null ? LogLevel.Warn : LogLevel.Info,
                s is null ? $"{rule}: Kiln.Gameplay's craft events are not available on this build; the rule does nothing" : $"{rule}: listening");
            return s;
        }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException or InvalidOperationException or NotSupportedException)
        {
            RulesRuntime.Log(LogLevel.Warn, $"{rule} unavailable: {e.Message}");
            return null;
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    // The game would take the inputs and give the output; Kiln drops the request and gives the output alone. Only
    // operators with the rule (players; an automated station is its own operator and has none).
    private static void OnCraft(CraftEvent e)
    {
        if (e.Canceled || e.Operator.IsNone) return;  // automated stations pay for themselves: they have no rules
        if (!RulesRuntime.Effective(e.Operator).FreeCrafting) return;
        e.Canceled = true;
        _crafts++;
        var who = e.Operator;
        var recipe = e.RecipeId;
        var amount = Math.Max(1, e.Amount);
        _ = Give(who, recipe, amount);
    }

    private static async Task Give(EntityId who, uint recipe, int amount)
    {
        try
        {
            var r = await Items.GiveRecipeResult(who, recipe, amount, GiveOptions.Notify);
            RulesRuntime.Log(LogLevel.Info, $"player {who}: free craft of recipe 0x{recipe:x8} x{amount}: {r.Status}, {r.Done} made");
        }
        catch (Exception x)
        {
            RulesRuntime.Log(LogLevel.Warn, $"player {who}: free craft of recipe 0x{recipe:x8}: {x.Message}");
        }
    }

    private static void OnConsumed(PlayerConsumedEvent e)
    {
        var who = e.Player.Entity;
        if (!RulesRuntime.Effective(who).KeepConsumables || e.Item.Id == 0) return;
        _kept++;
        _ = Keep(who, e.Item.Id, e.Item.ToString());
    }

    private static async Task Keep(EntityId who, uint item, string what)
    {
        try
        {
            var r = await Items.Give(who, new ItemId(item), 1);
            RulesRuntime.Log(LogLevel.Info, $"player {who}: {what} kept: {r.Status}");
        }
        catch (Exception x)
        {
            RulesRuntime.Log(LogLevel.Warn, $"player {who}: keeping {what}: {x.Message}");
        }
    }
}
