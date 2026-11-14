// SPDX-License-Identifier: MIT
// World.SetTransformScale (0.2.0-dev.28, capability world.transform_scale) on a placed prop: chapter 13 §13b.6's sequence.
// Every CurrentTransform byte recorded, Scale3(1.25, 0.75, 1.5) written and awaited, read again one tick later: the 12
// scale bytes are the requested vector and every other byte is unchanged; exactly one EntityScaleChangedEvent; the same
// scale again is Unchanged with no event; the original restored through the same method. Refusals change nothing.
// PASS world.scale-vector only when all of that holds. Then the scale is set again and left for the save: boot 2 records
// whether it came back (persistence is reported, not required) and restores it.
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Kiln;

namespace WorldWritesTest;

public sealed partial class WorldWritesTestMod
{
    private static readonly Scale3 Asked = new(1.25f, 0.75f, 1.5f);

    private static byte[] Bytes(Scale3 s) => MemoryMarshal.AsBytes(new ReadOnlySpan<Scale3>(in s)).ToArray();

    // Where CurrentTransform keeps its scale, read here through the public reflection API (the service resolves it itself).
    private static int ScaleOffset()
    {
        var ct = GameTypes.Find("keen::ecs::CurrentTransform");
        if (ct?.Field("transform") is not { } tf || tf.Type.Resolved.Field("scale") is not { } sf) return -1;
        return tf.Offset + sf.Offset;
    }

    private static Scale3? ScaleOf(EntityId e, int offset) =>
        World.ReadBytes(e, "CurrentTransform") is { } b && offset >= 0 && b.Length >= offset + 12 ? MemoryMarshal.Read<Scale3>(b.AsSpan(offset)) : null;

