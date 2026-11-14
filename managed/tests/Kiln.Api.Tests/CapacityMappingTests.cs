// SPDX-License-Identifier: MIT
// The shared capacity and registration contract (kiln_capacity.h, chapter 17 I1 and I5): layout of the mirrors, the
// mechanical native-to-managed mapping, the registration id, and the kit's ledger that fakes reuse.
using System.Runtime.InteropServices;
using FakeCapacityLedger = Kiln.Testing.FakeCapacityLedger;
using Xunit;

namespace Kiln.Api.Tests;

public sealed unsafe class CapacityMappingTests
{
    private static KilnCapacityInfo Native(uint capacity, uint game, uint kiln, uint reserved, uint flags, uint readiness,
        uint worldKind = KilnCapacityInfo.NoWorldKind, uint generation = 0, ulong revision = 5)
    {
        var n = KilnCapacityInfo.New();
        (n.Capacity, n.GameUsed, n.KilnUsed, n.Reserved, n.Flags, n.Readiness) = (capacity, game, kiln, reserved, flags, readiness);
        (n.WorldKind, n.Generation, n.Revision) = (worldKind, generation, revision);
        return n;
    }

    [Fact]
    public void Mirrors_have_the_headers_layout()
    {
        Assert.Equal(48, sizeof(KilnCapacityInfo));
        Assert.Equal(40, (int)Marshal.OffsetOf<KilnCapacityInfo>(nameof(KilnCapacityInfo.Revision)));
        Assert.Equal(64, sizeof(KilnDefinitionReply));
        Assert.Equal(48, (int)Marshal.OffsetOf<KilnDefinitionReply>(nameof(KilnDefinitionReply.SnapshotRevision)));
        Assert.Equal(56, (int)Marshal.OffsetOf<KilnDefinitionReply>(nameof(KilnDefinitionReply.ReasonCode)));
        Assert.Equal(48u, KilnCapacityInfo.New().Size);
        Assert.Equal(64u, KilnDefinitionReply.New().Size);
    }

    [Fact]
    public void A_ready_process_budget_maps_field_for_field()
    {
        var r = CapacityMapping.ToResult(Native(4096, 0, 12, 3, KilnCapacityInfo.FlagReady | KilnCapacityInfo.FlagProcess | KilnCapacityInfo.FlagExpanded, 1));
        Assert.True(r.Ok);
        Assert.Null(r.Reason);
        Assert.Equal(new CapacityInfo(4096, 0, 12, 3, CapacityReadiness.Ready, true, CapacityScope.Process, false, null, 5), r.Budget);
        Assert.Equal(4081u, r.Budget.Available);
    }

    [Fact]
    public void A_ready_world_budget_carries_its_token()
    {
        var r = CapacityMapping.ToResult(Native(896, 817, 20, 0, KilnCapacityInfo.FlagReady | KilnCapacityInfo.FlagWorld, 1, worldKind: 2, generation: 3));
        Assert.True(r.Ok);
        Assert.Equal(CapacityScope.World, r.Budget.Scope);
        Assert.Equal(new CapacityWorldToken(CapacityWorldKind.Client, 3), r.Budget.World);
        Assert.False(r.Budget.Expanded);
    }

    [Theory]
    [InlineData(0u, "NotReady")]
    [InlineData(2u, "NoWorld")]
    [InlineData(3u, "WorldEnded")]
    [InlineData(4u, "UnknownExternalUsage")]
    [InlineData(5u, "AccountingMismatch")]
    [InlineData(6u, "NotFound")]
    public void Every_unready_state_is_a_refusal_named_after_it(uint readiness, string reason)
    {
        var flags = KilnCapacityInfo.FlagWorld | (readiness == 4 ? KilnCapacityInfo.FlagExternalUnknown : 0);
        var r = CapacityMapping.ToResult(Native(0, 0, 0, 0, flags, readiness, worldKind: 0, generation: readiness == 2 ? 0u : 7u));
        Assert.False(r.Ok);
        Assert.Equal(reason, r.Reason);
        Assert.Equal((CapacityReadiness)readiness, r.Budget.Readiness);
        Assert.Equal((0u, 0u, 0u, 0u), (r.Budget.Capacity, r.Budget.GameUsed, r.Budget.KilnUsed, r.Budget.Reserved));
        Assert.Null(r.Budget.Available);
        Assert.Equal(readiness == 4, r.Budget.ExternalUsageUnknown);
        Assert.Equal(readiness == 2 ? null : new CapacityWorldToken(CapacityWorldKind.Server, 7), r.Budget.World);
    }

