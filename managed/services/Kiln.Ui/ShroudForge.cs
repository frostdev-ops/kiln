// SPDX-License-Identifier: MIT
// ShroudForge's mod packages for the Kiln menu: a package's switch and settings as ShroudForge reads them, and the
// writer that changes them. ShroudForge (github.com/bonsaibauer/shroudforge) reads <mods>/<package>/mod.json and
// extended.mod.json; its running runtime looks at both files about once a second and hands a changed switch or
// setting to the mod while the game runs, when it can (Live below). Nothing here is ShroudForge's code: the rules are
// written down from its behaviour (docs/kiln/shroudforge.md) and checked against the launcher's Rust writer
// (crates/kiln-manage/src/shroudforge.rs) by the shared cases in schema/fixtures/shroudforge/cases.json, so the
// launcher and the in-game page write the same bytes.
//
// A write splices only the changed value into the file, so the rest stays byte-identical (comments, layout, keys we
// do not know). A refused value never reaches the file.
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>One choice of a setting with options: the value ShroudForge stores and the label it shows.</summary>
internal sealed record SfOption(JsonNode? Value, string Label);

/// <summary>A condition: the setting can be changed only while <see cref="Setting"/> holds <see cref="EqualsValue"/>.</summary>
internal sealed record SfCondition(string Setting, JsonNode? EqualsValue);

