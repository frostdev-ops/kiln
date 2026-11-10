// SPDX-License-Identifier: MIT
// Texts: the game's English texts by loca tag, on a server too.
//
// The game keeps its texts in one loca collection (keen::LocaTagCollectionResource: a language table per language, each a
// blob of LocaTagResource {tag id, text, ...}) that only the game CLIENT ships. A dedicated server's data has neither the
// resource nor its blobs (checked on 26430253: no LocaTagCollectionResource among its 43,958 resources, none of the client's
// loca content hashes in its directory; docs/re/world-status.md section 12), so a server cannot read them from its own game.
// What a server machine can have is Kiln's own catalog (`kiln catalog build`, docs/kiln/modding/catalogs.md): its `loca`
// section is the client's en-us table, made from the owner's installed game. Texts reads that, lazily and once.
//
// Bounded: the catalog file is read only when it is at most 512 MB, at most 2,000,000 texts are kept, and a text longer than
// 64 KiB is skipped. A missing, unreadable or malformed catalog is "no texts" with a reason (Status), never an exception.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>Where <see cref="Texts"/> got its table from, and how it went.</summary>
/// <param name="Loaded">A table is loaded (<see cref="Count"/> texts).</param>
/// <param name="Source">The catalog file the table came from, or null.</param>
/// <param name="Build">The game build the catalog was made from ("26430625 (svn 1287369)"), or null.</param>
/// <param name="Seconds">How long the load took.</param>
/// <param name="Reason">Why there is no table ("" when loaded).</param>
public sealed record TextsStatus(bool Loaded, string? Source, string? Build, int Count, double Seconds, string Reason)
{
    public override string ToString() => Loaded ? $"{Count} texts from {Source} (game build {Build}) in {Seconds:0.00} s" : $"no texts: {Reason}";
}

/// <summary>The game's English texts by loca tag. <c>Texts.Get(item.NameTag)</c>; see the file header for where they come
/// from. The first call loads the table (about a quarter of a second for the 17,000 texts; <see cref="Preload"/> does it in
/// the background). Any thread. While a load runs (a <see cref="Preload"/>, or a first call on another thread) the other
/// calls do not wait for it: they answer null (no table yet) until it is done. A catalog that gave no table (missing, no
/// <c>loca</c> section, broken) is looked at again after a minute, and read again only when a catalog file changed (a file that
/// could not be opened is tried again after a minute either way). <see cref="Status"/> says "still loading" meanwhile.</summary>
public static class Texts
{
    private const long MaxFileBytes = 512L << 20;
    private const int MaxTexts = 2_000_000, MaxTextLength = 1 << 16;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private static readonly object Lock = new();
    private static volatile Dictionary<uint, string>? _table;
    private static TextsStatus _status = new(false, null, null, 0, 0, "not loaded yet");
    private static long _failedAt;  // Stopwatch timestamp of the last failed load (or of the last check that found the catalog
                                    // files unchanged since), 0 when none
    private static string? _failedFiles;  // what the catalog files looked like at that failed load (see FileStamps)
    private static TaskCompletionSource<bool>? _inFlight;  // the load that runs now, outside the lock
    private static long _epoch;  // bumped by Reset: a load still running from before publishes nothing

    /// <summary>True when a table is loaded (this loads it on the first call).</summary>
    public static bool Available => Table() is not null;

    /// <summary>What is loaded, or why not (this loads the table on the first call).</summary>
    public static TextsStatus Status
    {
        get
        {
            Table();
            lock (Lock)
                return _table is null && _inFlight is not null
                    ? new TextsStatus(false, null, null, 0, 0, "still loading (Get answers null until the load is done)")
                    : _status;
        }
    }

