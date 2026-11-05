// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>
/// Data attached to non-player entities (<see cref="EntityData"/>). In a session an entity is its id. Across a
/// restart the game renumbers entities and keeps no id of its own (it saves template + transform and a few
/// components, docs/re/saves.md), so Kiln keys each attachment by the entity's <b>template and position at the
/// save</b> and matches loaded entities against that: props and other static entities come back at exactly their saved
/// position (matched within 0.5 m), creatures and NPCs within 16 m (nearest unclaimed one of the template).
/// </summary>
/// <remarks>Entities whose template never reaches the save (DoNotSave, e.g. most enemies) keep attachments for the
/// session only. A destroyed entity loses its attachments within a second. Attachments not matched within 10 minutes of a
/// load are kept for 3 sessions, then dropped.</remarks>
internal sealed class EntityTable
{
    private const double StaticRadius = 0.5, MobileRadius = 16;
    private static readonly TimeSpan MatchWindow = TimeSpan.FromMinutes(10);
    private const int MaxMissedSessions = 3;

    private readonly Dictionary<uint, Rec> _live = new();
    private readonly List<Rec> _unresolved = [];
    private DateTime _matchUntil = DateTime.MinValue, _nextMatch = DateTime.MinValue, _nextCheck = DateTime.MinValue;
    private bool _missCounted;
    private int _matched, _dropped;

    internal sealed class Rec
    {
        public uint Entity;
        public Guid? Template;
        public string? Name;
        public double X, Y, Z;
        public float Yaw;
        public int Missed;
        public bool Transient;  // template never saved: session only
        public readonly Dictionary<string, SortedDictionary<string, WorldStore.Slot>> ByMod = new(StringComparer.Ordinal);
        public string Where => $"({X:F1}, {Y:F1}, {Z:F1})";
    }

    public string Summary => $"entity attachments: {_live.Count} live, {_unresolved.Count} waiting for their entity";
    public int LiveCount => _live.Count;
    public int UnresolvedCount => _unresolved.Count;
    public int Matched => _matched;

    public void Clear()
    {
        _live.Clear();
        _unresolved.Clear();
        _missCounted = false;
    }

    // ---- load ----
    public void Absorb(string mod, JsonObject doc)
    {
        if (doc["entities"] is not JsonArray list) return;
        foreach (var n in list)
        {
            if (n is not JsonObject o || o["pos"] is not JsonArray p || p.Count != 3) continue;
            var rec = new Rec
            {
                Template = Guid.TryParse(o["template"]?.GetValue<string>(), out var g) ? g : null,
                Name = o["name"]?.GetValue<string>(),
                X = p[0]!.GetValue<double>(),
                Y = p[1]!.GetValue<double>(),
                Z = p[2]!.GetValue<double>(),
                Yaw = o["yaw"]?.GetValue<float>() ?? 0,
                Missed = o["missed"]?.GetValue<int>() ?? 0,
            };
            // Several mods may have attached data to the same entity: one record per (template, position).
            var same = _unresolved.FirstOrDefault(r => r.Template == rec.Template && Math.Abs(r.X - rec.X) < 0.01 &&
                                                       Math.Abs(r.Y - rec.Y) < 0.01 && Math.Abs(r.Z - rec.Z) < 0.01);
            if (same != null) rec = same;
            else _unresolved.Add(rec);
            var keys = new SortedDictionary<string, WorldStore.Slot>(StringComparer.Ordinal);
            if (o["keys"] is JsonObject ks)
                foreach (var (k, v) in ks)
                    keys[k] = new WorldStore.Slot { Json = v?.ToJsonString() ?? "null" };
            rec.ByMod[mod] = keys;
        }
    }

    public void StartMatching()
    {
        _matchUntil = DateTime.UtcNow + MatchWindow;
        _nextMatch = DateTime.MinValue;
    }

    // ---- access ----
    public Rec? Find(EntityId e) => _live.GetValueOrDefault(e.Value);

    public IEnumerable<Rec> LiveRecs => _live.Values;

