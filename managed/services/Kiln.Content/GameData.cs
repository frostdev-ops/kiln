// SPDX-License-Identifier: MIT
// Managed face of kiln.gamedata (native/core/abi/services/kiln_gamedata.h; guide docs/kiln/services/gamedata.md):
// templates, arsenals, sequences, buffs, perks and skill nodes as registries (Content.Templates, ...), item config
// values (item.Config("ManaCost")), and pack sections of other services (Content.RegisterSection).
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static unsafe class GameDataNative
{
    public const string Service = "kiln.gamedata";
    public const uint Version = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Entry  // 56 bytes
    {
        public uint Size;
        public uint Section;
        public uint Id;
        public uint Flags;
        public NativeStr Name;
        public ulong Address;
        public fixed byte Guid[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, uint> Count;
        public delegate* unmanaged[Cdecl]<uint, uint, Entry*, int> EntryAt;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, Entry*, int> Find;
        public delegate* unmanaged[Cdecl]<uint, uint, NativeStr, ContentNative.Value*, ContentNative.Value*, int> Get;
        public delegate* unmanaged[Cdecl]<uint, uint, NativeStr, byte*, nuint, nuint*, int> GetJson;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, NativeStr, ContentNative.Value*, int> Set;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, NativeStr, NativeStr, byte*, nuint, nuint*, int> SetJson;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, NativeStr, int> Reset;
        public delegate* unmanaged[Cdecl]<uint, uint, NativeStr, uint, byte*, nuint, nuint*, int> Fields;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, NativeStr, byte*, nuint, nuint*, int> Dump;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Configs;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, delegate* unmanaged[Cdecl]<void*, NativeStr, NativeStr, NativeStr, NativeStr, ulong, uint, byte*, nuint, nuint*, int>, delegate* unmanaged[Cdecl]<void*, NativeStr, NativeStr, NativeStr, void>, void*, int> RegisterSection;
        public delegate* unmanaged[Cdecl]<NativeStr, int> UnregisterSection;
        public delegate* unmanaged[Cdecl]<ulong, uint, NativeStr, byte*, nuint, nuint*, int> Define;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Definitions;
    }

    public const uint Modified = 1, Clone = 2;

    /// <summary>Calls a JSON-returning function, growing the buffer when it reports BufferTooSmall.</summary>
    public static (int Rc, string Text) Json(Func<IntPtr, nuint, IntPtr, int> call, int cap = 1 << 16)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buf = (byte*)NativeMemory.Alloc((nuint)cap);
            try
            {
                nuint len = 0;
                var rc = call((IntPtr)buf, (nuint)cap, (IntPtr)(&len));
                if (rc == ContentNative.BufferTooSmall && (long)len >= cap)
                {
                    cap = (int)Math.Min((long)len + 1024, int.MaxValue / 2);
                    continue;
                }
                return (rc, System.Text.Encoding.UTF8.GetString(buf, (int)Math.Min(len, (nuint)(cap - 1))));
            }
            finally
            {
                NativeMemory.Free(buf);
            }
        }
        return (ContentNative.BufferTooSmall, "");
    }
}

/// <summary>
/// Starts the services that own pack sections a data pack uses when no mod loaded them (a data-only mod with a
/// "creatures" section): every ~2 s it asks kiln.gamedata which sections wait for a handler and runs the module
/// initializer of the service known for that section, which registers it (the waiting sections are then applied).
/// </summary>
internal static class GameDataSectionStarter
{
    private static readonly (string Section, string Type, string Assembly)[] Known =
        [("creatures", "Kiln.Creatures", "Kiln.Creatures"), ("abilities", "Kiln.Abilities", "Kiln.Abilities")];
    private static readonly HashSet<string> Tried = new();

    [ModuleInitializer]
    internal static void Init() => ServiceTick.Add("kiln gamedata sections", static (in TickInfo t) => Tick(t.Step));

    private static bool _blocksStarted;

    // A data-only mod's blocks/*.json: nothing else would load Kiln.Blocks for it.
    private static void StartBlocks()
    {
        if (_blocksStarted || !Mods.All.Any(m => m.IsLoaded && m.Directory.Length > 0 && Directory.Exists(Path.Combine(m.Directory, "blocks")))) return;
        _blocksStarted = true;
        try
        {
            var t = Type.GetType("Kiln.BlocksStartup, Kiln.Blocks");
            if (t != null) RuntimeHelpers.RunClassConstructor(t.TypeHandle);
            Backend.Current.Log(0, t != null ? LogLevel.Info : LogLevel.Warn, t != null
                ? "Kiln.Content: started Kiln.Blocks for a mod's blocks/ folder"
                : "Kiln.Content: a mod has a blocks/ folder and Kiln.Blocks is not installed");
        }
        catch (Exception e)
        {
            try { Backend.Current.Log(0, LogLevel.Warn, $"Kiln.Content: starting Kiln.Blocks: {e.Message}"); } catch { }
        }
    }

    private static void Tick(ulong step)
    {
        if (step % 250 == 17) StartBlocks();
        if (step % 50 != 13 || Tried.Count == Known.Length || !Content.GameDataAvailable) return;
        JsonNode? st;
        try
        {
            st = JsonNode.Parse(Content.GameDataStatus());
        }
        catch (JsonException)
        {
            return;
        }
        if (st?["waiting"] is not JsonArray waiting || waiting.Count == 0) return;
        foreach (var w in waiting)
        {
            var name = (string?)w;
            var known = Known.FirstOrDefault(k => k.Section == name);
            if (known.Section == null || !Tried.Add(known.Section)) continue;
            try
            {
                var t = Type.GetType($"{known.Type}, {known.Assembly}");
                if (t != null) RuntimeHelpers.RunModuleConstructor(t.Module.ModuleHandle);
                Backend.Current.Log(0, t != null ? LogLevel.Info : LogLevel.Warn, t != null
                    ? $"Kiln.Content: started {known.Assembly} for the waiting pack section \"{name}\""
                    : $"Kiln.Content: pack section \"{name}\" waits for {known.Assembly}, which is not installed");
            }
            catch (Exception e)
            {
                try { Backend.Current.Log(0, LogLevel.Warn, $"Kiln.Content: starting {known.Assembly} for \"{name}\": {e.Message}"); } catch { }
            }
        }
    }
}

