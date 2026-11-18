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

    /// <summary>kiln.gamedata's buff budget (<see cref="GameDataRegistry.GetBudget"/>; native row, since 0.2.0-dev.29).</summary>
    public const string BuffBudget = "gamedata.buff_budget";

    /// <summary>Result-bearing buff definitions and their receipts (<see cref="GameDataRegistry.Define(GameDataClone, DefinitionRegistrationOptions)"/>,
    /// <see cref="GameDataRegistry.GetRegistration"/>; native row, since 0.2.0-dev.29).</summary>
    public const string BuffRegistration = "gamedata.buff_registration";

    /// <summary>The 4,096-clone buff room is reserved (native session row, since 0.2.0-dev.29).</summary>
    public const string ExpandedBuffRoom = "gamedata.expanded_buff_room";

    /// <summary>The game's own texts (<see cref="Content.Text(uint)"/>; native session row, since 0.2.0-dev.29.18). kiln.content
    /// reports it; this file only when its table is missing or older.</summary>
    public const string Text = Content.TextCapability;

    /// <summary>Why the row is off with a kiln.content from before the game-text slots.</summary>
    public const string TextOlder = "this kiln.content has no game-text lookup (0.2.0-dev.29.18; update Kiln)";

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
        // kiln.gamedata reports these with its table; missing or older, nobody would: say why here
        if (GameDataNative.Buffs(Content.GD) == null)
        {
            var (reason, text) = Content.GD == null
                ? (CapabilityReason.ServiceMissing, "kiln.gamedata is not loaded")
                : (CapabilityReason.ServiceOlder, "this kiln.gamedata has no buff budget or result-bearing buff definitions (update Kiln)");
            foreach (var row in (string[])[BuffBudget, BuffRegistration, ExpandedBuffRoom])
                ServiceCaps.Static(row, false, reason, text, GameData);
        }
        ServiceCaps.Static(References, t != null && ContentNative.Has(t, 4) && t->SetRef != null, CapabilityReason.ServiceOlder,
            "this kiln.content has no reference support (v4; update Kiln)", Service);
        // kiln.content reports content.text itself (pending, then available; wrong-side on a server); said here only when it cannot
        ServiceCaps.Static(Text, t != null && ContentNative.HasText(t), CapabilityReason.ServiceOlder, TextOlder, Service);
        ServiceCaps.Session(Ready, Content.Ready, "the registries load with the world's game data; the data packs are applied after that", Service);
    }

    public static KilnUnavailableException Off(string capability, string detail)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }
}
