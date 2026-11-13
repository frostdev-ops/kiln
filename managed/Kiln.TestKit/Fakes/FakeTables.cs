// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;

namespace Kiln.TestKit;

/// <summary>
/// The kit's fake native service tables: written in C#, mirrors of the public headers in <c>native/core/abi/services/</c>,
/// allocated once and published for the whole process before any Kiln service can ask (the services cache the pointer
/// they got first, a 0 included). Each table's functions look up the harness that runs now and answer from its state;
/// with no harness, or with the service switched off by the test (<see cref="HarnessCapabilities.Override"/>), they
/// answer as the native service does when it is unavailable. A function a fake does not model answers
/// <c>KILN_ERR_UNAVAILABLE</c> (or <c>KILN_ERR_NOT_FOUND</c>), never "ok".
/// </summary>
internal static class FakeTables
{
    // KilnResult (native/core/abi/kiln_api.h)
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5, Internal = 7;

    public const string PlayersService = "kiln.players";
    public const string SavesService = "kiln.saves";

    private static int _registered;

#pragma warning disable CA2255  // a test kit: the tables must be published before any Kiln service asks for one
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ModuleInit() => Register();

    /// <summary>Publishes every fake table (once per process).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        KitBackend.Instance.RegisterTable(PlayersService, PlayersTable.Create());
        KitBackend.Instance.RegisterTable(SavesService, SavesTable.Create());
        // dev.27's recording fakes: each table, the game features it serves as rows of the kit's (a test may switch one
        // off), and what it does not model as offline
        KitBackend.Instance.RegisterTable(SpawnAbi.Service, SpawnTable.Create());
        Features(SpawnFake.Features);
        Offline(SpawnFake.ComponentsRow, "extra spawn components are not modelled by the test kit");
        Session(SpawnFake.WorldRow, false, SpawnFake.NoServer);
        KitBackend.Instance.RegisterTable(ChatAbi.Service, ChatTable.Create());
        Features(ChatFake.Features);
    }

    /// <summary>Game features a fake models: available rows of the kit's, which a test can switch off.</summary>
    private static void Features(IEnumerable<string> names)
    {
        foreach (var name in names)
            KitBackend.Instance.Capabilities.Report(KitBackend.CapabilityOwner, name, CapState.Available, CapReason.None, "faked by the test kit",
                KitBackend.CapabilityOwner);
    }

    /// <summary>A row a fake does not model: unavailable with reason offline (and a test cannot switch it on).</summary>
    private static void Offline(string name, string text) =>
        KitBackend.Instance.Capabilities.Report(KitBackend.CapabilityOwner, name, CapState.Unavailable, CapReason.Offline, text, KitBackend.CapabilityOwner);

    /// <summary>A session row of a fake in the kit's table: available while <paramref name="on"/>, else pending.</summary>
    public static void Session(string name, bool on, string waiting) =>
        KitBackend.Instance.Capabilities.Report(KitBackend.CapabilityOwner, name, on ? CapState.Available : CapState.Pending,
            on ? CapReason.None : CapReason.Session, on ? "faked by the test kit" : waiting, KitBackend.CapabilityOwner);

    /// <summary>Whether the test switched <paramref name="name"/> off (<see cref="HarnessCapabilities.Override"/>): a fake
    /// then answers for it as the native service does when its feature is off.</summary>
    public static bool Off(string name) =>
        KitBackend.Instance.Capabilities.Get(name) is { State: CapState.Unavailable } row && row.Source == HarnessCapabilities.TestOwner;

    /// <summary>The harness a table answers for: the one that runs now, when the test has not switched
    /// <paramref name="service"/> off.</summary>
    public static KilnHarness? Active(string service)
    {
        var h = KitBackend.Instance.Harness;
        if (h is null || !KitBackend.Instance.Capabilities.Is(service)) return null;
        return h;
    }

    /// <summary>Whether the calling thread is the running harness's tick thread.</summary>
    public static bool OnTickThread => KitBackend.Instance.IsTickThread;
}
