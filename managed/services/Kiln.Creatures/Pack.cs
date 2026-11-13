// SPDX-License-Identifier: MIT
// The "creatures" section of data packs: variants and natural-spawn rules as JSON. kiln.content dispatches the section
// to ApplyJson / RevertJson (Content.RegisterSection, bound by reflection so this assembly does not need a kiln.content
// build that has it). Private AI copies are rebuilt after content (template data) changes.
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Kiln;

/// <summary>What <see cref="Creatures.ApplyJson"/> did.</summary>
public sealed record CreaturesPackReport(int Applied, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
}

public static unsafe partial class Creatures
{
    /// <summary>
    /// Applies a "creatures" section: <c>{ "variants": [ {"id", "base", "name"?, "level"?, "faction"?, "scale"?,
    /// "xp"?, "speed"?, "damage"?, "resistances"?: {"Fire": 0.5}, "loot"?: {"labels", "slots": [min, max],
    /// "disabled"}, "features"?: ["-FleeHome", "+Sleep"], "ai"?: {"aggroSetup.viewRange": 30}, "persist"?} ],
    /// "spawnRules": [ {"replace", "with", "chance"?, "night"?, "day"?} ] }</c>. Applying the same (owner, source)
    /// again replaces what it added before. Tick thread.
    /// </summary>
    /// <param name="sectionJson">The section's value.</param>
    /// <param name="owner">Who owns it: "pack:&lt;mod id&gt;" for data packs, a mod id, or any name.</param>
    /// <param name="source">Where it came from (file), for per-file replacement.</param>
    [TickThreadOnly]
    [RequiresCapability("kiln.creatures")]
    public static CreaturesPackReport ApplyJson(string sectionJson, string owner, string source = "")
    {
        Services.RequireTickThread("Creatures.ApplyJson");
        var key = PackOwner(owner);
        var errors = new List<string>();
        var warnings = new List<string>();
        var applied = 0;
        CreatureVariants.RemoveOwned(key, source);
        Arsenals.RemoveOwned(key, source);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(sectionJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { return new CreaturesPackReport(0, [$"creatures: bad JSON: {e.Message}"], []); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new CreaturesPackReport(0, ["creatures: expected an object"], []);
            foreach (var p in root.EnumerateObject())
                if (p.Name is not ("variants" or "spawnRules" or "arsenals" or "format" or "comment"))
                    warnings.Add($"creatures: unknown key '{p.Name}' ignored");
            if (root.TryGetProperty("variants", out var vs) && vs.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vs.EnumerateArray())
                {
                    try
                    {
                        CreatureVariants.Add(ParseVariant(v, warnings), key, source);
                        applied++;
                    }
                    catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
                    {
                        errors.Add($"creatures.variants: {e.Message}");
                    }
                }
            }
            if (root.TryGetProperty("arsenals", out var ars) && ars.ValueKind == JsonValueKind.Array)
                applied += Arsenals.ApplyPack(ars, key, source, errors);
            if (root.TryGetProperty("spawnRules", out var rs) && rs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rs.EnumerateArray())
                {
                    try
                    {
                        var rule = new CreatureSpawnRule(Str(r, "replace") ?? throw new ArgumentException("a rule needs \"replace\""),
                            Str(r, "with") ?? throw new ArgumentException("a rule needs \"with\""),
                            r.TryGetProperty("chance", out var c) ? c.GetDouble() : 1,
                            r.TryGetProperty("night", out var n) && n.ValueKind == JsonValueKind.True,
                            r.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.True);
                        CreatureVariants.AddRule(rule, key, source);
                        applied++;
                    }
                    catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
                    {
                        errors.Add($"creatures.spawnRules: {e.Message}");
                    }
                }
            }
        }
        return new CreaturesPackReport(applied, errors, warnings);
    }

    /// <summary>Removes what <paramref name="owner"/> added (one source, or all of them; all also reverts the changes
    /// its variants made to live creatures). Returns the number of variants and rules removed. Tick thread.</summary>
    public static int RevertJson(string owner, string? source = null)
    {
        var key = PackOwner(owner);
        var n = CreatureVariants.RemoveOwned(key, source);
        n += Arsenals.RemoveOwned(key, source);
        var t = CreaturesNative.T;
        if (source is null && key.Mod is null && t != null) t->ReleaseOwner(key.Native);
        return n;
    }

    // "mod:<id>" from Content.Apply in the calling mod's code belongs to that mod; everything else gets a named owner.
    private static OwnerKey PackOwner(string owner)
    {
        var caller = OwnerKey.Caller;
        if (!caller.IsNone && (owner == caller.Name || owner == "mod:" + caller.Name)) return caller;
        return OwnerKey.Named(owner);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    private static CreatureVariant ParseVariant(JsonElement v, List<string> warnings)
    {
        var id = Str(v, "id") ?? throw new ArgumentException("a variant needs an \"id\"");
        var baseName = Str(v, "base") ?? throw new ArgumentException($"variant {id} needs a \"base\" template");
        float? F(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetSingle() : null;
        Faction? faction = null;
        if (v.TryGetProperty("faction", out var f))
            faction = f.ValueKind == JsonValueKind.Number ? (Faction)f.GetByte()
                : Enum.TryParse<Faction>(f.GetString(), true, out var fv) ? fv : throw new ArgumentException($"variant {id}: unknown faction '{f.GetString()}'");
        var res = new Dictionary<DamageKind, float>();
        if (v.TryGetProperty("resistances", out var r) && r.ValueKind == JsonValueKind.Object)
            foreach (var p in r.EnumerateObject())
                res[Enum.TryParse<DamageKind>(p.Name, true, out var k) ? k : throw new ArgumentException($"variant {id}: unknown damage kind '{p.Name}'")] = p.Value.GetSingle();
        Guid? labels = null;
        (int, int)? slots = null;
        bool? lootOff = null;
        if (v.TryGetProperty("loot", out var l) && l.ValueKind == JsonValueKind.Object)
        {
            if (l.TryGetProperty("labels", out var lb)) labels = Guid.Parse(lb.GetString()!);
            if (l.TryGetProperty("slots", out var sl) && sl.ValueKind == JsonValueKind.Array && sl.GetArrayLength() == 2)
                slots = (sl[0].GetInt32(), sl[1].GetInt32());
            if (l.TryGetProperty("disabled", out var di)) lootOff = di.ValueKind == JsonValueKind.True;
        }
        EnemyFeatures on = 0, off = 0;
        if (v.TryGetProperty("features", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in fs.EnumerateArray())
            {
                var s = x.GetString() ?? "";
                var minus = s.StartsWith('-');
                var name = s.TrimStart('+', '-');
                if (!Enum.TryParse<EnemyFeatures>(name, true, out var bit) || bit == 0)
                    throw new ArgumentException($"variant {id}: unknown feature '{name}'");
                if (minus) off |= bit;
                else on |= bit;
            }
        }
        var ai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (v.TryGetProperty("ai", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject())
                ai[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString()!,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => p.Value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                    _ => throw new ArgumentException($"variant {id}: ai.{p.Name} must be a string, number or bool"),
                };
        foreach (var p in v.EnumerateObject())
            if (p.Name is not ("id" or "base" or "name" or "level" or "faction" or "scale" or "xp" or "speed" or "damage"
                or "resistances" or "loot" or "features" or "ai" or "persist" or "comment"))
                warnings.Add($"variant {id}: unknown key '{p.Name}' ignored");
        return new CreatureVariant
        {
            Id = id, Base = baseName, Name = Str(v, "name"),
            Level = v.TryGetProperty("level", out var lv) ? lv.GetInt32() : null,
            Faction = faction, Scale = F("scale"), XpFactor = F("xp"), SpeedFactor = F("speed"), DamageFactor = F("damage"),
            Resistances = res, LootLabels = labels, LootSlots = slots, LootDisabled = lootOff,
            EnableFeatures = on, DisableFeatures = off, Ai = ai,
            Persist = v.TryGetProperty("persist", out var pe) && pe.ValueKind == JsonValueKind.True,
        };
    }
}

// Binds the "creatures" section to kiln.content when its build has Content.RegisterSection; rebuilds private copies
// on ContentReadyEvent (template data may have changed).
internal static class ContentLink
{
    private static int _done;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // global, not owned by the mod that happened to touch Kiln.Creatures first
        try
        {
            Events.On<ContentReadyEvent>(_ =>
            {
                if (Creatures.Available) Creatures.RefreshCopies();
                if (Creatures.Available) Arsenals.Refresh();
            });
            BindSection();
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Warn, "Kiln.Creatures: content link failed: " + e.Message);
        }
        finally { ModContext.CurrentMod = outer; }
    }

    private static void BindSection()
    {
        var content = typeof(ContentReadyEvent).Assembly;
        var cls = content.GetType("Kiln.Content");
        var call = content.GetType("Kiln.ContentSectionCall");
        var result = content.GetType("Kiln.ContentSectionResult");
        var reg = cls?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "RegisterSection" && m.GetParameters().Length == 3);
        if (cls is null || call is null || result is null || reg is null)
        {
            Backend.Current.Log(0, LogLevel.Info, "Kiln.Creatures: this kiln.content has no section dispatch; mods call Creatures.ApplyJson themselves");
            return;
        }
        var ctor = result.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 3);
        if (ctor is null) return;
        // apply: call => new ContentSectionResult(r.Applied, r.Errors, r.Warnings), with r = Apply(call.Json, call.Owner, call.Source)
        var p = Expression.Parameter(call, "call");
        var r = Expression.Variable(typeof(CreaturesPackReport), "r");
        var applyCall = Expression.Call(typeof(ContentLink).GetMethod(nameof(Apply), BindingFlags.NonPublic | BindingFlags.Static)!,
            Expression.Property(p, "Json"), Expression.Property(p, "Owner"), Expression.Property(p, "Source"));
        var ps = ctor.GetParameters();
        var body = Expression.Block([r],
            Expression.Assign(r, applyCall),
            Expression.New(ctor,
                Expression.Convert(Expression.Property(r, nameof(CreaturesPackReport.Applied)), ps[0].ParameterType),
                Expression.Convert(Expression.Property(r, nameof(CreaturesPackReport.Errors)), ps[1].ParameterType),
                Expression.Convert(Expression.Property(r, nameof(CreaturesPackReport.Warnings)), ps[2].ParameterType)));
        var applyType = typeof(Func<,>).MakeGenericType(call, result);
        var apply = Expression.Lambda(applyType, body, p).Compile();
        Action<string, string?> revert = (owner, source) => Creatures.RevertJson(owner, source);
        reg.Invoke(null, ["creatures", apply, revert]);
        Backend.Current.Log(0, LogLevel.Info, "Kiln.Creatures: \"creatures\" data-pack section registered with kiln.content");
    }

    private static CreaturesPackReport Apply(string json, string owner, string source) => Creatures.ApplyJson(json, owner, source);
}