    [Fact]
    public void Replies_that_break_the_contract_are_a_mismatch_never_room()
    {
        const uint ready = KilnCapacityInfo.FlagReady, process = KilnCapacityInfo.FlagProcess, world = KilnCapacityInfo.FlagWorld;
        KilnCapacityInfo[] broken =
        [
            Native(64, 35, 20, 10, ready | process, 1),                    // the sum is over the capacity
            Native(64, uint.MaxValue, 1, 0, ready | process, 1),           // ... also when it would wrap in 32 bits
            Native(64, 1, 1, 1, ready | process | world, 1),               // both scope flags
            Native(64, 1, 1, 1, ready, 1),                                 // no scope flag
            Native(64, 1, 1, 1, process, 1),                               // VALID without READY
            Native(64, 1, 1, 1, ready | process, 0),                       // READY without VALID
            Native(64, 1, 1, 1, process, 2),                               // counts while not ready
            Native(64, 1, 1, 1, ready | world, 1, worldKind: 1, generation: 4),  // the prediction world is no selector
            Native(64, 1, 1, 1, ready | world, 1, worldKind: 0, generation: 0),  // a ready world budget without a world
            Native(64, 1, 1, 1, ready | process, 1, worldKind: 0),         // a process budget that names a world
            Native(64, 1, 1, 1, ready | process, 99),                      // an unknown readiness
        ];
        foreach (var n in broken)
        {
            var r = CapacityMapping.ToResult(n);
            Assert.False(r.Ok);
            Assert.Equal("AccountingMismatch", r.Reason);
            Assert.Equal(0u, r.Budget.Capacity);
            Assert.Null(r.Budget.Available);
        }
    }

    [Fact]
    public void Available_is_null_when_a_hand_made_budget_does_not_add_up()
    {
        var info = new CapacityInfo(10, 6, 6, 0, CapacityReadiness.Ready, false, CapacityScope.Process, false, null, 1);
        Assert.Null(info.Available);
        Assert.Null((info with { GameUsed = uint.MaxValue }).Available);
        Assert.Equal(0u, (info with { KilnUsed = 4 }).Available);
    }

    [Fact]
    public void Tokens_an_author_passes_are_checked()
    {
        CapacityMapping.CheckToken(new CapacityWorldToken(CapacityWorldKind.Client, 1), "world");
        Assert.Throws<ArgumentException>(() => CapacityMapping.CheckToken(new CapacityWorldToken(CapacityWorldKind.Server, 0), "world"));
        Assert.Throws<ArgumentException>(() => CapacityMapping.CheckToken(new CapacityWorldToken((CapacityWorldKind)1, 3), "world"));
    }

    [Fact]
    public void A_revision_alone_is_no_capacity_change()
    {
        var a = new CapacityInfo(10, 1, 2, 3, CapacityReadiness.Ready, false, CapacityScope.Process, false, null, 1);
        Assert.Null(CapacityMapping.Changed("x.budget", a, a with { Revision = 9 }));
        var ev = CapacityMapping.Changed("x.budget", a, a with { KilnUsed = 3, Revision = 9 });
        Assert.NotNull(ev);
        Assert.Equal(("x.budget", 2u, 3u), (ev.Quota, ev.Previous.KilnUsed, ev.Current.KilnUsed));
    }

    // ---- registration ----

    private static readonly Guid Definition = new([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
    private static byte[] Sha() => [.. Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i))];

