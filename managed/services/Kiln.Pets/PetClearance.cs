// SPDX-License-Identifier: MIT
// Measured path clearance (19d §4.3a, gate C6): the HorizontalSpace/VerticalSpace a pet's route asks kiln.queries for.
// Kiln never derives it from the body's radius: a profile is a calibration's result, at one scale, on one game build,
// measured on one body (the template's walker, movement collider and own scale). A game update (another build number) or
// a changed body invalidates it, and the pet only catches up (ClearanceMappingUnverified).
//
// Two kinds of profile:
//   - a creature profile names one creature (its Kiln id): that creature was calibrated;
//   - a body profile ("match": "body") names a body: at least three walking creatures with exactly that body were
//     calibrated on the build at the scale and every one passed with the same pair (the routes kiln.queries finds do not
//     depend on the creature, and the walk is the movement collider's: tools/pets-clearance, docs/kiln/services/pets.md).
//     It applies to every walking creature with that body.
// A creature profile wins over a body profile; where profiles come from, first match wins:
//   1. the defining mod's own <mod folder>/pets/clearance.json, for that mod's pets only (its creature, its scale);
//   2. the server's kiln/data/kiln.pets/clearance.json (an operator's or a calibration run's file);
//   3. Kiln's shipped profiles (clearance.json, embedded in Kiln.Pets.dll; tools/pets-clearance makes them).
// Format 1 (docs/kiln/services/pets.md):
//   {"format": 1, "profiles": [{"creature": "game:enemy_wildbeast_wolf", "scale": 1, "game": 1287369,
//     "horizontalSpace": 1, "verticalSpace": 2,
//     "body": {"walkerRadius": 0, "walkerHeight": 2, "colliderWidth": 0.8, "colliderHeight": 1.4}, "calibration": {...}},
//    {"match": "body", "scale": 1, "game": 1287369, "horizontalSpace": 0.5, "verticalSpace": 2,
//     "body": {"walkerRadius": 0, "walkerHeight": 2, "colliderWidth": 1, "colliderHeight": 1, "templateScale": 1}, ...}]}
// "game" is the game's own build number (svn), as Kiln's build record of the running exe names it, so the dedicated server
// and the client that hosts share it. "templateScale" (default 1; a body profile states it) is the template's own
// ScaleComponent.uniformScale, a creature block's Body.Scale. "calibration" is the evidence summary; the loader does not
// read it. A game creature's id matches whatever its case, as the creatures registry resolves it.
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A measured path clearance for a creature at a scale (19d §4.3a, gate C6): the HorizontalSpace and VerticalSpace
/// a route request asks for. Kiln never derives it from the body's radius; without one, QueryInput follow is catch-up only.</summary>
internal sealed record PetClearanceProfile(double HorizontalSpace, double VerticalSpace);

/// <summary>The body a profile was measured on: the template's path walker (radius, height) and movement collider (width,
/// height), unscaled, and the template's own uniform scale, as the game data holds them.</summary>
internal sealed record PetClearanceBody(double WalkerRadius, double WalkerHeight, double ColliderWidth, double ColliderHeight)
{
    internal const double Tolerance = 0.005;

    /// <summary>The template's own uniform scale (ScaleComponent.uniformScale: a creature block's Body.Scale); 1 when it
    /// has none.</summary>
    public double TemplateScale { get; init; } = 1;

    /// <summary>The template walks: its path walker finds paths and it has no flying component. Only a walker takes a body
    /// profile; not compared by <see cref="Same"/>.</summary>
    public bool Walker { get; init; } = true;

    public bool Same(PetClearanceBody o) =>
        Math.Abs(WalkerRadius - o.WalkerRadius) <= Tolerance && Math.Abs(WalkerHeight - o.WalkerHeight) <= Tolerance &&
        Math.Abs(ColliderWidth - o.ColliderWidth) <= Tolerance && Math.Abs(ColliderHeight - o.ColliderHeight) <= Tolerance &&
        Math.Abs(TemplateScale - o.TemplateScale) <= Tolerance;

    public override string ToString() =>
        FormattableString.Invariant($"walker radius {WalkerRadius:0.###} height {WalkerHeight:0.###}, collider width {ColliderWidth:0.###} height {ColliderHeight:0.###}") +
        (Math.Abs(TemplateScale - 1) > Tolerance ? FormattableString.Invariant($", template scale {TemplateScale:0.###}") : "");
}

