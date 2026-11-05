// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln.Testing;

internal sealed class TestRunOptions
{
    /// <summary>Only tests whose name contains this (case-insensitive).</summary>
    public string? Filter { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary><c>kiln/logs/tests</c>; null = no JSON file.</summary>
    public string? ResultsDirectory { get; init; }

    public string Version { get; init; } = "";
}

/// <summary>Finds and runs <see cref="KilnTestAttribute"/> methods. Started by Kiln.Host inside the mod's context on
/// the tick thread, so every await in a test (and in the runner) continues on the tick thread.</summary>
internal static class TestRunner
{
    internal sealed record Case(string Name, MethodInfo Method, KilnTestAttribute Attribute, string? Problem);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static List<Case> Discover(IEnumerable<Assembly> assemblies, string? filter)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;
        var cases = new List<(Case Case, int Order, string Type, int Token)>();
        foreach (var asm in assemblies)
        {
            Type?[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            foreach (var t in types)
            {
                if (t is null) continue;
                foreach (var m in t.GetMethods(flags))
                {
                    var attr = m.GetCustomAttribute<KilnTestAttribute>();
                    if (attr is null) continue;
                    var name = attr.Name ?? $"{t.Name}.{m.Name}";
                    if (filter is { Length: > 0 } && !(filter[0] == '=' ? name.Equals(filter[1..], StringComparison.OrdinalIgnoreCase) : name.Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;
                    var ps = m.GetParameters();
                    string? problem = null;
                    if (m.IsGenericMethodDefinition || t.ContainsGenericParameters) problem = "test methods cannot be generic";
                    else if (ps.Length > 1 || (ps.Length == 1 && ps[0].ParameterType != typeof(TestContext)))
                        problem = "a test takes no parameter or one TestContext";
                    else if (m.ReturnType != typeof(void) && m.ReturnType != typeof(Task) && m.ReturnType != typeof(ValueTask))
                        problem = "a test returns void, Task or ValueTask";
                    else if (!m.IsStatic && t.IsAbstract) problem = "instance tests need a concrete class";
                    cases.Add((new Case(name, m, attr, problem), attr.Order, t.FullName ?? t.Name, m.MetadataToken));
                }
            }
        }
        return cases.OrderBy(c => c.Order).ThenBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Token).Select(c => c.Case).ToList();
    }

    public static async Task<TestReport> RunAsync(Mod instance, IReadOnlyList<Assembly> assemblies, TestRunOptions options)
    {
        var mod = instance.Context;
        var started = DateTime.UtcNow;
        var total = Stopwatch.StartNew();
        var cases = Discover(assemblies, options.Filter);
        mod.Log.Info($"tests: running {cases.Count} test(s) of {mod.Info.Id} {options.Version}" +
                     (options.Filter is { Length: > 0 } f ? $" matching '{f}'" : ""));
        var results = new List<TestResult>();
        // An unload ends the run: its timeout timers are canceled so nothing (a pending Task.Delay) keeps the unloaded
        // mod's load context alive; the continuation then goes to the retired scheduler, which drops it.
        using var unload = new CancellationTokenSource();
        Action retire = unload.Cancel;
        mod.Retiring += retire;
        try
        {
            foreach (var c in cases)
            {
                if (unload.IsCancellationRequested) break;
                var r = await RunOne(instance, c, options, unload.Token);
                results.Add(r);
                Report(mod, r);
            }
        }
        finally
        {
            mod.Retiring -= retire;
        }
        var report = new TestReport(mod.Info.Id, options.Version, started, total.Elapsed.TotalMilliseconds, results);
        string? file = null;
        if (options.ResultsDirectory is { } dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, mod.Info.Id + ".json");
                var json = JsonSerializer.Serialize(new
                {
                    mod = report.Mod, version = report.Version, started = report.Started, ms = Math.Round(report.Ms, 2),
                    ok = report.Ok, passed = report.Passed, failed = report.Failed, skipped = report.Skipped,
                    results = report.Results.Select(x => new { x.Name, x.Outcome, ms = Math.Round(x.Ms, 3), x.Message, x.Stack }),
                }, Json);
                File.WriteAllText(file + ".tmp", json);
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (IOException e)
            {
                mod.Log.Warn($"tests: could not write {file}: {e.Message}");
                file = null;
            }
        }
        report = report with { File = file };
        var summary = $"tests: {report}" + (file is null ? "" : $"; results {file}");
        if (report.Ok) mod.Log.Info(summary);
        else mod.Log.Error(summary);
        return report;
    }

