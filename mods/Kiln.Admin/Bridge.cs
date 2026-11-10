// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kiln.Admin;

/// <summary>The op table and its two transports' shared entry point, <see cref="Execute"/>.
/// <para>The file bridge for external admin tools: the tool writes
/// <c>requests/&lt;id&gt;.json</c> ({"op": ...}); each request runs once on the tick thread, is deleted, and
/// <c>results/&lt;id&gt;.json</c> gets {ok, op, ms, data | error}; <c>state.json</c> is refreshed about once a second.
/// Writes go through a temp file + rename so the dashboard never reads half a file.</para>
/// <para>KAP/1 (<see cref="TcpAdmin"/>) calls <see cref="Execute"/> with the same request objects, so every op answers
/// the same over both transports (docs/kiln/launcher/admin-protocol.md).</para></summary>
internal sealed partial class Bridge(string directory, Logger log)
{
    public delegate JsonNode? Op(JsonObject request);
    public delegate Task<JsonNode?> AsyncOp(JsonObject request);

    private sealed record Pending(string Name, Task<JsonNode?> Task, long Started, Action<JsonObject> Done);
    private readonly List<Pending> _pending = [];
    private readonly Dictionary<string, AsyncOp> _async = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly Dictionary<string, Op> _ops = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly string _requests = Path.Combine(directory, "requests");
    private readonly string _results = Path.Combine(directory, "results");

    public string Directory => directory;
    public IReadOnlyCollection<string> Ops => _ops.Keys;

    /// <summary>Op names in registration order (stable across transports and runs).</summary>
    public IReadOnlyList<string> OpNames => _order;

    public bool Has(string name) => _ops.ContainsKey(name);

