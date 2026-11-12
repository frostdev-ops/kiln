// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace Kiln.Testing;

/// <summary>
/// <c>kiln/control/checks/&lt;name&gt;.cmd</c> → <c>&lt;name&gt;.result.json</c> (<c>{command, ok, message, data, time}</c>, as
/// the host's control folder writes): <c>list</c>, <c>status</c>, <c>run &lt;mod&gt; &lt;id&gt;</c>, <c>run-all [filter]</c>,
/// <c>pass</c>, <c>fail &lt;one line&gt;</c>, <c>skip [one line]</c>, <c>yes</c>, <c>no</c>, <c>stop</c>. Polled twice a second,
/// only while the folder exists. A command whose outcome comes later (run, a verdict, stop) gets its result then.
/// </summary>
internal sealed class ControlFolder
{
    private const int EveryMs = 500;
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Waiter(string File, string Command, Func<RunEvent, bool> Matches, DateTime Until);

    private readonly CheckRunner _runner;
    private readonly IRunnerEnv _env;
    private readonly List<Waiter> _waiting = new();
    private long _next;

    public ControlFolder(CheckRunner runner, string directory, IRunnerEnv env)
    {
        _runner = runner;
        _env = env;
        Directory = directory;
        runner.Events += OnEvent;
    }

    public string Directory { get; }

    public static bool Enabled(string directory) => System.IO.Directory.Exists(directory);

