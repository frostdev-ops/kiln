// SPDX-License-Identifier: MIT
// Kiln.Assets: the content blob catalog (kiln_assets.h blob_catalog_open/read/close, since 0.2.0-dev.28; capability
// assets.blob_catalog). An immutable, paged snapshot of every content blob the game serves: the game's own content index
// and every live registration (assets.json, managed, native), with the origins of each and the registration served now.
// The C# equivalent of ShroudForge's file-content listing and EML's get_all_contents, as a live served catalog (not an
// offline archive listing). Records and JSON share one shape: schema/blob-catalog.schema.json. Guide:
// docs/kiln/services/assets.md#the-content-blob-catalog.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>What <see cref="Assets.OpenBlobCatalog"/> builds.</summary>
public enum BlobCatalogMode
{
    /// <summary>The game's content index and every registration. Refused (<see cref="KilnUnavailableException"/>) while the
    /// game's index cannot be read yet.</summary>
    RequireComplete = 0,

    /// <summary>The registrations only (assets.json, managed, native), status <see cref="BlobCatalogStatus.Incomplete"/>:
    /// not the game's contents.</summary>
    RegistrationsOnly = 1,
}

/// <summary>What a catalog open answered (<see cref="BlobCatalogInfo.Status"/>).</summary>
public enum BlobCatalogStatus
{
    /// <summary>The game's index and every registration.</summary>
    Complete = 0,

    /// <summary>The registrations only (<see cref="BlobCatalogMode.RegistrationsOnly"/>).</summary>
    Incomplete = 1,

    /// <summary>The game's index cannot be read yet, or moved while it was copied.</summary>
    NotReady = 2,

    /// <summary>A budget would be exceeded; nothing was opened.</summary>
    LimitExceeded = 3,

    /// <summary>The content hooks are off on this build, or the game's index is laid out otherwise.</summary>
    Unavailable = 4,
}

/// <summary>Why a catalog open was refused (<see cref="BlobCatalogInfo.Reason"/>).</summary>
public enum BlobCatalogReason
{
    /// <summary>It was not.</summary>
    None = 0,

    /// <summary>The game has not opened its content index yet.</summary>
    GameNotReady = 1,

    /// <summary>A read of the game's index faulted, or it changed while it was copied.</summary>
    UnreadableMap = 2,

    /// <summary>More than 1,000,000 entries.</summary>
    EntryBudget = 3,

    /// <summary>Over 64 MiB for the snapshot, 128 MiB for every snapshot held, or 64 snapshots open.</summary>
    MemoryBudget = 4,

    /// <summary>The content hooks are off, or the game's index is not laid out as kiln.assets reads it.</summary>
    UnsupportedTable = 5,
}

/// <summary>Where the bytes of a content blob come from (<see cref="BlobCatalogEntry.Origins"/>). Descriptive: a native
/// caller says what it is.</summary>
[Flags]
public enum BlobOrigins
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The game's own content index lists the hash.</summary>
    Game = 1,

    /// <summary>A mod's assets/assets.json (its icons and textures too).</summary>
    Manifest = 2,

    /// <summary><see cref="Assets.AddBlob"/> or <see cref="Assets.OverrideBlob"/>.</summary>
    Managed = 4,

    /// <summary>Any other caller of kiln.assets (a native mod or plugin; icons and textures registered by call).</summary>
    NativeApi = 8,
}

/// <summary>What changed the catalog (<see cref="AssetsCatalogChangedEvent.Reason"/>).</summary>
public enum AssetsCatalogChange
{
    /// <summary>Nothing said.</summary>
    None = 0,

    /// <summary>The game's content index became readable.</summary>
    Ready = 1,

    /// <summary>A blob was registered (added or overridden).</summary>
    Registration = 2,

    /// <summary>A blob registration was removed.</summary>
    Remove = 3,

    /// <summary>A mod's registrations went with it (unloaded, or its owner revoked).</summary>
    Revoke = 4,
}

