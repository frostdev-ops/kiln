// SPDX-License-Identifier: MIT
// A ShroudForge mod that tries to change blocks in a game that joined a server: kiln-shroudforge says so in
// shroudforge.json "joinedWorldEdit" (native/plugins/shroudforge/src/joined_notice.h), and Kiln.Ui shows one toast per
// joined session (ShroudForgeJoinedNotice.cs). netjunk, Altar Cleanup on a client joined to a dedicated server. No game.
using System.Text.Json;
using Xunit;

namespace Kiln.ShroudForgeJoinedNoticeTests;

public sealed class ShroudForgeJoinedNoticeTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // as plugin.cpp writes it (set_status("joinedWorldEdit", {session, mod, at}))
    private const string Hosted = """{"mode": "hosted", "runtime": "running", "pid": 1, "joinedWorldEdit": {"session": 2, "mod": "altar-cleanup", "at": 1795750596}}""";

    [Fact]
    public void Reads_the_joined_world_edit_kiln_shroudforge_wrote()
    {
        var e = ShroudForgeJoinedNotice.Read(Doc(Hosted));
        Assert.Equal(new ShroudForgeJoinedNotice.Edit(2, "altar-cleanup"), e);
        Assert.Equal(new ShroudForgeJoinedNotice.Edit(3, ""),
            ShroudForgeJoinedNotice.Read(Doc("""{"mode": "hosted", "joinedWorldEdit": {"session": 3, "mod": ""}}""")));
    }

    [Theory]
    [InlineData("""{"mode": "hosted"}""")]
    [InlineData("""{"mode": "hosted", "joinedWorldEdit": null}""")]
    [InlineData("""{"mode": "hosted", "joinedWorldEdit": {"mod": "x"}}""")]
    [InlineData("""{"mode": "hosted", "joinedWorldEdit": {"session": 0, "mod": "x"}}""")]
    [InlineData("""{"mode": "hosted", "joinedWorldEdit": {"session": "2", "mod": "x"}}""")]
    [InlineData("""{"mode": "self-hosted", "joinedWorldEdit": {"session": 2, "mod": "x"}}""")]
    [InlineData("""[1, 2]""")]
    public void Anything_else_is_no_joined_world_edit(string json) => Assert.Null(ShroudForgeJoinedNotice.Read(Doc(json)));

    [Fact]
    public void No_file_or_another_start_is_none() => Assert.Null(ShroudForgeJoinedNotice.Read(null));

    [Fact]
    public void One_toast_per_joined_session_however_often_the_file_is_read()
    {
        var shown = new List<(string Text, ToastKind Kind, double Seconds)>();
        var gate = new ShroudForgeJoinedNotice.Gate();
        void Look(string json) => ShroudForgeJoinedNotice.Check(gate, Doc(json), (t, k, s) => shown.Add((t, k, s)));

        Look("""{"mode": "hosted"}""");
        Assert.Empty(shown);
        Look(Hosted);
        Look(Hosted);  // the status file is read again every 2 s: no spam
        Look(Hosted.Replace("altar-cleanup", "another-mod"));  // the same session: still one
        Assert.Single(shown);
        Assert.Equal(ShroudForgeJoinedNotice.Text(), shown[0].Text);
        Assert.Equal(ToastKind.Warning, shown[0].Kind);
        Assert.Equal(ShroudForgeJoinedNotice.Seconds, shown[0].Seconds);
        Look(Hosted.Replace("\"session\": 2", "\"session\": 3"));  // joined again later: told again
        Assert.Equal(2, shown.Count);
    }

    [Fact]
    public void The_toast_says_what_the_player_can_do_in_English()
    {
        Assert.Equal("A ShroudForge mod tried to change blocks, but in a joined game only the server or host can. Run that mod there.",
            ShroudForgeJoinedNotice.Text());
    }

    [Theory]
    [InlineData("""{"mode": "off"}""", true)]
    [InlineData("""{"mode": "not-installed"}""", true)]
    [InlineData("""{"mode": "self-hosted"}""", true)]
    [InlineData("""{"mode": "failed"}""", true)]
    [InlineData("""{"mode": "hosted", "runtime": "stopped"}""", true)]
    [InlineData("""{"mode": "hosted", "side": "server"}""", true)]
    [InlineData("""{"mode": "hosted", "runtime": "running"}""", false)]
    [InlineData("""{"mode": "hosted", "menu": "off"}""", false)]  // no menu shortcut: the runtime still runs mods
    public void Watching_ends_when_shroudforge_cannot_run_hosted_any_more(string json, bool final) =>
        Assert.Equal(final, ShroudForgeJoinedNotice.Final(Doc(json)));

    // The watcher reads the file every 2 s; 60 looks are its first two minutes. kiln-shroudforge writes its status at
    // Kiln's plugin step, before Kiln.Ui starts, so a start with none of this start's by then hosts no ShroudForge:
    // nothing to watch for the rest of the session.
    [Fact]
    public void Watching_ends_when_no_status_of_this_start_comes()
    {
        var other = new ModCatalog.StateFile(Doc(Hosted), Exists: true, Current: false, Error: null);  // an earlier start's
        var none = new ModCatalog.StateFile(null, Exists: false, Current: false, Error: null);
        Assert.False(ShroudForgeJoinedNotice.Done(other, 10));
        Assert.True(ShroudForgeJoinedNotice.Done(other, 61));  // was: read every 2 s for the whole session
        Assert.False(ShroudForgeJoinedNotice.Done(none, 10));
        Assert.True(ShroudForgeJoinedNotice.Done(none, 61));
    }

    [Fact]
    public void Watching_goes_on_while_this_start_hosts_shroudforge_or_the_file_is_unreadable_a_moment()
    {
        Assert.False(ShroudForgeJoinedNotice.Done(new ModCatalog.StateFile(Doc(Hosted), true, true, null), 10_000));
        // a read that failed (shared while kiln-shroudforge replaces it, cut short): the next look reads it again
        Assert.False(ShroudForgeJoinedNotice.Done(new ModCatalog.StateFile(null, true, false, "unreadable"), 10_000));
        Assert.True(ShroudForgeJoinedNotice.Done(new ModCatalog.StateFile(Doc("""{"mode": "off"}"""), true, true, null), 0));
    }
}
