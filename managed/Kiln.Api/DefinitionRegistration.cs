// SPDX-License-Identifier: MIT
// The shared shape of a definition registration that answers (native/core/abi/services/kiln_capacity.h, since
// 0.2.0-dev.28): registered now, accepted for the next start, or refused and why.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kiln;

/// <summary>What a definition call may settle for.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public enum DefinitionTiming : uint
{
    /// <summary>Register in this process, or refuse. Nothing is stored for later.</summary>
    RequireRegistered = 0,
    /// <summary>Register now when that is possible; when only the moment is wrong (the registry is closed, no world
    /// yet), store the definition so the next start registers it. A definition that is wrong, collides or does not
    /// fit is still refused.</summary>
    AllowNextStart = 1,
}

/// <summary>Options of a result-bearing definition call.</summary>
/// <param name="Timing">What the call may settle for.</param>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public sealed record DefinitionRegistrationOptions(DefinitionTiming Timing = DefinitionTiming.RequireRegistered);

/// <summary>What became of a definition.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public enum DefinitionRegistrationState : uint
{
    /// <summary>Not registered and not stored.</summary>
    Refused = 0,
    /// <summary>Really registered in this process.</summary>
    Registered = 1,
    /// <summary>Stored for the next process start; it uses no room now, and the next start checks it again.</summary>
    AcceptedNextStart = 2,
    /// <summary>A record that could not be freed stays in the game and keeps its room.</summary>
    Retained = 3,
}

/// <summary>The kinds of definition that register with a result.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public enum DefinitionRegistrationKind : uint
{
    /// <summary>A visual effect resource.</summary>
    Vfx = 0,
    /// <summary>A buff clone.</summary>
    Buff = 1,
    /// <summary>A building-material pool.</summary>
    BuildingMaterialPool = 2,
}

/// <summary>Why a definition was refused, as a value a mod can branch on.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public enum DefinitionRefusalCode : uint
{
    /// <summary>Not refused.</summary>
    None = 0,
    /// <summary>The quota is full.</summary>
    CapacityExceeded = 1,
    /// <summary>The game entry it builds on does not exist.</summary>
    UnknownBase = 2,
    /// <summary>Its id is taken.</summary>
    IdCollision = 3,
    /// <summary>Its name is taken.</summary>
    NameTaken = 4,
    /// <summary>The game's registry is closed for this process (too late in the start).</summary>
    RegistryClosed = 5,
    /// <summary>It needs a world and there is none.</summary>
    NoWorld = 6,
    /// <summary>The service is not ready yet.</summary>
    NotReady = 7,
    /// <summary>The definition could not be stored for the next start.</summary>
    StorageFailure = 8,
    /// <summary>No registration of that id.</summary>
    NotFound = 9,
    /// <summary>The mod that owns it is not active.</summary>
    OwnerInactive = 10,
    /// <summary>Registration failed after the game took the record; the record stays and keeps its room.</summary>
    RegistrationFailedRetained = 11,
    /// <summary>A later definition replaced it.</summary>
    Superseded = 12,
    /// <summary>It was removed.</summary>
    Removed = 13,
    /// <summary>The game refused it.</summary>
    GameRefused = 14,
}

/// <summary>The answer of a result-bearing definition call, and of a receipt query.</summary>
/// <param name="Ok">True for <see cref="DefinitionRegistrationState.Registered"/> and
/// <see cref="DefinitionRegistrationState.AcceptedNextStart"/>.</param>
/// <param name="Reason">Null when <paramref name="Ok"/>; else why, in words.</param>
/// <param name="Code">Why, as a value; <see cref="DefinitionRefusalCode.None"/> when <paramref name="Ok"/>.</param>
/// <param name="RegistrationId">The receipt: stable for the same mod, kind, definition and content.</param>
/// <param name="Definition">The definition's GUID.</param>
/// <param name="Kind">What was defined.</param>
/// <param name="State">What became of it.</param>
/// <param name="SlotsConsumed">The room this definition occupies now (not an increase: a repeated call returns the
/// same number); 0 when stored for the next start.</param>
/// <param name="SnapshotRevision">The service revision the answer was committed in
/// (<see cref="CapacityInfo.Revision"/> of the same service).</param>
/// <param name="Warnings">Things the author should know that did not stop it.</param>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public sealed record DefinitionRegistrationResult(bool Ok, string? Reason, DefinitionRefusalCode Code, Guid RegistrationId,
    Guid Definition, DefinitionRegistrationKind Kind, DefinitionRegistrationState State, uint SlotsConsumed,
    ulong SnapshotRevision, IReadOnlyList<string> Warnings) : IKilnResult;

