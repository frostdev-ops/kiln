// SPDX-License-Identifier: MIT
// KilnBakeClean (Sdk/Kiln.Bake.targets): removes from a mod's output folder what Kiln.Sdk's bake wrote, by the files' own
// marker, never by a list: when the bake is turned off, and when a bake fails or is ended at its deadline (the bake process
// cannot clean up after itself then). The author's files stay, an author's file now at a path an earlier bake wrote
// included. docs/kiln/modding/baked.md.
using System.Text.Json;
using Microsoft.Build.Framework;

namespace Kiln.Sdk;

/// <summary>What the bake wrote into a mod folder, by its marker (the "comment" every baked file has).</summary>
public static class BakedOutputs
{
    /// <summary>The marker; the same text as the bake's (Kiln.Sdk.Bake Baker.Comment, a test keeps them equal).</summary>
    public const string Comment = "baked by the Kiln SDK at build from what the mod defines in code: docs/kiln/modding/baked.md";

    /// <summary>Removes every baked file of the three kinds from <paramref name="folder"/>: the mod's own
    /// (<paramref name="modId"/>) and those named for another id (a rename). Returns what was removed ('/', sorted).</summary>
    public static IReadOnlyList<string> Clean(string folder, string modId)
    {
        var removed = new List<string>();
        if (!Directory.Exists(folder)) return removed;
        foreach (var (dir, pattern) in new[] { ("quests/generated", "*.json"), ("skills", "*.generated.json"), ("blocks/generated", "*.json") })
        {
            var at = Path.Combine(folder, dir);
            if (!Directory.Exists(at)) continue;
            foreach (var file in Directory.GetFiles(at, pattern).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (!IsBaked(folder, relative)) continue;
                File.Delete(file);
                removed.Add(relative);
            }
        }
        _ = modId;  // every baked file there is this mod's or a renamed id's: all go
        return removed;
    }

    /// <summary>A file the bake wrote: its "comment" is the marker (read bounded, as the game reads baked files).</summary>
    public static bool IsBaked(string folder, string relative)
    {
        if (BakedFiles.Read(folder, relative) is not { } text) return false;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("comment", out var c) &&
                   c.ValueKind == JsonValueKind.String && c.GetString() == Comment;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Removes what Kiln.Sdk's bake wrote from a mod's output folder (by marker; the author's files stay).</summary>
public sealed class KilnBakeClean : Microsoft.Build.Utilities.Task
{
    [Required] public string Folder { get; set; } = "";
    [Required] public string ModId { get; set; } = "";
    /// <summary>Why it cleans, for the build line ("KilnBake is off", "the bake failed").</summary>
    public string Reason { get; set; } = "";
    [Output] public string[] Removed { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            Removed = BakedOutputs.Clean(Folder, ModId).ToArray();
            if (Removed.Length > 0)
                Log.LogMessage(MessageImportance.High, $"{(Reason.Length > 0 ? Reason + ": " : "")}removed what the bake wrote: {string.Join(", ", Removed)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(null, "KILN030", null, Folder, 0, 0, 0, 0, $"KilnBake: what the bake wrote could not all be removed: {e.Message}");
        }
        return true;
    }
}
