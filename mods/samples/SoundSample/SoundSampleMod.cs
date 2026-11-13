// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiln;

namespace SoundSample;

/// <summary>
/// Sample: mod sounds cooked by tools/soundcook (docs/kiln/modding/sounds.md).
/// <list type="bullet">
/// <item>audio/**: synthetic test tones, one frequency per cue (tools/soundcook/samples/tones.py).</item>
/// <item>assets/cook.json: the recipe (sounds, cues, an override of the vanilla dialogue-confirm cue, a voiced
/// dialogue line with an English fallback, an English-only narration line).</item>
/// <item>assets/assets.json (kiln.assets, loaded natively at boot) serves the cooked resources and blobs;
/// assets/cooked/audio.json is the name → GUID index Kiln.Audio plays by.</item>
/// </list>
/// The code only checks what the cook produced, one PASS/FAIL line each: the index exists, its GUIDs follow the
/// UUIDv5 rule (so code can name cues without the index), every cooked file is in assets.json, and, where kiln.assets
/// runs, the voice-over resources read back as cooked (both sides; sounds and cues are served on clients only).
/// </summary>
public sealed class SoundSampleMod : Mod
{
    public override void OnLoad()
    {
        var assetsDir = Path.Combine(Context.Info.Directory, "assets");
        var indexPath = Path.Combine(assetsDir, "cooked", "audio.json");
        if (!File.Exists(indexPath))
        {
            Log.Warn("SoundSample: assets/cooked/audio.json missing: run build-assets.sh before deploying");
            return;
        }
        using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(assetsDir, "assets.json")));
        var root = index.RootElement;
        var ns = Guid.Parse(root.GetProperty("guidNamespace").GetString()!);
        var listed = new HashSet<string>();
        foreach (var e in manifest.RootElement.GetProperty("entries").EnumerateArray())
            if (e.TryGetProperty("guid", out var g)) listed.Add(g.GetString()!);

        int pass = 0, fail = 0;
        void Check(bool ok, string what)
        {
            if (ok) pass++; else fail++;
            Log.Info($"{(ok ? "PASS" : "FAIL")} SoundSample: {what}");
        }

        foreach (var (kind, op) in new[] { ("sounds", "sound"), ("cues", "cue"), ("voiceovers", "voiceover") })
        {
            foreach (var p in root.GetProperty(kind).EnumerateObject())
            {
                var guid = p.Value.GetProperty("guid").GetString()!;
                var mode = p.Value.GetProperty("mode").GetString();
                if (mode == "new")
                    Check(Uuid5(ns, $"{op}:{p.Name}").ToString() == guid, $"{op} {p.Name} = UUIDv5(\"{op}:{p.Name}\") {guid}");
                Check(listed.Contains(guid), $"{op} {p.Name} is in assets.json");
            }
        }
        var greeting = root.GetProperty("voiceovers").GetProperty("greeting").GetProperty("languages");
        Check(greeting.GetProperty("fr").GetProperty("fallbackOf").GetString() == "en", "greeting: fr slot filled from en (fallback)");
        var narration = root.GetProperty("voiceovers").GetProperty("narration_intro");
        Check(narration.GetProperty("languages").GetProperty("de").ValueKind == JsonValueKind.Null,
              "narration_intro: de slot empty (fallback null: silence, like the game)");
        Check(narration.GetProperty("durations10ms")[0].GetInt32() > 0, "narration_intro: en duration set (lore queue timing)");

        // kiln.assets can be here with its resource hooks off: reading a resource asks for assets.resources itself
        if (Assets.Available && Capabilities.Is("assets.resources"))
        {
            foreach (var p in root.GetProperty("voiceovers").EnumerateObject())
            {
                var guid = Guid.Parse(p.Value.GetProperty("guid").GetString()!);
                var served = Assets.ReadResource(new AssetKey(guid, "keen::VoiceoverResource"));
                var cooked = File.ReadAllBytes(Path.Combine(assetsDir, "cooked", "sound", p.Name + ".vo.bin"));
                Check(served != null && served.AsSpan().SequenceEqual(cooked), $"kiln.assets serves voiceover {p.Name} as cooked");
            }
        }
        Log.Info($"SoundSample: {pass} PASS, {fail} FAIL");
    }

    /// <summary>RFC 4122 version 5 (SHA-1) UUID: the cook's GUID rule, UUIDv5(guidNamespace, "&lt;op&gt;:&lt;name&gt;").</summary>
    public static Guid Uuid5(Guid ns, string name)
    {
        Span<byte> nsBytes = stackalloc byte[16];
        ns.TryWriteBytes(nsBytes, bigEndian: true, out _);
        var data = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        nsBytes.CopyTo(data);
        Encoding.UTF8.GetBytes(name, data.AsSpan(16));
        var h = SHA1.HashData(data);
        h[6] = (byte)((h[6] & 0x0F) | 0x50);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }
}