/// <summary>A registration changed: a definition was registered, stored for the next start, refused after it had a
/// receipt, or a stored one was settled at this start. Posted on the bus by the owning service after the change is
/// committed. A repeated call that changes nothing posts nothing.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public sealed class DefinitionRegistrationChangedEvent : KilnEvent
{
    internal DefinitionRegistrationChangedEvent(string modId, DefinitionRegistrationResult current,
        DefinitionRegistrationState? previousState, uint previousSlots, CapacityWorldToken? world)
    {
        ModId = modId;
        Current = current;
        PreviousState = previousState;
        PreviousSlotsConsumed = previousSlots;
        World = world;
    }

    /// <summary>The mod that owns the definition.</summary>
    public string ModId { get; }

    /// <summary>The registration now (its id, definition, kind, state, room, reason and revision).</summary>
    public DefinitionRegistrationResult Current { get; }

    /// <summary>Its state before; null when it had no receipt.</summary>
    public DefinitionRegistrationState? PreviousState { get; }

    /// <summary>The room it occupied before.</summary>
    public uint PreviousSlotsConsumed { get; }

    /// <summary>The world it belongs to, for a per-world registry; else null.</summary>
    public CapacityWorldToken? World { get; }
}

/// <summary>kiln_capacity.h <c>KilnDefinitionReply</c>; for Kiln's own service libraries and the test kit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnDefinitionReply
{
    public uint Size, Ok, Disposition, SlotsConsumed;
    public fixed byte RegistrationId[16];
    public fixed byte DefinitionGuid[16];
    public ulong SnapshotRevision;
    public uint ReasonCode, Kind;

    /// <summary>A struct to hand to a native define member (its size set).</summary>
    public static KilnDefinitionReply New() => new() { Size = (uint)sizeof(KilnDefinitionReply) };
}

/// <summary>The registration id and the native-to-managed mapping (chapter 17, I1).</summary>
internal static unsafe class DefinitionRegistration
{
    private static readonly Guid Namespace = new("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77");  // as Kiln.Blocks' BlockIds

