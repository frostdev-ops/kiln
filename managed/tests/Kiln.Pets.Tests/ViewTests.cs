// SPDX-License-Identifier: MIT
// Package 7 (19d §4.8, §5.4, gate 7): the owners' view. The codec round-trips and refuses oversized, malformed and
// out-of-range messages before it allocates; the cache takes only increasing sequences of its session, never a delayed old
// connection's, goes stale after 3 s and clears; the publisher sends each owner only their own pets, at most 5 Hz while
// dirty and 1 Hz as a keepalive, with a fresh token per join; the bar's geometry; HUD options are checked.
using System.Buffers.Binary;
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

public sealed class ViewTests : IDisposable
{
    public ViewTests() => PetsClient.ResetForTests();
    public void Dispose() => PetsClient.ResetForTests();

    private static PetViewEntry Entry(int n, PetPresence presence = PetPresence.Live, PetLife life = PetLife.Ready, int cur = 50, int max = 100) => new()
    {
        Id = new PetId(new Guid(n, 0, 0, new byte[8])), Definition = "mod-a:wolf", Label = $"Rex {n}", Life = life, Presence = presence,
        Body = presence == PetPresence.Live ? new PetBodyViewKey(3000u + (uint)n, 7) : null,
        RecoveryAnchor = presence == PetPresence.RecoveryAnchor ? new WorldPosition(1, 2, 3) : null, Level = 5, Current = cur, Maximum = max,
        StateRevision = 9, DurableRevision = 8,
    };

    private static PetOwnerView View(ulong seq, ulong session = 77, params PetViewEntry[] entries) => new(1, session, seq, entries);

    [Fact]
    public void The_codec_round_trips_every_field()
    {
        var e = Entry(1) with { Transition = PetTransition.Reviving, ReviveOperation = Guid.NewGuid(), ReviveElapsedMs = 1500, ReviveDurationMs = 3000, ShowMarker = false };
        var a = Entry(2, PetPresence.RecoveryAnchor, PetLife.Downed, 0, 100);
        var v = View(5, 77, e, a);
        var back = PetViewCodec.Read(PetViewCodec.Write(v), out var error);
        Assert.Null(error);
        Assert.Equal((v.WorldEpoch, v.CharacterSession, v.Sequence), (back!.WorldEpoch, back.CharacterSession, back.Sequence));
        Assert.Equal(e, back.Entries[0]);
        Assert.Equal(a, back.Entries[1]);
    }

    [Fact]
    public void Malformed_or_oversized_messages_are_refused()
    {
        var good = PetViewCodec.Write(View(1, 77, Entry(1)));
        Assert.NotNull(PetViewCodec.Read(good, out _));
        Assert.Null(PetViewCodec.Read(good.AsSpan(0, good.Length - 1), out var truncated));
        Assert.Equal("truncated", truncated);
        Assert.Null(PetViewCodec.Read([.. good, 0], out var trailing));
        Assert.Equal("trailing bytes", trailing);
        var wrongProtocol = good.ToArray();
        wrongProtocol[0] = 2;
        Assert.Null(PetViewCodec.Read(wrongProtocol, out _));
        Assert.Null(PetViewCodec.Read(new byte[PetViewCodec.MaxBytes + 1], out var big));
        Assert.Equal("oversized", big);
        // a count beyond the cap is refused before any entry is read
        var many = good.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(many.AsSpan(26), 500);
        Assert.Null(PetViewCodec.Read(many, out var count));
        Assert.Equal("too many entries", count);
        // an entry length beyond 384 bytes
        var longEntry = good.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(longEntry.AsSpan(28), 400);
        Assert.Null(PetViewCodec.Read(longEntry, out var entry));
        Assert.Equal("entry too large", entry);
        // a label length beyond 64 bytes is refused before it is decoded
        var longLabel = good.ToArray();
        var labelLenAt = 30 + 16 + 1 + "mod-a:wolf".Length;
        longLabel[labelLenAt] = 65;
        Assert.Null(PetViewCodec.Read(longLabel, out _));
        // a session of 0 is no session
        var noSession = PetViewCodec.Write(View(1, 0, Entry(1)));
        Assert.Null(PetViewCodec.Read(noSession, out _));
    }

