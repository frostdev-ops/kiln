// SPDX-License-Identifier: MIT
// A ShroudForge mod's news in the Kiln menu when Kiln hosts ShroudForge (the Legacy branch: hosted or installed).
//
// Why: a ShroudForge Lua mod has no way to draw on the game's HUD. What it has to tell the player goes to
// shroudforge.notifications.publish, which ShroudForge's own Modloader window shows in its news feed, and a mod that wants
// the player to read it now calls shroudforge.ui.toggle_modloader() to open that window. Kiln's bundled runtime has no
// such window: the toggle opens Kiln's ShroudForge page, which until now showed none of the news. netjunk, 2026-11-26:
// Altar Cleanup's "Ask before cleaning up" (Nexus 417) scans, publishes what it found and "press Keypad 1 again within 60
// seconds to clean up", and opens the window; under Kiln the page opened with no question on it.
//
// Where ShroudForge keeps them (fork pin 6cf01443): notifications.publish (mod-runtime create_mod_api.rs) checks the
// notice and calls shroudforge_news::write_event(game, "<mod>-<id>", payload) (notifications/write_notification_event.rs),
// which puts the payload under the state file's "events" section as "<mod>-<id>.json": {id: "<mod>.<id>", modId, title,
// message, level, actionUrl, updatedAt (Unix seconds)}. The state file is <state>/state.json (loader-config
// resolve_installation_paths.rs state_file; <state> is ShroudForge's paths.state, by default <game>/shroudforge), the file
// ModCatalog already reads for the runtime's per-mod state. A later publish with the same id replaces the entry.
//
// Threads: Read does the file IO (a worker, or the menu thread for a mod's toggle). The render thread asks Cached, which
// starts a read on a worker at most once a second and returns what the last one found.
//
// ShroudForge writes the file as a flushed temp file renamed over state.json (loader-config write_json_atomically.rs),
// retrying a refused rename for about 150 ms only; its runtime status, assets record and every notice go through it. So
// Kiln opens it as it opens every file another process replaces (read, write and delete sharing, as ModCatalog.ReadJson),
// holds it only while it copies the bytes, and opens it only when its size or time changed since the last read.
using System.Text.Json;

namespace Kiln;

/// <summary>One notice a ShroudForge mod published, as its runtime keeps it.</summary>
internal sealed record SfNews(string Id, string ModId, string Title, string Message, string Level, long UpdatedAt);

internal static class ShroudForgeNews
{
    /// <summary>The most notices kept from one read (newest first) and the largest state file read (ModCatalog reads the
    /// same file with the same limit).</summary>
    internal const int MaxNotices = 512;
    internal const long MaxStateBytes = 8L * 1024 * 1024;

    /// <summary>ShroudForge's state file (tests point it elsewhere). Asked only for a hosted or installed start's rows.</summary>
    internal static Func<string> StatePath = () => Path.Combine(ShroudForgePackages.Folder(ShroudForgeUi.GameDir(), "state"), "state.json");

    private static volatile IReadOnlyList<SfNews> _cached = [];
    private static long _readAtMs = long.MinValue / 2;
    private static int _reading;

    // what the last Reload found, with the file's size and time then: the same size and time are not read again
    private sealed record Seen(string Path, long Length, long WrittenTicks, IReadOnlyList<SfNews> News);
    private static volatile Seen? _seen;

    /// <summary>The notices in the state file at <paramref name="path"/>, newest first; none when it is missing, too big or
    /// not ShroudForge's. An entry that is not what notifications.publish writes is left out, never changed.</summary>
    public static IReadOnlyList<SfNews> Read(string path) => TryRead(path, out var news) ? news : [];

    /// <summary><see cref="Read"/>; false when the file could not be read just now (an IO error, or it grew past the limit
    /// while read), which the next look tries again. Missing, too big or not JSON reads as no news.</summary>
    private static bool TryRead(string path, out IReadOnlyList<SfNews> news)
    {
        news = [];
        try
        {
            var info = new FileInfo(path);
            // a FIFO or a socket has no length: opening one could block this thread
            if (!info.Exists || info.Length <= 0 || info.Length > MaxStateBytes || info.LinkTarget is not null) return true;
            if (Bytes(path) is not { } bytes) return false;
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("events", out var events) ||
                events.ValueKind != JsonValueKind.Object) return true;
            var list = new List<SfNews>();
            foreach (var e in events.EnumerateObject())
                if (Entry(e.Value) is { } n) list.Add(n);
            news = list.OrderByDescending(n => n.UpdatedAt).ThenBy(n => n.Id, StringComparer.Ordinal).Take(MaxNotices).ToList();
            return true;
        }
        catch (JsonException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>The file's bytes, opened with read, write and delete sharing so that ShroudForge's rename over it never
    /// fails because Kiln has it open; at most <see cref="MaxStateBytes"/> however much it grows meanwhile (null then).</summary>
    private static ReadOnlyMemory<byte>? Bytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var buf = new byte[(int)Math.Min(fs.Length, MaxStateBytes) + 1];
        var n = 0;
        for (int r; n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0;) n += r;
        return n > MaxStateBytes ? null : buf.AsMemory(0, n);
    }

