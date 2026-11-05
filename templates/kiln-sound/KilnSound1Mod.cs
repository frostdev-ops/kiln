using System.Text.Json;
using Kiln;

namespace KilnSound1;

/// <summary>The sounds are data (assets/): kiln.assets serves them to the game at boot (on clients; a dedicated server
/// does not play sounds). This code reads the cook's index, assets/cooked/audio.json (name -> GUID, what Kiln.Audio
/// plays by), and logs what the mod provides. To play one from code, add Audio to KilnServices and call
/// Sound.Play2D(SoundId.Named(Context, "chime")) (docs/kiln/modding/sounds.md).</summary>
public sealed class KilnSound1Mod : Mod
{
    public override void OnLoad()
    {
        var index = Path.Combine(Context.Info.Directory, "assets", "cooked", "audio.json");
        if (!File.Exists(index))
        {
            Log.Warn("assets/cooked/audio.json is missing: run `kiln cook` before deploying");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(index));
        foreach (var kind in new[] { "sounds", "cues", "voiceovers" })
        {
            if (!doc.RootElement.TryGetProperty(kind, out var list)) continue;
            foreach (var p in list.EnumerateObject())
                Log.Info($"{kind[..^1]} {p.Name}: {p.Value.GetProperty("guid").GetString()} ({p.Value.GetProperty("mode").GetString()})");
        }
    }
}
