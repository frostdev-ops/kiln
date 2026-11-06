// SPDX-License-Identifier: MIT
// Client side of a refusal: which mods the server refused this client for (from the REJECT's details), and the text
// Kiln.Ui shows for it. The game's own login error that follows ("An update of the game is available") names no mod.
using System.Text;
using System.Text.RegularExpressions;

namespace Kiln;

internal static partial class NetRefusal
{
    // The server writes one detail per missing mod as "<id> <version> (missing)" or "<id> <version> (client has <v>)"
    // (NetEngine.HandleHello). Channels ("channel <name> v<n> (...)"), the protocol and other reasons don't match: their
    // second word is no version, or their first is no mod id (kiln.mod.json's id rule).
    [GeneratedRegex(@"^(?<id>[a-z0-9][a-z0-9._-]{0,63}) (?<ver>v?[0-9][0-9A-Za-z.+-]*) \((?<state>missing|client has [^)]*)\)$")]
    private static partial Regex ModDetail();

    /// <summary>The mods a REJECT's details name, in the server's order, without duplicates.</summary>
    public static List<(string Id, string Version, string? ClientHas)> NamedMods(IReadOnlyList<string> details)
    {
        var list = new List<(string, string, string?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in details)
        {
            if (d is null || ModDetail().Match(d) is not { Success: true } m || !seen.Add(m.Groups["id"].Value)) continue;
            var state = m.Groups["state"].Value;
            list.Add((m.Groups["id"].Value, m.Groups["ver"].Value, state == "missing" ? null : state["client has ".Length..]));
        }
        return list;
    }

    /// <summary>last-server.json's <c>missing</c> for a refusal: the mods the server refused this client for. Under the
    /// default policy those are only mods that state <c>clients: "mod"</c>, under <c>refuse</c> every shared mod the
    /// client lacks; a required mod the server didn't name (say one whose <c>clients</c> only defaults from
    /// <c>side</c>) is not why it was refused. A REJECT without details (a client refused before its handshake was
    /// read, or one that didn't decode) names nothing: then every required id this client lacks.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<string> details, IReadOnlyList<string> lacking) =>
        details.Count == 0 ? lacking : NamedMods(details).Select(m => m.Id).ToList();

    /// <summary>The notice Kiln.Ui shows (a few short lines): who refused, the mods it named (else its reason), and
    /// where to get them.</summary>
    public static string Notice(string? serverName, string reason, IReadOnlyList<string> details, bool modpack)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(serverName) ? "The server refused this game." : $"{Clip(serverName.Trim(), 64)} refused this game.");
        var mods = NamedMods(details);
        if (mods.Count > 0)
        {
            sb.Append('\n').Append(mods.Count == 1 ? "It needs a mod you don't have:" : "It needs mods you don't have:");
            var shown = mods.Take(8).Select(m => m.ClientHas is { } has ? $"{m.Id} {m.Version} (you have {has})" : $"{m.Id} {m.Version}").ToList();
            if (mods.Count > shown.Count) shown.Add($"and {mods.Count - shown.Count} more");
            foreach (var line in Lines(shown, 80)) sb.Append("\n  ").Append(line);
            sb.Append('\n').Append(modpack ? "The Kiln launcher offers this server's modpack." : "Add them in the Kiln launcher, then join again.");
        }
        else
        {
            foreach (var line in Wrap(string.IsNullOrWhiteSpace(reason) ? "Refused by the server." : reason.Trim(), 80)) sb.Append('\n').Append(line);
        }
        return sb.ToString();
    }

    /// <summary>Items joined with ", ", a new line before one would pass <paramref name="width"/>.</summary>
    private static IEnumerable<string> Lines(IReadOnlyList<string> items, int width) =>
        Fill(items.Select((s, i) => i < items.Count - 1 ? s + "," : s).ToList(), width);

    /// <summary>Plain text in lines of at most <paramref name="width"/> (a longer word stays whole), at most 6 lines.</summary>
    private static IEnumerable<string> Wrap(string text, int width) => Fill(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), width).Take(6);

    private static IEnumerable<string> Fill(IReadOnlyList<string> words, int width)
    {
        var cur = new StringBuilder();
        foreach (var w in words)
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > width)
            {
                yield return cur.ToString();
                cur.Clear();
            }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(w);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
