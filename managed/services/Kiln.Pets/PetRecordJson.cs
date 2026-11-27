// SPDX-License-Identifier: MIT
// A character's pet records as kept in the world's save (19d §4.1, schema 1): fixed, service-owned camelCase JSON, never an
// author's serializer. Session state (bodies, leases, targets, transitions, the HUD) is never written; an attack order is
// written as follow. Life: Ready 0, Downed 1, Released 2 (a tombstone until a committed save holds it).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <param name="Owner">The character.</param>
/// <param name="Pets">The records Kiln read.</param>
/// <param name="Kept">Records that did not read (or name a pet another character holds), as their JSON: written back as they
/// were, never dropped by the next write.</param>
internal sealed record PetRecordDoc(CharacterKey Owner, IReadOnlyList<PetRecordData> Pets, IReadOnlyList<string>? Kept = null);

internal sealed record PetRecordData
{
    public PetId Id { get; init; }
    public string Definition { get; init; } = "";
    public PetLife Life { get; init; }
    public bool WantsSummoned { get; init; }
    public PetOrder RequestedOrder { get; init; }
    public int Level { get; init; } = 1;
    public double HealthFraction { get; init; } = 1;
    public int LastHealthMax { get; init; }
    public WorldPosition? RecoveryAnchor { get; init; }
    public string? AnchorWorld { get; init; }
    public ulong StateRevision { get; init; }
}

internal static class PetRecordJson
{
    public const int Schema = 1;

    public static string Write(PetRecordDoc doc)
    {
        var pets = new JsonArray();
        foreach (var p in doc.Pets) pets.Add(Record(p));
        foreach (var raw in doc.Kept ?? [])
        {
            try { pets.Add(JsonNode.Parse(raw)); }
            catch (JsonException) { /* never: it was read from a document */ }
        }
        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["owner"] = new JsonObject { ["account"] = doc.Owner.Account.ToString("x16", CultureInfo.InvariantCulture), ["character"] = doc.Owner.Id.ToString("N") },
            ["pets"] = pets,
        };
        return root.ToJsonString();
    }

    /// <summary>One record as schema 1 writes it.</summary>
    internal static JsonObject Record(PetRecordData p)
    {
        var o = new JsonObject
        {
            ["id"] = p.Id.Value.ToString("N"),
            ["definition"] = p.Definition,
            ["life"] = (int)p.Life,
            ["wantsSummoned"] = p.WantsSummoned,
            ["requestedOrder"] = p.RequestedOrder == PetOrder.Stay ? "stay" : "follow",
            ["level"] = p.Level,
            ["healthFraction"] = Math.Clamp(double.IsFinite(p.HealthFraction) ? p.HealthFraction : 0, 0, 1),
            ["lastHealthMax"] = Math.Max(0, p.LastHealthMax),
            ["stateRevision"] = p.StateRevision,
        };
        if (p.RecoveryAnchor is { } a && double.IsFinite(a.X) && double.IsFinite(a.Y) && double.IsFinite(a.Z))
            o["recoveryAnchor"] = new JsonObject { ["world"] = p.AnchorWorld ?? "", ["x"] = a.X, ["y"] = a.Y, ["z"] = a.Z };
        else o["recoveryAnchor"] = null;
        return o;
    }

    /// <summary>Reads a document; a record that does not read is not used (and named in <paramref name="problems"/>), never
    /// guessed, and comes back in <see cref="PetRecordDoc.Kept"/> as it was. A document of a newer schema is not read at all.</summary>
    public static PetRecordDoc? Read(string json, CharacterKey owner, List<string>? problems = null)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException e)
        {
            problems?.Add($"the document does not read: {e.Message}");
            return null;
        }
        if (root is null) return null;
        int? schema;
        try { schema = root["schema"]?.GetValue<int>(); }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { schema = null; }
        if (schema is null || schema > Schema)
        {
            problems?.Add($"schema {root["schema"]?.ToJsonString()} is not one this Kiln reads ({Schema}): kept as it is, not read");
            return null;
        }
        if (root["pets"] is not null and not JsonArray)
        {
            problems?.Add("pets is not a list: kept as it is, not read");
            return null;
        }
        var list = new List<PetRecordData>();
        var kept = new List<string>();
        foreach (var n in root["pets"] as JsonArray ?? [])
        {
            try
            {
                if (n is not JsonObject o) continue;
                var id = Guid.ParseExact(o["id"]!.GetValue<string>(), "N");
                if (id == Guid.Empty) throw new FormatException("empty id");
                var life = o["life"]!.GetValue<int>();
                if (life is < 0 or > 2) throw new FormatException($"life {life}");
                WorldPosition? anchor = null;
                string? world = null;
                if (o["recoveryAnchor"] is JsonObject a)
                {
                    var (x, y, z) = (a["x"]!.GetValue<double>(), a["y"]!.GetValue<double>(), a["z"]!.GetValue<double>());
                    if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)) anchor = new WorldPosition(x, y, z);
                    world = a["world"]?.GetValue<string>();
                }
                list.Add(new PetRecordData
                {
                    Id = new PetId(id),
                    Definition = o["definition"]!.GetValue<string>(),
                    Life = (PetLife)life,
                    WantsSummoned = o["wantsSummoned"]?.GetValue<bool>() ?? false,
                    // an attack order is never kept: follow
                    RequestedOrder = o["requestedOrder"]?.GetValue<string>() == "stay" ? PetOrder.Stay : PetOrder.Follow,
                    Level = Math.Max(1, o["level"]?.GetValue<int>() ?? 1),
                    HealthFraction = Math.Clamp(o["healthFraction"]?.GetValue<double>() ?? 1, 0, 1),
                    LastHealthMax = Math.Max(0, o["lastHealthMax"]?.GetValue<int>() ?? 0),
                    RecoveryAnchor = anchor,
                    AnchorWorld = world,
                    StateRevision = o["stateRevision"]?.GetValue<ulong>() ?? 0,
                });
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or NullReferenceException or JsonException or ArgumentException)
            {
                problems?.Add($"a pet record does not read ({e.Message}): it is kept as it is");
                if (n is not null) kept.Add(n.ToJsonString());
            }
        }
        return new PetRecordDoc(owner, list, kept);
    }

    /// <summary>The revisions a staged bundle's bytes hold: pet id to (state revision, life). The receipt reads exactly what
    /// was serialized, never the live records.</summary>
    public static Dictionary<PetId, (ulong Revision, PetLife Life)> Revisions(byte[] bundle)
    {
        var map = new Dictionary<PetId, (ulong, PetLife)>();
        try
        {
            if (JsonNode.Parse(bundle)?["keys"]?["record"] is not JsonObject doc) return map;
            foreach (var n in doc["pets"] as JsonArray ?? [])
            {
                if (n is not JsonObject o || o["id"]?.GetValue<string>() is not { } s || !Guid.TryParseExact(s, "N", out var g)) continue;
                map[new PetId(g)] = (o["stateRevision"]?.GetValue<ulong>() ?? 0, (PetLife)(o["life"]?.GetValue<int>() ?? 0));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // a bundle that does not read acknowledges nothing
        }
        return map;
    }
}
