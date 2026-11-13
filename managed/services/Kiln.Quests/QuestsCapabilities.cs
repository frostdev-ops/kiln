// SPDX-License-Identifier: MIT
// Kiln.Quests in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (QuestsStartup, the first service tick) and at its first use. kiln.knowledge has only the core's row; the
// world's progress lists (WorldProgress.PlacesReady, BossesReady) are built on demand from kiln.assets' data and say so
// themselves: they are no capability (nothing throws for them) and get no row.
namespace Kiln;

internal static unsafe class QuestsCaps
{
    /// <summary>kiln.knowledge's table is here (the core's row).</summary>
    public const string Service = "kiln.knowledge";

    /// <summary>The sigdb features kiln.knowledge serves.</summary>
    public static readonly string[] Features =
        ["quests.knowledge", "quests.journal", "quests.dialogue", "quests.npc", "quests.changes", "quests.client_input"];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static void Report()
    {
        if (KnowledgeNative.T == null) ServiceCaps.LowerFeatures(Service, Features);
    }
}
