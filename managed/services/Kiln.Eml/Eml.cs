// SPDX-License-Identifier: MIT
// What kiln-eml (the GPL-3 EML plugin, gpl/kiln-eml) did this start, for Kiln mods: the managed port of EML's
// `loader.has_mod` plus the run's result. Reads <game>/kiln/state/eml.json (format 1), which kiln-eml writes before the
// game starts; Kiln's own code, no GPL code involved.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>The EML mods of this start and what kiln-eml did with them (docs/kiln/eml.md).</summary>
public static class Eml
{
    private static readonly Lazy<EmlStatus?> Current = new(() => Load(DefaultStateFile));

    /// <summary><c>&lt;game&gt;/kiln/state/eml.json</c>, found next to Kiln.Api (<c>&lt;game&gt;/kiln/host/</c>).</summary>
    public static string DefaultStateFile =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Game).Assembly.Location) ?? ".", "..", "state", "eml.json"));

    /// <summary>This start's result, or null when kiln-eml did not run (not installed, an older Kiln).</summary>
    public static EmlStatus? Status => Current.Value;

    /// <summary>True when the EML mod <paramref name="id"/> ran this start (EML <c>loader.has_mod</c>).</summary>
    public static bool IsLoaded(string id) => Status?.Mods.Any(m => m.State == "loaded" && m.Id == id) ?? false;

    /// <summary>Reads a state file; null when it is missing or not format 1.</summary>
    public static EmlStatus? Load(string path)
    {
        try
        {
            var s = JsonSerializer.Deserialize<EmlStatus>(File.ReadAllText(path), Options);
            return s is { Format: 1 } ? s : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>kiln-eml's run (state/eml.json).</summary>
public sealed record EmlStatus
{
    public int Format { get; init; }
    [JsonPropertyName("kilnEml")] public string? KilnEmlVersion { get; init; }
    [JsonPropertyName("eml")] public string? EmlVersion { get; init; }
    /// <summary><c>done</c> (patched or unchanged since the last run), <c>failed</c> (game data restored to vanilla),
    /// <c>aside</c> (EML's own loader ran instead), <c>idle</c> (no EML mods).</summary>
    public string Phase { get; init; } = "";
    /// <summary>The game's data file: <c>enshrouded</c> or <c>enshrouded_server</c>.</summary>
    public string? File { get; init; }
    public int Patched { get; init; }
    public bool Cached { get; init; }
    public bool Restored { get; init; }
    public double Ms { get; init; }
    public string? Error { get; init; }
    public string? Aside { get; init; }
    public IReadOnlyList<EmlModStatus> Mods { get; init; } = [];
}

/// <summary>One EML mod folder kiln-eml looked at.</summary>
public sealed record EmlModStatus
{
    public string? Id { get; init; }
    public string? Version { get; init; }
    public string? Name { get; init; }
    /// <summary><c>profile</c>, <c>kiln-dropin</c> (kiln/eml/mods), <c>game-mods</c> (EML's mods folder), or the engine
    /// plugin that added it.</summary>
    public string Source { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary><c>loaded</c>, <c>failed</c>, <c>skipped</c> (not an EML mod), <c>disabled</c>, <c>duplicate</c>.</summary>
    public string State { get; init; } = "";
    public string? Reason { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
