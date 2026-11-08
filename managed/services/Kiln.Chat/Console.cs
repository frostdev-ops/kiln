// SPDX-License-Identifier: MIT
// The console folder: kiln/control/console/<name>.cmd -> <name>.result.json (kilnctl cmd, scripts, the dashboard).
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>
/// Runs command lines dropped into <c>kiln/control/console/*.cmd</c> and writes <c>&lt;name&gt;.result.json</c> next
/// to them (looked at about 4 times a second, by the clock). The lines run as the console; on a game that hosts its
/// own world, once its local player is in the world, as the console acting as that player
/// (<see cref="CommandSource.IsHostConsole"/>), so commands that need a player work there. One command per line;
/// empty lines and lines starting with <c>//</c> are skipped. Directives (lines starting with <c>#</c>) change the
/// source of the following lines: <c>#as console</c> (the plain console, also on a host),
/// <c>#as remote &lt;name&gt; [admin|user]</c>, <c>#as steam &lt;SteamID64&gt; [name]</c>,
/// <c>#as player &lt;name|entity&gt;</c>; <c>#complete &lt;text&gt;</c> returns completions instead of running
/// anything. A command whose handler is still running (an async one) gets its final result in the file when it ends,
/// or after <see cref="WaitForRunning"/> with <c>"running": true</c>.
/// </summary>
internal static class ConsoleControl
{
    // By the clock, not the step count: the folder check and listing are file-system (wineserver) calls, and a 60 Hz
    // hosted world ran them 10 times a second at every 6 steps.
    private const long PollEveryMs = 250;
    private static long _nextPoll;
    private const int MaxFiles = 8, MaxLines = 64;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>How long a result file waits for commands still running.</summary>
    internal static TimeSpan WaitForRunning = TimeSpan.FromSeconds(10);

    /// <summary>The local player of a game that hosts the world running here; none on a dedicated server, on a client
    /// that joined another machine, and before the player exists. Tests replace it.</summary>
    internal static Func<EntityId> LocalHost = ReadLocalHost;

    private sealed record Waiting(string Target, JsonObject Doc, List<(JsonObject Entry, Task<CommandResult> Done)> Running, DateTime Until);
    private static readonly List<Waiting> Waits = new();

    internal static void ResetForTests()
    {
        Waits.Clear();
        LocalHost = ReadLocalHost;
        WaitForRunning = TimeSpan.FromSeconds(10);
    }

    static ConsoleControl()
    {
        ServiceTick.Add("kiln console", OnTick);
    }

    public static string Directory => Path.Combine(KilnPaths.Control, "console");

    private static bool _announced;

    private static void OnTick(in TickInfo tick)
    {
        var now = Environment.TickCount64;
        if (now < _nextPoll) return;
        _nextPoll = now + PollEveryMs;
        Poll(Directory);
    }

