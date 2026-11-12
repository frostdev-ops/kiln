// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln.Testing;

/// <summary>A check as the ledger's catalogue lists it.</summary>
internal sealed record LedgerCheck
{
    [JsonPropertyName("mod")] public string Mod { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("area")] public string Area { get; init; } = "";
    [JsonPropertyName("since")] public string? Since { get; init; }
    [JsonPropertyName("tags")] public string[] Tags { get; init; } = [];
    [JsonPropertyName("needs")] public string Needs { get; init; } = "any";
    [JsonPropertyName("order")] public int Order { get; init; }

    public static LedgerCheck Of(CheckCase c) => new()
    {
        Mod = c.Mod, Id = c.Id, Title = c.Title, Area = c.Area, Since = c.Since, Tags = c.Tags.ToArray(), Needs = NeedsName(c.Needs),
        Order = c.Order,
    };

    public static string NeedsName(ClientCheckNeeds n) => n switch
    {
        ClientCheckNeeds.Hosted => "hosted",
        ClientCheckNeeds.Joined => "joined",
        _ => "any",
    };
}

internal sealed record LedgerGame
{
    [JsonPropertyName("side")] public string Side { get; init; } = "server";
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("verified")] public bool Verified { get; init; }
}

internal sealed record LedgerAssertion
{
    [JsonPropertyName("what")] public string What { get; init; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

internal sealed record LedgerGuard
{
    [JsonPropertyName("hookInstalled")] public bool HookInstalled { get; init; }
    [JsonPropertyName("targetVerified")] public bool TargetVerified { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("dropped")] public long Dropped { get; init; }
    [JsonPropertyName("passed")] public long Passed { get; init; }

    public static LedgerGuard Of(GuardStatus s) => new()
    {
        HookInstalled = s.HookInstalled, TargetVerified = s.TargetVerified, Active = s.Active, Dropped = Math.Max(0, s.Dropped),
        Passed = Math.Max(0, s.Passed),
    };
}

/// <summary>One verdict.</summary>
internal sealed record LedgerEntry
{
    [JsonPropertyName("mod")] public string Mod { get; init; } = "";
    [JsonPropertyName("modVersion")] public string ModVersion { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("area")] public string Area { get; init; } = "";
    [JsonPropertyName("since")] public string? Since { get; init; }
    [JsonPropertyName("tags")] public string[] Tags { get; init; } = [];
    [JsonPropertyName("kiln")] public string Kiln { get; init; } = "";
    [JsonPropertyName("game")] public LedgerGame Game { get; init; } = new();
    [JsonPropertyName("session")] public string Session { get; init; } = "server";
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "skip";
    [JsonPropertyName("note")] public string Note { get; init; } = "";
    [JsonPropertyName("time")] public string Time { get; init; } = "";
    [JsonPropertyName("assertions")] public LedgerAssertion[] Assertions { get; init; } = [];
    [JsonPropertyName("log")] public string[] Log { get; init; } = [];
    [JsonPropertyName("logDropped"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int LogDropped { get; init; }
    [JsonPropertyName("guard"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public LedgerGuard? Guard { get; init; }

    public bool Has(string tag) => Tags.Contains(tag, StringComparer.Ordinal);

    public static string SessionName(ClientCheckSession s) => s switch
    {
        ClientCheckSession.Hosted => "hosted",
        ClientCheckSession.Joined => "joined",
        _ => "server",
    };

    public static string VerdictName(ClientCheckVerdict v) => v switch
    {
        ClientCheckVerdict.Pass => "pass",
        ClientCheckVerdict.Fail => "fail",
        _ => "skip",
    };
}

internal sealed record LedgerDocument
{
    [JsonPropertyName("format")] public int Format { get; init; } = 1;
    [JsonPropertyName("checks")] public List<LedgerCheck> Checks { get; init; } = new();
    [JsonPropertyName("entries")] public List<LedgerEntry> Entries { get; init; } = new();
}

/// <summary>What reading the ledger file gave.</summary>
internal enum LedgerReadState
{
    /// <summary>Read and understood.</summary>
    Ok,
    /// <summary>No file yet.</summary>
    Missing,
    /// <summary>There, but not read (an IO error) or not understood (not JSON, another format).</summary>
    Unreadable,
}

internal readonly record struct LedgerRead(LedgerReadState State, LedgerDocument? Document, string? Error);

/// <summary>
/// <c>kiln/state/client-checks.json</c>: the catalogue of checks seen and one entry per verdict. Every write puts the
/// whole document into a temporary file, flushes it to disk and renames it over the old one, so a crash leaves either
/// the old ledger or the new one, never a part. Entries only grow. A file that cannot be read is moved aside with the
/// time in its name and a new ledger is started; it is never overwritten. Thread-safe (one lock).
/// </summary>
internal sealed class Ledger
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private LedgerDocument _doc = new();
    private bool _loaded;

    public Ledger(string path) => Path = path;

    public string Path { get; }

    /// <summary>For tests: runs after the temporary file is flushed and before the rename (a throw there is a crash
    /// between the two).</summary>
    internal Action<string>? BeforeRename;

    /// <summary>The last write's error, null after a good write.</summary>
    public string? WriteError { get; private set; }

    /// <summary>Where an unreadable ledger was moved at load, if one was.</summary>
    public string? MovedAside { get; private set; }

    /// <summary>Reads the file as it is on disk now (for the <c>cheats</c> rule: never the copy in memory).</summary>
    public LedgerRead ReadFile()
    {
        try
        {
            if (!File.Exists(Path)) return new LedgerRead(LedgerReadState.Missing, null, null);
            var bytes = File.ReadAllBytes(Path);
            return Parse(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LedgerRead(LedgerReadState.Unreadable, null, e.Message);
        }
    }

    internal static LedgerRead Parse(byte[] bytes)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<LedgerDocument>(bytes, Json);
            if (doc is null || doc.Format != 1 || doc.Checks is null || doc.Entries is null || doc.Entries.Any(e => e is null) ||
                doc.Checks.Any(c => c is null))
                return new LedgerRead(LedgerReadState.Unreadable, null, "not a format 1 check ledger");
            return new LedgerRead(LedgerReadState.Ok, doc, null);
        }
        catch (JsonException e)
        {
            return new LedgerRead(LedgerReadState.Unreadable, null, e.Message);
        }
    }

    /// <summary>Loads the file into memory once. An unreadable one is moved aside (never overwritten) and a new ledger
    /// starts. Returns a line for the log when something was moved or could not be.</summary>
    public string? Load()
    {
        lock (_lock)
        {
            if (_loaded) return null;
            _loaded = true;
            var r = ReadFile();
            if (r.State == LedgerReadState.Ok)
            {
                _doc = r.Document!;
                return null;
            }
            if (r.State == LedgerReadState.Missing) return null;
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            var stem = System.IO.Path.GetFileNameWithoutExtension(Path);
            for (var i = 0; i < 1000; i++)
            {
                var aside = System.IO.Path.Combine(dir, $"{stem}.unreadable-{stamp}{(i == 0 ? "" : "-" + i)}.json");
                if (File.Exists(aside)) continue;
                try
                {
                    File.Move(Path, aside, overwrite: false);
                    MovedAside = aside;
                    return $"the check ledger could not be read ({r.Error}); moved to {System.IO.Path.GetFileName(aside)}, a new one starts";
                }
                catch (IOException) when (File.Exists(aside))
                {
                    // taken in between: the next name
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // cannot move it: keep it untouched and write nothing over it
                    _loaded = false;
                    _blocked = $"the check ledger could not be read ({r.Error}) nor moved aside ({e.Message}); verdicts are not written";
                    return _blocked;
                }
            }
            _loaded = false;
            _blocked = "the check ledger could not be read and no free name was found to move it aside";
            return _blocked;
        }
    }

    private string? _blocked;

    /// <summary>A copy of the document in memory.</summary>
    public LedgerDocument Snapshot()
    {
        lock (_lock) return new LedgerDocument { Checks = _doc.Checks.ToList(), Entries = _doc.Entries.ToList() };
    }

    /// <summary>The newest entry of each check (mod/id).</summary>
    public Dictionary<string, LedgerEntry> Newest()
    {
        lock (_lock)
        {
            var d = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
            foreach (var e in _doc.Entries) d[e.Mod + "/" + e.Id] = e;
            return d;
        }
    }

    /// <summary>Merges <paramref name="checks"/> into the catalogue (by mod and id); true when it changed.</summary>
    public bool MergeCatalogue(IEnumerable<LedgerCheck> checks)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var c in checks)
            {
                var i = _doc.Checks.FindIndex(x => x.Mod == c.Mod && x.Id == c.Id);
                if (i < 0)
                {
                    _doc.Checks.Add(c);
                    changed = true;
                }
                else if (!Same(_doc.Checks[i], c))
                {
                    _doc.Checks[i] = c;
                    changed = true;
                }
            }
            return changed;
        }
    }

    private static bool Same(LedgerCheck a, LedgerCheck b) =>
        a.Title == b.Title && a.Area == b.Area && a.Since == b.Since && a.Needs == b.Needs && a.Order == b.Order && a.Tags.SequenceEqual(b.Tags);

    /// <summary>Adds <paramref name="entry"/> and writes the ledger. False when the write failed (<see cref="WriteError"/>);
    /// the entry stays in memory and goes out with the next write.</summary>
    public bool Append(LedgerEntry entry)
    {
        lock (_lock)
        {
            _doc.Entries.Add(entry);
            return WriteLocked();
        }
    }

    /// <summary>Writes the document as it is in memory.</summary>
    public bool Write()
    {
        lock (_lock) return WriteLocked();
    }

    private bool WriteLocked()
    {
        if (!_loaded)
        {
            Load();
            if (!_loaded)
            {
                WriteError = _blocked ?? "the ledger was not loaded";
                return false;
            }
        }
        var tmp = Path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_doc, Json);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            BeforeRename?.Invoke(tmp);
            File.Move(tmp, Path, overwrite: true);
            WriteError = null;
            return true;
        }
        catch (Exception e)
        {
            WriteError = e.Message;
            return false;
        }
    }
}
