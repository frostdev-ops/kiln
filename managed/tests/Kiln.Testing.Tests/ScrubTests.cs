// SPDX-License-Identifier: MIT
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.CheckRunnerTests;

public sealed class ScrubTests
{
    private static Scrubber S()
    {
        var s = new Scrubber();
        s.AddWord("alice", "<user>");
        s.AddWord("ALICE-PC", "<host>");
        return s;
    }

    // what must never reach the ledger, and what it must become
    public static TheoryData<string, string, string> Private => new()
    {
        { "user name", "alice saw it", "alice" },
        { "machine name", "on ALICE-PC today", "ALICE-PC" },
        { "home path, forward slashes", "file /home/alice/games/x.json", "/home/alice" },
        { "home path, backslashes", @"file C:\Users\alice\AppData\x.json", @"C:\Users\alice" },
        { "home path, Wine drive", @"file Z:\home\bob\x", @"Z:\home\bob" },
        { "home path, doubled backslashes", @"""path"": ""C:\\Users\\bob\\x""", @"\\Users\\bob" },
        { "home path, another user", "/home/carol/.steam/x", "carol" },
        { "17-digit Steam id", "steam 76561198012345678 joined", "76561198012345678" },
        { "Steam id in userdata", "userdata/123456789/config", "123456789" },
        { "Steam id 3", "[U:1:44556677]", "44556677" },
        { "IPv4 address", "from 192.168.1.20:15636", "192.168.1.20" },
        { "IPv6 address", "from fe80::1ff:fe23:4567:890a", "fe80::1ff:fe23:4567:890a" },
        { "IPv6 address, full", "addr 2001:0db8:85a3:0000:0000:8a2e:0370:7334 x", "2001:0db8" },
        { "token", "token=abcdef0123456789 next", "abcdef0123456789" },
        { "bearer", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc", "eyJhbGci" },
    };

    [Theory]
    [MemberData(nameof(Private))]
    public void Private_data_is_removed(string what, string text, string secret)
    {
        var s = S().Scrub(text);
        Assert.DoesNotContain(secret, s, StringComparison.OrdinalIgnoreCase);
        Assert.True(s.Contains('<'), $"{what}: {s}");
    }

    [Theory]
    [InlineData("Kiln 0.2.0-dev.25 on keen::UiState at 12:34:56")]
    [InlineData("loopback 127.0.0.1 and ::1")]
    [InlineData("sha256 ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12")]
    [InlineData("version v1.2.3.4 of the mod, 3 hits, step 1234567")]
    [InlineData("kiln/mods/my-mod/blocks/a.json")]
    public void Ordinary_text_stays(string text) => Assert.Equal(text, S().Scrub(text));

    [Fact]
    public void A_line_is_one_line_and_capped()
    {
        var s = S().Line("a\nb\r\tc" + new string('x', 500), 100);
        Assert.DoesNotContain('\n', s);
        Assert.True(s.Length <= 100);
    }

    [Fact]
    public void This_machines_names_are_known()
    {
        var s = Scrubber.ForThisMachine();
        var user = Environment.UserName;
        if (user.Length > 3 && !string.Equals(user, "root", StringComparison.OrdinalIgnoreCase))
            Assert.DoesNotContain(user, s.Scrub($"by {user} here"), StringComparison.OrdinalIgnoreCase);
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home)) Assert.DoesNotContain(home, s.Scrub($"at {home}/x"));
    }

    private const string Note = @"seen by alice at /home/alice/x and C:\Users\alice\y, steam 76561198012345678, from 192.168.1.20 and fe80::1ff:fe23:4567:890a";
    private static readonly string[] Secrets = ["alice", "76561198012345678", "192.168.1.20", "fe80::1ff", "/home/alice", @"C:\Users\alice"];

    [Fact]
    public void A_note_the_player_types_is_scrubbed_in_the_entry()
    {
        using var h = new Harness(x => x.Scrubber.AddWord("alice", "<user>"));
        h.Start("t.pass");
        var e = h.Answer(AnswerKind.Fail, Note);
        foreach (var secret in Secrets) Assert.DoesNotContain(secret, e.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<home>", e.Note);
        Assert.Contains("<steamid>", e.Note);
        Assert.Contains("<ip>", e.Note);
        var file = File.ReadAllText(h.LedgerPath);
        foreach (var secret in Secrets) Assert.DoesNotContain(secret.Replace(@"\", @"\\"), file, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_captured_log_line_and_an_assertion_detail_are_scrubbed()
    {
        using var h = new Harness(x => x.Scrubber.AddWord("alice", "<user>"));
        CheckMod.DuringBody = () => h.Env.LogTail.Lines.Add("[I] [mod:cct] " + Note);
        h.Start("t.pass");
        var e = h.Answer(AnswerKind.Pass);
        Assert.Single(e.Log);
        foreach (var secret in Secrets) Assert.DoesNotContain(secret, e.Log[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<steamid>", e.Log[0]);
        h.Start("t.throws");
        var t = h.Answer(AnswerKind.Skip);
        Assert.Contains(t.Assertions, a => !a.Ok && a.Detail!.Contains("<ip>") && !a.Detail.Contains("10.1.2.3"));
    }

    [Fact]
    public void The_captured_log_is_capped_in_lines_and_bytes()
    {
        using var h = new Harness();
        CheckMod.DuringBody = () =>
        {
            for (var i = 0; i < 500; i++) h.Env.LogTail.Lines.Add($"line {i} " + new string('y', 300));
        };
        h.Start("t.pass");
        var e = h.Answer(AnswerKind.Pass);
        Assert.True(e.Log.Length <= CheckRunner.MaxLogLines);
        Assert.True(e.Log.Sum(l => l.Length) <= CheckRunner.MaxLogBytes);
        Assert.Equal(500, e.Log.Length + e.LogDropped);
        Assert.StartsWith("line 0 ", e.Log[0]);
    }
}
