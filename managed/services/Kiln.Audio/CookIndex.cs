// SPDX-License-Identifier: MIT
// The cooked index of a mod's sounds: <mod>/assets/cooked/audio.json, written by tools/soundcook next to
// cook-report.json (format: docs/kiln/services/audio.md, "Cooked index"). Names -> final
// GUIDs, cue properties (default scene, loop, positional), voice-over languages (null slot = silent), subtitles and
// durations. Without the file, names map to UUIDv5(guidNamespace of assets/cook.json, "<op>:<name>") like the cook.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kiln;

/// <summary>A cue as the mod's cook wrote it (null fields: not in the index).</summary>
public sealed record CueInfo(
    string Mod, string Name, SoundId Id, string? Bus, SoundScene DefaultScene, bool Loop, double? MinDistance,
    double? MaxDistance, float Volume, int DurationMs, bool Override);

/// <summary>One voice language of a cooked voice-over line.</summary>
public sealed record VoiceoverLanguage(string Language, Guid Cue, int DurationMs, string? FallbackOf);

/// <summary>A voice-over line as the mod's cook wrote it. <see cref="Languages"/> holds en, de, fr, es, zh; a null
/// value means that slot is empty, so a player with that voice language hears nothing (the mod's choice).</summary>
public sealed record VoiceoverInfo(
    string Mod, string Name, VoiceoverId Id, bool Narration, string? Fallback,
    IReadOnlyDictionary<string, VoiceoverLanguage?> Languages, IReadOnlyDictionary<string, string> Subtitles)
{
    /// <summary>The longest take over the languages (ms).</summary>
    public int MaxDurationMs => Languages.Values.Where(l => l is not null).Select(l => l!.DurationMs).DefaultIfEmpty(0).Max();
}

internal sealed class CookIndex
{
    public static readonly string[] Slots = ["en", "de", "fr", "es", "zh"];

    public string ModId = "";
    public string Directory = "";
    public Guid? Namespace;
    public readonly Dictionary<string, CueInfo> Cues = new(StringComparer.Ordinal);
    public readonly Dictionary<string, VoiceoverInfo> Voiceovers = new(StringComparer.Ordinal);
    public readonly Dictionary<string, Guid> Sounds = new(StringComparer.Ordinal);
    public bool FromFile;
    public string? Error;
    public DateTime Stamp;

    private static readonly ConcurrentDictionary<string, CookIndex> ByDir = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<Guid, CueInfo> CueByGuid = new();
    private static readonly ConcurrentDictionary<Guid, VoiceoverInfo> VoByGuid = new();

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string AudioJson(string dir) => Path.Combine(dir, "assets", "cooked", "audio.json");
    public static string CookJson(string dir) => Path.Combine(dir, "assets", "cook.json");

    /// <summary>The index of the mod folder (cached; re-read when audio.json changes).</summary>
    public static CookIndex For(string modId, string dir)
    {
        var file = AudioJson(dir);
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (ByDir.TryGetValue(dir, out var have) && have.Stamp == stamp) return have;
        var idx = Load(modId, dir, file, stamp);
        ByDir[dir] = idx;
        foreach (var c in idx.Cues.Values) CueByGuid[c.Id.Cue] = c;
        foreach (var v in idx.Voiceovers.Values) VoByGuid[v.Id.Value] = v;
        if (idx.Error is not null) AudioLog.Warn($"{modId}: {idx.Error}");
        return idx;
    }

    public static CueInfo? Cue(Guid g) => CueByGuid.TryGetValue(g, out var c) ? c : null;
    public static VoiceoverInfo? Voiceover(Guid g) => VoByGuid.TryGetValue(g, out var v) ? v : null;

    /// <summary>Indexes every loaded mod that has cooked audio (for lookups by GUID).</summary>
    public static void ScanLoadedMods()
    {
        foreach (var m in Mods.All)
        {
            try
            {
                if (m.Directory.Length > 0 && (File.Exists(AudioJson(m.Directory)) || File.Exists(CookJson(m.Directory))))
                    For(m.Id, m.Directory);
            }
            catch (Exception e)
            {
                AudioLog.Warn($"{m.Id}: cooked audio index unreadable: {e.Message}");
            }
        }
    }

    public Guid CueGuid(string name) =>
        Cues.TryGetValue(name, out var c) ? c.Id.Cue : Derived("cue", name);

    public Guid VoiceoverGuid(string name) =>
        Voiceovers.TryGetValue(name, out var v) ? v.Id.Value : Derived("voiceover", name);

    private Guid Derived(string op, string name)
    {
        if (Namespace is not { } ns)
            throw new InvalidOperationException(
                $"mod {ModId}: no {op} '{name}' in assets/cooked/audio.json and no guidNamespace in assets/cook.json " +
                "(cook the mod's audio first: tools/soundcook)");
        return Uuid5(ns, $"{op}:{name}");
    }

