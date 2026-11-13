// SPDX-License-Identifier: MIT
using System.Reflection;

namespace Kiln;

/// <summary>
/// A service's first use, for what it starts then beyond its capability rows (Kiln.Gameplay's and Kiln.Combat's native
/// event listeners: with one running, a handler can cancel the game's actions and kiln.combat holds hits a step). Kiln's
/// first service tick loads a service for its rows (<see cref="ServiceTick.AutoStart"/>); that is not a use. Before
/// dev.27 nothing loaded these assemblies until a mod did, so a server whose mods never touched them had no listener,
/// whatever catch-all handler (<see cref="KilnEvent"/>, <see cref="CancelableEvent"/>) a mod had.
/// </summary>
internal static class ServiceUse
{
    /// <summary>From a service's module initializer: runs <paramref name="start"/> now, unless the first service tick is
    /// starting the service for its rows; then at its first use: a subscription to an event type the service declares (the
    /// way a mod that only subscribes started it before: the bus runs the event type's static constructors, which ran the
    /// module initializer). Every other use of such a service calls its own start (a call into it does).</summary>
    internal static void Start(Assembly service, string table, Func<bool> start)
    {
        if (!ServiceTick.StartingServices)
        {
            Activate(table, start);
            return;
        }
        var waiter = new Waiter(service, () => Activate(table, start));
        Events.SubscriptionTypeAdded += waiter.Added;
        // subscriptions made before the first service tick (a mod's [Subscribe] handlers at its load)
        foreach (var s in Events.Subscriptions)
            if (s.Active)
                waiter.Added(s.Type);
    }

    /// <summary>Whether a call into a service now is a mod's use of it (dev.26: a mod's first call loaded the service,
    /// whose module initializer started it): a mod's code runs on this thread, and neither the first service tick's start
    /// nor a capability rows report (the service's own reads) is running. A service's public entries ask it while their
    /// listener is not running yet (Combat.Used, Gameplay.Used): one field read each once it runs.</summary>
    internal static bool ByMod => ModContext.CurrentMod is not null && !ServiceTick.StartingServices && !ServiceCapRows.Reporting;

    // A start that found no table yet (start false) is not used up: it runs again once the refresh finds the table.
    private static void Activate(string table, Func<bool> start)
    {
        if (start()) return;
        ServiceCaps.WhenFound(table, () => start());
        ServiceCaps.NoteMissing(table);
    }

    private sealed class Waiter(Assembly service, Action start)
    {
        private int _done;

        public void Added(Type type)
        {
            if (type.Assembly != service || Interlocked.Exchange(ref _done, 1) != 0) return;
            Events.SubscriptionTypeAdded -= Added;
            start();
        }
    }
}
