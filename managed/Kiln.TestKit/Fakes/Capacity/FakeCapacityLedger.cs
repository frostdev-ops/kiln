// SPDX-License-Identifier: MIT
// A scriptable budget and receipt ledger for the fakes of the services that answer with kiln_capacity.h (kiln.vfx,
// kiln.gamedata, kiln.knowledge, kiln.world, kiln.worldtables): one quota, its counts, and definition requests that
// are registered, stored for the next start or refused by the rules of chapter 17 (I1, I5). It is not a service and
// not an IKitFake: a service's fake holds one ledger per quota (KitFakes.State) and lets its table functions answer
// from it, so every fake counts and refuses the same way the native helpers (kiln_capacity_util.hpp) do.
using System.Security.Cryptography;

namespace Kiln.Testing;

/// <summary>One quota of a fake service: counts, readiness and the receipts of definition requests.</summary>
internal sealed unsafe class FakeCapacityLedger
{
    private readonly Dictionary<Guid, Receipt> _receipts = [];
    private readonly Lock _gate = new();

    /// <summary>What a request left behind.</summary>
    internal sealed record Receipt(Guid RegistrationId, Guid Definition, string Mod, DefinitionRegistrationKind Kind,
        DefinitionRegistrationState State, uint Slots, DefinitionRefusalCode Code, string? Reason, ulong Revision);

    /// <summary>A ledger with <paramref name="capacity"/> units of which the game uses <paramref name="gameUsed"/>.</summary>
    public FakeCapacityLedger(uint capacity, uint gameUsed = 0, CapacityWorldToken? world = null)
    {
        Capacity = capacity;
        GameUsed = gameUsed;
        World = world;
    }

    /// <summary>The script: change these between calls.</summary>
    public uint Capacity, GameUsed, KilnUsed, Reserved;
    /// <summary>Not <see cref="CapacityReadiness.Ready"/>: every query answers that, with zero counts.</summary>
    public CapacityReadiness Readiness = CapacityReadiness.Ready;
    /// <summary>The larger quota is installed.</summary>
    public bool Expanded;
    /// <summary>The world of a per-world quota; null = a process quota.</summary>
    public CapacityWorldToken? World;
    /// <summary>False: the game's registry is closed (a request can only be stored for the next start).</summary>
    public bool RegistryOpen = true;
    /// <summary>True: storing a request for the next start fails.</summary>
    public bool StorageFails;
    /// <summary>The next request for this definition is refused with this code (a collision, an unknown base, ...).</summary>
    public Dictionary<Guid, DefinitionRefusalCode> Refuse { get; } = [];
    /// <summary>The service's revision; rises with every committed change.</summary>
    public ulong Revision { get; private set; } = 1;
    /// <summary>Requests stored for the next start, in the order they were stored.</summary>
    public List<Guid> PendingFiles { get; } = [];

    /// <summary>Fills a native capacity reply as the service would (KILN_OK).</summary>
    public void Query(KilnCapacityInfo* o)
    {
        lock (_gate)
        {
            var size = o->Size;
            *o = default;
            o->Size = size;
            o->Flags = World is null ? KilnCapacityInfo.FlagProcess : KilnCapacityInfo.FlagWorld;
            o->WorldKind = World is { } w ? (uint)w.Kind : KilnCapacityInfo.NoWorldKind;
            o->Generation = World?.Generation ?? 0;
            o->Revision = Revision;
            if (Expanded) o->Flags |= KilnCapacityInfo.FlagExpanded;
            var readiness = Readiness;
            if (readiness == CapacityReadiness.Ready && (ulong)GameUsed + KilnUsed + Reserved > Capacity)
                readiness = CapacityReadiness.AccountingMismatch;
            o->Readiness = (uint)readiness;
            if (readiness == CapacityReadiness.UnknownExternalUsage) o->Flags |= KilnCapacityInfo.FlagExternalUnknown;
            if (readiness != CapacityReadiness.Ready) return;
            o->Flags |= KilnCapacityInfo.FlagReady;
            o->Capacity = Capacity;
            o->GameUsed = GameUsed;
            o->KilnUsed = KilnUsed;
            o->Reserved = Reserved;
        }
    }

    /// <summary>The managed result of <see cref="Query"/>.</summary>
    public CapacityQueryResult Query()
    {
        var n = KilnCapacityInfo.New();
        Query(&n);
        return CapacityMapping.ToResult(n);
    }