    /// <summary>The name of a kind inside a registration id.</summary>
    public static string KindName(DefinitionRegistrationKind kind) => kind switch
    {
        DefinitionRegistrationKind.Vfx => "vfx",
        DefinitionRegistrationKind.Buff => "buff",
        DefinitionRegistrationKind.BuildingMaterialPool => "building-material-pool",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The 16 bytes of a registration id (as the native side holds them): UUIDv5 in Kiln's id namespace over
    /// <c>"&lt;mod&gt;:&lt;kind&gt;:&lt;the definition's 16 bytes as hex&gt;:&lt;sha256 as hex&gt;"</c>, the same as
    /// <c>kiln::capacity::registration_id</c>. <paramref name="definition"/> is in the game's byte order
    /// (<see cref="Guid.ToByteArray()"/>).</summary>
    /// <exception cref="ArgumentException">An empty mod id, or a hash that is not 32 bytes.</exception>
    public static byte[] DeriveIdBytes(string mod, DefinitionRegistrationKind kind, Guid definition, ReadOnlySpan<byte> sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(mod);
        if (sha256.Length != 32) throw new ArgumentException("a SHA-256 hash is 32 bytes", nameof(sha256));
        Span<byte> ns = stackalloc byte[16];
        Namespace.TryWriteBytes(ns, bigEndian: true, out _);
        var text = Encoding.UTF8.GetBytes($"{mod}:{KindName(kind)}:{Convert.ToHexStringLower(definition.ToByteArray())}:{Convert.ToHexStringLower(sha256)}");
        var all = new byte[16 + text.Length];
        ns.CopyTo(all);
        text.CopyTo(all, 16);
        var h = SHA1.HashData(all);
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        return h.AsSpan(0, 16).ToArray();
    }

    /// <summary>A registration id as a <see cref="Guid"/> (its text is the UUID as written).</summary>
    public static Guid DeriveId(string mod, DefinitionRegistrationKind kind, Guid definition, ReadOnlySpan<byte> sha256) =>
        new(DeriveIdBytes(mod, kind, definition, sha256), bigEndian: true);

    /// <summary>The 16 bytes a native call takes for a registration id.</summary>
    public static byte[] IdBytes(Guid registrationId) => registrationId.ToByteArray(bigEndian: true);

    /// <summary>The result of a native reply (the call returned KILN_OK) and its report JSON
    /// (<c>{"reason": null | "text", "warnings": ["..."]}</c>; null or empty when the service sent none). A reply
    /// that breaks the contract (ok with a refusal code, a refusal without one, an unknown state) is turned into a
    /// refusal, never into a success.</summary>
    public static DefinitionRegistrationResult ToResult(in KilnDefinitionReply r, string? reportJson)
    {
        string? reason = null;
        IReadOnlyList<string> warnings = [];
        if (!string.IsNullOrEmpty(reportJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(reportJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("reason", out var jr) && jr.ValueKind == JsonValueKind.String) reason = jr.GetString();
                    if (doc.RootElement.TryGetProperty("warnings", out var jw) && jw.ValueKind == JsonValueKind.Array)
                        warnings = [.. jw.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
                }
            }
            catch (JsonException)
            {
                warnings = ["the service's report was not valid JSON"];
            }
        }
        Guid id, definition;
        fixed (byte* p = r.RegistrationId) id = new Guid(new ReadOnlySpan<byte>(p, 16), bigEndian: true);
        fixed (byte* p = r.DefinitionGuid) definition = new Guid(new ReadOnlySpan<byte>(p, 16));
        var state = r.Disposition <= (uint)DefinitionRegistrationState.Retained ? (DefinitionRegistrationState)r.Disposition : DefinitionRegistrationState.Refused;
        var code = r.ReasonCode <= (uint)DefinitionRefusalCode.GameRefused ? (DefinitionRefusalCode)r.ReasonCode : DefinitionRefusalCode.GameRefused;
        var kind = r.Kind <= (uint)DefinitionRegistrationKind.BuildingMaterialPool ? (DefinitionRegistrationKind)r.Kind : DefinitionRegistrationKind.Vfx;
        var ok = r.Ok == 1 && code == DefinitionRefusalCode.None
                 && state is DefinitionRegistrationState.Registered or DefinitionRegistrationState.AcceptedNextStart
                 && r.Disposition <= (uint)DefinitionRegistrationState.Retained
                 && r.Kind <= (uint)DefinitionRegistrationKind.BuildingMaterialPool
                 && !(state == DefinitionRegistrationState.AcceptedNextStart && r.SlotsConsumed != 0);
        if (ok) return new DefinitionRegistrationResult(true, null, DefinitionRefusalCode.None, id, definition, kind, state, r.SlotsConsumed, r.SnapshotRevision, warnings);
        if (r.Ok == 1)
        {
            // the service said ok in a shape the contract does not have: do not pass it on as a success
            reason = "the service's answer was not a valid registration result";
            state = state == DefinitionRegistrationState.Retained ? state : DefinitionRegistrationState.Refused;
        }
        else if (state is DefinitionRegistrationState.Registered or DefinitionRegistrationState.AcceptedNextStart)
        {
            state = DefinitionRegistrationState.Refused;
        }
        if (code == DefinitionRefusalCode.None) code = DefinitionRefusalCode.GameRefused;
        return new DefinitionRegistrationResult(false, reason ?? code.ToString(), code, id, definition, kind, state, r.SlotsConsumed, r.SnapshotRevision, warnings);
    }

    /// <summary>The event for a registration that changed (state or room), or null when nothing did.</summary>
    public static DefinitionRegistrationChangedEvent? Changed(string modId, DefinitionRegistrationResult current,
        DefinitionRegistrationState? previousState, uint previousSlots, CapacityWorldToken? world = null) =>
        previousState == current.State && previousSlots == current.SlotsConsumed
            ? null
            : new DefinitionRegistrationChangedEvent(modId, current, previousState, previousSlots, world);

    /// <summary>Checks the options argument of a definition call.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">An unknown timing.</exception>
    public static uint Timing(DefinitionRegistrationOptions options, string paramName)
    {
        ArgumentNullException.ThrowIfNull(options, paramName);
        if (options.Timing is not (DefinitionTiming.RequireRegistered or DefinitionTiming.AllowNextStart))
            throw new ArgumentException($"timing {(uint)options.Timing} is not RequireRegistered or AllowNextStart", paramName);
        return (uint)options.Timing;
    }

    /// <summary>Checks a registration id an author passed to a receipt query.</summary>
    /// <exception cref="ArgumentException">The empty GUID.</exception>
    public static void CheckId(Guid registrationId, string paramName)
    {
        if (registrationId == Guid.Empty) throw new ArgumentException("the empty GUID is not a registration id", paramName);
    }
}
