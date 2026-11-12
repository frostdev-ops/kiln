// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

/// <summary>The native service tables Kiln's core publishes, by name: the <c>KILN_*_SERVICE</c> names of
/// <c>native/core/abi/services/*.h</c>. Embedded, since a mod author's machine has no Kiln repository; a kit test compares
/// the list with the headers and fails when they differ.</summary>
internal static class ServiceNames
{
    public static readonly IReadOnlyList<string> All =
    [
        "kiln.abilities", "kiln.assets", "kiln.audio", "kiln.build", "kiln.chat", "kiln.client", "kiln.combat",
        "kiln.content", "kiln.craftmenu", "kiln.creatures", "kiln.eml", "kiln.example", "kiln.gamedata", "kiln.gameplay",
        "kiln.hooks", "kiln.items", "kiln.net", "kiln.npcai", "kiln.pacing", "kiln.players", "kiln.profile",
        "kiln.progress", "kiln.render", "kiln.rules", "kiln.saves", "kiln.screen", "kiln.settings", "kiln.spawn",
        "kiln.surfaces", "kiln.time", "kiln.types", "kiln.ui", "kiln.ui.tick", "kiln.vfx", "kiln.world",
        "kiln.worldtables",
    ];

    private static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

    public static bool IsService(string name) => Set.Contains(name);
}
