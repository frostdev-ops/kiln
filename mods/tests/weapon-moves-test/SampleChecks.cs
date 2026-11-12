// SPDX-License-Identifier: MIT
// mods/samples/WeaponMoves loaded beside this test, cooked on this machine: what its two swords (turning-blade in C#, turning-blade-json) made, read
// from the game's data and its blocks report, without changing the sample. Skipped, with a line, when the sample is not loaded.
using System.Text.Json.Nodes;
using Kiln;

namespace WeaponMovesTest;

public sealed partial class WeaponMovesTestMod
{
    private const string Sample = "weapon-moves";
    private static readonly string[] SampleSwords = ["turning-blade", "turning-blade-json"];
    private static readonly string[] SampleStates = ["slash-1", "slash-2", "cleave", "guard"];

    // the sample's moves as its blocks write them: the clone's suffix, its like, hitAt, length
    private static readonly (string Suffix, string Like, double? HitAt, double? Length)[] SampleMoves =
        [("primary.1", Light1, 0.42, 1.0), ("primary.2", Light2, 0.37, 0.87), ("heavy", SwordHeavy, 0.9, 1.6), ("block", GameBlock, null, null)];

    internal static Guid SampleClone(string block, string suffix) => BlockIds.Derive(Sample, "sequence", $"{block}.{suffix}");

    private void SampleLoads()
    {
        if (!Mods.IsLoaded(Sample)) { Info($"the sample {Sample} is not loaded here: its checks are skipped"); return; }
        var report = Blocks.Report().GetValueOrDefault(Sample);
        var errors = (report.Errors ?? []).ToList();
        Info($"{Sample}: blocks [{string.Join(", ", report.Blocks ?? [])}]; errors [{string.Join(" | ", errors)}]");
        Check(SampleSwords.All(b => (report.Blocks ?? []).Contains(b)), $"{Sample}: both swords are blocks, none refused: [{string.Join(", ", report.Blocks ?? [])}]");
        foreach (var b in SampleSwords)
            foreach (var s in SampleStates)
            {
                var said = errors.Count(e => e.Contains($"weapon {b}, ", StringComparison.Ordinal) && e.Contains($"\"{s}\" cannot be looked up: player states are not available in this build", StringComparison.Ordinal));
                Check(said == (_wrong && b == SampleSwords[0] && s == "guard" ? 2 : 1), $"{Sample}: {b}'s state \"{s}\" is reported once as not available in this build ({said})");
            }
        var other = errors.Where(e => !e.Contains("player states are not available in this build", StringComparison.Ordinal)).ToList();
        Check(other.Count == 0, $"{Sample}: nothing else in its blocks report: [{string.Join(" | ", other)}]");

        foreach (var b in SampleSwords)
        {
            var low = JsonNode.Parse(Blocks.Lowered(b, Sample) ?? "{}")!;
            Info($"{Sample} {b}: moves {low["moves"]?.ToJsonString()}");
            Info($"{Sample} {b}: movesReady slots {low["movesReady"]?["slots"]?.ToJsonString()}, bases {low["movesReady"]?["bases"]?.ToJsonString()}");
            var item = Content.ItemData.Get($"{Sample}.{b}")!;
            var rows = Slots(item);
            var starter = Slots(Content.ItemData.Get(SwordStarter)!);
            foreach (var (suffix, like, hitAt, length) in SampleMoves)
            {
                var name = $"{Sample}.{b}.{suffix}";
                var clone = Content.Sequences.Get(name);
                var atLoad = suffix != "block";
                var bases = low["movesReady"]?["bases"]?[name]?.ToString();
                if (!Check(clone != null && clone.Guid == SampleClone(b, suffix) && clone.IsClone,
                        $"{name}: made {(atLoad ? "at load, from its like" : "when content was ready, from the slot's own")} {bases}, GUID {clone?.Guid}")) continue;
                var game = Seq(like);
                Check(StateOf(clone!) == StateOf(game), $"{name}: plays its like {like}'s own state {StateOf(clone!)}: the sample's state name could not be looked up");
                if (hitAt is { } h)
                {
                    var first = EventsOf(clone!).Where(e => e.Type == "SpawnImpact").Min(e => e.Time);
                    Check(Math.Abs(first - h) < 1e-4 && Math.Abs(clone!.Get("length").Number - length!.Value) < 1e-4, $"{name}: its blow at {first:0.###} s and its length {clone!.Get("length").Number:0.###} s, the sample's {h} and {length}");
                }
            }
            // the slots, by their selectors on the sword starter's list
            var one = SampleClone(b, "primary.1");
            var want = new Dictionary<string, Guid> { ["primary"] = one, ["heavyAir"] = one, ["heavy"] = SampleClone(b, "heavy"), ["block"] = SampleClone(b, "block") };
            foreach (var (slot, g) in want)
            {
                var at = Where(starter, slot);
                Check(at.Length > 0 && at.All(i => rows[i].Sequence == g), $"{Sample} {b}: its {slot} slot [{string.Join(",", at)}] plays {Content.Sequences.Get(g.ToString())?.Name}");
            }
            // the chain: 1 to 2 to 1, each link's Heavy to the heavy
            var two = SampleClone(b, "primary.2");
            var heavy = SampleClone(b, "heavy");
            foreach (var (from, next) in new[] { (one, two), (two, one) })
            {
                var o = Openings(Content.Sequences.Get(from.ToString())!).Where(x => x.Orig != Guid.Empty).ToList();
                Check(o.Where(x => x.Trigger == "Primary").All(x => x.FollowUp == next) && o.Where(x => x.Trigger == "Heavy").All(x => x.FollowUp == heavy) && o.Any(x => x.Trigger == "Heavy"),
                    $"{Sample} {b}: {Content.Sequences.Get(from.ToString())?.Name} hands Primary to {Content.Sequences.Get(next.ToString())?.Name} and Heavy to the heavy: [{string.Join(", ", o.Select(x => $"{x.Trigger}->{Content.Sequences.Get(x.FollowUp.ToString())?.Name}"))}]");
            }
            // its moving template, cooked on this machine
            var mt = Content.Templates.Get($"{Sample}.{b}.moving");
            var hand = StarterVisual(SwordStarter) is { } sv ? SlotOf(sv) : null;
            Check(mt != null && mt.Guid == BlockIds.Derive(Sample, "template", $"{b}.moving") && Content.Items.Get($"{Sample}.{b}")!.GetRef("equipment.visualEntity") == mt.Guid,
                $"{Sample} {b}: the item holds its moving template {mt?.Name} {mt?.Guid}");
            if (mt == null) continue;
            Check(mt.Get("ModelResource.model").Guid == BlockIds.Derive(Sample, "model", "moves_blade_anim") && mt.Get("AnimationGraphResourceComponent2.animationGraph").Guid == BlockIds.Derive(Sample, "graph", "moves_blade"),
                $"{Sample} {b}: the moving template has the cooked model {mt.Get("ModelResource.model").Guid} and graph {mt.Get("AnimationGraphResourceComponent2.animationGraph").Guid}");
            Check(hand != null && SlotOf(mt) == hand, $"{Sample} {b}: held in {SlotOf(mt)}, the sword's hand as the game's {SwordStarter} has it ({hand})");
        }
    }
}