    [Fact]
    public void The_registration_id_equals_the_native_helpers()
    {
        // native/core/tests/capacity_test.cpp prints the same vector ("registration id vector")
        var bytes = DefinitionRegistration.DeriveIdBytes("my-mod", DefinitionRegistrationKind.Buff, Definition, Sha());
        Assert.Equal("c167767190f154909c57d211561d30fd", Convert.ToHexStringLower(bytes));
        var id = DefinitionRegistration.DeriveId("my-mod", DefinitionRegistrationKind.Buff, Definition, Sha());
        Assert.Equal("c1677671-90f1-5490-9c57-d211561d30fd", id.ToString());
        Assert.Equal(bytes, DefinitionRegistration.IdBytes(id));
        Assert.NotEqual(id, DefinitionRegistration.DeriveId("my-mod", DefinitionRegistrationKind.Vfx, Definition, Sha()));
        Assert.NotEqual(id, DefinitionRegistration.DeriveId("other", DefinitionRegistrationKind.Buff, Definition, Sha()));
        Assert.Throws<ArgumentException>(() => DefinitionRegistration.DeriveId("my-mod", DefinitionRegistrationKind.Buff, Definition, new byte[31]));
        Assert.Throws<ArgumentException>(() => DefinitionRegistration.DeriveId("", DefinitionRegistrationKind.Buff, Definition, Sha()));
    }

    private static KilnDefinitionReply Reply(uint ok, DefinitionRegistrationState state, uint slots, DefinitionRefusalCode code,
        DefinitionRegistrationKind kind = DefinitionRegistrationKind.Buff)
    {
        var n = KilnDefinitionReply.New();
        (n.Ok, n.Disposition, n.SlotsConsumed, n.ReasonCode, n.Kind, n.SnapshotRevision) = (ok, (uint)state, slots, (uint)code, (uint)kind, 21);
        DefinitionRegistration.DeriveIdBytes("my-mod", kind, Definition, Sha()).CopyTo(new Span<byte>(n.RegistrationId, 16));
        Definition.ToByteArray().CopyTo(new Span<byte>(n.DefinitionGuid, 16));
        return n;
    }

    [Fact]
    public void A_registered_reply_maps_to_ok_with_its_slot_and_warnings()
    {
        var r = DefinitionRegistration.ToResult(Reply(1, DefinitionRegistrationState.Registered, 1, DefinitionRefusalCode.None),
            """{"reason":null,"warnings":["icon missing"]}""");
        Assert.True(r.Ok);
        Assert.Null(r.Reason);
        Assert.Equal((DefinitionRefusalCode.None, DefinitionRegistrationState.Registered, 1u, 21ul), (r.Code, r.State, r.SlotsConsumed, r.SnapshotRevision));
        Assert.Equal(Definition, r.Definition);
        Assert.Equal(DefinitionRegistration.DeriveId("my-mod", DefinitionRegistrationKind.Buff, Definition, Sha()), r.RegistrationId);
        Assert.Equal(["icon missing"], r.Warnings);
    }

    [Fact]
    public void Accepted_for_the_next_start_is_ok_with_no_slot()
    {
        var r = DefinitionRegistration.ToResult(Reply(1, DefinitionRegistrationState.AcceptedNextStart, 0, DefinitionRefusalCode.None), null);
        Assert.True(r.Ok);
        Assert.Equal((DefinitionRegistrationState.AcceptedNextStart, 0u), (r.State, r.SlotsConsumed));
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData(DefinitionRefusalCode.CapacityExceeded)]
    [InlineData(DefinitionRefusalCode.RegistryClosed)]
    [InlineData(DefinitionRefusalCode.StorageFailure)]
    [InlineData(DefinitionRefusalCode.NotFound)]
    [InlineData(DefinitionRefusalCode.GameRefused)]
    public void A_refusal_has_its_code_and_a_reason(DefinitionRefusalCode code)
    {
        var with = DefinitionRegistration.ToResult(Reply(0, DefinitionRegistrationState.Refused, 0, code), """{"reason":"why not","warnings":[]}""");
        Assert.False(with.Ok);
        Assert.Equal(("why not", code, DefinitionRegistrationState.Refused), (with.Reason, with.Code, with.State));
        var without = DefinitionRegistration.ToResult(Reply(0, DefinitionRegistrationState.Refused, 0, code), null);
        Assert.Equal(code.ToString(), without.Reason);  // Reason is null only when Ok
    }