/// <summary>The game-data registries of kiln.gamedata.</summary>
public enum GameDataSection
{
    /// <summary>Entity templates (<c>keen::ecs::TemplateResource</c>): creatures, projectiles, explosions, props.</summary>
    Templates = 1,
    /// <summary>Enemy AI arsenals (attacks and behaviour rules), by arsenal GUID or <c>template:&lt;name&gt;</c>.</summary>
    Arsenals = 2,
    /// <summary>Actor sub-sequences (timed events of casts, attacks, abilities), by name or sequence GUID.</summary>
    Sequences = 3,
    /// <summary>Buff types (<c>keen::BuffType</c>), by <c>0x</c> id.</summary>
    Buffs = 4,
    /// <summary>Perks (<c>keen::Perk</c>), by debug name.</summary>
    Perks = 5,
    /// <summary>Skill-tree nodes (<c>keen::SkillTreeNode</c>), by <c>0x</c> id or <c>item:&lt;ability item&gt;</c>.</summary>
    Skills = 6,
    /// <summary>Items, for paths kiln.content's flat schema does not have (config values, lists).</summary>
    Items = 7,
}

/// <summary>A call of a registered pack section (see <see cref="Content.RegisterSection"/>).</summary>
/// <param name="Json">The section's value, verbatim.</param>
/// <param name="Owner"><c>pack:&lt;mod&gt;</c> for data packs, <c>mod:&lt;mod&gt;</c> for <see cref="Content.Apply"/>, <c>sync</c> for the content sync.</param>
/// <param name="Source">The pack file (<c>&lt;mod&gt;/content/&lt;file&gt;.json</c>) or the Apply source.</param>
/// <param name="Reload">Part of a re-application of every pack.</param>
public sealed record ContentSectionCall(string Section, string Json, string Owner, string Source, ulong Generation, bool Reload);

/// <summary>What a pack section handler did.</summary>
public sealed record ContentSectionResult(int Applied, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public static ContentSectionResult Ok(int applied) => new(applied, [], []);
}

/// <summary>One leaf below an entry (from the game's reflection).</summary>
public sealed record GameDataField(string Path, string Type, JsonNode? Value, bool Writable, JsonNode? Original);

public static unsafe partial class Content
{
    private static GameDataNative.Table* _gd;
    private static bool _gdTried;

    internal static GameDataNative.Table* GD
    {
        get
        {
            if (_gd == null && !_gdTried)
            {
                _gd = Services.Get<GameDataNative.Table>(GameDataNative.Service, GameDataNative.Version);
                _gdTried = _gd != null;
            }
            return _gd;
        }
    }

    internal static GameDataNative.Table* RequireGD(string api)
    {
        Services.RequireTickThread(api);
        var t = GD;
        if (t == null) throw new NotSupportedException("kiln.gamedata is not available on this build");
        return t;
    }

    /// <summary>kiln.gamedata is loaded (it is ready once <see cref="Ready"/>; see <see cref="GameDataStatus"/>).</summary>
    public static bool GameDataAvailable => GD != null;

    /// <summary>Entity templates: creatures, projectiles, explosions. Paths start with a component type:
    /// <c>Enemy01BehaviorState.aggroSetup.viewRange</c>, <c>LifeTime.lifeTime</c>.</summary>
    public static GameDataRegistry Templates { get; } = new(GameDataSection.Templates);

    /// <summary>Enemy arsenals: <c>attacks[1].description.maxRange</c>, <c>behaviors[0].settings.chance</c>.</summary>
    public static GameDataRegistry Arsenals { get; } = new(GameDataSection.Arsenals);

    /// <summary>Actor sub-sequences: <c>events[SpawnEntityEvent#0].time</c>, <c>length</c>.</summary>
    public static GameDataRegistry Sequences { get; } = new(GameDataSection.Sequences);

    /// <summary>Buff types: <c>defaultLifeTime</c>, <c>impactValues.Damage</c>.</summary>
    public static GameDataRegistry Buffs { get; } = new(GameDataSection.Buffs);

    /// <summary>Perks: <c>damageModifier.distribution.magicalIceDamage</c>, <c>impactValues.*</c>.</summary>
    public static GameDataRegistry Perks { get; } = new(GameDataSection.Perks);

    /// <summary>Skill-tree nodes: <c>costs</c>, <c>maxLevel</c>, <c>configValues.*</c>.</summary>
    public static GameDataRegistry Skills { get; } = new(GameDataSection.Skills);

    /// <summary>Items through kiln.gamedata (config values, lists): <c>impactValues.ManaCost</c>,
    /// <c>sequences[0].triggeredBy</c>. Same entries as <see cref="Items"/>.</summary>
    public static GameDataRegistry ItemData { get; } = new(GameDataSection.Items);

