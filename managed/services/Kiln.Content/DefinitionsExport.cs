// SPDX-License-Identifier: MIT
using System.Text;

namespace Kiln;

/// <summary>
/// Writes every new-content definition (items and recipes of every mod, data pack and server sync, placeholders) to
/// <c>kiln/state/content-definitions.json</c> whenever the list changes, so tools outside the game (the Kiln
/// Launcher's save editor) can name and draw items defined in C# code. Format:
/// <c>{"format": 1, "written": "&lt;RFC 3339 UTC&gt;", "definitions": [ … ]}</c>, the array being exactly
/// <see cref="NewContent.DefinitionsJson"/> (kind, guid, id, name, base, mod, state, displayName, iconFile,
/// placeholder, definition: the normalized data-pack form). Replaced atomically (tmp + rename). Checked every 25 ticks
/// from the content service tick; costs one native call when nothing changed.
/// </summary>
internal static class DefinitionsExport
{
    private static string? _last;
    private static bool _warned;

    public static string FilePath => Path.Combine(KilnPaths.State, "content-definitions.json");

    public static void Tick()
    {
        if (!NewContent.Available) return;
        try
        {
            var json = NewContent.DefinitionsJson();
            if (json == _last) return;
            var file = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var text = new StringBuilder(json.Length + 96)
                .Append("{\"format\": 1, \"written\": \"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
                .Append("\", \"definitions\": ").Append(json).Append("}\n").ToString();
            File.WriteAllText(file + ".tmp", text, new UTF8Encoding(false));
            File.Move(file + ".tmp", file, overwrite: true);
            _last = json;
        }
        catch (Exception e)
        {
            if (_warned) return;
            _warned = true;
            try { Backend.Current.Log(0, LogLevel.Warn, $"Kiln.Content: could not write {FilePath}: {e.Message}"); } catch { }
        }
    }
}