    /// <summary>Ops still running (async ops awaiting their task).</summary>
    public int PendingCount => _pending.Count;

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}\\.json$")]
    private static partial Regex RequestName();

    public void Register(string name, Op op)
    {
        if (!_ops.ContainsKey(name)) _order.Add(name);
        _ops[name] = op;
    }

    /// <summary>An op that completes later on the tick thread (e.g. a teleport); its result is delivered when done.</summary>
    public void RegisterAsync(string name, AsyncOp op)
    {
        _async[name] = op;
        Register(name, _ => throw new InvalidOperationException("async op called synchronously"));
    }

    public void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(_requests);
        System.IO.Directory.CreateDirectory(_results);
    }

    /// <summary>Seconds since something else wrote this folder's state.json: a file newer than
    /// <paramref name="fresh"/> that is not Kiln.Admin's (its state always has "kiln"). Null when there is none. Asked
    /// before the first write of a load, so a state.json found then is not this instance's.</summary>
    public double? ForeignState(double fresh = 5)
    {
        try
        {
            var file = new FileInfo(Path.Combine(directory, "state.json"));
            if (!file.Exists) return null;
            var age = (DateTime.UtcNow - file.LastWriteTimeUtc).TotalSeconds;
            if (age > fresh) return null;
            return JsonNode.Parse(File.ReadAllText(file.FullName)) is JsonObject o && o.ContainsKey("kiln") ? null : Math.Max(0, age);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>File bridge: handles up to <paramref name="max"/> pending requests (oldest first).</summary>
    public int Poll(int max = 16)
    {
        FinishPending();
        if (!System.IO.Directory.Exists(_requests)) return 0;
        var files = new DirectoryInfo(_requests).EnumerateFiles("*.json")
            .Where(f => RequestName().IsMatch(f.Name))
            .OrderBy(f => f.LastWriteTimeUtc).Take(max).ToList();
        foreach (var f in files) Handle(f.FullName);
        return files.Count;
    }

    private void Handle(string file)
    {
        var id = Path.GetFileNameWithoutExtension(file);
        string raw;
        try
        {
            raw = File.ReadAllText(file);
            File.Delete(file);
        }
        catch (IOException)
        {
            return;  // still being written; next poll
        }
        var resultPath = Path.Combine(_results, id + ".json");
        JsonObject? request = null;
        try
        {
            request = JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException e)
        {
            WriteAtomic(resultPath, new JsonObject { ["ok"] = false, ["error"] = "bad request: " + e.Message }.ToJsonString(Json));
            return;
        }
        if (request is null)
        {
            WriteAtomic(resultPath, new JsonObject { ["ok"] = false }.ToJsonString(Json));
            return;
        }
        Execute(request, result => WriteAtomic(resultPath, result.ToJsonString(Json)));
    }

    /// <summary>Runs one request ({"op": name, ...args}) on the tick thread. <paramref name="done"/> gets the result
    /// object {op, data, ok, ms} | {op, ok, error, ms} (unknown op: {op, ok, error}): at once for a synchronous op,
    /// from a later <see cref="Poll"/>/<see cref="FinishPending"/> for an async one. Key order is the file bridge's
    /// (the file format is stable, byte for byte).</summary>
    public void Execute(JsonObject request, Action<JsonObject> done)
    {
        var name = request["op"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var result = new JsonObject { ["op"] = name };
        if (!_ops.TryGetValue(name, out var op))
        {
            result["ok"] = false;
            result["error"] = "unknown op " + name;
            Deliver(done, result, name);
            return;
        }
        if (_async.TryGetValue(name, out var asyncOp))
        {
            Task<JsonNode?> task;
            try
            {
                task = asyncOp(request);
            }
            catch (Exception e)
            {
                task = Task.FromException<JsonNode?>(e);
            }
            _pending.Add(new Pending(name, task, System.Diagnostics.Stopwatch.GetTimestamp(), done));
            FinishPending();
            return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            result["data"] = op(request);
            result["ok"] = true;
        }
        catch (AdminError e)
        {
            result["ok"] = false;
            result["error"] = e.Message;
        }
        catch (Exception e)
        {
            result["ok"] = false;
            result["error"] = e.Message;
            log.Error(e, $"op {name}");
        }
        result["ms"] = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (name != "ping") log.Info($"{name} {(result["ok"]!.GetValue<bool>() ? "ok" : "failed: " + result["error"])}");
        Deliver(done, result, name);
    }

    /// <summary>Delivers finished async ops (called every poll; KAP's drain calls it too).</summary>
    public void FinishPending()
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (!p.Task.IsCompleted) continue;
            _pending.RemoveAt(i);
            var result = new JsonObject { ["op"] = p.Name };
            if (p.Task.IsCompletedSuccessfully)
            {
                result["data"] = p.Task.Result;
                result["ok"] = true;
            }
            else
            {
                var e = p.Task.Exception?.GetBaseException();
                result["ok"] = false;
                result["error"] = e?.Message ?? "cancelled";
                if (e is not AdminError and not null) log.Error(e, $"op {p.Name}");
            }
            result["ms"] = (int)System.Diagnostics.Stopwatch.GetElapsedTime(p.Started).TotalMilliseconds;
            log.Info($"{p.Name} {(result["ok"]!.GetValue<bool>() ? "ok" : "failed: " + result["error"])}");
            Deliver(p.Done, result, p.Name);
        }
    }

    /// <summary>Unload: async ops that never finished are answered as failed (KAP clients get shutting_down from the
    /// listener anyway; file-bridge results are simply not written, as before).</summary>
    public void Abandon() => _pending.Clear();

    private void Deliver(Action<JsonObject> done, JsonObject result, string name)
    {
        try
        {
            done(result);
        }
        catch (Exception e)
        {
            log.Error(e, $"delivering the result of {name}");
        }
    }

    /// <summary>The state.json text (also pushed to KAP subscribers).</summary>
    public static string Serialize(JsonNode node) => node.ToJsonString(Json);

    public void WriteState(JsonObject state) => WriteStateText(Serialize(state));

    public void WriteStateText(string text) => WriteAtomic(Path.Combine(directory, "state.json"), text);

    internal static void WriteAtomic(string path, string text)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".tmp");
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>An expected failure: its message goes to the dashboard as-is, no stack trace in the log.</summary>
internal sealed class AdminError(string message) : Exception(message);
