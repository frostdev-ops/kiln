// SPDX-License-Identifier: MIT
using System.Collections;
using System.Reflection;

namespace Kiln.TestKit;

/// <summary>
/// The last resort of T2-needs N1: Kiln.Saves keeps the first world of the process open for good (a dedicated server has
/// one world per process, and only a hosted client's world close resets it). Between two harnesses this does what a new
/// server process would: the world store is closed and opens again for the next harness's world, and the per-session flags
/// start over. Only a reset between harnesses, never behaviour: it runs at the start of a harness, before its first step,
/// with no mod loaded. <c>Kiln.TestKit.Tests</c> fails when one of these members is gone or renamed
/// (<see cref="Resolve"/>). Goes when Kiln.Saves has a reset of its own (an internal <c>EndSession</c>).
/// </summary>
internal static class SavesReset
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>The fields of <c>SavesRuntime</c> set back to their start values (false, null, 0).</summary>
    internal static readonly string[] RuntimeFields = ["_shutdownAnnounced", "_loadedEventSeen", "_worldFile", "_savingSeq", "_snapshotSeq", "_savingRevision"];

    internal sealed record Members(Type Runtime, FieldInfo Store, FieldInfo Requests, FieldInfo[] Flags, MethodInfo CloseWorld, FieldInfo Closed);

    /// <summary>Every member the reset needs; throws naming the first one that is missing.</summary>
    internal static Members Resolve(Assembly saves)
    {
        var runtime = saves.GetType("Kiln.SavesRuntime") ?? throw Missing("type Kiln.SavesRuntime");
        var store = runtime.GetField("Store", Static) ?? throw Missing("SavesRuntime.Store");
        var requests = runtime.GetField("Requests", Static) ?? throw Missing("SavesRuntime.Requests");
        if (!typeof(IList).IsAssignableFrom(requests.FieldType)) throw Missing("SavesRuntime.Requests as a list");
        var flags = RuntimeFields.Select(n => runtime.GetField(n, Static) ?? throw Missing($"SavesRuntime.{n}")).ToArray();
        var close = store.FieldType.GetMethod("CloseWorld", Instance, Type.EmptyTypes) ?? throw Missing("WorldStore.CloseWorld()");
        var closed = store.FieldType.GetField("_closed", Instance) ?? throw Missing("WorldStore._closed");
        if (closed.FieldType != typeof(bool)) throw Missing("WorldStore._closed as a bool");
        return new Members(runtime, store, requests, flags, close, closed);
    }

    private static InvalidOperationException Missing(string what) =>
        new($"Kiln.TestKit cannot reset Kiln.Saves between harnesses: {what} is not there any more (see T2-needs N1: the kit needs a reset of the service's own)");

    /// <summary>At the start of a harness: when Kiln.Saves ran in this process, its world session starts over.</summary>
    public static void BetweenHarnesses()
    {
        var saves = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Kiln.Saves");
        if (saves is null) return;  // never used in this process: nothing to reset
        var m = Resolve(saves);
        var store = m.Store.GetValue(null) ?? throw Missing("SavesRuntime.Store's value");
        m.CloseWorld.Invoke(store, null);  // drops the old world's data, id and folder; announces "no world" to per-world configs
        m.Closed.SetValue(store, false);   // a dedicated server's world opens at first use, before the game loads it
        foreach (var f in m.Flags) f.SetValue(null, f.FieldType.IsValueType ? Activator.CreateInstance(f.FieldType) : null);
        if (m.Requests.GetValue(null) is IList requests)
            lock (requests) requests.Clear();  // requests made outside a mod; a mod's went with its unload
    }
}
