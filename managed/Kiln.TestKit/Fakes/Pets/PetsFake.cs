// SPDX-License-Identifier: MIT
// The kit's Kiln.Pets has no native table of its own (KilnHarness.Pets is a managed world under the real roster): this only
// ends what a harness installed and says what the kit covers.
namespace Kiln.TestKit;

internal sealed class PetsFake : IKitFake
{
    public string Service => "kiln.pets";
    public void Register() { }
    public void Start(KilnHarness harness) { }
    public void BeforeStep(KilnHarness harness, ulong step) { }
    public void End(KilnHarness harness) => harness.PetsIfUsed?.End();

    public void AddCoverage(List<CoverageRow> rows) => rows.Add(new CoverageRow("Kiln.Pets (KilnHarness.Pets)", CoverageKind.RecordingFake,
        ["kiln.pets", "pets.records", "pets.summon", "pets.control", "pets.follow", "pets.fight", "pets.downed"],
        "the real roster on the kit's world: owners the test joins, bodies the kit keeps (no game entity, no walking), downs the test makes, the hostile pairs; no save receipts, no owner's view"));
}