    /// <summary>Boot 1: the sequence on <paramref name="prop"/>; true when the scale was left set for the save.</summary>
    private async Task<bool> ScaleVector(EntityId prop)
    {
        const string cap = World.TransformScaleCapability;
        _ = World.SupportsTrackedWrites;  // Kiln.World reports its rows at its first use
        Info($"transform scale: {Capabilities.Why(cap)}");
        if (!Check(Capabilities.Is(cap), $"world.transform_scale: {Capabilities.Why(cap)}")) return false;
        var info = World.WriteInfo("CurrentTransform");
        Check(info.CanWrite, $"CurrentTransform is listed (the build added it from World.SetTransformScale): {info.State}, {info.GameWriterCount} game writers " +
                             $"({string.Join(", ", info.GameWriters)}), tracked for the save {info.SaveTracked}, for replication {info.ReplicationTracked}{(info.Reason is null ? "" : ": " + info.Reason)}");
        var offset = ScaleOffset();
        var before = World.ReadBytes(prop, "CurrentTransform");
        if (!Check(before is { Length: 56 } && offset is >= 0 and <= 44, $"{Describe(prop)} has a CurrentTransform of {before?.Length} bytes, its scale at +{offset}")) return false;
        var original = MemoryMarshal.Read<Scale3>(before!.AsSpan(offset));
        Info($"{Describe(prop)}: scale {original} at +{offset}");
        var events = new List<EntityScaleChangedEvent>();
        var ok = true;
        using (Events.On<EntityScaleChangedEvent>(events.Add, EventFilter.ForEntity(prop)))
        {
            var r = await World.SetTransformScale(prop, Asked);
            await Steps(1);  // one tick more
            var after = World.ReadBytes(prop, "CurrentTransform") ?? [];
            var target = after.Length == 56 && after.AsSpan(offset, 12).SequenceEqual(Bytes(Asked));
            var rest = after.Length == 56 && after.AsSpan(0, offset).SequenceEqual(before.AsSpan(0, offset)) &&
                       after.AsSpan(offset + 12).SequenceEqual(before.AsSpan(offset + 12));
            ok &= Check(r is { Ok: true, WorldWriteStatus: WorldWriteStatus.Written } && r.Before == original && r.Now == Asked && r.Kept != false && target && rest,
                $"SetTransformScale {original} -> {Asked}: {r.WorldWriteStatus}, before {r.Before}, now {r.Now}, kept {r.Kept?.ToString() ?? "null (no game writer checked)"}, " +
                $"save {r.SaveTracked}, replication {r.ReplicationTracked}; read one tick later: the 12 scale bytes {(target ? "are" : "are NOT")} the vector, " +
                $"the other 44 bytes {(rest ? "unchanged" : "CHANGED")}");
            await Steps(2);
            ok &= Check(events.Count == 1 && events[0].Before == original && events[0].Now == Asked && events[0].ModId == Me,
                $"one EntityScaleChangedEvent: {events.Count} ({string.Join("; ", events)})");
            var same = await World.SetTransformScale(prop, Asked);
            await Steps(2);
            ok &= Check(same is { Ok: true, WorldWriteStatus: WorldWriteStatus.Unchanged, Changed: false } && events.Count == 1,
                $"the same scale again: {same.WorldWriteStatus}, {events.Count} event(s)");
            // refusals: an entity that is not there; bad arguments throw and send nothing
            var gone = await World.SetTransformScale(new EntityId(0x7ffffff0), Asked);
            ok &= Check(gone is { Ok: false, Now: null } && gone.Reason is { Length: > 0 }, $"an entity that is not there: {gone.WorldWriteStatus}: {gone.Reason}");
            var threw = 0;
            foreach (var bad in new[] { new Scale3(0f, 1f, 1f), new Scale3(1f, float.NaN, 1f), new Scale3(1f, 1f, float.PositiveInfinity), new Scale3(-1f, 1f, 1f) })
                try { await World.SetTransformScale(prop, bad); } catch (ArgumentException) { threw++; }
            try { await World.SetTransformScale(EntityId.None, Asked); } catch (ArgumentException) { threw++; }
            ok &= Check(threw == 5 && ScaleOf(prop, offset) == Asked, $"zero, NaN, infinite, negative factors and no entity are bad arguments ({threw} of 5), nothing written");
            // restored through the same method
            var back = original.IsValid ? await World.SetTransformScale(prop, original) : null;
            if (back is null) World.WriteRaw(prop, "CurrentTransform", offset, original);
            await Steps(2);
            var restored = World.ReadBytes(prop, "CurrentTransform") ?? [];
            ok &= Check(restored.AsSpan().SequenceEqual(before) && (back is null || (back.Ok && events.Count == 2)),
                $"restored: {back?.WorldWriteStatus.ToString() ?? "raw (the original was not a valid factor)"}, every byte as recorded {restored.AsSpan().SequenceEqual(before)}, {events.Count} event(s)");
        }
        Check(ok, ok ? "world.scale-vector" : "world.scale-vector: see the FAIL lines above");
        // left set for the save: boot 2 says whether it came back
        var keep = await World.SetTransformScale(prop, Asked);
        Info($"transform scale left at {Asked} on {Describe(prop)} for the restart: {keep.WorldWriteStatus}");
        return keep.Ok;
    }

    /// <summary>Boot 2: whether the scale written in boot 1 came back after the save and restart; restores the original.</summary>
    private async Task ScaleAfterRestart(EntityId prop, JsonObject st)
    {
        if (st["scaleLeft"]?.GetValue<bool>() != true) return;
        var offset = ScaleOffset();
        var now = ScaleOf(prop, offset);
        var original = new Scale3(st["scaleX"]!.GetValue<float>(), st["scaleY"]!.GetValue<float>(), st["scaleZ"]!.GetValue<float>());
        Info($"transform scale after the save and restart: {Describe(prop)} has {now} ({(now == Asked ? "the written scale persisted" : now == original ? "back at the original: the scale did NOT persist" : "another value")}) " +
             "(reported, not required: chapter 13 §13b.6)");
        if (original.IsValid && now != original)
        {
            var back = await World.SetTransformScale(prop, original);
            Info($"transform scale restored to {original}: {back.WorldWriteStatus}");
        }
    }
}
