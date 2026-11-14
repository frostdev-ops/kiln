// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

/// <summary>The native service tables Kiln's core publishes, by name: the <c>KILN_*_SERVICE</c> names of
/// every header under <c>native/</c>. Embedded, since a mod author's machine has no Kiln repository; a kit test compares
/// the list with the headers and fails when they differ.</summary>
internal static class ServiceNames
{
    // One name per line, sorted: a package that adds a service adds one line, and two such additions merge.
    public static readonly IReadOnlyList<string> All =
    [
        "kiln.abilities",
        "kiln.assets",
        "kiln.audio",
        "kiln.build",
        "kiln.caps",
        "kiln.characters",
        "kiln.chat",
        "kiln.client",
        "kiln.combat",
        "kiln.content",
        "kiln.craftmenu",
        "kiln.creature_control",
        "kiln.creatures",
        "kiln.eml",
        "kiln.example",
        "kiln.flame",
        "kiln.gamedata",
        "kiln.gameplay",
        "kiln.hooks",
        "kiln.items",
        "kiln.knowledge",
        "kiln.net",
        "kiln.npcai",
        "kiln.pacing",
        "kiln.playeranim",
        "kiln.players",
        "kiln.playerstate",
        "kiln.profile",
        "kiln.progress",
        "kiln.progression",
        "kiln.questmark",
        "kiln.render",
        "kiln.rules",
        "kiln.saves",
        "kiln.screen",
        "kiln.settings",
        "kiln.skills",
        "kiln.spawn",
        "kiln.steamguard",
        "kiln.surfaces",
        "kiln.time",
        "kiln.types",
        "kiln.ui",
        "kiln.ui.tick",
        "kiln.vfx",
        "kiln.world",
        "kiln.worldtables",
    ];

    private static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

    public static bool IsService(string name) => Set.Contains(name);
}