    private static SfNews? Entry(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return null;
        try { return EntryOf(v); }
        catch (InvalidOperationException) { return null; }  // a string that is not UTF-8 (a damaged file): this entry only
    }

    private static SfNews? EntryOf(JsonElement v)
    {
        static string? S(JsonElement o, string k, int max) =>
            o.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String && x.GetString() is { } s && s.Length <= max ? s : null;
        if (S(v, "id", 200) is not { Length: > 0 } id || S(v, "modId", 80) is not { Length: > 0 } mod || S(v, "title", 120) is not { } title ||
            S(v, "message", 2000) is not { } message) return null;
        // ShroudForge's own record of a mod installed, updated or removed (its Modloader window's sync_mod_events ->
        // write_event_notice): no words, a kind that window words; Kiln has none to show
        if (title.Length == 0 && message.Length == 0) return null;
        if (!v.TryGetProperty("updatedAt", out var at) || at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out var seconds) || seconds < 0)
            return null;
        return new SfNews(id, mod, title, message, S(v, "level", 16) ?? "info", seconds);
    }

    /// <summary>The notices of mod <paramref name="modId"/>, in the order given.</summary>
    public static IReadOnlyList<SfNews> ForMod(IReadOnlyList<SfNews> news, string modId) =>
        news.Where(n => string.Equals(n.ModId, modId, StringComparison.Ordinal)).ToList();

    /// <summary>The mod whose notice is the newest one dated from <paramref name="sinceSeconds"/> to a little after
    /// <paramref name="nowSeconds"/> (the runtime's clock and ours may differ by a second); null when none is.</summary>
    public static string? Fresh(IReadOnlyList<SfNews> news, long nowSeconds, long sinceSeconds) =>
        news.Where(n => n.UpdatedAt >= sinceSeconds && n.UpdatedAt <= nowSeconds + 2).MaxBy(n => n.UpdatedAt)?.ModId;

    /// <summary>The last read's notices (render thread: no file access); starts a read on a worker when the last one is
    /// older than a second.</summary>
    public static IReadOnlyList<SfNews> Cached()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _readAtMs) > 1000 && Interlocked.CompareExchange(ref _reading, 1, 0) == 0)
            Task.Run(() =>
            {
                try { Reload(); }
                finally { Volatile.Write(ref _reading, 0); }
            });
        return _cached;
    }

    /// <summary>Reads the state file now, unless its size and time are those of the last read, and keeps what it holds for
    /// <see cref="Cached"/> (a worker, the menu thread, a test). A missing, damaged or unreadable file is no news, never an
    /// exception: the menu thread that calls it holds F9 and toggle_modloader().</summary>
    public static IReadOnlyList<SfNews> Reload()
    {
        IReadOnlyList<SfNews> news;
        try
        {
            var path = StatePath();
            var info = new FileInfo(path);
            long length = info.Exists ? info.Length : -1, written = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
            var last = _seen;
            if (last is not null && last.Path == path && last.Length == length && last.WrittenTicks == written) news = last.News;
            else if (length < 0)
            {
                news = [];
                _seen = new Seen(path, length, written, news);
            }
            // the size and time come first: a write after them makes the next look differ, so none is missed; a read that
            // failed just now is not remembered (the next look reads again), and what the last one found stays meanwhile
            else if (TryRead(path, out news)) _seen = new Seen(path, length, written, news);
            else news = last is not null && last.Path == path ? last.News : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or
                                       InvalidOperationException or SfEditException)
        {
            news = [];
        }
        _cached = news;
        Interlocked.Exchange(ref _readAtMs, Environment.TickCount64);
        return news;
    }

    internal static void ResetForTests()
    {
        _cached = [];
        _seen = null;
        Interlocked.Exchange(ref _readAtMs, long.MinValue / 2);
        StatePath = () => Path.Combine(ShroudForgePackages.Folder(ShroudForgeUi.GameDir(), "state"), "state.json");
    }
}