/// <summary>One setting of a package (extended.mod.json <c>settings.&lt;key&gt;</c>).</summary>
internal sealed record SfSettingDef
{
    public required string Key { get; init; }
    /// <summary>boolean, integer, number, string or array: from the declared value, as ShroudForge types it.</summary>
    public required string Type { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    /// <summary>toggle, checkbox, text, textarea, number, slider, select, radio, segmented, multiselect, keybind, color;
    /// null for a plain one.</summary>
    public string? Control { get; init; }
    /// <summary>What the mod declares: live or restart (ShroudForge's default: restart).</summary>
    public string ApplyAt { get; init; } = "restart";
    /// <summary>ShroudForge hands a change to the running mod at once.</summary>
    public bool Live { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public IReadOnlyList<SfOption> Options { get; init; } = [];
    public SfCondition? EnabledWhen { get; init; }
    /// <summary>Written as <c>"key": value</c> (no label, no control).</summary>
    public bool Shorthand { get; init; }
    public JsonNode? Value { get; init; }
}

/// <summary>A package's settings in the order it declares them, indexed by key (the Mods page looks settings up every
/// frame; a package may declare many).</summary>
internal sealed class SfSettingList : IReadOnlyList<SfSettingDef>
{
    private readonly List<SfSettingDef> _list;
    private readonly Dictionary<string, SfSettingDef> _byKey = new(StringComparer.Ordinal);

    public SfSettingList(List<SfSettingDef> list)
    {
        _list = list;
        foreach (var s in list) _byKey.TryAdd(s.Key, s);
    }

    public SfSettingDef? Find(string key) => _byKey.GetValueOrDefault(key);
    public SfSettingDef this[int index] => _list[index];
    public int Count => _list.Count;
    public IEnumerator<SfSettingDef> GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A button a package declares in a settings group; it runs the mod's <c>shroudforge.ui.on_action</c> handler.</summary>
internal sealed record SfAction(string Id, string Label, string? Style, string? Confirm);

/// <summary>A settings group (extended.mod.json <c>groups[]</c>).</summary>
internal sealed record SfGroup(string Label, string? Description, IReadOnlyList<string> Settings, IReadOnlyList<SfAction> Actions);

/// <summary>A ShroudForge mod package as its files say now.</summary>
internal sealed record SfPackage
{
    public required string Folder { get; init; }
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>extended.mod.json <c>enabled</c> (off when the file or the member is missing, as in ShroudForge).</summary>
    public bool Enabled { get; init; }
    /// <summary>extended.mod.json exists (a legacy EML package has none until its first change).</summary>
    public bool Extended { get; init; }
    /// <summary>Switching it on or off applies while the game runs: a runtime mod without startup asset changes.</summary>
    public bool SwitchLive { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> Targets { get; init; } = [];
    public IReadOnlyList<SfSettingDef> Settings { get; init; } = [];
    public IReadOnlyList<SfGroup> Groups { get; init; } = [];

    /// <summary>The settings' values by key.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Values => Settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
}

/// <summary>A change ShroudForge would refuse (the file is left as it was).</summary>
internal sealed class SfEditException(string message) : Exception(message);

/// <summary>Reading and writing ShroudForge's package files. Every member is safe on any thread; nothing is cached.</summary>
internal static class ShroudForgePackages
{
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    /// <summary>The file ShroudForge's <c>ensure_eml_extension</c> writes for a legacy EML package, as the launcher
    /// writes it (kiln-manage <c>extension_text</c>), before the switch is set.</summary>
    internal const string EmlExtension =
        "{\n  \"$schema\": \"https://bonsaibauer.github.io/shroudforge/schemas/extended.mod.schema.json\",\n  \"enabled\": false,\n" +
        "  \"launcher\": \"EML\",\n  \"schemaVersion\": 1,\n  \"settings\": {},\n  \"targets\": [\n    \"client\",\n    \"server\"\n  ]\n}\n";

    internal const int MaxFileBytes = 2 * 1024 * 1024;

    private static void PlainFile(string path)
    {
        // Steam/library ancestors may be links; the package and its files are the authorised edit boundary.
        if ((File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new SfEditException("linked package files cannot be edited");
    }

    private static string ReadText(string path)
    {
        PlainFile(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (f.Length > MaxFileBytes) throw new SfEditException("the settings file is too large");
        // Bound the read too, in case an external editor grows the file after the length check.
        var bytes = new byte[(int)f.Length + 1];
        var n = f.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (n == bytes.Length) throw new SfEditException("the settings file changed while reading; try again");
        var text = new UTF8Encoding(false, true).GetString(bytes, 0, n);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static JsonNode? Parse(string text) =>
        JsonNode.Parse(text, NodeOptions, ReadOptions);

    private static string? Str(JsonNode? o, string key) =>
        o is JsonObject obj && obj.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? o, string key) =>
        o is JsonObject obj && obj.TryGetPropertyValue(key, out var v) ? NumberOf(v) : null;

    /// <summary>A number however the node holds it (read from text, or made with JsonValue.Create).</summary>
    internal static double? NumberOf(JsonNode? v)
    {
        if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number) return null;
        if (jv.TryGetValue<JsonElement>(out var e)) return e.GetDouble();
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<float>(out var f)) return f;
        if (jv.TryGetValue<decimal>(out var m)) return (double)m;
        return null;
    }

    private static bool? Flag(JsonNode? o, string key) =>
        o is JsonObject obj && obj.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
        jv.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? jv.GetValue<bool>() : null;

    private static IReadOnlyList<string> Strings(JsonNode? o, string key) =>
        o is JsonObject obj && obj.TryGetPropertyValue(key, out var v) && v is JsonArray a
            ? a.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray() : [];

    /// <summary>ShroudForge's type of a value: boolean, integer (a whole number written without a fraction), number,
    /// string, array; null for anything else.</summary>
    internal static string? TypeOf(JsonNode? v)
    {
        switch (v)
        {
            case JsonArray: return "array";
            case JsonValue jv:
                switch (jv.GetValueKind())
                {
                    case JsonValueKind.True or JsonValueKind.False: return "boolean";
                    case JsonValueKind.String: return "string";
                    case JsonValueKind.Number:
                        // a JsonValue read from text keeps its token: "3" is an integer, "3.0" and "1e2" are not
                        if (jv.TryGetValue<JsonElement>(out var e)) return e.TryGetInt64(out _) ? "integer" : "number";
                        if (jv.TryGetValue<long>(out _) || jv.TryGetValue<int>(out _)) return "integer";
                        return "number";
                }
                return null;
            default: return null;
        }
    }

    /// <summary>ShroudForge's label for a key without one: a space before each capital (<c>noFallDamage</c> → "no Fall Damage").</summary>
    internal static string DisplayKey(string key)
    {
        var sb = new StringBuilder(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            if (i > 0 && char.IsUpper(key[i])) sb.Append(' ');
            sb.Append(key[i]);
        }
        return sb.ToString();
    }

    private static JsonNode? OptionValue(string type, JsonNode? value, string option)
    {
        var elementType = type == "array" && value is JsonArray a && a.Count > 0 ? TypeOf(a[0]) : type == "array" ? "string" : type;
        return elementType switch
        {
            "boolean" when option is "true" or "false" => JsonValue.Create(option == "true"),
            "integer" when long.TryParse(option, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
            "number" when double.TryParse(option, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => JsonValue.Create(d),
            "boolean" or "integer" or "number" => throw new SfEditException($"invalid {elementType} option {option}"),
            _ => JsonValue.Create(option),
        };
    }

    private static SfSettingDef ParseSetting(string key, JsonNode? entry, bool runtime, bool patch)
    {
        var meta = entry as JsonObject;
        var shorthand = meta is null || !meta.ContainsKey("value");
        var value = shorthand ? entry?.DeepClone() : meta!["value"]?.DeepClone();
        if (shorthand) meta = null;
        var type = TypeOf(value) ?? throw new SfEditException($"setting {key} has no editable value");
        var options = new List<SfOption>();
        if (meta?["options"] is JsonObject opts)
            foreach (var (k, label) in opts)
                options.Add(new SfOption(OptionValue(type, value, k), label is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : k));
        var control = Str(meta, "control");
        if (control is null && options.Count > 0) control = type == "array" ? "multiselect" : "select";
        var applyAt = Str(meta, "applyAt") ?? (Flag(meta, "restartRequired") == false ? "live" : "restart");
        if (applyAt is not ("live" or "restart") || Flag(meta, "restartRequired") is { } restart && restart != (applyAt == "restart"))
            throw new SfEditException($"setting {key}: applyAt and restartRequired must agree");
        if (type == "array" && options.Count == 0) throw new SfEditException($"list setting {key} must declare options");
        SfCondition? when = null;
        if (meta?["enabledWhen"] is { } condition)
        {
            if (condition is not JsonObject w || Str(w, "setting") is not { } target || !w.ContainsKey("equals"))
                throw new SfEditException($"setting {key}: invalid enabledWhen");
            when = new SfCondition(target, w["equals"]?.DeepClone());
        }
        int? Length(string keyName) => Num(meta, keyName) is { } n && n >= 0 && n <= int.MaxValue && Math.Floor(n) == n ? (int)n : null;
        return new SfSettingDef
        {
            Key = key, Type = type, Label = Str(meta, "label") ?? DisplayKey(key), Description = Str(meta, "description"),
            Control = control, ApplyAt = applyAt, Live = runtime && (!patch || applyAt == "live"),
            Min = Num(meta, "min"), Max = Num(meta, "max"), Step = Num(meta, "step"),
            MinLength = Length("minLength"), MaxLength = Length("maxLength"),
            Options = options, EnabledWhen = when, Shorthand = shorthand, Value = value,
        };
    }

    /// <summary>Reads the package in <paramref name="folder"/>. Throws when mod.json or extended.mod.json cannot be read.</summary>
    public static SfPackage Read(string folder)
    {
        var mod = Parse(ReadText(Path.Combine(folder, "mod.json"))) as JsonObject
                  ?? throw new SfEditException("mod.json is not a JSON object");
        var id = Str(mod, "id") ?? throw new SfEditException("mod.json has no \"id\"");
        var extPath = Path.Combine(folder, "extended.mod.json");
        var extended = File.Exists(extPath);
        var ext = extended ? Parse(ReadText(extPath)) as JsonObject ?? throw new SfEditException("extended.mod.json is not a JSON object") : new JsonObject();
        var caps = Strings(mod, "capabilities");
        var runtime = caps.Any(c => c is "runtime" or "runtime-register-dll");
        var patch = caps.Contains("patch");
        var launcher = Str(ext, "launcher") ?? (extended ? null : "EML");
        var targets = Strings(ext, "targets");
        if (ext["targets"] is not JsonArray)
            targets = launcher == "EML" || !extended ? ["client", "server"] : ["client"];
        var settings = new List<SfSettingDef>();
        if (ext["settings"] is JsonObject so)
        {
            // duplicate keys: the last one counts (JsonNode refuses duplicates, so a file with them fails to read above)
            foreach (var (k, v) in so) settings.Add(ParseSetting(k, v, runtime, patch));
        }
        var groups = new List<SfGroup>();
        if (ext["groups"] is JsonArray ga)
            foreach (var g in ga.OfType<JsonObject>())
            {
                var actions = new List<SfAction>();
                if (g["actions"] is JsonArray aa)
                    foreach (var a in aa.OfType<JsonObject>())
                        if (Str(a, "id") is { } aid)
                            actions.Add(new SfAction(aid, Str(a, "label") ?? aid, Str(a, "style"), Str(a, "confirm")));
                groups.Add(new SfGroup(Str(g, "label") ?? "Settings", Str(g, "description"), Strings(g, "settings"), actions));
            }
        return new SfPackage
        {
            Folder = folder, Id = id, Name = Str(mod, "name") ?? id, Version = Str(mod, "version") ?? "", Description = Str(mod, "description") ?? "",
            Enabled = Flag(ext, "enabled") ?? Flag(mod, "enabled") ?? false, Extended = extended, SwitchLive = runtime && !patch,
            Capabilities = caps, Targets = targets, Settings = new SfSettingList(settings), Groups = groups,
        };
    }

    // ---------------------------------------------------------------------------------------- the rules ----

    private static bool SameValue(JsonNode? a, JsonNode? b)
    {
        // ShroudForge's editor compares numbers by value (5 = 5.0)
        if (a is JsonValue av && b is JsonValue bv && av.GetValueKind() == JsonValueKind.Number && bv.GetValueKind() == JsonValueKind.Number)
            return NumberOf(av) == NumberOf(bv);
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>The setting <paramref name="key"/> (an index lookup for a package's own list).</summary>
    public static SfSettingDef? Find(IReadOnlyList<SfSettingDef> settings, string key) =>
        settings is SfSettingList l ? l.Find(key) : settings.FirstOrDefault(x => x.Key == key);

    /// <summary>ShroudForge's <c>setting_is_enabled</c>: the <c>enabledWhen</c> chain holds for <paramref name="values"/>.
    /// A missing controller or a cycle disables the setting.</summary>
    public static bool IsEnabled(string key, IReadOnlyList<SfSettingDef> settings, IReadOnlyDictionary<string, JsonNode?> values)
    {
        var current = key;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var s = Find(settings, current);
            if (s is null) return false;
            if (s.EnabledWhen is not { } cond) return true;
            if (!seen.Add(current)) return false;
            if (!values.TryGetValue(cond.Setting, out var v)) return false;
            if (!SameValue(v, cond.EqualsValue)) return false;
            current = cond.Setting;
        }
    }

    /// <summary>Why ShroudForge would refuse <paramref name="value"/> for <paramref name="s"/> (null: it accepts it).</summary>
    public static string? Check(SfSettingDef s, JsonNode? value)
    {
        var kind = TypeOf(value);
        var same = (s.Type, kind) switch
        {
            ("boolean", "boolean") or ("string", "string") or ("array", "array") => true,
            ("number", "integer" or "number") => true,
            ("integer", "integer") => true,
            ("integer", "number") => NumberOf(value) is { } w && Math.Abs(w) < 9e15 && Math.Floor(w) == w,
            _ => false,
        };
        if (!same)
            return s.Type == "integer" && kind == "number"
                ? $"{s.Label} takes a whole number"
                : $"{s.Label} takes a {Kind(s.Type)}, not a {Kind(kind)}";
        if (value is JsonValue sv && sv.GetValueKind() == JsonValueKind.String)
        {
            var text = sv.GetValue<string>();
            if (s.Control == "keybind" && !ValidShortcut(text))
                return $"{s.Label}: use an SDL shortcut such as Ctrl+SDL:66 or Mouse:4 (empty disables it; modifier order Ctrl, Alt, Shift, Meta)";
            var n = text.EnumerateRunes().Count();
            if ((s.MinLength is { } lo && n < lo) || (s.MaxLength is { } hi && n > hi))
                return $"{s.Label} must be {s.MinLength ?? 0} to {s.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? "any number of"} characters long";
        }
        if (NumberOf(value) is { } d)
        {
            if ((s.Min is { } lo && d < lo) || (s.Max is { } hi && d > hi))
                return $"{s.Label} must be between {Show(s.Min)} and {Show(s.Max)}";
        }
        if (s.Options.Count > 0)
        {
            bool Known(JsonNode? v) => s.Options.Any(o => SameValue(o.Value, v));
            var all = value is JsonArray arr ? arr.All(Known) : Known(value);
            if (!all) return $"{s.Label}: not one of {string.Join(", ", s.Options.Select(o => o.Label))}";
        }
        return null;
    }

    // Validate SF's portable/canonical SDL syntax without initializing a second input/window backend.
    // The running SF SDL backend additionally decides whether a scancode is available on this keyboard.
    internal static bool ValidShortcut(string text)
    {
        if (text.Length == 0) return true;
        if (Encoding.UTF8.GetByteCount(text) > 100) return false;
        var keyboard = false;
        var mouse = false;
        foreach (var chord in text.Split('|'))
        {
            var parts = chord.Split('+');
            var trigger = parts[^1].Split(':');
            if (trigger.Length != 2 || !int.TryParse(trigger[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ||
                code.ToString(CultureInfo.InvariantCulture) != trigger[1]) return false;
            var primary = 0;
            if (trigger[0] == "SDL")
            {
                if (keyboard || mouse || code is < 1 or > 511) return false;
                keyboard = true;
                primary = code switch { 224 or 228 => 1, 226 or 230 => 2, 225 or 229 => 4, 227 or 231 => 8, _ => 0 };
            }
            else if (trigger[0] == "Mouse")
            {
                if (mouse || code is < 1 or > 32) return false;
                mouse = true;
            }
            else return false;
            var previous = 0;
            foreach (var mod in parts[..^1])
            {
                var bit = mod switch { "Ctrl" => 1, "Alt" => 2, "Shift" => 4, "Meta" => 8, _ => 0 };
                if (bit <= previous || (bit & primary) != 0) return false;
                previous = bit;
            }
        }
        return true;
    }

    private static string Kind(string? t) => t switch
    {
        "boolean" => "yes/no value", "integer" => "whole number", "number" => "number", "array" => "list", "string" => "text", _ => "value",
    };

    internal static string Show(double? d) => d is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "";

    // ---------------------------------------------------------------------------------------- the writer ----

    /// <summary>Switches the package on or off (extended.mod.json <c>enabled</c>). Returns the package as written.</summary>
    public static SfPackage SetEnabled(string folder, bool on)
    {
        var p = Read(folder);
        if (p.Extended && p.Enabled == on) return p;
        var text = p.Extended ? ReadText(Path.Combine(folder, "extended.mod.json")) : EmlExtension;
        var root = JsonSpans.Parse(text);
        var rendered = on ? "true" : "false";
        var updated = root.Get("enabled") is { } node
            ? text[..node.Start] + rendered + text[node.End..]
            : JsonSpans.InsertMember(text, root, "enabled", rendered);
        Write(folder, updated);
        return Read(folder);
    }

    /// <summary>Sets one setting. Throws <see cref="SfEditException"/> (and writes nothing) when ShroudForge would refuse it.</summary>
    public static SfPackage SetSetting(string folder, string key, JsonNode? value)
    {
        var p = Read(folder);
        var s = Find(p.Settings, key) ?? throw new SfEditException($"{p.Name} has no setting {key}");
        if (Check(s, value) is { } why) throw new SfEditException(why);
        var text = p.Extended ? ReadText(Path.Combine(folder, "extended.mod.json")) : EmlExtension;
        var root = JsonSpans.Parse(text);
        var at = root.Get("settings")?.Get(key);
        if (at is not null && !s.Shorthand) at = at.Get("value");
        if (at is null) throw new SfEditException($"{p.Name}: setting {key} is not in extended.mod.json");
        var lineIndent = JsonSpans.LineIndent(text, at.Start);
        var rendered = s.Type == "number" && NumberOf(value) is { } d && Math.Floor(d) == d && Math.Abs(d) < 1e15
            // a whole value of a fractional setting stays fractional (2.0): ShroudForge types a setting by what it reads
            ? d.ToString("0.0", CultureInfo.InvariantCulture)
            : Render(value, lineIndent, text.Contains("\r\n", StringComparison.Ordinal));
        Write(folder, text[..at.Start] + rendered + text[at.End..]);
        return Read(folder);
    }

    private static void Write(string folder, string text)
    {
        // never write a file ShroudForge cannot read
        try { using var _ = JsonDocument.Parse(text, ReadOptions); }
        catch (JsonException e) { throw new SfEditException("the edit would break extended.mod.json: " + e.Message); }
        var path = Path.Combine(folder, "extended.mod.json");
        PlainFile(path);
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        if (bytes.Length > MaxFileBytes) throw new SfEditException("the settings file would be too large");
        var tmp = path + ".kiln-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                f.Write(bytes);
                // on disk before the rename: a crash must not leave a renamed, empty settings file (kiln-manage write_atomic)
                f.Flush(flushToDisk: true);
            }
            PlainFile(path);
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>A value as JSON text the way the launcher writes it (kiln-manage <c>jsonc::render</c>): scalar lists as
    /// <c>[a, b]</c>, strings with only quotes, backslashes and control characters escaped.</summary>
    internal static string Render(JsonNode? v, string indent = "", bool crlf = false)
    {
        switch (v)
        {
            case null: return "null";
            case JsonArray a:
                if (a.Count == 0) return "[]";
                if (a.All(x => x is not JsonArray and not JsonObject)) return "[" + string.Join(", ", a.Select(x => Render(x, indent, crlf))) + "]";
                var nl = crlf ? "\r\n" : "\n";
                var inner = indent + "  ";
                return "[" + nl + string.Join("," + nl, a.Select(x => inner + Render(x, inner, crlf))) + nl + indent + "]";
            case JsonObject o:
                if (o.Count == 0) return "{}";
                var nl2 = crlf ? "\r\n" : "\n";
                var inner2 = indent + "  ";
                return "{" + nl2 + string.Join("," + nl2, o.Select(kv => inner2 + Quote(kv.Key) + ": " + Render(kv.Value, inner2, crlf))) + nl2 + indent + "}";
            case JsonValue jv:
                switch (jv.GetValueKind())
                {
                    case JsonValueKind.True: return "true";
                    case JsonValueKind.False: return "false";
                    case JsonValueKind.String: return Quote(jv.GetValue<string>());
                    case JsonValueKind.Number:
                        if (jv.TryGetValue<JsonElement>(out var e) && e.TryGetInt64(out var whole)) return whole.ToString(CultureInfo.InvariantCulture);
                        if (jv.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
                        var d = NumberOf(jv) ?? 0;
                        if (Math.Floor(d) == d && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
                        return d.ToString("R", CultureInfo.InvariantCulture);
                }
                return "null";
            default: return "null";
        }
    }

    /// <summary>kiln-manage <c>jsonc::quote</c>.</summary>
    internal static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ---------------------------------------------------------------------------- actions and SF's folders ----

    /// <summary>ShroudForge's folder for <paramref name="key"/> (its <c>paths</c> setting; ui, state, mods), from
    /// <c>&lt;game&gt;/shroudforge/config/modloader-config.json</c>.</summary>
    public static string Folder(string gameDir, string key)
    {
        try
        {
            var cfg = Path.Combine(gameDir, "shroudforge", "config", "modloader-config.json");
            if (File.Exists(cfg) && Parse(ReadText(cfg)) is JsonObject o && o["paths"] is JsonObject paths && Str(paths, key) is { Length: > 0 } p)
                return Path.IsPathRooted(p) ? p : Path.Combine(gameDir, p);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or SfEditException or DecoderFallbackException) { }
        return key switch
        {
            "mods" => Path.Combine(gameDir, "mods"),
            "state" => Path.Combine(gameDir, "shroudforge"),
            _ => Path.Combine(gameDir, "shroudforge", key),
        };
    }

    /// <summary>Asks the running mod to run one of its declared actions, as ShroudForge's Modloader window does: a
    /// request file in <c>&lt;ui&gt;/actions/</c> that its runtime picks up (and removes) on its next turn.</summary>
    public static void QueueAction(string gameDir, SfPackage p, string action)
    {
        static bool Identifier(string s) => s.Length is > 0 and <= 80 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
        if (!Identifier(p.Id) || !Identifier(action)) throw new SfEditException("invalid ShroudForge action identifier");
        if (!p.Groups.Any(g => g.Actions.Any(a => a.Id == action))) throw new SfEditException($"{p.Name} declares no action {action}");
        var dir = Path.Combine(Folder(gameDir, "ui"), "actions");
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        var nanos = (now.ToUnixTimeMilliseconds() * 1_000_000L).ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var body = $"{{\"modId\":{Quote(p.Id)},\"action\":{Quote(action)},\"createdAt\":{now.ToUnixTimeSeconds()}}}";
        // written next to the folder and moved in: the runtime reads every file there and drops one it cannot parse
        var tmp = Path.Combine(Path.GetDirectoryName(dir)!, $".kiln-action-{Environment.ProcessId}-{nanos}.tmp");
        File.WriteAllText(tmp, body, new UTF8Encoding(false));
        File.Move(tmp, Path.Combine(dir, $"{p.Id}-{nanos}-{Environment.ProcessId}.json"), overwrite: true);
    }
}

/// <summary>Where each value of a JSON(C) text stands (offsets in the string): enough to splice one value or add one
/// member, as kiln-manage's <c>jsonc</c> does. Accepts what System.Text.Json accepts with comments skipped and trailing
/// commas allowed.</summary>
internal sealed class JsonSpans
{
    public int Start, End;
    public char Kind;  // '{', '[', or 'v' for a scalar
    public readonly List<(string Key, int KeyStart, JsonSpans Value, int? Comma)> Members = [];
    public readonly List<(JsonSpans Value, int? Comma)> Items = [];

    /// <summary>The member's value (the last one with this key).</summary>
    public JsonSpans? Get(string key)
    {
        for (var i = Members.Count - 1; i >= 0; i--)
            if (Members[i].Key == key) return Members[i].Value;
        return null;
    }

    public static JsonSpans Parse(string text)
    {
        var i = 0;
        var root = Value(text, ref i, 0);
        Skip(text, ref i);
        if (i != text.Length) throw new SfEditException("extended.mod.json has text after its end");
        return root;
    }

    private static void Skip(string t, ref int i)
    {
        while (i < t.Length)
        {
            var c = t[i];
            if (c is ' ' or '\t' or '\r' or '\n') i++;
            else if (c == '/' && i + 1 < t.Length && t[i + 1] == '/')
                // a line comment ends at CR or LF (System.Text.Json, kiln-manage jsonc)
                while (i < t.Length && t[i] is not ('\n' or '\r')) i++;
            else if (c == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                var end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new SfEditException("extended.mod.json has an open comment");
                i = end + 2;
            }
            else break;
        }
    }

    private static string StringToken(string t, ref int i)
    {
        var start = i;
        i++;
        while (i < t.Length && t[i] != '"')
            i += t[i] == '\\' ? 2 : 1;
        if (i >= t.Length) throw new SfEditException("extended.mod.json has an open string");
        i++;
        return JsonSerializer.Deserialize<string>(t.AsSpan(start, i - start))!;
    }

    private static JsonSpans Value(string t, ref int i, int depth)
    {
        if (depth > 64) throw new SfEditException("extended.mod.json is nested too deeply");
        Skip(t, ref i);
        if (i >= t.Length) throw new SfEditException("extended.mod.json ends early");
        var n = new JsonSpans { Start = i };
        var c = t[i];
        if (c == '{')
        {
            n.Kind = '{';
            i++;
            while (true)
            {
                Skip(t, ref i);
                if (i < t.Length && t[i] == '}') { i++; break; }
                if (i >= t.Length || t[i] != '"') throw new SfEditException("extended.mod.json: a key was expected");
                var keyStart = i;
                var key = StringToken(t, ref i);
                Skip(t, ref i);
                if (i >= t.Length || t[i] != ':') throw new SfEditException("extended.mod.json: ':' was expected");
                i++;
                var v = Value(t, ref i, depth + 1);
                Skip(t, ref i);
                int? comma = null;
                if (i < t.Length && t[i] == ',') { comma = i; i++; }
                n.Members.Add((key, keyStart, v, comma));
                if (comma is null)
                {
                    Skip(t, ref i);
                    if (i >= t.Length || t[i] != '}') throw new SfEditException("extended.mod.json: '}' was expected");
                    i++;
                    break;
                }
            }
        }
        else if (c == '[')
        {
            n.Kind = '[';
            i++;
            while (true)
            {
                Skip(t, ref i);
                if (i < t.Length && t[i] == ']') { i++; break; }
                var v = Value(t, ref i, depth + 1);
                Skip(t, ref i);
                int? comma = null;
                if (i < t.Length && t[i] == ',') { comma = i; i++; }
                n.Items.Add((v, comma));
                if (comma is null)
                {
                    Skip(t, ref i);
                    if (i >= t.Length || t[i] != ']') throw new SfEditException("extended.mod.json: ']' was expected");
                    i++;
                    break;
                }
            }
        }
        else if (c == '"')
        {
            n.Kind = 'v';
            StringToken(t, ref i);
        }
        else
        {
            n.Kind = 'v';
            while (i < t.Length && t[i] is not (',' or '}' or ']' or ' ' or '\t' or '\r' or '\n' or '/')) i++;
            if (i == n.Start) throw new SfEditException("extended.mod.json: a value was expected");
        }
        n.End = i;
        return n;
    }

    private static int LineStart(string t, int at)
    {
        var i = t.LastIndexOf('\n', Math.Max(0, at - 1));
        return at == 0 ? 0 : i < 0 ? 0 : i + 1;
    }

    /// <summary>The whitespace before <paramref name="at"/> on its line, when only whitespace stands there.</summary>
    private static string? IndentAt(string t, int at)
    {
        var ls = LineStart(t, at);
        var pre = t[ls..at];
        return pre.All(ch => ch is ' ' or '\t') ? pre : null;
    }

    /// <summary>The indentation of the line holding <paramref name="at"/>.</summary>
    public static string LineIndent(string t, int at)
    {
        var ls = LineStart(t, at);
        var e = ls;
        while (e < t.Length && t[e] is ' ' or '\t') e++;
        return t[ls..e];
    }

    /// <summary>From <paramref name="at"/>: the end of its line when only spaces or a // comment follow, else at.</summary>
    private static int EndOfLineAfter(string t, int at)
    {
        var i = at;
        while (i < t.Length && t[i] is ' ' or '\t') i++;
        if (i + 1 < t.Length && t[i] == '/' && t[i + 1] == '/')
            while (i < t.Length && t[i] is not ('\n' or '\r')) i++;
        return i >= t.Length || t[i] is '\r' or '\n' ? i : at;
    }

    /// <summary>The text with member <paramref name="key"/> = <paramref name="rendered"/> added to the object
    /// <paramref name="obj"/> (kiln-manage <c>jsonc::insert_member</c>: a new last line in a multi-line object).</summary>
    public static string InsertMember(string t, JsonSpans obj, string key, string rendered)
    {
        var nl = t.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var body = ShroudForgePackages.Quote(key) + ": " + rendered;
        if (obj.Members.Count > 0)
        {
            var last = obj.Members[^1];
            var indent = IndentAt(t, last.KeyStart);
            var trailing = last.Comma is not null;
            var after = last.Comma is { } cm ? cm + 1 : last.Value.End;
            if (indent is not null)
            {
                var at = EndOfLineAfter(t, after);
                var insert = nl + indent + body + (trailing ? "," : "");
                // the later offset first, so the earlier one stays valid
                var s = t[..at] + insert + t[at..];
                return trailing ? s : s[..last.Value.End] + "," + s[last.Value.End..];
            }
            return trailing ? t[..after] + " " + body + "," + t[after..] : t[..after] + ", " + body + t[after..];
        }
        var open = obj.Start;
        var close = obj.End - 1;
        var outer = LineIndent(t, open);
        var inner = outer + "  ";
        var interior = t[(open + 1)..close];
        if (interior.Trim(' ', '\t', '\r', '\n').Length == 0)
            return t[..(open + 1)] + nl + inner + body + nl + outer + t[close..];
        if (IndentAt(t, close) is not null)
        {
            var ls = LineStart(t, close);
            return t[..ls] + inner + body + nl + t[ls..];
        }
        return t[..close] + nl + inner + body + nl + outer + t[close..];
    }
}