/// <summary>A catalog snapshot's header (<see cref="BlobCatalogSnapshot.Info"/>), or why an open was refused
/// (<see cref="BlobCatalogOpenResult.Info"/>). JSON: <c>schema/blob-catalog.schema.json</c> <c>info</c> (the uint64 fields
/// as decimal strings).</summary>
/// <param name="Generation">The catalog's generation the snapshot was built at; it moves on every registration, removal,
/// mod unload and readiness change.</param>
/// <param name="Total">Entries in the snapshot; 0 without one.</param>
/// <param name="Status">Complete, incomplete, or why there is no snapshot.</param>
/// <param name="GameComplete">The game's whole content index is in it.</param>
/// <param name="RegistrationsComplete">Every live registration is in it.</param>
/// <param name="Reason">Why it was refused; <see cref="BlobCatalogReason.None"/> with a snapshot.</param>
[JsonConverter(typeof(BlobCatalogJson.InfoConverter))]
public sealed record BlobCatalogInfo(ulong Generation, ulong Total, BlobCatalogStatus Status, bool GameComplete,
    bool RegistrationsComplete, BlobCatalogReason Reason);

/// <summary>One content blob of a catalog snapshot. JSON: <c>schema/blob-catalog.schema.json</c> <c>entry</c>.</summary>
/// <param name="ContentHash">The blob's hash.</param>
/// <param name="Origins">Every origin contributing it: <see cref="BlobOrigins.Game"/> when the game's index lists it (also
/// when a mod overrides it), plus the origin of every live registration.</param>
/// <param name="SelectedOrigin">Whose bytes are served now: one origin; <see cref="BlobOrigins.Game"/> for the game's own.</param>
/// <param name="Owner">The served registration's native owner; 0 for the game's own.</param>
/// <param name="Sequence">The served registration's number (kiln.assets counts every registration); 0 for the game's own.</param>
/// <param name="Overridden">The game lists the hash and a registration's bytes are served in place of the game's.</param>
[JsonConverter(typeof(BlobCatalogJson.EntryConverter))]
public sealed record BlobCatalogEntry(ContentHash ContentHash, BlobOrigins Origins, BlobOrigins SelectedOrigin, ulong Owner,
    ulong Sequence, bool Overridden);

/// <summary>What <see cref="Assets.OpenBlobCatalog"/> answered: a snapshot, or (<see cref="Ok"/> false) a budget refusal
/// with its reason.</summary>
/// <param name="Ok">A snapshot was opened.</param>
/// <param name="Reason">Why not, in words; null when <see cref="Ok"/>.</param>
/// <param name="Snapshot">The snapshot (dispose it); null when not <see cref="Ok"/>.</param>
/// <param name="Info">The snapshot's header, or the refusal's status and reason.</param>
public sealed record BlobCatalogOpenResult(bool Ok, string? Reason, BlobCatalogSnapshot? Snapshot, BlobCatalogInfo Info) : IKilnResult;

/// <summary>An immutable snapshot of the content blob catalog: it never changes, whatever is registered after it was
/// opened (open a new one to see that). Read it a page at a time; dispose it when done (Kiln closes what a mod leaves open
/// when the mod unloads). Any thread.</summary>
public sealed unsafe class BlobCatalogSnapshot : IDisposable
{
    /// <summary>The most entries <see cref="ReadPage"/> copies at once.</summary>
    public const int MaxPage = 256;

    private readonly AssetsNative.TableEx* _table;
    private ulong _handle;  // 0 once closed

    internal BlobCatalogSnapshot(AssetsNative.TableEx* table, ulong handle, BlobCatalogInfo info)
    {
        _table = table;
        _handle = handle;
        Info = info;
    }

    /// <summary>The snapshot's header: generation, total, status and completeness.</summary>
    public BlobCatalogInfo Info { get; }

    /// <summary>Entries in the snapshot.</summary>
    public ulong Total => Info.Total;