    [Fact]
    public void A_retained_record_is_a_refusal_that_keeps_its_slot()
    {
        var r = DefinitionRegistration.ToResult(Reply(0, DefinitionRegistrationState.Retained, 1, DefinitionRefusalCode.RegistrationFailedRetained), null);
        Assert.False(r.Ok);
        Assert.Equal((DefinitionRegistrationState.Retained, 1u), (r.State, r.SlotsConsumed));
    }

    [Fact]
    public void Replies_that_break_the_contract_never_become_a_success()
    {
        KilnDefinitionReply[] broken =
        [
            Reply(1, DefinitionRegistrationState.Registered, 1, DefinitionRefusalCode.CapacityExceeded),  // ok with a refusal code
            Reply(1, DefinitionRegistrationState.Refused, 0, DefinitionRefusalCode.None),                 // ok and refused
            Reply(1, DefinitionRegistrationState.AcceptedNextStart, 1, DefinitionRefusalCode.None),       // a slot for later
            Reply(1, (DefinitionRegistrationState)9, 1, DefinitionRefusalCode.None),                      // an unknown state
            Reply(2, DefinitionRegistrationState.Registered, 1, DefinitionRefusalCode.None),              // ok is 0 or 1
            Reply(0, DefinitionRegistrationState.Registered, 1, DefinitionRefusalCode.None),              // not ok, yet "registered", no code
        ];
        foreach (var n in broken)
        {
            var r = DefinitionRegistration.ToResult(n, null);
            Assert.False(r.Ok);
            Assert.NotNull(r.Reason);
            Assert.NotEqual(DefinitionRefusalCode.None, r.Code);
            Assert.NotEqual(DefinitionRegistrationState.Registered, r.State);
            Assert.NotEqual(DefinitionRegistrationState.AcceptedNextStart, r.State);
        }
        var junk = DefinitionRegistration.ToResult(Reply(1, DefinitionRegistrationState.Registered, 1, DefinitionRefusalCode.None), "not json");
        Assert.True(junk.Ok);
        Assert.Single(junk.Warnings);
    }

    [Fact]
    public void Options_and_ids_an_author_passes_are_checked()
    {
        Assert.Equal(0u, DefinitionRegistration.Timing(new DefinitionRegistrationOptions(), "options"));
        Assert.Equal(1u, DefinitionRegistration.Timing(new DefinitionRegistrationOptions(DefinitionTiming.AllowNextStart), "options"));
        Assert.Throws<ArgumentNullException>(() => DefinitionRegistration.Timing(null!, "options"));
        Assert.Throws<ArgumentException>(() => DefinitionRegistration.Timing(new DefinitionRegistrationOptions((DefinitionTiming)7), "options"));
        Assert.Throws<ArgumentException>(() => DefinitionRegistration.CheckId(Guid.Empty, "id"));
        DefinitionRegistration.CheckId(Guid.NewGuid(), "id");
    }

    [Fact]
    public void A_registration_event_is_made_only_for_a_change()
    {
        var r = DefinitionRegistration.ToResult(Reply(1, DefinitionRegistrationState.Registered, 1, DefinitionRefusalCode.None), null);
        Assert.Null(DefinitionRegistration.Changed("my-mod", r, DefinitionRegistrationState.Registered, 1));
        var ev = DefinitionRegistration.Changed("my-mod", r, DefinitionRegistrationState.AcceptedNextStart, 0);
        Assert.NotNull(ev);
        Assert.Equal(("my-mod", DefinitionRegistrationState.AcceptedNextStart, 0u, 1u), (ev.ModId, ev.PreviousState, ev.PreviousSlotsConsumed, ev.Current.SlotsConsumed));
        Assert.NotNull(DefinitionRegistration.Changed("my-mod", r, null, 0));
    }

    // ---- the kit's ledger ----