    /// <summary>A definition request of one slot, by the one rule: an idempotent repeat answers as before; a scripted
    /// refusal, a full quota and a closed registry refuse (the last one stores the request when the timing allows
    /// it and storage works). Returns the reply and the reason text a service would put into its report JSON.</summary>
    public (KilnDefinitionReply Reply, string? Reason) Define(string mod, DefinitionRegistrationKind kind, Guid definition,
        ReadOnlySpan<byte> canonical, DefinitionTiming timing)
    {
        var id = DefinitionRegistration.DeriveId(mod, kind, definition, SHA256.HashData(canonical));
        lock (_gate)
        {
            if (_receipts.TryGetValue(id, out var before) && before.State is DefinitionRegistrationState.Registered or DefinitionRegistrationState.AcceptedNextStart
                && !(before.State == DefinitionRegistrationState.AcceptedNextStart && RegistryOpen && Readiness == CapacityReadiness.Ready))
                return (ReplyOf(before), before.Reason);
            Receipt now;
            if (Refuse.Remove(definition, out var code))
                now = Refused(code, $"scripted: {code}");
            else if (Readiness != CapacityReadiness.Ready)
                now = Later(Readiness == CapacityReadiness.NoWorld ? DefinitionRefusalCode.NoWorld : DefinitionRefusalCode.NotReady, "the service is not ready");
            else if (!RegistryOpen)
                now = Later(DefinitionRefusalCode.RegistryClosed, "the game's registry is closed for this process");
            else if ((ulong)GameUsed + KilnUsed + Reserved + 1 > Capacity)
                now = Refused(DefinitionRefusalCode.CapacityExceeded, $"the quota of {Capacity} is full");
            else
            {
                ++KilnUsed;
                now = new Receipt(id, definition, mod, kind, DefinitionRegistrationState.Registered, 1, DefinitionRefusalCode.None, null, ++Revision);
            }
            _receipts[id] = now;
            return (ReplyOf(now), now.Reason);

            Receipt Refused(DefinitionRefusalCode c, string why) =>
                new(id, definition, mod, kind, DefinitionRegistrationState.Refused, 0, c, why, Revision);
            Receipt Later(DefinitionRefusalCode c, string why)
            {
                if (timing != DefinitionTiming.AllowNextStart) return Refused(c, why);
                if (StorageFails) return Refused(DefinitionRefusalCode.StorageFailure, "the definition could not be stored for the next start");
                PendingFiles.Add(definition);
                return new Receipt(id, definition, mod, kind, DefinitionRegistrationState.AcceptedNextStart, 0, DefinitionRefusalCode.None, null, ++Revision);
            }
        }
    }

    /// <summary>The receipt of a registration id; a valid unknown id answers NotFound (ok = 0), as the services do.</summary>
    public (KilnDefinitionReply Reply, string? Reason) Receipt_(Guid registrationId, DefinitionRegistrationKind kind)
    {
        lock (_gate)
        {
            if (_receipts.TryGetValue(registrationId, out var r)) return (ReplyOf(r), r.Reason);
            var none = new Receipt(registrationId, Guid.Empty, "", kind, DefinitionRegistrationState.Refused, 0, DefinitionRefusalCode.NotFound,
                "no registration of that id", Revision);
            return (ReplyOf(none), none.Reason);
        }
    }

    /// <summary>Every receipt, for a test's assertions.</summary>
    public IReadOnlyList<Receipt> Receipts
    {
        get
        {
            lock (_gate) return [.. _receipts.Values];
        }
    }

    /// <summary>The report JSON a service sends next to a reply.</summary>
    public static string Report(string? reason, params string[] warnings) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["reason"] = reason, ["warnings"] = warnings });

    private static KilnDefinitionReply ReplyOf(Receipt r)
    {
        var n = KilnDefinitionReply.New();
        n.Ok = r.Code == DefinitionRefusalCode.None ? 1u : 0u;
        n.Disposition = (uint)r.State;
        n.SlotsConsumed = r.Slots;
        n.SnapshotRevision = r.Revision;
        n.ReasonCode = (uint)r.Code;
        n.Kind = (uint)r.Kind;
        DefinitionRegistration.IdBytes(r.RegistrationId).CopyTo(new Span<byte>(n.RegistrationId, 16));
        r.Definition.ToByteArray().CopyTo(new Span<byte>(n.DefinitionGuid, 16));
        return n;
    }
}