    [Fact]
    public void Long_names_are_cut_on_a_character_boundary()
    {
        var e = Entry(1) with { Label = new string('é', 60) };   // 120 UTF-8 bytes
        var back = PetViewCodec.Read(PetViewCodec.Write(View(1, 77, e)), out _)!;
        Assert.Equal(new string('é', 32), back.Entries[0].Label);
    }

    [Fact]
    public void The_cache_takes_increasing_sequences_of_its_session_only()
    {
        var c = new PetViewCache();
        Assert.True(c.Accept(View(1), 0));
        Assert.True(c.Accept(View(2), 0.1));
        Assert.False(c.Accept(View(2), 0.2));                  // a repeat
        Assert.False(c.Accept(View(1), 0.3));                  // out of order
        Assert.False(c.Accept(View(9, session: 5), 0.4));      // a delayed packet of an old connection
        Assert.Equal(2ul, c.View!.Sequence);
        Assert.False(c.Accept(new PetOwnerView(2, 77, 5, []), 0.5));   // another world epoch
    }

    [Fact]
    public void The_cache_goes_stale_after_three_seconds_and_a_new_join_then_wins()
    {
        var c = new PetViewCache();
        c.Accept(View(4), 0);
        Assert.False(c.Tick(2.9));
        Assert.True(c.Tick(3.0));
        Assert.Equal(PetViewState.Stale, c.State);
        Assert.True(c.Accept(View(1, session: 99), 3.1));      // a new join after the old one went quiet
        Assert.Equal(PetViewState.Current, c.State);
        Assert.True(c.Clear());
        Assert.Equal(PetViewState.Disconnected, c.State);
        Assert.Null(c.View);
        Assert.True(c.Accept(View(1, session: 100), 4));       // after a disconnect any session starts fresh
    }

    [Fact]
    public void The_publisher_sends_each_owner_only_their_own_view_at_its_cadence()
    {
        using var t = new RosterTestsHost();
        var a = t.R.Summoned(t.R.A);
        t.R.World.Join(t.R.B);
        var p = new PetViewPublisher { NewToken = () => 1234 };
        var sent = new List<(CharacterKey Owner, PetOwnerView View)>();
        bool Send(PetOwner o, byte[] b) { sent.Add((o.Key, PetViewCodec.Read(b, out _)!)); return true; }
        var online = t.R.World.OnlineOwners();
        p.Step(t.R.Roster, online, 1, 0, Send);
        Assert.Equal(2, sent.Count);                                     // a first full snapshot each
        var mine = sent.Single(s => s.Owner == t.R.A).View;
        Assert.Single(mine.Entries);
        Assert.Equal(a.Id, mine.Entries[0].Id);
        Assert.Empty(sent.Single(s => s.Owner == t.R.B).View.Entries);  // never another character's pets
        sent.Clear();
        p.Step(t.R.Roster, online, 1, 0.5, Send);
        Assert.Empty(sent);                                              // nothing changed, keepalive not due
        p.MarkDirty(t.R.A);
        p.Step(t.R.Roster, online, 1, 0.6, Send);
        Assert.Single(sent);                                             // dirty: sent (0.2 s since the last)
        p.MarkDirty(t.R.A);
        p.Step(t.R.Roster, online, 1, 0.7, Send);
        Assert.Single(sent);                                             // at most 5 Hz
        p.Step(t.R.Roster, online, 1, 1.6, Send);
        Assert.Equal(3, sent.Count);                                     // A's dirty one, then B's keepalive
        var seqs = sent.Where(s => s.Owner == t.R.A).Select(s => s.View.Sequence).ToList();
        Assert.True(seqs.SequenceEqual(seqs.Order()) && seqs.Distinct().Count() == seqs.Count);
    }

    [Fact]
    public void A_new_join_gets_a_new_token_and_its_sequence_starts_again()
    {
        using var t = new RosterTestsHost();
        t.R.World.Join(t.R.A);
        ulong next = 10;
        var p = new PetViewPublisher { NewToken = () => next++ };
        var sent = new List<PetOwnerView>();
        p.Step(t.R.Roster, t.R.World.OnlineOwners(), 1, 0, (o, b) => { sent.Add(PetViewCodec.Read(b, out var err)!); return true; });
        t.R.World.Leave(t.R.A);
        t.R.World.Join(t.R.A);   // another session
        p.Step(t.R.Roster, t.R.World.OnlineOwners(), 1, 0.1, (o, b) => { sent.Add(PetViewCodec.Read(b, out var err)!); return true; });
        Assert.Equal(2, sent.Count);
        Assert.NotEqual(sent[0].CharacterSession, sent[1].CharacterSession);
        Assert.Equal(1ul, sent[1].Sequence);
    }

