// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text;
using Xunit;

namespace Kiln.TestKit.Tests;

/// <summary>The table of docs/kiln/modding/unit-tests.md is generated from <see cref="Coverage.All"/>: this fails when the
/// page's table is not what the kit gives. <c>KILN_UPDATE_DOCS=1 dotnet test --filter "FullyQualifiedName~DocTable"</c>
/// rewrites the table in the page instead.</summary>
public sealed class DocTableTests
{
    public const string Begin = "<!-- coverage:begin: generated from Kiln.TestKit's Coverage.All; KILN_UPDATE_DOCS=1 and the DocTableTests rewrite it -->";
    public const string End = "<!-- coverage:end -->";

    private static string Page()
    {
        var repo = typeof(DocTableTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "KilnRepository").Value!;
        return Path.Combine(repo, "docs", "kiln", "modding", "unit-tests.md");
    }

    /// <summary>The table for <paramref name="rows"/>, as the page holds it between the markers.</summary>
    public static string Table(IEnumerable<CoverageRow> rows)
    {
        static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        var b = new StringBuilder();
        b.Append("| Area | Offline | Capability | What holds |\n");
        b.Append("|---|---|---|---|\n");
        foreach (var r in rows)
        {
            var kind = r.Kind switch
            {
                CoverageKind.Real => "real",
                CoverageKind.RecordingFake => "recording fake",
                CoverageKind.Unavailable => "unavailable",
                _ => throw new ArgumentOutOfRangeException(nameof(rows), r.Kind, "a coverage kind the table does not know"),
            };
            var caps = r.Capabilities.Count == 0 ? "" : string.Join(", ", r.Capabilities.Select(c => $"`{c}`"));
            b.Append($"| {Cell(r.Area)} | {kind} | {caps} | {Cell(r.Note)} |\n");
        }
        return b.ToString();
    }

    /// <summary>What the page holds between the markers (line ends as \n).</summary>
    private static (string Before, string Table, string After) Split(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var b = text.IndexOf(Begin, StringComparison.Ordinal);
        var e = text.IndexOf(End, StringComparison.Ordinal);
        Assert.True(b >= 0 && e > b, $"the page has no '{Begin}' ... '{End}' markers");
        var start = b + Begin.Length + 1;
        return (text[..start], text[start..e], text[e..]);
    }

    [Fact]
    public void ThePagesTableIsWhatTheKitGenerates()
    {
        var page = Page();
        Assert.True(File.Exists(page), page);
        var (before, table, after) = Split(File.ReadAllText(page));
        var generated = Table(Coverage.All);
        if (Environment.GetEnvironmentVariable("KILN_UPDATE_DOCS") == "1" && table != generated)
        {
            File.WriteAllText(page, before + generated + after);
            table = Split(File.ReadAllText(page)).Table;
        }
        Assert.True(generated == table,
            "docs/kiln/modding/unit-tests.md's table is not Coverage.All: run the DocTableTests with KILN_UPDATE_DOCS=1 to rewrite it.\n" +
            $"--- generated ---\n{generated}--- page ---\n{table}");
    }

    [Fact]
    public void AChangedRowGivesAnotherTable()
    {
        var rows = Coverage.All.ToList();
        var page = Split(File.ReadAllText(Page())).Table;
        rows[0] = rows[0] with { Note = rows[0].Note + " (changed)" };
        Assert.NotEqual(page, Table(rows));
        rows = [.. Coverage.All];
        rows[^1] = rows[^1] with { Kind = rows[^1].Kind == CoverageKind.Real ? CoverageKind.Unavailable : CoverageKind.Real };
        Assert.NotEqual(page, Table(rows));
    }
}
