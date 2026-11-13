// SPDX-License-Identifier: MIT
// Kiln.Quests in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (QuestsStartup, the first service tick) and at its first use, and every 25 steps while kiln.knowledge is here
// (a fault of the game's knowledge apply turns Set and Revoke off at run time). kiln.knowledge has only the core's row; the
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

    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25, refreshOnlyWithService: true);

    private static void Report()
    {
        var t = KnowledgeNative.T;
        if (t == null)
        {
            ServiceCaps.LowerFeatures(Service, Features);
            return;
        }
        // the table is published also when single hooks failed: each feature follows its switch in the status
        KnowledgeNative.Status s = default;
        s.Size = (uint)sizeof(KnowledgeNative.Status);
        if (t->Status == null || t->Status(&s) != KnowledgeNative.Ok) return;
        var why = KnowledgeNative.Text(s.Reason, 160);
        var text = "kiln.knowledge: " + (why.Length > 0 ? why : "this part is off on this build (see kiln.log)");
        if (s.Knowledge == 0) ServiceCaps.LowerOff("quests.knowledge", CapabilityReason.Faulted, text);
        if (s.Changes == 0) ServiceCaps.LowerOff("quests.changes", CapabilityReason.Faulted, text);
        if (s.Journal == 0) ServiceCaps.LowerOff("quests.journal", CapabilityReason.Faulted, text);
        if (s.Npc == 0) ServiceCaps.LowerOff("quests.npc", CapabilityReason.Faulted, text);
        if (s.Dialogue == 0) ServiceCaps.LowerOff("quests.dialogue", CapabilityReason.Faulted, text);
        if (s.ClientInput == 0) ServiceCaps.LowerOff("quests.client_input", CapabilityReason.Faulted, text);
    }
}