    public static GameDataRegistry GameData(GameDataSection section) => section switch
    {
        GameDataSection.Templates => Templates,
        GameDataSection.Arsenals => Arsenals,
        GameDataSection.Sequences => Sequences,
        GameDataSection.Buffs => Buffs,
        GameDataSection.Perks => Perks,
        GameDataSection.Skills => Skills,
        GameDataSection.Items => ItemData,
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    /// <summary>kiln.gamedata's state as JSON: <c>{ready, detail, resources, sections: {name: {available, count, changed}}, externals}</c>.</summary>
    public static string GameDataStatus()
    {
        var t = GD;
        if (t == null) return "{\"ready\": false, \"detail\": \"kiln.gamedata is not loaded\"}";
        return GameDataNative.Json((b, c, l) => Content.GD->Status((byte*)b, c, (nuint*)l)).Text;
    }

    /// <summary>Every clone definition of kiln.gamedata (templates, sequences, perks, buffs; data packs, code, the
    /// content sync, placeholders). Any thread.</summary>
    public static IReadOnlyList<GameDataDefinition> GameDataDefinitions()
    {
        var t = GD;
        if (t == null) return [];
        var (rc, text) = GameDataNative.Json((b, c, l) => Content.GD->Definitions((byte*)b, c, (nuint*)l), 1 << 16);
        if (rc != ContentNative.Ok) return [];
        var list = new List<GameDataDefinition>();
        foreach (var x in (JsonNode.Parse(text) as JsonArray) ?? [])
            if (x is JsonObject o && GameDataDefinition.From(o) is { } d) list.Add(d);
        return list;
    }

    /// <summary>The config vocabulary (config ids, their aliases from the game's Impact programs, C++-read costs).</summary>
    public static string ConfigVocabulary()
    {
        var t = RequireGD("Content.ConfigVocabulary");
        var (rc, text) = GameDataNative.Json((b, c, l) => Content.GD->Configs((byte*)b, c, (nuint*)l), 1 << 18);
        if (rc != ContentNative.Ok) throw Failure("Content.ConfigVocabulary", rc);
        return text;
    }

    // ------------------------------------------------------------------------------------------- pack sections ---

    private static readonly ConcurrentDictionary<string, (Func<ContentSectionCall, ContentSectionResult> Apply, Action<string, string?> Revert)> Sections = new();

    /// <summary>
    /// Adds a data-pack section for another service (Forge: a custom data-pack reload listener). Every pack document
    /// and <see cref="Apply"/> call with a top-level key <paramref name="name"/> calls <paramref name="apply"/> with
    /// that value; <paramref name="revert"/> runs when what an (owner, source) applied must come off: a pack file that
    /// is no longer applied at a re-application (owner, source), an owner that went away (owner, null), and the world
    /// reset ("", null). Both run on the tick thread (the active world's thread) inside kiln.content's pack
    /// application, in pack order. Registered after the packs applied, the waiting sections are applied at once.
    /// Exceptions become pack errors. Do not call <see cref="Apply"/> from a handler.
    /// </summary>
    public static void RegisterSection(string name, Func<ContentSectionCall, ContentSectionResult> apply, Action<string, string?> revert)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(revert);
        var t = GD;
        if (t == null) throw new NotSupportedException("kiln.gamedata is not available on this build");
        Sections[name] = (apply, revert);
        using var n = new ContentNative.Utf8(name);
        var rc = t->RegisterSection(0, n.Str, &OnSectionApply, &OnSectionRevert, null);
        if (rc != ContentNative.Ok) throw Failure($"Content.RegisterSection({name})", rc);
    }