    [Fact]
    public void The_ledger_counts_refuses_and_stores_by_the_one_rule()
    {
        var ledger = new FakeCapacityLedger(capacity: 2);
        byte[] a = [1], b = [2], c = [3];
        Guid ga = Guid.NewGuid(), gb = Guid.NewGuid(), gc = Guid.NewGuid();
        var first = Result(ledger.Define("m", DefinitionRegistrationKind.Buff, ga, a, DefinitionTiming.RequireRegistered));
        Assert.True(first.Ok);
        Assert.Equal((DefinitionRegistrationState.Registered, 1u), (first.State, first.SlotsConsumed));
        var again = Result(ledger.Define("m", DefinitionRegistrationKind.Buff, ga, a, DefinitionTiming.RequireRegistered));
        Assert.Equal((first.RegistrationId, first.SnapshotRevision, 1u), (again.RegistrationId, again.SnapshotRevision, again.SlotsConsumed));
        Assert.Equal(1u, ledger.Query().Budget.KilnUsed);  // an idempotent repeat is not charged twice

        ledger.Refuse[gb] = DefinitionRefusalCode.NameTaken;
        var taken = Result(ledger.Define("m", DefinitionRegistrationKind.Buff, gb, b, DefinitionTiming.AllowNextStart));
        Assert.Equal((false, DefinitionRefusalCode.NameTaken), (taken.Ok, taken.Code));  // a collision is never stored for later
        Assert.Empty(ledger.PendingFiles);

        Assert.True(Result(ledger.Define("m", DefinitionRegistrationKind.Buff, gb, b, DefinitionTiming.RequireRegistered)).Ok);
        var full = Result(ledger.Define("m", DefinitionRegistrationKind.Buff, gc, c, DefinitionTiming.AllowNextStart));
        Assert.Equal((false, DefinitionRefusalCode.CapacityExceeded, 0u), (full.Ok, full.Code, full.SlotsConsumed));
        Assert.Equal(0u, ledger.Query().Budget.Available);

        ledger.Capacity = 8;
        ledger.RegistryOpen = false;
        var closed = Result(ledger.Define("m", DefinitionRegistrationKind.Buff, gc, c, DefinitionTiming.RequireRegistered));
        Assert.Equal((false, DefinitionRefusalCode.RegistryClosed), (closed.Ok, closed.Code));
        Assert.Empty(ledger.PendingFiles);  // RequireRegistered never writes a pending file
        ledger.StorageFails = true;
        Assert.Equal(DefinitionRefusalCode.StorageFailure, Result(ledger.Define("m", DefinitionRegistrationKind.Buff, gc, c, DefinitionTiming.AllowNextStart)).Code);
        ledger.StorageFails = false;
        var later = Result(ledger.Define("m", DefinitionRegistrationKind.Buff, gc, c, DefinitionTiming.AllowNextStart));
        Assert.Equal((true, DefinitionRegistrationState.AcceptedNextStart, 0u), (later.Ok, later.State, later.SlotsConsumed));
        Assert.Equal([gc], ledger.PendingFiles);
        Assert.Equal(2u, ledger.Query().Budget.KilnUsed);  // accepted for later uses no room now

        var receipt = Result(ledger.Receipt_(later.RegistrationId, DefinitionRegistrationKind.Buff));
        Assert.Equal(DefinitionRegistrationState.AcceptedNextStart, receipt.State);
        var unknown = Result(ledger.Receipt_(Guid.NewGuid(), DefinitionRegistrationKind.Buff));
        Assert.Equal((false, DefinitionRefusalCode.NotFound), (unknown.Ok, unknown.Code));

        ledger.Readiness = CapacityReadiness.UnknownExternalUsage;
        var q = ledger.Query();
        Assert.Equal((false, "UnknownExternalUsage", true), (q.Ok, q.Reason, q.Budget.ExternalUsageUnknown));
        ledger.Readiness = CapacityReadiness.Ready;
        ledger.Reserved = 100;
        Assert.Equal("AccountingMismatch", ledger.Query().Reason);

        static DefinitionRegistrationResult Result((KilnDefinitionReply Reply, string? Reason) x) =>
            DefinitionRegistration.ToResult(x.Reply, FakeCapacityLedger.Report(x.Reason));
    }

    [Fact]
    public void A_world_ledger_answers_with_its_token()
    {
        var ledger = new FakeCapacityLedger(64, 35, new CapacityWorldToken(CapacityWorldKind.Server, 4));
        var q = ledger.Query();
        Assert.Equal((true, CapacityScope.World, 29u), (q.Ok, q.Budget.Scope, q.Budget.Available));
        Assert.Equal(new CapacityWorldToken(CapacityWorldKind.Server, 4), q.Budget.World);
    }
}