    /// <summary>The record of a live entity, or of a loaded attachment saved for it (matched now); null if neither.</summary>
    public Rec? FindOrMatch(EntityId e)
    {
        if (_live.TryGetValue(e.Value, out var r)) return r;
        if (_unresolved.Count == 0 || !SpawnLink.Available || SpawnLink.Inspect(e) is not { } info) return null;
        foreach (var u in _unresolved)
        {
            if (u.Template != info.Template || !Near(u, info.Position, RadiusFor(u.Template))) continue;
            Claim(u, e);
            return u;
        }
        return null;
    }

    /// <summary>The record of a live entity; creates it (after trying to match a loaded attachment to it).</summary>
    public Rec Attach(EntityId e)
    {
        if (_live.TryGetValue(e.Value, out var r)) return r;
        if (!SpawnLink.Available) throw new NotSupportedException("EntityData needs kiln.spawn (entity lookups), which is unavailable");
        var info = SpawnLink.Inspect(e) ?? throw new ArgumentException($"entity {e} does not exist", nameof(e));
        // A loaded attachment waiting for exactly this entity?
        foreach (var u in _unresolved)
        {
            if (u.Template != info.Template || !Near(u, info.Position, RadiusFor(u.Template))) continue;
            Claim(u, e);
            return u;
        }
        r = new Rec { Entity = e.Value };
        Update(r, info);
        _live[e.Value] = r;
        return r;
    }

    private static void Update(Rec r, SpawnLink.Ent info)
    {
        r.Template = info.Template;
        r.Name = info.Name;
        r.X = info.Position.X;
        r.Y = info.Position.Y;
        r.Z = info.Position.Z;
        r.Yaw = info.Yaw;
        r.Transient = info.Template is { } g && SpawnLink.Template(g).neverSaved;
    }

    public void Drop(uint entity) => _live.Remove(entity);