    /// <summary>Entries <paramref name="first"/> onward, at most <paramref name="capacity"/>, sorted by (size, hash0, hash1,
    /// hash2): bounded copies, never borrowed memory. Empty when <paramref name="first"/> is <see cref="Total"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="first"/> is past <see cref="Total"/>, or
    /// <paramref name="capacity"/> is not 1 to <see cref="MaxPage"/>.</exception>
    /// <exception cref="ObjectDisposedException">The snapshot was disposed, or closed when its mod unloaded.</exception>
    [AnyThread]
    public IReadOnlyList<BlobCatalogEntry> ReadPage(ulong first, int capacity = MaxPage)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(first, Total);
        if (capacity is < 1 or > MaxPage) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, $"1 to {MaxPage} entries");
        var handle = Volatile.Read(ref _handle);
        ObjectDisposedException.ThrowIf(handle == 0, this);
        var raw = stackalloc AssetsNative.CatalogEntry[capacity];
        nuint written = 0;
        var rc = _table->BlobCatalogRead(handle, first, raw, (uint)sizeof(AssetsNative.CatalogEntry), (nuint)capacity, &written);
        if (rc == AssetsNative.NotFound)
        {
            Volatile.Write(ref _handle, 0);
            throw new ObjectDisposedException(nameof(BlobCatalogSnapshot), "the catalog snapshot was closed (its mod unloaded)");
        }
        if (rc != AssetsNative.Ok) throw new InvalidOperationException($"ReadPage: kiln.assets answered {rc}");
        var page = new BlobCatalogEntry[(int)written];
        for (var i = 0; i < page.Length; i++) page[i] = Assets.EntryOf(raw[i]);
        return page;
    }

    /// <summary>Closes the snapshot. A second call does nothing.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) _table->BlobCatalogClose(handle);
    }
}

/// <summary>The content blob catalog changed: a blob was registered or a registration removed, a mod's registrations went
/// with it, or the game's content index became readable. Posted on the tick thread by a check every 25 steps, only while
/// someone subscribes; several changes between two checks are one event with the newest generation and the last change.
/// An open snapshot never changes: open a new one to see the change. Capability <c>assets.blob_catalog</c>.</summary>
public sealed class AssetsCatalogChangedEvent : KilnEvent
{
    internal AssetsCatalogChangedEvent(ulong generation, AssetsCatalogChange reason)
    {
        Generation = generation;
        Reason = reason;
    }

    /// <summary>The catalog's generation now (<see cref="BlobCatalogInfo.Generation"/> of a snapshot opened now).</summary>
    public ulong Generation { get; }

    /// <summary>The last change.</summary>
    public AssetsCatalogChange Reason { get; }

    /// <summary>The change in one line, for a log.</summary>
    public override string ToString() => $"blob catalog changed: generation {Generation} ({Reason})";
}

