// SPDX-License-Identifier: MIT
// Kiln.Content in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (the first service tick: Content's static constructor, in ServiceTick.AutoStart); content.ready follows the
// session and is refreshed on the service tick, never in a getter.
namespace Kiln;

internal static unsafe class ContentCaps
{
    /// <summary>kiln.content's table is here (the core's row).</summary>
    public const string Service = ContentNative.Service;

    /// <summary>kiln.gamedata's table is here (the core's row).</summary>
    public const string GameData = GameDataNative.Service;

    /// <summary>sigdb: new content (<see cref="NewContent"/>).</summary>
    public const string New = "content.new";

    /// <summary>sigdb: clones of the game's templates (<see cref="GameDataRegistry.Define(GameDataClone)"/>).</summary>
    public const string Clones = "gamedata.template_clones";

    /// <summary>Kiln.Content's own row: this kiln.content sets references (<see cref="ContentEntry.SetRef"/>, table v4).</summary>
    public const string References = "content.references";

    /// <summary>Kiln.Content's session row: the registries are loaded and the data packs applied
    /// (<see cref="Content.Ready"/>).</summary>
    public const string Ready = "content.ready";

    /// <summary>The server-side sigdb features kiln.content serves.</summary>
    public static readonly string[] Features =
    [
        "content.items", "content.recipes", "content.loot", "content.loot_new", "content.drops", New, "content.reflection",
        "content.stack_guard",
    ];

    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25, refreshOnlyWithService: true);

    private static void Report()
    {
        var t = Content.T;
        if (t == null) ServiceCaps.LowerFeatures(Service, Features);
        if (Content.GD == null) ServiceCaps.LowerFeatures(GameData, Clones, "gamedata.buff_lookup");
        ServiceCaps.Static(References, t != null && ContentNative.Has(t, 4) && t->SetRef != null, CapabilityReason.ServiceOlder,
            "this kiln.content has no reference support (v4; update Kiln)", Service);
        ServiceCaps.Session(Ready, Content.Ready, "the registries load with the world's game data; the data packs are applied after that", Service);
    }

    public static KilnUnavailableException Off(string capability, string detail)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }
}
