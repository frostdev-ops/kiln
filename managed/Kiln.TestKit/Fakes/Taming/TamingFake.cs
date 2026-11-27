// SPDX-License-Identifier: MIT
// The kit's Kiln.Taming has no native table of its own (KilnHarness.Taming is a managed world and item costs under the real
// engine): this only ends what a harness installed and says what the kit covers.
namespace Kiln.TestKit;

internal sealed class TamingFake : IKitFake
{
    public string Service => "kiln.taming";
    public void Register() { }
    public void Start(KilnHarness harness) { }
    public void BeforeStep(KilnHarness harness, ulong step) { }
    public void End(KilnHarness harness)
    {
        harness.TamingIfUsed?.End();
        // the engine is process-wide: the one made without kiln.Taming (a definition outside a mod) goes with the harness too
        TamingRuntime.Reset();
    }

    public void AddCoverage(List<CoverageRow> rows) => rows.Add(new CoverageRow("Kiln.Taming (KilnHarness.Taming)", CoverageKind.RecordingFake,
        ["taming.steps"],
        "the real attempt engine on the kit's world and item costs: players and creatures the test places, exact stack takes and refunds on the kit's inventories; no gear wear, no game's own taming (NativeStep counts the test's steps)"));
}