    /// <summary>Tick thread.</summary>
    public void Poll(bool now = false)
    {
        var t = Environment.TickCount64;
        if (!now && t < _next) return;
        _next = t + EveryMs;
        for (var i = _waiting.Count - 1; i >= 0; i--)
            if (_env.Now > _waiting[i].Until)
            {
                var w = _waiting[i];
                _waiting.RemoveAt(i);
                Write(w.File, w.Command, false, $"no outcome within {ResultTimeout.TotalMinutes:0} min");
            }
        if (!System.IO.Directory.Exists(Directory)) return;
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(Directory, "*.cmd").Order(StringComparer.Ordinal).Take(8).ToList();
        }
        catch (IOException)
        {
            return;
        }
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file).Trim();
                File.Delete(file);
            }
            catch (IOException)
            {
                continue;  // still being written
            }
            var result = Path.ChangeExtension(file, ".result.json");
            try
            {
                Execute(text, result);
            }
            catch (Exception e)
            {
                Write(result, text, false, e.Message);
            }
        }
    }

    private void Execute(string command, string result)
    {
        var line = command.Split('\n')[0].Trim();
        var space = line.IndexOf(' ');
        var verb = space < 0 ? line : line[..space];
        var rest = space < 0 ? "" : line[(space + 1)..].Trim();
        _env.Write(LogLevel.Info, $"checks: control: {_runner.Scrubber.Line(line, 200)}");
        switch (verb)
        {
            case "list":
            {
                _runner.Step();  // a fresh view
                var v = _runner.View;
                Write(result, line, true, $"{v.Items.Count} check(s): {v.Passes} passed, {v.Fails} failed, {v.Skips} skipped, {v.NeverRun} never run",
                    new { session = v.Session, guard = v.Guard, checks = v.Items });
                break;
            }
            case "status":
            {
                _runner.Step();
                var v = _runner.View;
                Write(result, line, true, v.Current is { } c ? $"{c.Mod}/{c.Id} {c.Phase}" : "idle",
                    new { session = v.Session, current = v.Current, queued = v.Queued, guard = v.Guard, protecting = v.Protecting, warning = v.Warning, message = v.Message, ledger = v.Ledger });
                break;
            }
            case "run":
            {
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) throw new ArgumentException("usage: run <mod> <id>");
                var (mod, id) = (parts[0], parts[1]);
                Wait(result, line, e => e.Mod == mod && e.Id == id && e.Origin == CheckOrigin.Control &&
                                       e.Kind is RunEventKind.Refused or RunEventKind.Started or RunEventKind.Confirming);
                _runner.Request(new StartRequest(mod, id, CheckOrigin.Control));
                break;
            }
            case "run-all":
            {
                var n = _runner.RequestAll(rest.Length > 0 ? rest : null, CheckOrigin.Control);
                Write(result, line, n > 0, $"{n} check(s) queued" + (rest.Length > 0 ? $" matching '{rest}'" : ""));
                break;
            }
            case "pass" or "fail" or "skip":
            {
                var kind = verb switch { "pass" => AnswerKind.Pass, "fail" => AnswerKind.Fail, _ => AnswerKind.Skip };
                var current = _runner.Current;
                if (current is null) throw new ArgumentException("no check is running");
                var key = (current.Case.Mod, current.Case.Id);
                if (_runner.Answer(kind, rest, null, origin: CheckOrigin.Control) is { } why) throw new ArgumentException(why);
                Wait(result, line, e => e.Kind == RunEventKind.Finished && (e.Mod, e.Id) == key);
                break;
            }
            case "yes" or "no":
            {
                var current = _runner.Current;
                if (current is null) throw new ArgumentException("no check is waiting for a yes or no");
                var key = (current.Case.Mod, current.Case.Id);
                Wait(result, line, e => (e.Mod, e.Id) == key && e.Kind is RunEventKind.Started or RunEventKind.Refused or RunEventKind.Declined);
                if (_runner.Answer(verb == "yes" ? AnswerKind.Yes : AnswerKind.No, null, null, origin: CheckOrigin.Control) is { } why)
                {
                    _waiting.RemoveAt(_waiting.Count - 1);
                    throw new ArgumentException(why);
                }
                break;
            }
            case "stop":
            {
                var current = _runner.Current;
                if (current is not null)
                {
                    var key = (current.Case.Mod, current.Case.Id);
                    Wait(result, line, e => (e.Mod, e.Id) == key && e.Kind is RunEventKind.Finished or RunEventKind.Declined);
                }
                if (_runner.Answer(AnswerKind.Stop, null, null, origin: CheckOrigin.Control) is { } why)
                {
                    if (current is not null) _waiting.RemoveAt(_waiting.Count - 1);
                    throw new ArgumentException(why);
                }
                if (current is null) Write(result, line, true, "the queue is cleared");
                break;
            }
            default:
                throw new ArgumentException($"unknown command '{line}' (list | status | run <mod> <id> | run-all [filter] | pass | " +
                                            "fail <one line> | skip [one line] | yes | no | stop)");
        }
    }

    private void Wait(string file, string command, Func<RunEvent, bool> matches) =>
        _waiting.Add(new Waiter(file, command, matches, _env.Now + ResultTimeout));

    private void OnEvent(RunEvent e)
    {
        for (var i = 0; i < _waiting.Count; i++)
        {
            var w = _waiting[i];
            if (!w.Matches(e)) continue;
            _waiting.RemoveAt(i);
            var ok = e.Kind switch
            {
                RunEventKind.Refused or RunEventKind.Declined => false,
                RunEventKind.Finished => e.Entry is not null,
                _ => true,
            };
            Write(w.File, w.Command, ok, e.Kind == RunEventKind.Refused ? "refused: " + e.Message : e.Message,
                new { kind = e.Kind.ToString().ToLowerInvariant(), mod = e.Mod, id = e.Id, refusal = e.Refusal, entry = e.Entry, current = e.Current });
            return;
        }
    }

    private void Write(string file, string command, bool ok, string message, object? data = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { command = _runner.Scrubber.Line(command, 400), ok, message, data, time = DateTime.UtcNow }, ResultJson);
            File.WriteAllText(file + ".tmp", json);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _env.Write(LogLevel.Warn, $"checks: could not write {Path.GetFileName(file)}: {e.Message}");
        }
    }
}