public static unsafe partial class Assets
{
    /// <summary>
    /// A snapshot of every content blob the game serves: the game's own content index and every live registration
    /// (assets.json, <see cref="AddBlob"/>/<see cref="OverrideBlob"/>, native), one entry per hash, with the origins that
    /// contribute it and the registration served now. <see cref="BlobCatalogMode.RegistrationsOnly"/> lists the
    /// registrations alone, visibly incomplete. Read pages with <see cref="BlobCatalogSnapshot.ReadPage"/>, the bytes of one
    /// blob with <see cref="ReadBlob"/>. Dispose the snapshot. Call it from mod code: the mod owns the snapshot.
    /// </summary>
    /// <returns>A snapshot; or <see cref="BlobCatalogOpenResult.Ok"/> false when a budget would be exceeded (1,000,000
    /// entries, 64 MiB for it, 128 MiB for every snapshot held, 64 open): never a truncated one.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a <see cref="BlobCatalogMode"/>.</exception>
    /// <exception cref="KilnUnavailableException"><c>assets.blob_catalog</c> is off, or (the default mode) the game's content
    /// index cannot be read yet.</exception>
    /// <exception cref="InvalidOperationException">Not called from mod code.</exception>
    [AnyThread]
    [RequiresCapability(Caps.Catalog)]
    public static BlobCatalogOpenResult OpenBlobCatalog(BlobCatalogMode mode = BlobCatalogMode.RequireComplete)
    {
        if (mode is not (BlobCatalogMode.RequireComplete or BlobCatalogMode.RegistrationsOnly))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "RequireComplete or RegistrationsOnly");
        Rows.Ensure();
        var ex = TEx;
        if (ex == null || ex->BlobCatalogOpen == null)
            throw ServiceCaps.Unavailable(Caps.Catalog, T == null ? "kiln.assets is not loaded"
                : "kiln.assets is older than 0.2.0-dev.28: it has no blob catalog");
        if (!Capabilities.Is(Caps.Catalog)) throw ServiceCaps.Unavailable(Caps.Catalog);
        var owner = Owner();
        var raw = new AssetsNative.CatalogInfo { Size = (uint)sizeof(AssetsNative.CatalogInfo) };
        ulong handle = 0;
        var rc = ex->BlobCatalogOpen(owner, (uint)mode, &handle, &raw);
        if (rc == AssetsNative.InvalidArgument) throw new ArgumentException("OpenBlobCatalog: kiln.assets refused the arguments");
        var info = InfoOf(raw);
        if (rc == AssetsNative.Ok && handle != 0) return new BlobCatalogOpenResult(true, null, new BlobCatalogSnapshot(ex, handle, info), info);
        return info.Status switch
        {
            BlobCatalogStatus.LimitExceeded => new BlobCatalogOpenResult(false, Why(info), null, info),
            BlobCatalogStatus.NotReady or BlobCatalogStatus.Unavailable => throw ServiceCaps.Unavailable(Caps.Catalog, Why(info)),
            _ => throw new InvalidOperationException($"OpenBlobCatalog: kiln.assets answered {rc} ({info.Status})"),
        };
    }

    /// <summary>The bytes the game reads for a content blob now: a registration's, else the game's own from its container
    /// (kiln.assets' blob_read). Null when no blob has the hash.</summary>
    [AnyThread]
    [RequiresCapability(Caps.Content)]
    public static byte[]? ReadBlob(ContentHash hash)
    {
        var t = T;
        if (t == null)
        {
            Rows.Ensure();
            throw ServiceCaps.Unavailable(Caps.Content, "kiln.assets is not loaded");
        }
        var h = new AssetsNative.ContentHash { Size = hash.Size, H0 = hash.H0, H1 = hash.H1, H2 = hash.H2 };
        nuint n = 0;
        var rc = t->BlobRead(&h, null, 0, &n);
        if (rc == AssetsNative.NotFound) return null;
        if (rc != AssetsNative.BufferTooSmall && rc != AssetsNative.Ok) Check(rc, "ReadBlob", Caps.Content);
        var buf = new byte[(int)n];
        fixed (byte* p = buf) rc = t->BlobRead(&h, p, n, &n);
        if (rc == AssetsNative.NotFound) return null;
        Check(rc, "ReadBlob", Caps.Content);
        return buf;
    }

    private static string Why(BlobCatalogInfo info) => info.Reason switch
    {
        BlobCatalogReason.GameNotReady => "the game has not opened its content index yet (try again later, or open RegistrationsOnly)",
        BlobCatalogReason.UnreadableMap => "the game's content index could not be read whole (it faulted or moved while copied)",
        BlobCatalogReason.EntryBudget => "the catalog would hold more than 1,000,000 entries",
        BlobCatalogReason.MemoryBudget => "a budget would be exceeded (64 MiB per snapshot, 128 MiB for all held, 64 open)",
        BlobCatalogReason.UnsupportedTable => "the content hooks are off on this build, or the game's content index is laid out otherwise",
        _ => $"the catalog answered {info.Status}",
    };

    internal static BlobCatalogInfo InfoOf(in AssetsNative.CatalogInfo i) => new(
        i.Generation, i.Total, (BlobCatalogStatus)i.Status, (i.Flags & AssetsNative.CatalogGameComplete) != 0,
        (i.Flags & AssetsNative.CatalogRegistrationsComplete) != 0, (BlobCatalogReason)i.Reason);

    internal static BlobCatalogEntry EntryOf(in AssetsNative.CatalogEntry e) => new(
        new ContentHash(e.Hash.Size, e.Hash.H0, e.Hash.H1, e.Hash.H2), (BlobOrigins)e.Origins, (BlobOrigins)e.SelectedOrigin,
        e.SelectedOwner, e.SelectedSequence, (e.Flags & AssetsNative.CatalogOverridden) != 0);

    // ---- AssetsCatalogChangedEvent: kiln.assets' status says the generation and the last change ----
    private static IDisposable? _catalogTick;
    private static ulong _catalogGeneration;

    /// <summary>AssetsStartup (the first service tick): the check every 25 steps.</summary>
    internal static void StartCatalogWatch()
    {
        lock (Lock)
        {
            _catalogTick?.Dispose();
            _catalogGeneration = 0;
            _catalogTick = ServiceTick.Add("kiln.assets catalog", static (in TickInfo tick) =>
            {
                if (tick.Step % 25 == 0) PollCatalog();
            });
        }
    }

    /// <summary>One check (tick thread): posts the event when the generation moved since the last one (the first check
    /// only notes it).</summary>
    internal static void PollCatalog()
    {
        if (TEx == null) return;
        var (generation, change) = CatalogState(Status());
        if (generation == 0) return;
        var was = _catalogGeneration;
        _catalogGeneration = generation;
        if (was == 0 || was == generation || !Events.HasSubscribers<AssetsCatalogChangedEvent>()) return;
        Events.Post(new AssetsCatalogChangedEvent(generation, change));
    }

    /// <summary>The <c>"catalog":{"generation":N,"change":"..."}</c> part of kiln.assets' status; (0, None) without one.</summary>
    internal static (ulong Generation, AssetsCatalogChange Change) CatalogState(string status)
    {
        var m = System.Text.RegularExpressions.Regex.Match(status, "\"catalog\":\\{\"generation\":([0-9]+),\"change\":\"([a-z]+)\"\\}");
        if (!m.Success || !ulong.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var gen)) return (0, AssetsCatalogChange.None);
        var change = m.Groups[2].Value switch
        {
            "ready" => AssetsCatalogChange.Ready,
            "registration" => AssetsCatalogChange.Registration,
            "remove" => AssetsCatalogChange.Remove,
            "revoke" => AssetsCatalogChange.Revoke,
            _ => AssetsCatalogChange.None,
        };
        return (gen, change);
    }
}