    /// <summary>Removes a section registered with <see cref="RegisterSection"/>.</summary>
    public static bool UnregisterSection(string name)
    {
        Sections.TryRemove(name, out _);
        var t = GD;
        if (t == null) return false;
        using var n = new ContentNative.Utf8(name);
        return t->UnregisterSection(n.Str) == ContentNative.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSectionApply(void* user, NativeStr section, NativeStr json, NativeStr owner, NativeStr source, ulong generation,
        uint reload, byte* report, nuint cap, nuint* len)
    {
        var name = section.ToString();
        var errors = new List<string>();
        var warnings = new List<string>();
        var applied = 0;
        try
        {
            if (Sections.TryGetValue(name, out var h))
            {
                var r = h.Apply(new ContentSectionCall(name, json.ToString(), owner.ToString(), source.ToString(), generation, reload != 0));
                applied = r.Applied;
                errors.AddRange(r.Errors);
                warnings.AddRange(r.Warnings);
            }
            else
            {
                warnings.Add("no handler");
            }
        }
        catch (Exception e)
        {
            errors.Add($"the handler threw {e.GetType().Name}: {e.Message}");
        }
        try
        {
            var text = JsonSerializer.Serialize(new { applied, errors, warnings });
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var n = Math.Min(bytes.Length, (int)cap - 1);
            if (n > 0) Marshal.Copy(bytes, 0, (IntPtr)report, n);
            if (cap > 0) report[Math.Max(n, 0)] = 0;
            *len = (nuint)Math.Max(n, 0);
        }
        catch
        {
        }
        return errors.Count == 0 ? ContentNative.Ok : ContentNative.InvalidArgument;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSectionRevert(void* user, NativeStr section, NativeStr owner, NativeStr source)
    {
        try
        {
            if (Sections.TryGetValue(section.ToString(), out var h))
            {
                var s = source.ToString();
                h.Revert(owner.ToString(), s.Length == 0 ? null : s);
            }
        }
        catch (Exception e)
        {
            try { Backend.Current.Log(0, LogLevel.Error, $"Kiln.Content: section {section} revert threw: {e}"); } catch { }
        }
    }
}

/// <summary>One kiln.gamedata registry. Entries are looked up by name (exact, then ignoring case), <c>"0x"</c> id,
/// GUID, and per section aliases (<c>template:&lt;name&gt;</c> for arsenals, <c>item:&lt;name&gt;</c> for skills).</summary>
public sealed unsafe class GameDataRegistry : IReadOnlyList<GameDataEntry>
{
    internal GameDataRegistry(GameDataSection section) => Section = section;

    public GameDataSection Section { get; }

    public int Count
    {
        get
        {
            var t = Content.RequireGD("GameDataRegistry.Count");
            return (int)t->Count((uint)Section);
        }
    }

    public GameDataEntry this[int index]
    {
        get
        {
            var t = Content.RequireGD("GameDataRegistry[]");
            GameDataNative.Entry e = default;
            e.Size = (uint)sizeof(GameDataNative.Entry);
            var rc = t->EntryAt((uint)Section, (uint)index, &e);
            if (rc != ContentNative.Ok) throw new ArgumentOutOfRangeException(nameof(index), ContentNative.Message(rc));
            return new GameDataEntry(Section, e);
        }
    }

    /// <summary>The entry for a key, or null.</summary>
    public GameDataEntry? Get(string key)
    {
        var t = Content.RequireGD("GameDataRegistry.Get");
        GameDataNative.Entry e = default;
        e.Size = (uint)sizeof(GameDataNative.Entry);
        using var k = new ContentNative.Utf8(key);
        return t->Find((uint)Section, k.Str, &e) == ContentNative.Ok ? new GameDataEntry(Section, e) : null;
    }

    public GameDataEntry? Get(uint id) => Get("0x" + id.ToString("x8"));

    public GameDataEntry? Get(Guid guid) => Get(guid.ToString());

    /// <summary>The entry is a clone (an "add" definition), not one of the game's.</summary>
    public bool IsClone(Guid guid) => Get(guid)?.IsClone == true;

    /// <summary>
    /// Defines a clone (templates, sequences, perks, buffs): a new entry with a fixed GUID made from <c>base</c>, with
    /// its own field values and (perks, buffs) its own name and description per language. The same as a data pack's
    /// <c>"add"</c> entry: <c>{"guid", "name", "base", "fields": {...}, "displayName", "description", "texts":
    /// {"de": {"name", "description"}}}</c>. Registered in the running world at once (and in every later world of the
    /// process); the fields are a layer of Kiln's "gamedata definitions" owner, so the calling mod's own changes and
    /// packs sit on top. Defining the same GUID again updates its fields and texts. Tick thread.
    /// </summary>
    /// <exception cref="ArgumentException">The definition is refused (the message says why).</exception>
    /// <exception cref="NotSupportedException">Clones of this section are not available on this build.</exception>
    public GameDataDefinition Define(JsonObject definition) => Define(definition, Content.Owner());

    /// <summary>Defines a clone as a given owner (Kiln.Blocks: a data-only mod's blocks).</summary>
    internal GameDataDefinition Define(JsonObject definition, ulong owner)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var t = Content.RequireGD("GameDataRegistry.Define");
        using var j = new ContentNative.Utf8(definition.ToJsonString());
        var (rc, text) = GameDataNative.Json((b, c, l) => Content.GD->Define(owner, (uint)Section, j.Str, (byte*)b, c, (nuint*)l), 1 << 16);
        var r = JsonNode.Parse(string.IsNullOrEmpty(text) ? "{}" : text) as JsonObject;
        if (rc == ContentNative.Unavailable) throw new NotSupportedException((string?)r?["error"] ?? $"{Section}: clones are not available");
        if (rc != ContentNative.Ok) throw new ArgumentException($"{Section} clone {(string?)definition["name"]}: {(string?)r?["error"] ?? ContentNative.Message(rc)}");
        // the report carries the definition as Definitions() lists it: listing them all for each one makes defining many slow
        if (r?["entry"] is JsonObject entry && GameDataDefinition.From(entry) is { } listed) return listed;
        var guid = Guid.TryParse((string?)definition["guid"], out var g) ? g : Guid.Empty;
        return Content.GameDataDefinitions().FirstOrDefault(d => d.Guid == guid)
               ?? throw new InvalidOperationException($"{Section} clone {guid}: defined but not listed");
    }

    /// <inheritdoc cref="Define(JsonObject)"/>
    public GameDataDefinition Define(GameDataClone clone)
    {
        clone.Values.Require(Section.ToString().ToLowerInvariant(), $"{Section} clone {clone.Name}");
        return Define(clone.ToJson());
    }

    /// <summary>This section's clone definitions (any thread).</summary>
    public IReadOnlyList<GameDataDefinition> Definitions() => Content.GameDataDefinitions().Where(d => d.Section == Section).ToList();

    /// <summary>Writes entries as JSON (key null: all of them) to <paramref name="path"/> (null:
    /// <c>kiln/data/content/gamedata-&lt;section&gt;.json</c>). Returns the path.</summary>
    public string Dump(string? key = null, string? path = null)
    {
        var t = Content.RequireGD("GameDataRegistry.Dump");
        using var k = new ContentNative.Utf8(key ?? "");
        using var p = new ContentNative.Utf8(path ?? "");
        var (rc, text) = GameDataNative.Json((b, c, l) => Content.GD->Dump((uint)Section, k.Str, p.Str, (byte*)b, c, (nuint*)l), 4096);
        if (rc != ContentNative.Ok) throw Content.Failure("GameDataRegistry.Dump", rc);
        return text;
    }

    public IEnumerator<GameDataEntry> GetEnumerator()
    {
        var n = Count;
        for (var i = 0; i < n; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One template, arsenal, sub-sequence, buff, perk, skill node or item. A handle: values are read from the
/// game each time; changes are layers of the calling mod (reverted when it unloads).</summary>
public sealed unsafe class GameDataEntry
{
    internal GameDataEntry(GameDataSection section, in GameDataNative.Entry e)
    {
        Section = section;
        Id = e.Id;
        Name = e.Name.ToString();
        Modified = (e.Flags & GameDataNative.Modified) != 0;
        IsClone = (e.Flags & GameDataNative.Clone) != 0;
        Address = e.Address;
        fixed (byte* g = e.Guid) Guid = new Guid(new ReadOnlySpan<byte>(g, 16));
    }

    public GameDataSection Section { get; }
    public uint Id { get; }
    public string Name { get; }
    public Guid Guid { get; }
    public bool Modified { get; }
    public bool IsClone { get; }

    /// <summary>The struct in game memory (diagnostics).</summary>
    public ulong Address { get; }

    public override string ToString() => $"{Section} {Name} (0x{Id:x8})";

    /// <summary>The current value of a leaf.</summary>
    public ContentValue Get(string path) => Read(path, false);

    /// <summary>The game's original value of a leaf.</summary>
    public ContentValue Original(string path) => Read(path, true);

    public double GetNumber(string path) => Get(path).AsDouble;

    /// <summary>Any node as JSON: <c>{path, type, value, original, writable, address}</c> (an array when the path
    /// fans out with <c>[*]</c>; <c>{"absent": true}</c> for a config the struct does not have yet).</summary>
    public JsonNode? GetJson(string path = "")
    {
        var t = Content.RequireGD("GameDataEntry.GetJson");
        using var p = new ContentNative.Utf8(path);
        var (rc, text) = GameDataNative.Json((b, c, l) => Content.GD->GetJson((uint)Section, Id, p.Str, (byte*)b, c, (nuint*)l), 1 << 18);
        if (rc == ContentNative.NotFound) throw new KeyNotFoundException($"{Name}.{path}: {JsonNode.Parse(text)?["error"]}");
        if (rc != ContentNative.Ok) throw Content.Failure($"{Name}.{path}", rc);
        return JsonNode.Parse(text);
    }

    public void Set(string path, double value) => SetJson(path, JsonSerializer.Serialize(value));
    public void Set(string path, long value) => SetJson(path, JsonSerializer.Serialize(value));
    public void Set(string path, int value) => SetJson(path, JsonSerializer.Serialize(value));
    public void Set(string path, bool value) => SetJson(path, value ? "true" : "false");
    public void Set(string path, TimeSpan value) => SetJson(path, JsonSerializer.Serialize(value.TotalSeconds));

    /// <summary>An enum name, a GUID, a template / sequence name for references, or <c>"0x..."</c>.</summary>
    public void Set(string path, string value) => SetJson(path, JsonSerializer.Serialize(value));

    public void Set(string path, Guid value) => SetJson(path, JsonSerializer.Serialize(value.ToString()));

    /// <summary>Any JSON value for the path, as in data packs: numbers, names, flag lists, objects for structs,
    /// <c>{"mul": 1.5}</c>. Returns how many leaves changed.</summary>
    /// <exception cref="ArgumentException">Nothing changed (the message lists why).</exception>
    public int SetJson(string path, string json)
    {
        var t = Content.RequireGD("GameDataEntry.Set");
        var owner = Content.Owner();
        using var p = new ContentNative.Utf8(path);
        using var v = new ContentNative.Utf8(json);
        var (rc, text) = GameDataNative.Json((b, c, l) => Content.GD->SetJson(owner, (uint)Section, Id, p.Str, v.Str, (byte*)b, c, (nuint*)l), 8192);
        var r = JsonNode.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
        var n = (int?)r?["applied"] ?? 0;
        var errors = (r?["errors"] as JsonArray)?.Select(x => (string?)x ?? "").ToList() ?? [];
        if (rc == ContentNative.InvalidArgument || n == 0) throw new ArgumentException($"{Name}.{path}: {string.Join("; ", errors)}");
        if (rc != ContentNative.Ok) throw Content.Failure($"{Name}.{path}", rc);
        return n;
    }

    /// <summary>Takes the calling mod's layers of the path (and below) off. False when it had none.</summary>
    public bool Reset(string path)
    {
        var t = Content.RequireGD("GameDataEntry.Reset");
        var owner = Content.Owner();
        using var p = new ContentNative.Utf8(path);
        return t->Reset(owner, (uint)Section, Id, p.Str) == ContentNative.Ok;
    }

    /// <summary>Every leaf below <paramref name="prefix"/> (null: the whole entry), from the game's reflection.</summary>
    public IReadOnlyList<GameDataField> Fields(string? prefix = null, int limit = 0)
    {
        var t = Content.RequireGD("GameDataEntry.Fields");
        using var p = new ContentNative.Utf8(prefix ?? "");
        var (rc, text) = GameDataNative.Json((b, c, l) => Content.GD->Fields((uint)Section, Id, p.Str, (uint)limit, (byte*)b, c, (nuint*)l), 1 << 20);
        if (rc != ContentNative.Ok) throw Content.Failure($"{Name}.fields", rc);
        var list = new List<GameDataField>();
        foreach (var x in JsonNode.Parse(text)?.AsArray() ?? [])
            list.Add(new GameDataField((string?)x?["path"] ?? "", (string?)x?["type"] ?? "", x?["value"]?.DeepClone(),
                (bool?)x?["writable"] ?? false, x?["original"]?.DeepClone()));
        return list;
    }

    /// <summary>A config value by alias or <c>0x</c> id (<c>ManaCost</c>, <c>NoiseRange</c>, <c>0xa19409b2</c>): items,
    /// perks and buffs <c>impactValues</c>, skills <c>configValues</c>, templates <c>ImpactConfiguration.configValues</c>.</summary>
    public GameDataConfig Config(string alias) => new(this, Section switch
    {
        GameDataSection.Templates => "ImpactConfiguration.configValues." + alias,
        GameDataSection.Skills => "configValues." + alias,
        _ => "impactValues." + alias,
    });

    /// <summary>Sequences: the k-th event of a type (<c>SpawnEntityEvent</c>, <c>PayUsageCost</c>, ...).</summary>
    public GameDataEvent Event(string type, int k = 0) => new(this, $"events[{type}#{k}]");

    private ContentValue Read(string path, bool original)
    {
        var t = Content.RequireGD("GameDataEntry.Get");
        ContentNative.Value cur = default, orig = default;
        using var p = new ContentNative.Utf8(path);
        var rc = t->Get((uint)Section, Id, p.Str, &cur, &orig);
        if (rc != ContentNative.Ok) throw Content.Failure($"{Name}.{path}", rc);
        var v = original ? orig : cur;
        string? name = null;
        if (v.Type == (uint)ContentValueType.Int)
        {
            var j = GetJson(path);
            var node = j?[original ? "original" : "value"];
            if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) name = s;
            else if (node is JsonArray ja) name = string.Join(",", ja.Select(x => x?.ToString()));
        }
        var g = new Guid(new ReadOnlySpan<byte>(v.Guid, 16));
        return new ContentValue((ContentValueType)v.Type, v.I, v.F, g, name);
    }
}

/// <summary>A config value of an entry (<see cref="GameDataEntry.Config"/>).</summary>
public sealed class GameDataConfig
{
    private readonly GameDataEntry _entry;

    internal GameDataConfig(GameDataEntry entry, string path)
    {
        _entry = entry;
        Path = path;
    }

    public string Path { get; }

    /// <summary>The entry has this config (else <see cref="Set(double)"/> adds it).</summary>
    public bool Exists => _entry.GetJson(Path)?["absent"] is null;

    public ContentValue Get() => _entry.Get(Path);
    public ContentValue Original() => _entry.Original(Path);
    public void Set(double value) => _entry.Set(Path, value);
    public void Set(long value) => _entry.Set(Path, value);
    public void Set(string value) => _entry.Set(Path, value);
    public bool Reset() => _entry.Reset(Path);
    public override string ToString() => $"{_entry.Name}.{Path}";
}

/// <summary>A sequence event (<see cref="GameDataEntry.Event"/>): time and duration in seconds, other fields by path.</summary>
public sealed class GameDataEvent
{
    private readonly GameDataEntry _entry;

    internal GameDataEvent(GameDataEntry entry, string path)
    {
        _entry = entry;
        Path = path;
    }

    public string Path { get; }

    public TimeSpan Time
    {
        get => TimeSpan.FromSeconds(_entry.Get(Path + ".time").Number);
        set => _entry.Set(Path + ".time", value);
    }

    public TimeSpan Duration
    {
        get => TimeSpan.FromSeconds(_entry.Get(Path + ".duration").Number);
        set => _entry.Set(Path + ".duration", value);
    }

    public ContentValue Get(string field) => _entry.Get(Path + "." + field);
    public void SetJson(string field, string json) => _entry.SetJson(Path + "." + field, json);
    public override string ToString() => $"{_entry.Name}.{Path}";
}

/// <summary>Item config values (kiln.gamedata) on kiln.content's item entries.</summary>
public static class GameDataItemExtensions
{
    /// <summary>The item as a kiln.gamedata entry (config values, lists).</summary>
    public static GameDataEntry Data(this ContentEntry item) =>
        item.Kind == ContentKind.Items
            ? Content.ItemData.Get(item.Id) ?? throw new KeyNotFoundException($"{item.Name} is not in kiln.gamedata")
            : throw new ArgumentException("only items have game data paths");

    /// <summary>A config value of an item by alias (<c>ManaCost</c>, <c>StaminaCost</c>, <c>Damage</c>) or <c>0x</c>
    /// id: <c>impactValues.&lt;alias&gt;</c>; its tooltip value follows.</summary>
    public static GameDataConfig Config(this ContentEntry item, string alias) => item.Data().Config(alias);
}

/// <summary>Texts of a perk or buff clone in one language.</summary>
public sealed record GameDataTexts(string? Name = null, string? Description = null);

/// <summary>
/// A clone definition for <see cref="GameDataRegistry.Define(GameDataClone)"/>: the data-pack <c>"add"</c> entry as an
/// object. <see cref="Fields"/> takes any pack value per path (numbers, names, <c>{"mul": 2}</c> as JSON nodes).
/// </summary>
public sealed class GameDataClone
{
    public required Guid Guid { get; init; }

    /// <summary>The clone's name (letters, digits, <c>_ - .</c>): templates and perks get it as their debug name;
    /// rules, references and lookups use it.</summary>
    public required string Name { get; init; }

    /// <summary>The entry to copy: a name, GUID or <c>0x</c> id of the same section (a clone too).</summary>
    public required string Base { get; init; }

    public Dictionary<string, JsonNode?> Fields { get; init; } = new();

    /// <summary><see cref="Fields"/> by typed path: <c>Values = { { GameFields.Templates.LifeTime.LifeTime, TimeSpan.FromSeconds(5) } }</c>.</summary>
    public FieldValues Values { get; } = new();

    /// <summary>Perks, buffs: the shown name / description in every language (<see cref="Texts"/> per language win).</summary>
    public string? DisplayName { get; init; }
    public string? Description { get; init; }

    /// <summary>Perks, buffs: texts per language key ("en", "de", "pt-BR"; "*" = every language).</summary>
    public Dictionary<string, GameDataTexts> Texts { get; init; } = new();

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["guid"] = Guid.ToString("D"), ["name"] = Name, ["base"] = Base };
        if (Fields.Count + Values.Count > 0)
        {
            var f = new JsonObject();
            foreach (var (k, v) in Fields) f[k] = v?.DeepClone();
            foreach (var (k, v) in Values.Json) f[k] = v.DeepClone();
            o["fields"] = f;
        }
        if (!string.IsNullOrEmpty(DisplayName)) o["displayName"] = DisplayName;
        if (!string.IsNullOrEmpty(Description)) o["description"] = Description;
        if (Texts.Count > 0)
        {
            var t = new JsonObject();
            foreach (var (lang, x) in Texts)
            {
                var e = new JsonObject();
                if (!string.IsNullOrEmpty(x.Name)) e["name"] = x.Name;
                if (!string.IsNullOrEmpty(x.Description)) e["description"] = x.Description;
                t[lang] = e;
            }
            o["texts"] = t;
        }
        return o;
    }
}

/// <summary>One clone definition as kiln.gamedata keeps it (<see cref="Content.GameDataDefinitions"/>).</summary>
/// <param name="State"><c>registered</c>, <c>pending</c> (waiting for a world), <c>waiting</c> (the world's registry is
/// not loaded yet), <c>error</c>, <c>orphaned</c> (its data pack went away; it stays registered).</param>
/// <param name="DefinitionJson">The definition as given (what Kiln clients get through the content sync).</param>
public sealed record GameDataDefinition(GameDataSection Section, Guid Guid, uint Id, string Name, string Base, string State,
    string Owner, string? Mod, bool IsPlaceholder, string? Error, IReadOnlyList<string> Warnings, string DefinitionJson)
{
    internal static GameDataDefinition? From(JsonObject o)
    {
        var section = (string?)o["section"] switch
        {
            "templates" => GameDataSection.Templates,
            "sequences" => GameDataSection.Sequences,
            "perks" => GameDataSection.Perks,
            "buffs" => GameDataSection.Buffs,
            _ => (GameDataSection)0,
        };
        if (section == 0 || !Guid.TryParse((string?)o["guid"], out var g)) return null;
        var idText = (string?)o["id"] ?? "0x0";
        var id = uint.TryParse(idText.StartsWith("0x") ? idText[2..] : idText, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
        var mod = (string?)o["mod"];
        return new GameDataDefinition(section, g, id, (string?)o["name"] ?? "", (string?)o["base"] ?? "", (string?)o["state"] ?? "",
            (string?)o["owner"] ?? "", string.IsNullOrEmpty(mod) ? null : mod, (bool?)o["placeholder"] ?? false, (string?)o["error"],
            (o["warnings"] as JsonArray)?.Select(x => (string?)x ?? "").ToList() ?? [], o["definition"]?.ToJsonString() ?? "{}");
    }
}

/// <summary>
/// Missing perk and buff clones (like kiln.content's missing items, MissingContent.cs): every perk / buff clone the
/// server world registered is recorded in the world's WorldData (Kiln's bundle <c>kiln.gamedata</c>, key
/// <c>missing-gamedata</c>) with its definition. At world load (kiln.content's ready callback, before the game reads the
/// save), a recorded clone that no loaded mod or pack defines gets a placeholder under its own GUID (the same id, so
/// saved items with the perk and saved buffs keep resolving): the recorded base made inert (no program, configs or
/// VFX), named <c>[Missing: mod:name]</c>. When the mod comes back its definition takes the GUID over (state
/// <c>restored</c>). Kiln clients get the placeholders through the content sync like any definition.
/// </summary>
internal static unsafe class GameDataMissing
{
    private const string Key = "missing-gamedata";

    public sealed class Record
    {
        public int Format { get; set; } = 1;
        /// <summary>GUID text -> what is needed to rebuild a placeholder.</summary>
        public Dictionary<string, SeenClone> Clones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SeenClone
    {
        public string Section { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Base { get; set; } = "";
        public string? Mod { get; set; }
        public string? DisplayName { get; set; }
        public string Definition { get; set; } = "{}";
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    private static ModContext? _ctx;
    private static ulong _owner, _handle;
    private static string? _world;
    private static int _lastCount = -1;
    private static readonly List<string> _placed = new();

    /// <summary>Placeholders made at the last world load ("perks mod:name (0x...)").</summary>
    public static IReadOnlyList<string> Placed => _placed;

    // Subscribed from the module initializer, before any mod's OnLoad reaches Content (whose own ready callback runs
    // the mods' OnReady handlers): native ready callbacks run in subscription order, so the placeholders exist when
    // mods' handlers run, like kiln.content's MissingContent. The tick retries if the service was not there yet.
    [ModuleInitializer]
    internal static void Init()
    {
        ServiceTick.Add("kiln gamedata missing", static (in TickInfo t) => Tick(t.Step));
        try
        {
            Subscribe();
        }
        catch (Exception)
        {
        }
    }

    private static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, "missing gamedata: " + text);

    private static bool HasServerWorld => !Kiln.Client.IsClient || Kiln.Client.IsHosting;

    private static void Subscribe()
    {
        if (_handle != 0) return;
        // the tables directly: touching Content here would run its static constructor (and its subscription) first
        if (Services.Get<GameDataNative.Table>(GameDataNative.Service, GameDataNative.Version) == null) return;
        var t = Services.Get<ContentNative.Table>(ContentNative.Service, ContentNative.Version);
        if (t == null) return;
        ulong h = 0;
        if (t->SubscribeReady(0, &OnNativeReady, null, &h) == ContentNative.Ok) _handle = h;
    }

    private static bool Ensure()
    {
        if (_ctx != null) return true;
        if (!Saves.Available) return false;
        _ctx = HostAccess.CreateContext(new ModInfo("kiln.gamedata", "Kiln game data", "1", AppContext.BaseDirectory), 0, AppContext.BaseDirectory);
        try
        {
            _owner = Backend.Current.OwnerCreate("gamedata-missing");
            if (Content.T != null && _owner != 0)
            {
                using var s = new ContentNative.Utf8("missing gamedata");
                Content.T->LabelOwner(_owner, s.Str);
            }
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"no owner for placeholders: {e.Message}");
        }
        return true;
    }

    private static T AsKiln<T>(Func<T> fn)
    {
        var prev = ModContext.CurrentMod;
        ModContext.CurrentMod = _ctx;
        try
        {
            return fn();
        }
        finally
        {
            ModContext.CurrentMod = prev;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeReady(ulong generation, uint reason, void* user)
    {
        try
        {
            OnReady(reason == 1);
        }
        catch (Exception e)
        {
            try { Log(LogLevel.Error, $"failed: {e}"); } catch { }
        }
    }

    /// <summary>World load: placeholders for recorded clones nobody defines, then the record is brought up to date.</summary>
    internal static void OnReady(bool reload)
    {
        if (Content.GD == null || !HasServerWorld || !Ensure()) return;
        var world = Saves.WorldId;
        var rec = AsKiln(() => WorldData.Get<Record>(Key));
        var defs = Content.GameDataDefinitions();
        if (!reload || world != _world)
        {
            _world = world;
            _placed.Clear();
            foreach (var (g, seen) in rec.Clones)
            {
                if (!Guid.TryParse(g, out var guid)) continue;
                var cur = defs.FirstOrDefault(d => d.Guid == guid);
                if (cur != null && cur.State != "orphaned") continue;  // defined (or already a placeholder)
                Place(guid, seen);
            }
            if (_placed.Count > 0)
                Log(LogLevel.Warn, $"{_placed.Count} perk / buff clone(s) of mods that are not loaded are kept as placeholders (saved references stay): {string.Join(", ", _placed)}");
            else if (rec.Clones.Count > 0)
                Log(LogLevel.Info, $"world {world}: {rec.Clones.Count} perk / buff clone(s) recorded, all defined");
            defs = Content.GameDataDefinitions();
        }
        Update(rec, defs);
    }

    private static void Tick(ulong step)
    {
        if (_handle == 0 && step % 25 == 5) Subscribe();
        if (step % 100 != 7 || _ctx == null || !HasServerWorld) return;
        try
        {
            if (!Content.Ready) return;
            if (Saves.WorldId != _world)
            {
                // a hosted world's registries come up before the game loads it: now its own record is there
                if (Saves.WorldDataReady && Saves.WorldId is not null) OnReady(false);
                return;
            }
            if (_world == null) return;
            var defs = Content.GameDataDefinitions();
            if (Recordable(defs).Count() == _lastCount) return;
            Update(AsKiln(() => WorldData.Get<Record>(Key)), defs);
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"record update failed: {e.Message}");
        }
    }

    private static IEnumerable<GameDataDefinition> Recordable(IReadOnlyList<GameDataDefinition> defs) =>
        defs.Where(d => d.Section is GameDataSection.Perks or GameDataSection.Buffs && d.State == "registered" && !d.IsPlaceholder &&
                        d.Owner != "server sync" && d.Owner != "sync");

    private static void Update(Record rec, IReadOnlyList<GameDataDefinition> defs)
    {
        var now = DateTime.UtcNow;
        var added = 0;
        var list = Recordable(defs).ToList();
        foreach (var d in list)
        {
            var key = d.Guid.ToString("D");
            if (!rec.Clones.TryGetValue(key, out var s))
            {
                rec.Clones[key] = s = new SeenClone { FirstSeen = now };
                added++;
            }
            s.Section = d.Section == GameDataSection.Perks ? "perks" : "buffs";
            s.Id = $"0x{d.Id:x8}";
            s.Name = d.Name;
            s.Base = d.Base;
            s.Mod = d.Mod;
            s.Definition = d.DefinitionJson;
            s.DisplayName = DisplayOf(d.DefinitionJson);
            s.LastSeen = now;
        }
        _lastCount = list.Count;
        if (added > 0) Log(LogLevel.Info, $"world {_world}: {added} new perk / buff clone(s) recorded ({rec.Clones.Count} in all): kept as placeholders if their mod goes away");
    }

    private static string? DisplayOf(string json)
    {
        try
        {
            var o = JsonNode.Parse(json) as JsonObject;
            return (string?)o?["displayName"] ?? (string?)o?["texts"]?["en"]?["name"] ?? (string?)o?["texts"]?["*"]?["name"];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Place(Guid guid, SeenClone seen)
    {
        var mod = seen.Mod ?? "?";
        var label = $"{mod}:{seen.Name}";
        var section = seen.Section == "perks" ? GameDataSection.Perks : GameDataSection.Buffs;
        var ph = new JsonObject
        {
            ["guid"] = guid.ToString("D"),
            ["name"] = seen.Name,
            ["base"] = seen.Base,
            ["displayName"] = $"[Missing: {label}]",
            ["description"] = $"This {(section == GameDataSection.Perks ? "perk" : "effect")} ({seen.DisplayName ?? seen.Name}) comes from the mod \"{mod}\", " +
                              "which is not installed. It does nothing until the mod is back.",
            ["placeholder"] = new JsonObject { ["mod"] = mod, ["name"] = seen.Name, ["displayName"] = seen.DisplayName },
        };
        try
        {
            using var j = new ContentNative.Utf8(ph.ToJsonString());
            var (rc, text) = GameDataNative.Json((b, c, l) => Content.GD->Define(_owner, (uint)section, j.Str, (byte*)b, c, (nuint*)l), 4096);
            if (rc != ContentNative.Ok) throw new InvalidOperationException((string?)JsonNode.Parse(text)?["error"] ?? ContentNative.Message(rc));
            _placed.Add($"{seen.Section} {label} ({seen.Id})");
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"{seen.Section} {label} ({guid}): no placeholder: {e.Message}");
        }
    }
}
