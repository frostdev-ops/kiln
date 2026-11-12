// SPDX-License-Identifier: MIT
namespace Kiln.Testing;

/// <summary>For Kiln.Testing's own unit tests (internal): a mod context without Kiln.Host, and its scheduler's queue run
/// by hand, as the host runs it each step.</summary>
internal static class TestHooks
{
    public static ModContext NewContext(string id, string name, string version, string dataDirectory) =>
        HostAccess.CreateContext(new ModInfo(id, name, version, dataDirectory) { Side = ModSide.Both }, 0, dataDirectory);

    public static void Bind(Mod mod, ModContext context) => HostAccess.Bind(mod, context);

    public static void Retire(ModContext context) => context.Retire();

    /// <summary>Runs the work posted to <paramref name="context"/>'s scheduler (await continuations) in its context, as
    /// the host does before a step. Returns how many ran.</summary>
    public static int Drain(ModContext context)
    {
        Action[] work;
        lock (context.PendingLock)
        {
            work = context.Pending.ToArray();
            context.Pending.Clear();
        }
        var previousSync = SynchronizationContext.Current;
        var previousMod = ModContext.CurrentMod;
        SynchronizationContext.SetSynchronizationContext(context.Scheduler.SynchronizationContext);
        ModContext.CurrentMod = context;
        try
        {
            foreach (var w in work) w();
        }
        finally
        {
            ModContext.CurrentMod = previousMod;
            SynchronizationContext.SetSynchronizationContext(previousSync);
        }
        return work.Length;
    }
}