/// <summary>One profile as a file lists it: a creature's (<paramref name="Creature"/> its Kiln id) or a body's
/// (<paramref name="Creature"/> null, "match": "body": every walking creature with <paramref name="Body"/>).</summary>
internal sealed record PetClearanceEntry(string? Creature, double Scale, int Game, PetClearanceProfile Profile, PetClearanceBody Body, string Source)
{
    public bool ByBody => Creature is null;
}

internal static class PetClearance
{
    internal const int Format = 1;
    internal const string FileName = "clearance.json";
    internal const string ShippedResource = "Kiln.Pets.clearance.json";
    internal const string Unverified = "ClearanceMappingUnverified";
    private const double RecheckSeconds = 10;

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Creature, double Scale), PetClearanceProfile> Overrides = [];
    private static readonly Dictionary<string, Source> Files = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);
    private static List<PetClearanceEntry>? _shipped;

    /// <summary>False after <see cref="ResetForTests"/>: only <see cref="SetForTests"/> profiles count until a test turns
    /// the sources (files, shipped profiles) back on with its own seams.</summary>
    internal static bool SourcesOn { get; set; } = true;

    // ---- seams (tests replace them; the defaults read the game) ----

    /// <summary>The running game's build number (svn); null while it is not known (yet).</summary>
    internal static Func<int?> GameBuild { get; set; } = RunningBuild;
    /// <summary>The body a definition's pets have (its creature's template), or null when it cannot be read here.</summary>
    internal static Func<PetDefinition, PetClearanceBody?> BodyOf { get; set; } = TemplateBody;
    /// <summary>Kiln's shipped profiles (the embedded clearance.json), or null.</summary>
    internal static Func<string?> ShippedJson { get; set; } = ReadShipped;
    /// <summary>The server's file (kiln/data/kiln.pets/clearance.json).</summary>
    internal static Func<string> ServerFile { get; set; } = () => Path.Combine(KilnPaths.Root, "data", "kiln.pets", FileName);
    /// <summary>A mod's folder by its id (case-insensitive), or null.</summary>
    internal static Func<string, string?> ModFolder { get; set; } = ModDirectory;
    /// <summary>Wall seconds, for the files' recheck.</summary>
    internal static Func<double> Clock { get; set; } = () => Environment.TickCount64 / 1000.0;

    // ---- the answer ----

    /// <summary>The accepted profile for a definition's pets, or null with why not (<c>ClearanceMappingUnverified: ...</c>).
    /// Tick thread (the body check reads the game data).</summary>
    public static PetClearanceProfile? For(PetDefinition def, out string? why)
    {
        var scale = Math.Round(def.Level.Scale, 3);
        lock (Gate)
            if (Overrides.TryGetValue((def.Creature, scale), out var forced))
            {
                why = null;
                return forced;
            }
        var where = $"{def.Creature} at scale {scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
        var candidates = Candidates(def, scale);
        if (candidates.Count == 0)
        {
            why = $"{Unverified}: no measured path clearance for {where}; the pet catches up instead of walking";
            return null;
        }
        var build = GameBuild();
        if (build is null)
        {
            why = Identifying()
                ? $"{Unverified}: the game build is still being identified; the pet catches up instead of walking until it is"
                : $"{Unverified}: Kiln does not know this game build (no build record of the running exe: a game update?), so no measured path clearance for {where} applies; the pet catches up instead of walking";
            return null;
        }
        var mine = candidates.Where(c => c.Game == build.Value).ToList();
        if (mine.Count == 0)
        {
            var builds = string.Join(", ", candidates.Select(c => c.Game).Distinct().OrderBy(b => b));
            why = $"{Unverified}: the path clearance for {where} was measured on game build {builds}, this is {build.Value} (a game update needs a new calibration); the pet catches up instead of walking";
            return null;
        }
        var body = BodyOf(def);
        if (body is null)
        {
            why = $"{Unverified}: the body of {def.Creature} cannot be read here to check its measured profile; the pet catches up instead of walking";
            return null;
        }
        // the creature's own profiles first (every source), then the body profiles (walking bodies only)
        foreach (var c in mine)
            if (!c.ByBody && c.Body.Same(body))
            {
                why = null;
                return c.Profile;
            }
        if (body.Walker)
            foreach (var c in mine)
                if (c.ByBody && c.Body.Same(body))
                {
                    why = null;
                    return c.Profile;
                }
        var own = mine.FirstOrDefault(c => !c.ByBody);
        why = own is not null
            ? $"{Unverified}: the body of {def.Creature} changed since its path clearance was measured (measured: {own.Body}; now: {body}); the pet catches up instead of walking"
            : body.Walker
                ? $"{Unverified}: no measured path clearance for {where}: no profile names it, and no body profile of game build {build.Value} is its body ({body}); the pet catches up instead of walking"
                : $"{Unverified}: no measured path clearance for {where}: no profile names it, and a body profile is for walking bodies only (its template finds no paths or flies); the pet catches up instead of walking";
        return null;
    }

    /// <summary>Every profile at the scale for the creature (its own) or for a body, in the order of the sources (the
    /// defining mod's, the server's, Kiln's).</summary>
    private static List<PetClearanceEntry> Candidates(PetDefinition def, double scale)
    {
        var all = new List<PetClearanceEntry>();
        lock (Gate)
        {
            if (!SourcesOn) return all;
            var mod = ModOf(def.Id);
            if (mod is not null && ModFolder(mod) is { } dir) all.AddRange(Read(Path.Combine(dir, "pets", FileName), $"mod {mod}"));
            all.AddRange(Read(ServerFile(), "the server's kiln/data/kiln.pets"));
            all.AddRange(Shipped());
        }
        return all.Where(e => (e.ByBody || SameCreature(e.Creature!, def.Creature)) && Math.Abs(e.Scale - scale) < 1e-6).ToList();
    }

    /// <summary>Whether two creature ids name the same creature: a game creature's whatever its case (the creatures
    /// registry resolves <c>game:</c> ids so, and the game's own template names are mixed case), a creature block's
    /// exactly.</summary>
    internal static bool SameCreature(string a, string b) =>
        a.StartsWith("game:", StringComparison.Ordinal) && b.StartsWith("game:", StringComparison.Ordinal)
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>The mod a definition id belongs to: its id up to the first ':' (Pets.Define checks that).</summary>
    private static string? ModOf(string id) => id.IndexOf(':') is var i and > 0 ? id[..i] : null;

    // ---- the files ----

    private sealed class Source
    {
        public DateTime Stamp;
        public double CheckedAt = double.NegativeInfinity;
        public List<PetClearanceEntry> Entries = [];
    }

    private static List<PetClearanceEntry> Read(string path, string label)
    {
        if (!Files.TryGetValue(path, out var s)) Files[path] = s = new Source { Stamp = DateTime.MinValue };
        var now = Clock();
        if (now - s.CheckedAt < RecheckSeconds) return s.Entries;
        s.CheckedAt = now;
        DateTime stamp;
        try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stamp = DateTime.MinValue; }
        if (stamp == s.Stamp) return s.Entries;
        s.Stamp = stamp;
        s.Entries = [];
        if (stamp == DateTime.MinValue) return s.Entries;
        try { s.Entries = Parse(File.ReadAllText(path), label, out var problems).ToList(); Warn(path, problems); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warn(path, [$"not read: {e.Message}"]); }
        return s.Entries;
    }

    private static List<PetClearanceEntry> Shipped()
    {
        if (_shipped is not null) return _shipped;
        var text = ShippedJson();
        if (text is null) return _shipped = [];
        _shipped = Parse(text, "Kiln's shipped profiles", out var problems).ToList();
        Warn(ShippedResource, problems);
        return _shipped;
    }

    private static void Warn(string where, IReadOnlyList<string> problems)
    {
        foreach (var p in problems)
            if (Warned.Add(where + "\n" + p))
                PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: {where}: {p}");
    }

    /// <summary>The profiles of a clearance file (format 1); <paramref name="problems"/> names every entry left out and why.
    /// A file of another format, or not JSON, gives none.</summary>
    internal static IReadOnlyList<PetClearanceEntry> Parse(string json, string source, out List<string> problems)
    {
        problems = [];
        var list = new List<PetClearanceEntry>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException e)
        {
            problems.Add($"not JSON: {e.Message}");
            return list;
        }
        if (root is JsonArray)
        {
            problems.Add("a bare list is no clearance file: format 1 is {\"format\": 1, \"profiles\": [...]}, each profile with its \"game\" build and measured \"body\"");
            return list;
        }
        if (root is not JsonObject o || Int(o["format"]) != Format)
        {
            problems.Add($"not a clearance file of format {Format}");
            return list;
        }
        if (o["profiles"] is not JsonArray profiles)
        {
            problems.Add("no \"profiles\" list");
            return list;
        }
        for (var i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i] as JsonObject;
            var name = $"profiles[{i}]";
            if (p is null) { problems.Add($"{name}: not an object"); continue; }
            var match = p["match"] is null ? "creature" : Str(p["match"]);
            if (match is not ("creature" or "body")) { problems.Add($"{name}: \"match\" is \"creature\" (the default) or \"body\""); continue; }
            string? creature = null;
            if (match == "body")
            {
                if (p["creature"] is not null) { problems.Add($"{name}: a body profile (\"match\": \"body\") names no \"creature\""); continue; }
                name = $"{name} (body)";
            }
            else
            {
                creature = Str(p["creature"]);
                if (creature is null || creature.IndexOf(':') <= 0) { problems.Add($"{name}: \"creature\" is not a creature's Kiln id"); continue; }
                name = $"{name} ({creature})";
            }
            var scale = Num(p["scale"]) ?? 1;
            if (!double.IsFinite(scale) || scale < 0.25 || scale > 4) { problems.Add($"{name}: \"scale\" is outside 0.25..4"); continue; }
            if (Int(p["game"]) is not { } game || game <= 0) { problems.Add($"{name}: no \"game\" build number (a profile is measured on one game build)"); continue; }
            var h = Num(p["horizontalSpace"]);
            var v = Num(p["verticalSpace"]);
            if (h is not (>= 0 and <= 16) || v is not (>= 0 and <= 16)) { problems.Add($"{name}: \"horizontalSpace\" and \"verticalSpace\" are metres, 0..16"); continue; }
            if (p["body"] is not JsonObject b || Num(b["walkerRadius"]) is not { } wr || Num(b["walkerHeight"]) is not { } wh ||
                Num(b["colliderWidth"]) is not { } cw || Num(b["colliderHeight"]) is not { } ch ||
                !new[] { wr, wh, cw, ch }.All(x => double.IsFinite(x) && x >= 0))
            {
                problems.Add($"{name}: no measured \"body\" (walkerRadius, walkerHeight, colliderWidth, colliderHeight)");
                continue;
            }
            var ts = Num(b["templateScale"]);
            if (b["templateScale"] is not null && ts is not (> 0 and <= 100))
            {
                problems.Add($"{name}: the body's \"templateScale\" is not a scale");
                continue;
            }
            if (creature is null && ts is null)
            {
                problems.Add($"{name}: a body profile's \"body\" names its \"templateScale\"");
                continue;
            }
            list.Add(new PetClearanceEntry(creature, Math.Round(scale, 3), game, new PetClearanceProfile(h.Value, v.Value),
                new PetClearanceBody(wr, wh, cw, ch) { TemplateScale = ts ?? 1 }, source));
        }
        return list;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static int? Int(JsonNode? n) => Num(n) is { } d && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;

    // ---- the defaults ----

    private static string? ReadShipped()
    {
        using var s = typeof(PetClearance).Assembly.GetManifestResourceStream(ShippedResource);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string? ModDirectory(string id)
    {
        foreach (var m in Mods.All)
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m.Directory;
        return null;
    }

    // The running exe's build: its SHA-256 (hashed once, off the tick thread, as Kiln.Game does) names Kiln's build record
    // (kiln/sigdb/builds/<side>/<sha256>.json), whose "svn" is the game's own build number.
    private static readonly Lazy<Task<int?>> Running = new(() => Task.Run(BuildOfProcess));

    /// <summary>Starts identifying the running build (a definition is registered long before a pet walks).</summary>
    internal static void Warm()
    {
        if (GameBuild == RunningBuild) _ = Running.Value;
    }

    private static bool Identifying() => GameBuild == RunningBuild && !Running.Value.IsCompleted;

    private static int? RunningBuild()
    {
        var t = Running.Value;
        return t.IsCompletedSuccessfully ? t.Result : null;
    }

    private static int? BuildOfProcess()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            string sha;
            using (var f = File.OpenRead(exe)) sha = Convert.ToHexStringLower(SHA256.HashData(f));
            return BuildNumber(Path.Combine(KilnPaths.Root, "sigdb", "builds"), sha);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The "svn" of the build record <c>&lt;builds&gt;/&lt;side&gt;/&lt;sha256&gt;.json</c> of either side; null when there is none.</summary>
    internal static int? BuildNumber(string builds, string sha256)
    {
        foreach (var side in new[] { "server", "client" })
        {
            var path = Path.Combine(builds, side, sha256 + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && Int(o["svn"]) is { } svn && svn > 0) return svn;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return null;
    }

    private static readonly Dictionary<Guid, (PetClearanceBody? Body, double At)> Bodies = [];

    /// <summary>A template's body as the game data holds it now (tests replace it).</summary>
    internal static Func<Guid, PetClearanceBody?> TemplateReader { get; set; } = ReadTemplate;

    /// <summary>The template's body (kiln.gamedata), read again every <see cref="RecheckSeconds"/>: a data pack, a reload or
    /// a mod's change to the template's walker, collider or scale is a changed body.</summary>
    internal static PetClearanceBody? TemplateBody(PetDefinition def)
    {
        var info = PetCreatures.Resolve(def.Creature, out _);
        if (info is null) return null;
        var now = Clock();
        lock (Gate)
            if (Bodies.TryGetValue(info.Template, out var known) && now - known.At < RecheckSeconds) return known.Body;
        var body = TemplateReader(info.Template);
        lock (Gate) Bodies[info.Template] = (body, now);
        return body;
    }

    private static PetClearanceBody? ReadTemplate(Guid template)
    {
        try
        {
            if (!Capabilities.Is("kiln.gamedata") || Content.Templates.Get(template.ToString("D")) is not { } t) return null;
            return new PetClearanceBody(t.GetNumber("PathWalker.horizontalRadius"), t.GetNumber("PathWalker.height"),
                t.GetNumber("MovementColliderConfig.width"), t.GetNumber("MovementColliderConfig.height"))
            {
                TemplateScale = Optional(t, "ScaleComponent.uniformScale") ?? 1,
                Walker = Walks(t),
            };
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A number the template may not have (its component absent): null then.</summary>
    private static double? Optional(GameDataEntry t, string path)
    {
        try { return t.GetNumber(path); }
        catch (KeyNotFoundException) { return null; }
    }

    /// <summary>The template's path walker finds paths and it has no flying component; false when that cannot be read.</summary>
    private static bool Walks(GameDataEntry t)
    {
        try
        {
            return Optional(t, "PathWalker.usePathFinding") is { } finds && finds != 0 && !Has(t, "Flying") && !Has(t, "FlyingEnemy");
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Whether the template has the component.</summary>
    private static bool Has(GameDataEntry t, string component)
    {
        try { return t.GetJson(component) is not JsonObject o || o["absent"] is null; }
        catch (KeyNotFoundException) { return false; }
    }

    // ---- tests and the calibration lab ----

    /// <summary>A profile that applies whatever the build and the body (tests; the calibration lab's sweep).</summary>
    internal static void SetForTests(string creature, double scale, PetClearanceProfile? p)
    {
        lock (Gate)
        {
            if (p is null) Overrides.Remove((creature, Math.Round(scale, 3)));
            else Overrides[(creature, Math.Round(scale, 3))] = p;
        }
    }

    /// <summary>No overrides and no sources: until a test sets its seams, nothing is measured.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Overrides.Clear();
            Files.Clear();
            Warned.Clear();
            Bodies.Clear();
            _shipped = null;
            SourcesOn = false;
            GameBuild = RunningBuild;
            BodyOf = TemplateBody;
            TemplateReader = ReadTemplate;
            ShippedJson = ReadShipped;
            ServerFile = () => Path.Combine(KilnPaths.Root, "data", "kiln.pets", FileName);
            ModFolder = ModDirectory;
            Clock = () => Environment.TickCount64 / 1000.0;
        }
    }
}