/// <summary>A hosted mod's new notices as Kiln's toasts, over the game: a ShroudForge mod cannot draw on the HUD, and
/// what it publishes is what it has to tell the player now (Altar Cleanup: its scan's result and "press the cleanup key
/// again within 60 seconds"). Only while Kiln answers ShroudForge's menu (no ShroudForge window runs to show its feed).
/// A notice toasts once when it comes after this game started, and again when its mod publishes it anew, while there is
/// room on the screen for it. Not thread-safe: one poller (the ShroudForge menu thread).</summary>
internal sealed class SfNewsToasts(long startSeconds)
{
    /// <summary>The most notice toasts on the screen at once (the newest notices get the room). kiln.ui keeps 8 toasts and
    /// drops the oldest: a burst, or a mod that publishes again and again (a counter, a status), must not push Kiln's own
    /// messages off the screen. A notice that finds no room is in the mod's details only.</summary>
    internal const int MaxOnScreen = 3;
    /// <summary>How long a toast stays: long enough to read a question with a time limit.</summary>
    internal const double Seconds = 10;
    /// <summary>A toast's lines and their width in columns (a wide CJK character counts two). kiln.ui's toasts do not wrap,
    /// and a notice may hold 2,000 characters; the mod's details show it whole.</summary>
    internal const int MaxLines = 4, LineColumns = 96;

    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    // when each toast still on the screen was made (the poller's clock, seconds)
    private readonly Queue<double> _shown = new();

    /// <summary>The notices of <paramref name="news"/> not toasted yet that fit on the screen at
    /// <paramref name="nowSeconds"/> (a steady clock), newest first. The rest count as seen.</summary>
    public IReadOnlyList<SfNews> Poll(IReadOnlyList<SfNews> news, double nowSeconds)
    {
        while (_shown.Count > 0 && nowSeconds - _shown.Peek() >= Seconds) _shown.Dequeue();
        var fresh = new List<SfNews>();
        foreach (var n in news.OrderByDescending(n => n.UpdatedAt))
        {
            if (n.UpdatedAt < startSeconds || _seen.TryGetValue(n.Id, out var at) && at >= n.UpdatedAt) continue;
            _seen[n.Id] = n.UpdatedAt;
            if (_shown.Count >= MaxOnScreen) continue;
            _shown.Enqueue(nowSeconds);
            fresh.Add(n);
        }
        return fresh;
    }

    /// <summary>A notice's toast: "title: message" (either alone when the other is empty) as plain words in at most
    /// <see cref="MaxLines"/> lines, in its level's look.</summary>
    public static (string Text, ToastKind Kind) Text(SfNews n)
    {
        string title = Plain(n.Title), message = Plain(n.Message);
        var text = title.Length == 0 ? message : message.Length == 0 ? title : title + ": " + message;
        var kind = n.Level switch
        {
            "warn" or "warning" => ToastKind.Warning,
            "error" => ToastKind.Error,
            "success" => ToastKind.Success,
            _ => ToastKind.Info,
        };
        return (Wrap(text, LineColumns, MaxLines), kind);
    }

    /// <summary>The mod's text as words: each run of control characters (line breaks and tabs too) and spaces is one
    /// space, and invisible format characters (bidi overrides, zero-width marks) are left out.</summary>
    internal static string Plain(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format) sb.Append(c);
        }
        return sb.ToString().TrimEnd(' ');
    }

    /// <summary><paramref name="s"/> (plain words) in lines of at most <paramref name="columns"/>, broken at spaces where it
    /// can be (a longer word, or text without spaces, at the line's end); past <paramref name="maxLines"/> the last line
    /// ends with "...".</summary>
    internal static string Wrap(string s, int columns, int maxLines)
    {
        var lines = new List<string>();
        var i = 0;
        while (i < s.Length && lines.Count < maxLines)
        {
            int j = i, width = 0, space = -1;
            while (j < s.Length && width + Columns(s[j]) <= columns)
            {
                if (s[j] == ' ') space = j;
                width += Columns(s[j++]);
            }
            if (j < s.Length && s[j] != ' ' && space > i) j = space;
            if (j < s.Length && j > i + 1 && char.IsHighSurrogate(s[j - 1])) j--;
            if (j == i) j++;  // a character wider than a line: alone on it
            lines.Add(s[i..j].TrimEnd(' '));
            for (i = j; i < s.Length && s[i] == ' '; i++) { }
        }
        if (i < s.Length && lines.Count > 0)
        {
            var last = lines[^1];
            while (last.Length > 0 && Width(last) + 3 > columns)
                last = last[..(last.Length >= 2 && char.IsLowSurrogate(last[^1]) ? last.Length - 2 : last.Length - 1)];
            lines[^1] = last.TrimEnd(' ') + "...";
        }
        return string.Join('\n', lines);
    }

    // a character's width in columns: two for the wide East Asian ranges (and a pair's high half), none for its low half
    private static int Columns(char c) => c switch
    {
        >= '\u1100' and <= '\u115F' or >= '\u2E80' and <= '\uA4CF' or >= '\uAC00' and <= '\uD7A3' or >= '\uF900' and <= '\uFAFF' or
            >= '\uFE30' and <= '\uFE4F' or >= '\uFF00' and <= '\uFF60' or >= '\uFFE0' and <= '\uFFE6' => 2,
        _ when char.IsHighSurrogate(c) => 2,
        _ when char.IsLowSurrogate(c) => 0,
        _ => 1,
    };

    private static int Width(string s)
    {
        var w = 0;
        foreach (var c in s) w += Columns(c);
        return w;
    }
}