    [Fact]
    public void An_unsent_view_is_tried_again_and_its_sequence_does_not_skip()
    {
        using var t = new RosterTestsHost();
        t.R.World.Join(t.R.A);
        var p = new PetViewPublisher();
        var sent = new List<PetOwnerView>();
        p.Step(t.R.Roster, t.R.World.OnlineOwners(), 1, 0, (_, _) => false);   // no channel on that game yet
        p.Step(t.R.Roster, t.R.World.OnlineOwners(), 1, 0.3, (o, b) => { sent.Add(PetViewCodec.Read(b, out var err)!); return true; });
        Assert.Equal(1ul, sent.Single().Sequence);
    }

    [Fact]
    public void The_bar_shows_live_pets_first_at_most_six_rows_with_clamped_fractions()
    {
        var entries = new List<PetViewEntry>
        {
            Entry(9, PetPresence.RecoveryAnchor, PetLife.Downed, 0, 100),
            Entry(3, cur: 150, max: 100),
            Entry(1, PetPresence.Absent, cur: 10, max: 0),
            Entry(2),
            Entry(4) with { ShowBar = false },
        };
        for (var i = 10; i < 16; i++) entries.Add(Entry(i));
        var rows = PetHudLayout.Rows(View(1, 77, [.. entries]), new PetHudOptions(), 1000, 1000, 2);
        Assert.Equal(PetHudLayout.MaxRows, rows.Count);
        Assert.All(rows, r => Assert.Equal(PetPresence.Live, r.Entry.Presence));
        Assert.Equal(new PetId(new Guid(2, 0, 0, new byte[8])), rows[0].Entry.Id);   // by PetId
        Assert.Equal(1f, rows.Single(r => r.Entry.Label == "Rex 3").Fraction);       // clamped
        Assert.Equal((30f, 720f, 440f, 28f), (rows[0].X, rows[0].Y, rows[0].Width, rows[0].BarHeight));
        Assert.Equal(720f + 48f, rows[1].Y);                                          // rows 24 x scale
        Assert.DoesNotContain(rows, r => r.Entry.Label == "Rex 4");                   // the definition hid it
        var few = PetHudLayout.Rows(View(1, 77, entries[0], entries[2]), new PetHudOptions(), 1000, 1000, 1);
        Assert.Equal(["Downed", "Ready"], few.Select(r => r.Status));
        Assert.Equal(0f, few[1].Fraction);                                            // max 0: fraction 0
        Assert.Empty(PetHudLayout.Rows(View(1, 77, [.. entries]), new PetHudOptions(Enabled: false), 1000, 1000, 1));
    }

    [Fact]
    public void Hud_options_are_checked()
    {
        Assert.Throws<ArgumentException>(() => PetsClient.Configure(new PetHudOptions(AnchorX: 1.5)));
        Assert.Throws<ArgumentException>(() => PetsClient.Configure(new PetHudOptions(AnchorY: double.NaN)));
        Assert.Throws<ArgumentException>(() => PetsClient.Configure(new PetHudOptions(WidthPixels: 60)));
        PetsClient.Configure(new PetHudOptions(Enabled: false, WidthPixels: 300));
        Assert.False(PetsClient.Options.Enabled);
    }

    [Fact]
    public void The_client_keeps_one_view_and_its_state()
    {
        Assert.Equal(PetViewState.Disconnected, PetsClient.State);
        Assert.True(PetsClient.Receive(View(1, 77, Entry(1))));
        Assert.Equal(PetViewState.Current, PetsClient.State);
        Assert.False(PetsClient.Receive(View(1, 77, Entry(1))));
        Assert.Single(PetsClient.Snapshot!.Entries);
        PetsClient.Disconnected();
        Assert.Null(PetsClient.Snapshot);
        Assert.Equal(PetViewState.Disconnected, PetsClient.State);
    }
}

/// <summary>A roster on fakes for tests outside RosterTests (disposes it).</summary>
internal sealed class RosterTestsHost : IDisposable
{
    public readonly RosterTests R = new();
    public void Dispose() => R.Dispose();
}