    /// <summary>Handles the waiting files in <paramref name="dir"/>; returns how many. Tick thread.</summary>
    internal static int Poll(string dir)
    {
        if (!System.IO.Directory.Exists(dir))
        {
            if (!System.IO.Directory.Exists(Path.GetDirectoryName(dir))) return 0;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            catch (IOException)
            {
                return 0;
            }
        }
        if (!_announced)
        {
            _announced = true;
            Backend.Current.Log(0, LogLevel.Info, $"kiln.chat: console folder {dir} (<name>.cmd -> <name>.result.json)");
        }
        Flush();
        var done = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.cmd").Order(StringComparer.Ordinal).Take(MaxFiles).ToList())
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
                File.Delete(file);
            }
            catch (IOException)
            {
                continue;  // still being written: next time
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            var result = Run(Path.GetFileName(file), text, out var running);
            var target = Path.ChangeExtension(file, ".result.json");
            if (running.Count > 0) Waits.Add(new Waiting(target, result, running, DateTime.UtcNow + WaitForRunning));
            else Write(target, result);
            done++;
        }
        Flush();
        return done;
    }

    // Result files whose commands were still running: written once those have ended, or when the wait is over.
    private static void Flush()
    {
        for (var i = 0; i < Waits.Count; i++)
        {
            var w = Waits[i];
            var ended = w.Running.All(r => r.Done.IsCompleted);
            if (!ended && DateTime.UtcNow < w.Until) continue;
            var allOk = true;
            foreach (var (entry, task) in w.Running)
            {
                if (!task.IsCompletedSuccessfully)
                {
                    entry["running"] = true;
                    continue;
                }
                var r = task.Result;
                entry["ok"] = r.Ok;
                entry["error"] = r.Error;
                entry["output"] = new JsonArray([.. r.Output.Select(o => (JsonNode)o)]);
            }
            foreach (var entry in w.Doc["results"]!.AsArray())
                if (entry?["ok"]?.GetValue<bool>() == false) allOk = false;
            w.Doc["ok"] = allOk;
            Write(w.Target, w.Doc);
            Waits.RemoveAt(i--);
        }
    }

    private static void Write(string target, JsonObject doc)
    {
        try
        {
            File.WriteAllText(target + ".tmp", doc.ToJsonString(Indented));
            File.Move(target + ".tmp", target, overwrite: true);
        }
        catch (IOException e)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"kiln.chat: console: cannot write {target}: {e.Message}");
        }
    }

    /// <summary>Who a file's lines run as until a <c>#as</c> line: on a hosting game whose local player is in the world,
    /// the console acting as that player; else the console.</summary>
    internal static CommandSource DefaultSource()
    {
        var host = LocalHost();
        if (host.IsNone) return CommandSource.Console;
        var player = Arg.OnlinePlayers().FirstOrDefault(p => p.Entity == host);
        // in the world: the roster has them in game, or the world knows where they are
        if (player is { InGame: false } || (player is null && Arg.PositionOf(host) is null)) return CommandSource.Console;
        return CommandSource.HostConsole(host, player?.Name);
    }

    private static EntityId ReadLocalHost()
    {
        try
        {
            return HostedLocalPlayer();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return EntityId.None;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static EntityId HostedLocalPlayer() => Client.IsClient && Client.IsHosting ? Client.LocalPlayer : EntityId.None;

    /// <summary>Runs the lines of one file; the result document.</summary>
    internal static JsonObject Run(string name, string text) => Run(name, text, out _);

    /// <summary>Runs the lines of one file; the result document, and the entries of commands still running.</summary>
    internal static JsonObject Run(string name, string text, out List<(JsonObject Entry, Task<CommandResult> Done)> running)
    {
        running = new();
        var results = new JsonArray();
        var source = DefaultSource();
        var allOk = true;
        var n = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (++n > MaxLines)
            {
                results.Add(new JsonObject { ["line"] = line, ["ok"] = false, ["error"] = $"more than {MaxLines} lines; the rest was skipped" });
                allOk = false;
                break;
            }
            if (line.StartsWith('#'))
            {
                var r = Directive(line, raw.TrimStart(), ref source);
                if (r is not null)
                {
                    if (r["ok"]?.GetValue<bool>() == false) allOk = false;
                    results.Add(r);
                }
                continue;
            }
            CommandResult res;
            try
            {
                res = Commands.Execute(source, line);
            }
            catch (Exception e)
            {
                results.Add(new JsonObject { ["line"] = line, ["source"] = source.ToString(), ["ok"] = false, ["error"] = e.Message });
                allOk = false;
                continue;
            }
            allOk &= res.Ok;
            var entry = new JsonObject
            {
                ["line"] = line,
                ["source"] = source.ToString(),
                ["ok"] = res.Ok,
                ["found"] = res.Found,
                ["denied"] = res.Denied,
                ["canceled"] = res.Canceled,
                ["parseError"] = res.ParseError,
                ["error"] = res.Error,
                ["output"] = new JsonArray([.. res.Output.Select(o => (JsonNode)o)]),
            };
            results.Add(entry);
            if (res.Completion is { } done) running.Add((entry, done));
        }
        return new JsonObject
        {
            ["file"] = name,
            ["ok"] = allOk,
            ["time"] = DateTime.UtcNow.ToString("O"),
            ["results"] = results,
        };
    }

    private static JsonObject? Directive(string line, string untrimmed, ref CommandSource source)
    {
        var parts = Commands.Tokenize(line[1..]);
        if (parts.Count == 0) return null;
        switch (parts[0].ToLowerInvariant())
        {
            case "as" when parts.Count >= 2:
                switch (parts[1].ToLowerInvariant())
                {
                    case "console":
                        source = CommandSource.Console;
                        return null;
                    case "remote" when parts.Count >= 3:
                        source = CommandSource.Remote(parts[2], parts.Count < 4 || parts[3].ToLowerInvariant() != "user");
                        return null;
                    case "steam" when parts.Count >= 3 && ulong.TryParse(parts[2], out var steam) && steam != 0:
                        source = CommandSource.ForSteamId(steam, parts.Count >= 4 ? parts[3] : null);
                        return null;
                    case "player" when parts.Count >= 3:
                    {
                        var online = Arg.OnlinePlayers().Where(p => p.InGame).ToList();
                        if (PlayerArg.Resolve(parts[2], online, out var p, out var error))
                        {
                            source = CommandSource.ForPlayer(p!.Entity, p.Name);
                            return null;
                        }
                        return new JsonObject { ["line"] = line, ["ok"] = false, ["error"] = error };
                    }
                }
                break;
            case "complete":
            {
                var rest = untrimmed.Length > "#complete ".Length ? untrimmed["#complete ".Length..] : "";
                var c = Commands.Complete(source, rest);
                return new JsonObject
                {
                    ["line"] = line,
                    ["source"] = source.ToString(),
                    ["ok"] = true,
                    ["start"] = c.Start,
                    ["completions"] = new JsonArray([.. c.Suggestions.Select(s => (JsonNode)s)]),
                };
            }
        }
        return new JsonObject { ["line"] = line, ["ok"] = false, ["error"] = "unknown directive (#as console | #as remote <name> [admin|user] | #as steam <id> [name] | #as player <name> | #complete <text>)" };
    }
}
