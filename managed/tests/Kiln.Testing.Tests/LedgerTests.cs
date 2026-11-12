// SPDX-License-Identifier: MIT
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.CheckRunnerTests;

public sealed class LedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-ledger-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "client-checks.json");

    public LedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static LedgerEntry Entry(string id, string verdict) => new()
    {
        Mod = "m", ModVersion = "1.0.0", Id = id, Title = id, Area = "A", Kiln = "0.2.0", Game = new LedgerGame { Side = "client", Sha256 = new string('a', 64) },
        Session = "hosted", Verdict = verdict, Time = "2026-11-12T00:00:00Z", Assertions = [new LedgerAssertion { What = "w", Ok = true }], Log = ["line"],
    };

    [Fact]
    public void Append_then_reread_keeps_every_entry_in_order()
    {
        var a = new Ledger(PathOf);
        Assert.Null(a.Load());
        Assert.True(a.MergeCatalogue([new LedgerCheck { Mod = "m", Id = "x", Title = "X", Area = "A" }]));
        Assert.True(a.Append(Entry("x", "pass")));
        Assert.True(a.Append(Entry("x", "fail")));
        Assert.True(a.Append(Entry("y", "skip")));
        var b = new Ledger(PathOf);
        Assert.Null(b.Load());
        var doc = b.Snapshot();
        Assert.Equal(["pass", "fail", "skip"], doc.Entries.Select(e => e.Verdict));
        Assert.Single(doc.Checks);
        Assert.Equal("fail", b.Newest()["m/x"].Verdict);  // run again: the newest counts
        Assert.False(File.Exists(PathOf + ".tmp"));
        // entries only grow: a catalogue change keeps them
        Assert.True(b.MergeCatalogue([new LedgerCheck { Mod = "m", Id = "x", Title = "X renamed", Area = "A" }]));
        Assert.True(b.Write());
        Assert.Equal(3, new Ledger(PathOf).ReadFile().Document!.Entries.Count);
    }

    [Theory]
    [InlineData("{ \"format\": 1, \"checks\": [], \"entries\": [ { \"mod\": ")]
    [InlineData("not json at all")]
    [InlineData("{ \"format\": 2, \"checks\": [], \"entries\": [] }")]
    [InlineData("{ \"format\": 1, \"checks\": [], \"entries\": [ null ] }")]
    public void A_corrupt_ledger_is_moved_aside_never_overwritten(string text)
    {
        File.WriteAllText(PathOf, text);
        var l = new Ledger(PathOf);
        var line = l.Load();
        Assert.NotNull(line);
        Assert.Contains("moved to", line);
        Assert.NotNull(l.MovedAside);
        Assert.Matches(@"client-checks\.unreadable-\d{8}T\d{6}Z\.json$", l.MovedAside);
        Assert.Equal(text, File.ReadAllText(l.MovedAside!));  // whole, untouched
        Assert.True(l.Append(Entry("x", "pass")));
        Assert.Single(new Ledger(PathOf).ReadFile().Document!.Entries);
        Assert.Equal(text, File.ReadAllText(l.MovedAside!));
        // a second corrupt file in the same second gets its own name
        File.WriteAllText(PathOf, "again");
        var m = new Ledger(PathOf);
        m.Load();
        Assert.NotEqual(l.MovedAside, m.MovedAside);
        Assert.Equal("again", File.ReadAllText(m.MovedAside!));
    }

    [Fact]
    public void A_write_cut_between_the_temporary_file_and_the_rename_leaves_the_old_ledger_whole()
    {
        var l = new Ledger(PathOf);
        l.Load();
        Assert.True(l.Append(Entry("x", "pass")));
        var before = File.ReadAllBytes(PathOf);
        l.BeforeRename = tmp =>
        {
            Assert.True(File.Exists(tmp));  // the new ledger is on disk, flushed, under its temporary name
            throw new IOException("the process died here");
        };
        Assert.False(l.Append(Entry("y", "fail")));
        Assert.Contains("died", l.WriteError);
        Assert.Equal(before, File.ReadAllBytes(PathOf));  // byte for byte the old one
        Assert.True(File.Exists(PathOf + ".tmp"));
        // the next start reads the old ledger and ignores the leftover
        var next = new Ledger(PathOf);
        Assert.Null(next.Load());
        Assert.Equal(["pass"], next.Snapshot().Entries.Select(e => e.Verdict));
        Assert.True(next.Append(Entry("z", "skip")));
        Assert.Equal(["pass", "skip"], new Ledger(PathOf).ReadFile().Document!.Entries.Select(e => e.Verdict));
        Assert.False(File.Exists(PathOf + ".tmp"));
    }

    [Fact]
    public void A_failed_write_keeps_the_entry_for_the_next_one()
    {
        var l = new Ledger(PathOf);
        l.Load();
        l.BeforeRename = _ => throw new IOException("disk full");
        Assert.False(l.Append(Entry("x", "pass")));
        l.BeforeRename = null;
        Assert.True(l.Append(Entry("y", "fail")));
        Assert.Equal(2, new Ledger(PathOf).ReadFile().Document!.Entries.Count);
    }

    [Fact]
    public void A_missing_ledger_reads_as_missing_not_unreadable()
    {
        var l = new Ledger(PathOf);
        Assert.Equal(LedgerReadState.Missing, l.ReadFile().State);
        Assert.Null(l.Load());
    }

    [Fact]
    public void The_written_file_has_the_schemas_field_names()
    {
        var l = new Ledger(PathOf);
        l.Load();
        l.Append(Entry("x", "pass") with { Guard = new LedgerGuard { Active = true }, LogDropped = 3 });
        var text = File.ReadAllText(PathOf);
        foreach (var name in new[] { "\"format\": 1", "\"checks\"", "\"entries\"", "\"modVersion\"", "\"sha256\"", "\"session\"", "\"verdict\"",
                     "\"assertions\"", "\"hookInstalled\"", "\"logDropped\": 3" })
            Assert.Contains(name, text);
        var plain = new Ledger(Path.Combine(_dir, "b.json"));
        plain.Load();
        plain.Append(Entry("x", "pass"));
        Assert.DoesNotContain("\"guard\"", File.ReadAllText(Path.Combine(_dir, "b.json")));  // only on cheats checks
        Assert.DoesNotContain("logDropped", File.ReadAllText(Path.Combine(_dir, "b.json")));
    }
}
