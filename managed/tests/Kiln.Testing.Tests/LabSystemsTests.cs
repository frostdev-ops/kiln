// SPDX-License-Identifier: MIT
// LabSystems (the lab parallel switch): its mirrors of kiln.world v4 match the header field for field
// (native/services/world/tests/abi.json, which the native host test checks the header against), and a window outside
// 1..3000 steps is refused before anything reaches the native table.
#pragma warning disable KILNX001
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.CheckRunnerTests;

public sealed class LabSystemsTests
{
    private static JsonNode Abi([CallerFilePath] string self = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", ".."));
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "native", "services", "world", "tests", "abi.json")))!;
    }

    private static int Offset(JsonNode abi, string native, string field) =>
        (int)abi[native]!["fields"]!.AsArray().Single(f => (string)f![0]! == field)![1]!;

    [Fact]
    public void The_status_mirror_matches_the_header_field_for_field()
    {
        var abi = Abi();
        const string S = "KilnGameProgramsStatus";
        var t = typeof(LabSystems.ProgramsStatus);
        Assert.Equal((int)abi[S]!["size"]!, Marshal.SizeOf(t));
        (string Mirror, string Native)[] pairs =
        [
            ("Size", "size"), ("Directory", "directory"), ("Threads", "threads"), ("Programs", "programs"),
            ("GamePrograms", "game_programs"), ("NoUpdate", "no_update"), ("PlayersMask", "players_mask"),
            ("ParallelForced", "parallel_forced"), ("ParallelSwitch", "parallel_switch"),
            ("ParallelStepsLeft", "parallel_steps_left"), ("WrapsActive", "wraps_active"),
            ("WrapsPending", "wraps_pending"), ("Detail", "detail"),
        ];
        foreach (var (mirror, native) in pairs)
            Assert.True(Offset(abi, S, native) == (int)Marshal.OffsetOf(t, mirror), $"{mirror} vs {native}");
    }

    [Fact]
    public void The_table_mirror_ends_with_the_switch()
    {
        var abi = Abi();
        var t = typeof(LabSystems.WorldV4);
        Assert.Equal(Offset(abi, "KilnWorldApiV4", "game_program_find"), (int)Marshal.OffsetOf(t, "GameProgramFind"));
        Assert.Equal(Offset(abi, "KilnWorldApiV4", "game_programs_status"), (int)Marshal.OffsetOf(t, "Status"));
        Assert.Equal(Offset(abi, "KilnWorldApiV4", "lab_parallel"), (int)Marshal.OffsetOf(t, "LabParallel"));
        Assert.Equal(Offset(abi, "KilnWorldApiV4", "lab_parallel") + 8, Marshal.SizeOf(t));
    }

    [Fact]
    public void A_window_outside_1_to_3000_steps_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LabSystems.ForceParallel(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LabSystems.ForceParallel(LabSystems.MaxSteps + 1));
    }
}
