// SPDX-License-Identifier: MIT
// Sounds as blocks: the cue's GUID follows from the mod's recipe (or the namespace `soundcook.py blocks` gives a recipe it makes), effects and stations play
// one by its name or handle, and a block the recipe doesn't have yet gets a warning that says how to cook it. What a client hears is the manual test's.
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksMoreTest;

public sealed partial class BlocksMoreTestMod
{
    private void DefineSounds()
    {
        var hum = Blocks.Define("hum", new SoundBlock { Clips = ["audio/hum.wav"], Volume = 0.6, Range = [3, 40], Loop = true });
        Blocks.Define("humming-rack", new StationBlock { Name = "Humming Rack", Processing = new ProcessingBlock { Running = new RunningBlock { Sound = hum } },
            Recipes = [new RecipeBlock { Seconds = 5, Needs = { ["Material_T1_Twigs"] = 1 }, Output = "Material_T1_Resin" }] });
        Blocks.Define("ping-effect", new EffectBlock { Sound = hum });
        Refused("a clip that isn't there", () => Blocks.Define("sound-missing", new SoundBlock { Clips = ["audio/nope.wav"] }), "sound-missing", "clips[0]", "not in the mod's folder");
        Refused("a clip that is no audio", () => Blocks.Define("sound-png", new SoundBlock { Clips = ["icons/chill.png"] }), "clips[0]", "not an audio file");
        Refused("a range back to front", () => Blocks.Define("sound-range", new SoundBlock { Clips = ["audio/hum.wav"], Range = [40, 3] }), "range", "max more than min");
        Refused("a scene that isn't", () => Blocks.Define("sound-scene", new SoundBlock { Clips = ["audio/hum.wav"], Scene = "Loud" }), "scene", "Sfx");
    }

    private static Guid Uuid5(Guid ns, string name)
    {
        var n = ns.ToByteArray(bigEndian: true);
        var text = System.Text.Encoding.UTF8.GetBytes(name);
        var h = System.Security.Cryptography.SHA1.HashData([.. n, .. text]);
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    private void Sounds()
    {
        // the recipe soundcook wrote for these blocks has this namespace: the one Kiln.Blocks derives for a mod with no recipe of its own
        var ns = Uuid5(new Guid("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77"), $"{Me}:sounds");
        foreach (var name in new[] { "hum", "hum-json", "ping-json" })
        {
            var h = Blocks.Find(name);
            Check(h?.Guid == Uuid5(ns, $"cue:{name}"), $"sound {name}: the cue GUID the cook gives it, {Uuid5(ns, $"cue:{name}")}: {h?.Guid}");
        }
        var rack = Content.Templates.Get($"{Me}.humming-rack.station")!;
        Check(S(rack, "FactoryStationClient.soundLoopRunning") == Blocks.Find("hum")?.Guid.ToString(), $"a station runs with the block's cue: {S(rack, "FactoryStationClient.soundLoopRunning")}");
        // the recipe has the JSON blocks' steps (soundcook.py blocks wrote them); the C# block's are named in a warning
        var warnings = Blocks.Warnings(Me);
        Check(warnings.Any(w => w.Contains("sound hum:", StringComparison.Ordinal) && w.Contains("no cue \"hum\"", StringComparison.Ordinal)), $"a C# sound the recipe lacks gets a warning: {warnings.FirstOrDefault(w => w.Contains("sound hum"))}");
        Check(!warnings.Any(w => w.Contains("hum-json", StringComparison.Ordinal) || w.Contains("ping-json", StringComparison.Ordinal)), "the JSON sounds are in the recipe: no warning");
        var low = JsonNode.Parse(Blocks.Lowered("ping-json") ?? "{}");
        Check(low?["sound"]?["clips"] is JsonArray { Count: 2 }, $"what it became: {low?["sound"]?.ToJsonString()}");
    }
}
