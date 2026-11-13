// SPDX-License-Identifier: MIT
// progress.json from Kiln.Host's side: LoadAll reports step "mods" through kiln.progress v2 (StartProgress), counting the
// mods up to their total, then "content" and "game"; a core whose kiln.progress table has no v2 entries (older than
// dev.27) gets no call and the mods load all the same.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.Host.Tests;

public sealed unsafe class StartProgressTests : ModsFixture
{
    private static readonly List<(string Id, string Label, string Detail, ulong Done, ulong Total)> Calls = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnStep(StartProgress.Str id, StartProgress.Str label, StartProgress.Str detail, ulong done, ulong total)
    {
        Calls.Add((Text(id), Text(label), Text(detail), done, total));
        return 0;
    }

    private static string Text(StartProgress.Str s) => s.Ptr == null ? "" : System.Text.Encoding.UTF8.GetString(s.Ptr, (int)s.Len);

    private static StartProgress.Table* FakeTable(uint size)
    {
        var t = (StartProgress.Table*)NativeMemory.AllocZeroed((nuint)sizeof(StartProgress.Table));
        t->Size = size;
        t->Version = size >= (uint)sizeof(StartProgress.Table) ? 2u : 1u;
        // a v1 core's table ends before step: whatever lies there must not be called
        t->Step = &OnStep;
        return t;
    }

    public override void Dispose()
    {
        StartProgress.Use(0);
        base.Dispose();
    }

    [Fact]
    public void A_core_with_the_v2_table_sees_the_mods_step_count_up_to_its_total_then_content_and_game()
    {
        InstallLibSet();
        var t = FakeTable((uint)sizeof(StartProgress.Table));
        try
        {
            StartProgress.Use((nint)t);
            Calls.Clear();
            var host = new FakeHost();
            using var mods = NewManager(host);
            mods.LoadAll();
            Assert.Equal(3, mods.Mods.Count);
            var steps = Calls.Where(c => c.Id == "mods").ToList();
            Assert.Equal(("mods", "Loading the mods", "", 0UL, 3UL), steps[0]);
            Assert.Equal([0UL, 1UL, 2UL], steps.Skip(1).Take(3).Select(c => c.Done));
            Assert.All(steps.Skip(1).Take(3), c => Assert.True(c.Detail.StartsWith("sample-", StringComparison.Ordinal) && c.Total == 3));
            Assert.Equal(("mods", "", "", 3UL, 3UL), steps[^1]);
            // no mod has a blocks/ folder: nothing to apply, the game's own loading follows at once
            Assert.Equal(["mods", "content", "game"], Calls.Select(c => c.Id).Distinct());
            Assert.Equal("The server is starting its world", Calls[^1].Label);
        }
        finally
        {
            NativeMemory.Free(t);
        }
    }

    [Fact]
    public void A_core_whose_table_has_no_step_entry_gets_no_call_and_the_mods_load()
    {
        InstallLibSet();
        var t = FakeTable(32);  // kiln.progress v1: begin, update, end
        try
        {
            StartProgress.Use((nint)t);
            Calls.Clear();
            var host = new FakeHost();
            using var mods = NewManager(host);
            mods.LoadAll();
            Assert.Equal(3, mods.Mods.Count);
            Assert.Empty(Calls);
            Assert.Equal(StartProgress.Unavailable, StartProgress.Step("mods"));
        }
        finally
        {
            NativeMemory.Free(t);
        }
    }

    [Fact]
    public void No_core_at_all_reports_nothing()
    {
        StartProgress.Use(0);
        Assert.Equal(StartProgress.Unavailable, StartProgress.Step("mods", "x", "y", 1, 2));
        Assert.Equal(StartProgress.Unavailable, StartProgress.Mark(StartProgress.MarkMenu));
    }
}