    private static void Report(ModContext mod, TestResult r)
    {
        switch (r.Outcome)
        {
            case TestOutcome.Pass:
                mod.Log.Info($"PASS {r.Name} ({r.Ms:F1} ms)");
                break;
            case TestOutcome.Skip:
                mod.Log.Info($"SKIP {r.Name}: {r.Message}");
                break;
            default:
                mod.Log.Error($"FAIL {r.Name}{(r.Outcome == TestOutcome.Timeout ? " (timeout)" : "")}: {r.Message}" +
                              (r.Stack is { Length: > 0 } s ? "\n" + s : ""));
                break;
        }
    }

    private static async Task<TestResult> RunOne(Mod instance, Case c, TestRunOptions options, CancellationToken unload)
    {
        if (c.Problem is { } problem) return new TestResult(c.Name, TestOutcome.Fail, 0, problem, null);
        if (c.Attribute.Skip is { } skip) return new TestResult(c.Name, TestOutcome.Skip, 0, skip, null);
        var timeout = c.Attribute.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(c.Attribute.TimeoutSeconds) : options.Timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(unload);
        var context = new TestContext(c.Name, instance.Context, cts.Token);
        var sw = Stopwatch.StartNew();
        object? target = null;
        Task task;
        try
        {
            var type = c.Method.DeclaringType!;
            target = c.Method.IsStatic ? null : type.IsInstanceOfType(instance) ? instance : Activator.CreateInstance(type, nonPublic: true);
            object?[] args = c.Method.GetParameters().Length == 1 ? [context] : [];
            task = c.Method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, args, null) switch
            {
                Task t => t,
                ValueTask vt => vt.AsTask(),
                _ => Task.CompletedTask,
            };
        }
        catch (Exception e)
        {
            task = Task.FromException(e);
        }
        try
        {
            if (!task.IsCompleted && await Task.WhenAny(task, Task.Delay(timeout, cts.Token)) != task)
            {
                cts.Cancel();
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);  // observed: not a mod fault
                return new TestResult(c.Name, TestOutcome.Timeout, sw.Elapsed.TotalMilliseconds,
                    $"did not finish within {timeout.TotalSeconds:0.#} s", null);
            }
            await task;
            return new TestResult(c.Name, TestOutcome.Pass, sw.Elapsed.TotalMilliseconds, null, null);
        }
        catch (AssertionException e)
        {
            return new TestResult(c.Name, TestOutcome.Fail, sw.Elapsed.TotalMilliseconds, e.Message, TestFrames(e));
        }
        catch (SkipException e)
        {
            return new TestResult(c.Name, TestOutcome.Skip, sw.Elapsed.TotalMilliseconds, e.Message, null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new TestResult(c.Name, TestOutcome.Timeout, sw.Elapsed.TotalMilliseconds, $"did not finish within {timeout.TotalSeconds:0.#} s", null);
        }
        catch (Exception e)
        {
            return new TestResult(c.Name, TestOutcome.Fail, sw.Elapsed.TotalMilliseconds, $"{e.GetType().Name}: {e.Message}", e.StackTrace);
        }
        finally
        {
            cts.Cancel();  // drops the pending timeout timer, which would otherwise pin the test (and the mod) for its duration
            if (target is not null && !ReferenceEquals(target, instance))
            {
                try
                {
                    if (target is IAsyncDisposable ad) await ad.DisposeAsync();
                    else if (target is IDisposable d) d.Dispose();
                }
                catch (Exception e)
                {
                    instance.Context.Log.Warn($"test {c.Name}: dispose threw {e.GetType().Name}: {e.Message}");
                }
            }
        }
    }

    /// <summary>The stack without Kiln.Testing's own frames (where the assertion was made).</summary>
    private static string? TestFrames(Exception e) =>
        e.StackTrace is { } s
            ? string.Join('\n', s.Split('\n').Where(l => !l.Contains("Kiln.Testing.Assert", StringComparison.Ordinal)).Select(l => l.TrimEnd()))
            : null;
}