/// <summary>The JSON of the catalog records (schema/blob-catalog.schema.json): camelCase names, uint64 fields as decimal
/// strings, enums as camelCase names, origins as an array of names, the hash as "size-h0-h1-h2" (decimal size, hex words).</summary>
internal static class BlobCatalogJson
{
    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static T Parse<T>(string? text, string field) where T : struct, Enum =>
        Enum.GetValues<T>().FirstOrDefault(v => Name(v) == text) is var v && Name(v) == text
            ? v
            : throw new JsonException($"{field}: unknown value '{text}'");

    private static ulong U64(ref Utf8JsonReader r, string field) =>
        r.TokenType == JsonTokenType.String && ulong.TryParse(r.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new JsonException($"{field}: a decimal string is required");

    private static readonly BlobOrigins[] OriginBits = [BlobOrigins.Game, BlobOrigins.Manifest, BlobOrigins.Managed, BlobOrigins.NativeApi];

    internal static ContentHash ParseHash(string? text)
    {
        var p = (text ?? "").Split('-');
        if (p.Length == 4 && uint.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size) &&
            uint.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h0) &&
            uint.TryParse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h1) &&
            uint.TryParse(p[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h2))
            return new ContentHash(size, h0, h1, h2);
        throw new JsonException($"contentHash: '{text}' is not size-h0-h1-h2");
    }

    internal sealed class InfoConverter : JsonConverter<BlobCatalogInfo>
    {
        public override void Write(Utf8JsonWriter w, BlobCatalogInfo v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            w.WriteString("generation", v.Generation.ToString(CultureInfo.InvariantCulture));
            w.WriteString("total", v.Total.ToString(CultureInfo.InvariantCulture));
            w.WriteString("status", Name(v.Status));
            w.WriteBoolean("gameComplete", v.GameComplete);
            w.WriteBoolean("registrationsComplete", v.RegistrationsComplete);
            w.WriteString("reason", Name(v.Reason));
            w.WriteEndObject();
        }

        public override BlobCatalogInfo Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("info: an object is required");
            ulong? generation = null, total = null;
            BlobCatalogStatus? status = null;
            bool? game = null, regs = null;
            BlobCatalogReason? reason = null;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                var name = r.GetString();
                r.Read();
                switch (name)
                {
                    case "generation": generation = U64(ref r, name); break;
                    case "total": total = U64(ref r, name); break;
                    case "status": status = Parse<BlobCatalogStatus>(r.GetString(), name); break;
                    case "gameComplete": game = r.GetBoolean(); break;
                    case "registrationsComplete": regs = r.GetBoolean(); break;
                    case "reason": reason = Parse<BlobCatalogReason>(r.GetString(), name); break;
                    default: throw new JsonException($"info: unknown field '{name}'");
                }
            }
            if (generation is null || total is null || status is null || game is null || regs is null || reason is null)
                throw new JsonException("info: generation, total, status, gameComplete, registrationsComplete and reason are required");
            return new BlobCatalogInfo(generation.Value, total.Value, status.Value, game.Value, regs.Value, reason.Value);
        }
    }

    internal sealed class EntryConverter : JsonConverter<BlobCatalogEntry>
    {
        public override void Write(Utf8JsonWriter w, BlobCatalogEntry v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            w.WriteString("contentHash", v.ContentHash.ToString());
            w.WriteStartArray("origins");
            foreach (var bit in OriginBits)
                if ((v.Origins & bit) != 0) w.WriteStringValue(Name(bit));
            w.WriteEndArray();
            w.WriteString("selectedOrigin", Name(v.SelectedOrigin));
            w.WriteString("owner", v.Owner.ToString(CultureInfo.InvariantCulture));
            w.WriteString("sequence", v.Sequence.ToString(CultureInfo.InvariantCulture));
            w.WriteBoolean("overridden", v.Overridden);
            w.WriteEndObject();
        }

        public override BlobCatalogEntry Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("entry: an object is required");
            ContentHash? hash = null;
            BlobOrigins? origins = null, selected = null;
            ulong? owner = null, sequence = null;
            bool? overridden = null;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                var name = r.GetString();
                r.Read();
                switch (name)
                {
                    case "contentHash": hash = ParseHash(r.GetString()); break;
                    case "origins":
                        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("origins: an array is required");
                        var bits = BlobOrigins.None;
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray) bits |= Parse<BlobOrigins>(r.GetString(), name);
                        origins = bits;
                        break;
                    case "selectedOrigin": selected = Parse<BlobOrigins>(r.GetString(), name); break;
                    case "owner": owner = U64(ref r, name); break;
                    case "sequence": sequence = U64(ref r, name); break;
                    case "overridden": overridden = r.GetBoolean(); break;
                    default: throw new JsonException($"entry: unknown field '{name}'");
                }
            }
            if (hash is null || origins is null || selected is null || owner is null || sequence is null || overridden is null)
                throw new JsonException("entry: contentHash, origins, selectedOrigin, owner, sequence and overridden are required");
            return new BlobCatalogEntry(hash.Value, origins.Value, selected.Value, owner.Value, sequence.Value, overridden.Value);
        }
    }
}