    private static CookIndex Load(string modId, string dir, string file, DateTime stamp)
    {
        var idx = new CookIndex { ModId = modId, Directory = dir, Stamp = stamp };
        try
        {
            var recipe = CookJson(dir);
            if (File.Exists(recipe))
            {
                using var r = JsonDocument.Parse(File.ReadAllText(recipe), Options);
                if (r.RootElement.TryGetProperty("guidNamespace", out var ns) && Guid.TryParse(ns.GetString(), out var g))
                    idx.Namespace = g;
            }
            if (!File.Exists(file)) return idx;
            idx.FromFile = true;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), Options);
            var root = doc.RootElement;
            if (root.TryGetProperty("format", out var fmt) && fmt.GetInt32() != 1)
                idx.Error = $"audio.json format {fmt.GetInt32()} is newer than this Kiln understands (1)";
            if (root.TryGetProperty("guidNamespace", out var ns2) && Guid.TryParse(ns2.GetString(), out var g2))
                idx.Namespace = g2;
            if (root.TryGetProperty("mod", out var mid) && mid.ValueKind == JsonValueKind.String &&
                mid.GetString() is { Length: > 0 } declared && declared != modId)
                idx.Error = $"audio.json was cooked for mod '{declared}', found in '{modId}'";
            if (root.TryGetProperty("sounds", out var sounds) && sounds.ValueKind == JsonValueKind.Object)
                foreach (var s in sounds.EnumerateObject())
                    if (s.Value.TryGetProperty("guid", out var sg) && Guid.TryParse(sg.GetString(), out var sgv))
                        idx.Sounds[s.Name] = sgv;
            if (root.TryGetProperty("cues", out var cues) && cues.ValueKind == JsonValueKind.Object)
                foreach (var c in cues.EnumerateObject())
                    if (ReadCue(modId, c.Name, c.Value) is { } ci) idx.Cues[c.Name] = ci;
            if (root.TryGetProperty("voiceovers", out var vos) && vos.ValueKind == JsonValueKind.Object)
                foreach (var v in vos.EnumerateObject())
                    if (ReadVo(modId, v.Name, v.Value) is { } vi) idx.Voiceovers[v.Name] = vi;
        }
        catch (Exception e)
        {
            idx.Error = $"cooked audio index {file}: {e.Message}";
        }
        return idx;
    }

    private static CueInfo? ReadCue(string mod, string name, JsonElement e)
    {
        if (!e.TryGetProperty("guid", out var g) || !Guid.TryParse(g.GetString(), out var guid)) return null;
        var bus = Str(e, "scene");
        var play = Str(e, "playScene");
        var scene = play switch
        {
            "Ui" => SoundScene.Ui,
            "Music" => SoundScene.Music,
            "Ambience" => SoundScene.Ambience,
            "LoreVoiceover" => SoundScene.LoreVoiceover,
            "Game" => SoundScene.Game,
            _ => bus switch { "Ui" => SoundScene.Ui, "Music" => SoundScene.Music, _ => SoundScene.Game },
        };
        double? min = null, max = null;
        if (e.TryGetProperty("positional", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("min", out var mn)) min = mn.GetDouble();
            if (p.TryGetProperty("max", out var mx)) max = mx.GetDouble();
        }
        return new CueInfo(mod, name, new SoundId(guid), bus, scene, Bool(e, "loop"), min, max,
            e.TryGetProperty("volume", out var vol) && vol.ValueKind == JsonValueKind.Number ? vol.GetSingle() : 1f,
            Int(e, "durationMs"), Str(e, "mode") == "override");
    }

    private static VoiceoverInfo? ReadVo(string mod, string name, JsonElement e)
    {
        if (!e.TryGetProperty("guid", out var g) || !Guid.TryParse(g.GetString(), out var guid)) return null;
        var langs = new Dictionary<string, VoiceoverLanguage?>(StringComparer.Ordinal);
        foreach (var slot in Slots) langs[slot] = null;
        if (e.TryGetProperty("languages", out var ls) && ls.ValueKind == JsonValueKind.Object)
            foreach (var l in ls.EnumerateObject())
            {
                if (l.Value.ValueKind != JsonValueKind.Object) continue;  // null = an empty slot
                if (!l.Value.TryGetProperty("cue", out var c) || !Guid.TryParse(c.GetString(), out var cue)) continue;
                langs[l.Name] = new VoiceoverLanguage(l.Name, cue, Int(l.Value, "durationMs"), Str(l.Value, "fallbackOf"));
            }
        var subs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("subtitles", out var st) && st.ValueKind == JsonValueKind.Object)
            foreach (var s in st.EnumerateObject())
                if (s.Value.ValueKind == JsonValueKind.String) subs[s.Name] = s.Value.GetString() ?? "";
        return new VoiceoverInfo(mod, name, new VoiceoverId(guid), Bool(e, "narration"), Str(e, "fallback"), langs, subs);
    }

    private static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
    private static int Int(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>RFC 4122 name-based UUID, version 5 (SHA-1), as Python's uuid.uuid5(namespace, name): the cook's GUIDs.</summary>
    public static Guid Uuid5(Guid ns, string name)
    {
        Span<byte> nsBytes = stackalloc byte[16];
        if (!ns.TryWriteBytes(nsBytes, bigEndian: true, out _)) throw new InvalidOperationException("uuid");
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        nsBytes.CopyTo(input);
        nameBytes.CopyTo(input, 16);
        var hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}

internal static class AudioLog
{
    public static void Info(string text) => Backend.Current.Log(0, LogLevel.Info, "audio: " + text);
    public static void Warn(string text) => Backend.Current.Log(0, LogLevel.Warn, "audio: " + text);
    public static void Debug(string text) => Backend.Current.Log(0, LogLevel.Debug, "audio: " + text);
}