    // ---- periodic (tick thread, ~1 s) ----
    public void Tick()
    {
        if (_live.Count == 0 && _unresolved.Count == 0) return;
        if (!SpawnLink.Available) return;
        var now = DateTime.UtcNow;
        if (now >= _nextCheck && _live.Count > 0)
        {
            _nextCheck = now + TimeSpan.FromSeconds(1);
            CheckDestroyed();
        }
        if (_unresolved.Count > 0 && now < _matchUntil && now >= _nextMatch && SpawnLink.WorldRunning)
        {
            _nextMatch = now + TimeSpan.FromSeconds(5);
            MatchLoaded();
        }
        if (now >= _matchUntil && _matchUntil != DateTime.MinValue && !_missCounted)
        {
            _missCounted = true;
            foreach (var u in _unresolved) u.Missed++;
            var gone = _unresolved.RemoveAll(u => u.Missed >= MaxMissedSessions);
            if (_unresolved.Count + gone > 0)
                SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: entity attachments: {_unresolved.Count} still without their entity after " +
                                                $"{MatchWindow.TotalMinutes:F0} min (kept for later sessions), {gone} dropped after {MaxMissedSessions} sessions");
        }
    }

    private void CheckDestroyed()
    {
        List<uint>? gone = null;
        foreach (var (id, r) in _live)
        {
            SpawnLink.Ent? info;
            try
            {
                info = SpawnLink.Inspect(new EntityId(id));
            }
            catch (InvalidOperationException)
            {
                return;  // kiln.spawn cannot tell right now: drop nothing
            }
            if (info != null && info.Template == r.Template)
            {
                r.X = info.Position.X;
                r.Y = info.Position.Y;
                r.Z = info.Position.Z;
                r.Yaw = info.Yaw;
                continue;
            }
            (gone ??= []).Add(id);
        }
        if (gone == null) return;
        foreach (var id in gone)
        {
            var r = _live[id];
            _live.Remove(id);
            _dropped++;
            SavesRuntime.Log(LogLevel.Debug, $"Kiln.Saves: entity {id} ({r.Name ?? "?"}) is gone: attachments of {string.Join(", ", r.ByMod.Keys)} dropped");
        }
    }

    private void MatchLoaded()
    {
        var before = _unresolved.Count;
        foreach (var u in _unresolved.ToList())
        {
            if (u.Template is not { } g) continue;
            var radius = RadiusFor(g);
            foreach (var cand in SpawnLink.Find(g, new WorldPosition(u.X, u.Y, u.Z), radius, 16))
            {
                if (_live.ContainsKey(cand.Value)) continue;
                Claim(u, cand);
                break;
            }
        }
        var n = before - _unresolved.Count;
        if (n > 0)
            SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: entity attachments: matched {n} loaded entities ({_matched} this session), {_unresolved.Count} waiting");
    }

    private void Claim(Rec u, EntityId e)
    {
        _unresolved.Remove(u);
        u.Entity = e.Value;
        u.Missed = 0;
        if (SpawnLink.Inspect(e) is { } info)
        {
            SavesRuntime.Log(LogLevel.Debug, $"Kiln.Saves: entity {e} ({info.Name}) at {info.Position} gets the attachments saved at {u.Where} " +
                                             $"({info.Position.DistanceTo(new WorldPosition(u.X, u.Y, u.Z)):F2} m)");
            Update(u, info);
        }
        _live[e.Value] = u;
        _matched++;
    }

    private static double RadiusFor(Guid? template)
    {
        if (template is not { } g) return StaticRadius;
        return SpawnLink.IsMobile(g) ? MobileRadius : StaticRadius;
    }

    private static bool Near(Rec r, WorldPosition p, double radius) => p.DistanceTo(new WorldPosition(r.X, r.Y, r.Z)) <= radius;

    // ---- staging ----
    /// <summary>One bundle per mod: {"entities":[{template, name, pos, yaw, missed, keys}]}.</summary>
    public IEnumerable<(string mod, byte[] data)> Stage(Dictionary<string, JsonSerializerOptions> json)
    {
        if (SpawnLink.Available) CheckDestroyed();
        var perMod = new SortedDictionary<string, List<(Rec r, SortedDictionary<string, WorldStore.Slot> keys)>>(StringComparer.Ordinal);
        foreach (var r in _live.Values.Where(r => !r.Transient).Concat(_unresolved))
            foreach (var (mod, keys) in r.ByMod)
                if (keys.Count > 0)
                {
                    if (!perMod.TryGetValue(mod, out var l)) perMod[mod] = l = [];
                    l.Add((r, keys));
                }
        foreach (var (mod, list) in perMod)
        {
            var opts = json.GetValueOrDefault(mod);
            var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("format", 1);
                w.WriteString("scope", "entity/" + mod);
                w.WriteStartArray("entities");
                foreach (var (r, keys) in list.OrderBy(t => t.r.Template).ThenBy(t => t.r.X).ThenBy(t => t.r.Z).ThenBy(t => t.r.Y))
                {
                    w.WriteStartObject();
                    if (r.Template is { } g) w.WriteString("template", g.ToString());
                    if (r.Name != null) w.WriteString("name", r.Name);
                    w.WriteStartArray("pos");
                    w.WriteNumberValue(Math.Round(r.X, 3));
                    w.WriteNumberValue(Math.Round(r.Y, 3));
                    w.WriteNumberValue(Math.Round(r.Z, 3));
                    w.WriteEndArray();
                    w.WriteNumber("yaw", MathF.Round(r.Yaw, 2));
                    if (r.Missed > 0) w.WriteNumber("missed", r.Missed);
                    w.WriteStartObject("keys");
                    foreach (var (k, s) in keys)
                    {
                        if (s.Value != null)
                        {
                            try
                            {
                                s.Json = JsonSerializer.Serialize(s.Value, s.Type ?? s.Value.GetType(), opts);
                            }
                            catch (Exception e)
                            {
                                SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: entity/{mod} key '{k}' could not be serialized ({e.Message})");
                            }
                        }
                        if (s.Json == null) continue;
                        w.WritePropertyName(k);
                        w.WriteRawValue(s.Json, skipInputValidation: true);
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            yield return (mod, ms.ToArray());
        }
    }

    public void Retire(string mod, JsonSerializerOptions? opts, ModContext ctx)
    {
        foreach (var r in _live.Values.Concat(_unresolved))
            if (r.ByMod.TryGetValue(mod, out var keys))
                foreach (var (k, s) in keys)
                    WorldStore.Freeze(s, opts, ctx, "entity/" + mod, k);
    }

    internal static string Describe(Rec r) => new StringBuilder().Append(r.Name ?? r.Template?.ToString() ?? "?").Append(' ').Append(r.Where).ToString();
}