    /// <summary>Loads the table on a background thread; completes with whether there is one. While it runs, <see cref="Get(uint)"/>
    /// answers null at once instead of waiting.</summary>
    public static Task<bool> Preload()
    {
        TaskCompletionSource<bool> mine;
        lock (Lock)
        {
            if (_table is not null) return Task.FromResult(true);
            if (_inFlight is { } running) return running.Task;
            if (!RetryDue()) return Task.FromResult(false);
            mine = _inFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Task.Run(() => Run(mine));
        return mine.Task;
    }

    /// <summary>The text of a loca tag id (the FNV-1a of the tag's GUID bytes), with the game's markup; null when unknown or
    /// when there is no table.</summary>
    public static string? Get(uint tag) => tag != 0 && Table() is { } t && t.TryGetValue(tag, out var s) ? s : null;

    /// <summary>The text a LocaTag reference names (<c>ItemInfo.name</c> and the like are such GUIDs).</summary>
    public static string? Get(Guid tag) => tag == Guid.Empty ? null : Get(TagOf(tag));

    /// <summary>A tag as <c>0x12345678</c>, as a decimal id, or as a GUID.</summary>
    public static string? Get(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var s = tag.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? Get(h) : null;
        if (Guid.TryParse(s, out var g)) return Get(g);
        return uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? Get(d) : null;
    }

    /// <summary>As <see cref="Get(uint)"/> with the game's markup tags (<c>&lt;b&gt;</c>, <c>&lt;lorename&gt;</c>, ...) removed.</summary>
    public static string? GetPlain(uint tag) => Plain(Get(tag));
    public static string? GetPlain(Guid tag) => Plain(Get(tag));
    public static string? GetPlain(string tag) => Plain(Get(tag));

    /// <summary>The tag id of a LocaTag reference: FNV-1a-32 of the GUID's 16 bytes in the game's order (.NET's
    /// <see cref="Guid.ToByteArray()"/>).</summary>
    public static uint TagOf(Guid tag)
    {
        Span<byte> b = stackalloc byte[16];
        tag.TryWriteBytes(b);
        var h = 0x811c9dc5u;
        foreach (var x in b) h = (h ^ x) * 0x01000193u;
        return h;
    }

    private static readonly Regex Markup = new("<[^<>]{1,64}>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? Plain(string? s) => s is null ? null : Markup.Replace(s, "");

    // ---- loading ------------------------------------------------------------------------------------------------

    private static Dictionary<uint, string>? Table()
    {
        var t = _table;
        if (t is not null) return t;
        TaskCompletionSource<bool> mine;
        lock (Lock)
        {
            if (_table is not null) return _table;
            if (_inFlight is not null) return null;  // another thread parses the catalog: never wait for it
            if (!RetryDue()) return null;
            mine = _inFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return Run(mine);
    }

    // Under Lock: whether a load may start. After a failure: not within RetryAfter, and after it only when the catalog files
    // changed (a server catalog without loca is not read again every minute); the check itself runs at most once a minute.
    private static bool RetryDue()
    {
        if (_failedAt == 0) return true;
        if (Stopwatch.GetElapsedTime(_failedAt) < RetryAfter) return false;
        if (_failedFiles is not null && FileStamps() == _failedFiles)
        {
            _failedAt = Stopwatch.GetTimestamp();
            return false;
        }
        return true;
    }

    // The single load: parses outside the lock, then publishes the table (or the failure) at once.
    private static Dictionary<uint, string>? Run(TaskCompletionSource<bool> mine)
    {
        Dictionary<uint, string>? table = null;
        long epoch;
        lock (Lock) epoch = _epoch;
        try
        {
            var stamps = FileStamps();
            var sw = Stopwatch.StartNew();
            Interlocked.Increment(ref LoadsForTests);
            BeforeLoadForTests?.Invoke();
            var (loaded, source, build, reason, transient) = Load();
            table = loaded;
            lock (Lock)
            {
                if (epoch != _epoch)
                {
                    // Reset ran meanwhile (tests): publish nothing
                }
                else if (table is null)
                {
                    _failedAt = Stopwatch.GetTimestamp();
                    _failedFiles = transient ? null : stamps;  // a read error may pass without the file changing: try again after RetryAfter
                    _status = new TextsStatus(false, source, build, 0, sw.Elapsed.TotalSeconds, reason);
                }
                else
                {
                    _table = table;
                    _failedAt = 0;
                    _failedFiles = null;
                    _status = new TextsStatus(true, source, build, table.Count, sw.Elapsed.TotalSeconds, "");
                }
            }
        }
        catch (Exception e)
        {
            table = null;
            lock (Lock)
            {
                if (epoch == _epoch)
                {
                    _failedAt = Stopwatch.GetTimestamp();
                    _failedFiles = null;  // not a file problem: try again after RetryAfter
                    _status = new TextsStatus(false, null, null, 0, 0, $"loading failed: {e.Message}");
                }
            }
        }
        finally
        {
            lock (Lock)
                if (ReferenceEquals(_inFlight, mine)) _inFlight = null;
        }
        mine.TrySetResult(table is not null);
        return table;
    }

    // Each candidate catalog file's size and write time, as one string (cheap: a stat per place, no read).
    private static string FileStamps()
    {
        var sb = new StringBuilder();
        foreach (var file in CatalogFiles())
        {
            sb.Append(file).Append('|');
            try
            {
                var info = new FileInfo(file);
                if (info.Exists) sb.Append(info.Length).Append('@').Append(info.LastWriteTimeUtc.Ticks);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                sb.Append('?');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Transient: the failure was reading the file (locked, vanished), so it may go away without the file changing.
    private static (Dictionary<uint, string>? Table, string? Source, string? Build, string Reason, bool Transient) Load()
    {
        var tried = new List<string>();
        foreach (var file in CatalogFiles())
        {
            tried.Add(file);
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                if (info.Length > MaxFileBytes) return (null, file, null, $"{file} is larger than 512 MB", false);
                var parsed = Parse(File.ReadAllBytes(file), out var build);
                if (parsed is null) return (null, file, build, $"{file} has no 'loca' section (a server catalog has none: use the client catalog, `kiln catalog build --side client`)", false);
                return (parsed, file, build, "", false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
            {
                return (null, file, null, $"{file}: {e.Message}", e is IOException or UnauthorizedAccessException);
            }
        }
        return (null, null, null, "no client catalog found (run `kiln catalog build --side client`, or set KILN_CATALOG to the folder or the client.json; " +
                                  $"looked at: {(tried.Count == 0 ? "nothing" : string.Join("; ", tried))})", false);
    }

    /// <summary>The <c>loca</c> section of a catalog's bytes, or null when it has none. <paramref name="build"/> is the game
    /// build the catalog records.</summary>
    internal static Dictionary<uint, string>? Parse(ReadOnlySpan<byte> json, out string? build)
    {
        build = null;
        var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new JsonException("not a catalog (no top-level object)");
        Dictionary<uint, string>? loca = null;
        string? steam = null, svn = null;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            var name = r.GetString();
            r.Read();
            if (name == "game" && r.TokenType == JsonTokenType.StartObject)
            {
                using var doc = JsonDocument.ParseValue(ref r);
                if (doc.RootElement.TryGetProperty("steamBuild", out var sb) && sb.ValueKind == JsonValueKind.String) steam = sb.GetString();
                if (doc.RootElement.TryGetProperty("svn", out var sv) && sv.ValueKind == JsonValueKind.String) svn = sv.GetString();
                build = steam is null ? svn : svn is null ? steam : $"{steam} (svn {svn})";
            }
            else if (name == "sections" && r.TokenType == JsonTokenType.StartObject)
            {
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    var section = r.GetString();
                    r.Read();
                    if (section == "loca" && r.TokenType == JsonTokenType.StartArray) loca = ReadLoca(ref r);
                    else r.Skip();
                }
            }
            else
            {
                r.Skip();
            }
        }
        if (r.TokenType != JsonTokenType.EndObject || r.Read()) throw new JsonException("invalid catalog root");
        return loca;
    }

    private static Dictionary<uint, string> ReadLoca(ref Utf8JsonReader r)
    {
        var table = new Dictionary<uint, string>(20_000);
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
            {
                r.Skip();
                continue;
            }
            uint id = 0;
            string? text = null;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var key = r.GetString();
                r.Read();
                if (key == "id")
                {
                    id = 0;
                    var s = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : "";
                    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
                    r.Skip();
                }
                else if (key == "text")
                {
                    text = r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= MaxTextLength * 3 ? r.GetString() : null;
                    r.Skip();
                }
                else
                {
                    r.Skip();
                }
            }
            if (id != 0 && text is { Length: <= MaxTextLength } && table.Count < MaxTexts) table[id] = text;
        }
        return table;
    }

    // ---- where the catalog is -------------------------------------------------------------------------------------

    /// <summary>Where a client catalog may be, in the order kiln-manage looks (docs/kiln/modding/catalogs.md) plus the Kiln folder
    /// of the server: <c>KILN_CATALOG</c> (a file or a folder), <c>&lt;kiln&gt;/catalog</c>, <c>KILN_DATA_DIR/catalog</c>,
    /// <c>XDG_DATA_HOME/kiln/catalog</c>, <c>~/.local/share/kiln/catalog</c>, <c>%LOCALAPPDATA%\Kiln\catalog</c>. Under Wine a
    /// Linux path is also tried through the Z: drive.</summary>
    internal static IEnumerable<string> CatalogFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Places())
        {
            var file = p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p : Path.Combine(p, "client.json");
            if (seen.Add(file)) yield return file;
        }
    }

    private static IEnumerable<string> Places()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        // Wine renames the variables it sets for itself (HOME, XDG_DATA_HOME) to WINE_HOST_<name> and gives the Linux home as
        // WINEHOMEDIR (\??\Z:\home\me)
        static IEnumerable<string> Host(string name) =>
            new[] { Env(name), Env("WINE_HOST_" + name) }.Where(v => v is not null).Select(v => v!);
        IEnumerable<string> WithZ(string path) => path.StartsWith('/') && OperatingSystem.IsWindows()
            ? [path, "Z:" + path.Replace('/', '\\')]
            : [path];

        if (Env("KILN_CATALOG") is { } c) foreach (var p in WithZ(c)) yield return p;
        foreach (var kiln in KilnFolders())
            yield return Path.Combine(kiln, "catalog");
        if (Env("KILN_DATA_DIR") is { } d) foreach (var p in WithZ(Path.Combine(d, "catalog"))) yield return p;
        foreach (var x in Host("XDG_DATA_HOME")) foreach (var p in WithZ(Path.Combine(x, "kiln", "catalog"))) yield return p;
        foreach (var h in Host("HOME")) foreach (var p in WithZ(Path.Combine(h, ".local", "share", "kiln", "catalog"))) yield return p;
        if (Env("WINEHOMEDIR") is { } w)
            yield return Path.Combine(w.StartsWith(@"\??\", StringComparison.Ordinal) ? w[4..] : w, ".local", "share", "kiln", "catalog");
        if (Env("LOCALAPPDATA") is { } l) yield return Path.Combine(l, "Kiln", "catalog");
    }

    private static IEnumerable<string> KilnFolders()
    {
        if (ModContext.CurrentMod?.DataDirectory is { Length: > 0 } data &&
            Path.GetDirectoryName(Path.GetDirectoryName(data.TrimEnd('/', '\\'))) is { Length: > 0 } fromData)
            yield return fromData;  // <kiln>/data/<mod id>
        string? host = null;
        try { host = Path.GetDirectoryName(typeof(Texts).Assembly.Location); } catch (NotSupportedException) { }
        if (!string.IsNullOrEmpty(host) && Path.GetDirectoryName(host) is { Length: > 0 } fromHost) yield return fromHost;  // <kiln>/host
        yield return Path.Combine(AppContext.BaseDirectory, "kiln");
        yield return AppContext.BaseDirectory;
    }

    /// <summary>Test seams: how many loads ran, and a call made on the loading thread before the catalog is read.</summary>
    internal static int LoadsForTests;
    internal static Action? BeforeLoadForTests;

    /// <summary>Test seam: forget the loaded table and any failure.</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            _table = null;
            _failedAt = 0;
            _failedFiles = null;
            _inFlight = null;
            _epoch++;
            _status = new TextsStatus(false, null, null, 0, 0, "not loaded yet");
        }
    }
}
