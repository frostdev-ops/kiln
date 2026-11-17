// SPDX-License-Identifier: MIT
// Crash reports' Tasks section (issue #49): the managed report reads the core's diagnostics through
// the host table's appended members, a running task and the last fault show up in it, and a supplier
// that throws can never stop a crash report from being written.
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class TaskCrashReportTests : HostTaskTest
{
    [Fact]
    public void The_tasks_section_names_the_running_task_and_the_last_fault()
    {
        var mod = NewMod("alpha");
        var gate = new TaskCompletionSource();
        var running = mod.Tasks.Run("crash-listed", async _ => await gate.Task);
        var faulted = mod.Tasks.Run("crash-fault", _ => throw new InvalidOperationException("boom"));
        Ended(faulted);
        Native.WaitIdle();

        var section = Tasks.CrashTasksSection();
        Assert.Contains("running", section);
        Assert.Contains("crash-listed", section);
        Assert.Contains("state=suspended", section);  // parked on the await: no worker held
        Assert.Contains("fault #1", section);
        Assert.Contains("crash-fault", section);
        gate.SetResult();
        Ended(running);
        Quiesce();
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("nl\nhere")]
    [InlineData("nul\0here")]
    [InlineData("del\u007f")]
    [InlineData("c1\u0085")]
    [InlineData("lone-surrogate")]  // built below: a lone surrogate does not survive test-case serialization
    public void A_task_name_with_a_control_character_or_invalid_text_is_refused_at_submit(string name)
    {
        if (name == "lone-surrogate") name = "lone" + (char)0xD800;
        var mod = NewMod("alpha");
        Assert.Throws<ArgumentException>(() => mod.Tasks.Run(name, _ => { }));
        Assert.DoesNotContain(Native.Admitted, a => a.Name == name);
        Assert.Empty(Native.Admitted);
    }

    [Fact]
    public void Quotes_and_backslashes_in_a_name_are_flattened_and_a_full_length_name_stays_whole()
    {
        var mod = NewMod("alpha");
        var gate = new TaskCompletionSource();
        var quoted = mod.Tasks.Run("say \"hi\" \\ back", async _ => await gate.Task);
        var longest = new string('a', 126) + "\u00e9";  // 128 UTF-8 bytes, the last character two of them
        var full = mod.Tasks.Run(longest, async _ => await gate.Task);
        Native.WaitIdle();

        var section = Tasks.CrashTasksSection();
        Assert.Contains("task=\"say ?hi? ? back\"", section);
        Assert.Contains("task=\"" + longest + "\"", section);
        gate.SetResult();
        Ended(quoted);
        Ended(full);
        Quiesce();
    }

    [Fact]
    public void A_throwing_tasks_supplier_never_stops_a_crash_report()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-crash-tasks-" + Guid.NewGuid().ToString("N"));
        var reports = new CrashReports(dir, null, () => "Kiln: test\n", () => "  mod\n", null,
            () => throw new InvalidOperationException("the supplier is broken"));
        var path = reports.Write("managed-unhandled", "a title", "details");
        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains("-- Tasks --\n(task snapshot unavailable)", text);
        Assert.Contains("-- Details --", text);
        Assert.Contains("details", text);
    }

    [Fact]
    public void A_tasks_supplier_lands_between_details_and_the_log_tail()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-crash-tasks-" + Guid.NewGuid().ToString("N"));
        var reports = new CrashReports(dir, null, () => "Kiln: test\n", () => "  mod\n", null,
            () => "  running thread=1 owner=\"alpha\" task=\"x\" handle=0x2 started_ns=3 elapsed_ms=4.000 state=running\n");
        var path = reports.Write("managed-unhandled", "a title", "details");
        var text = File.ReadAllText(path!);
        var tasks = text.IndexOf("-- Tasks --", StringComparison.Ordinal);
        var details = text.IndexOf("-- Details --", StringComparison.Ordinal);
        var tail = text.IndexOf("-- Last kiln.log lines --", StringComparison.Ordinal);
        Assert.True(details >= 0 && tasks > details && tail > tasks, text);
        Assert.Contains("task=\"x\"", text);
    }
}

