// SPDX-License-Identifier: MIT
// blocks/*.json: the same blocks, the same shape. A file has one object per kind, block name -> block; a field that takes a block takes
// it inline or by name. System.Text.Json fills the same records C# writes, so every block type works here with no loader code of its
// own. Errors name the file, the block and the field; the file's other blocks still load.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

internal static class BlockJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new Vec3Converter() },
    };

    private static readonly JsonDocumentOptions Doc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("statuses", Make<StatusBlock>), ("effects", Make<EffectBlock>), ("spells", Make<SpellBlock>), ("weapons", Make<WeaponBlock>), ("vfx", MakeVfx), ("materials", MakeMaterial), ("screenEffects", Make<ScreenEffectBlock>), ("sounds", Make<SoundBlock>), .. WorldJson.Kinds, .. ItemJson.Kinds, .. AbilityJson.Kinds, .. FurnitureJson.Kinds, .. NpcJson.Kinds,
    ];

    /// <summary>One block as blocks/*.json writes it (tests).</summary>
    internal static T Parse<T>(string json) where T : class, IBlock => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("null");

    internal static BlockHandle Make<T>(ModBlocks mod, string name, JsonElement e) where T : class, IBlock =>
        Compiler.Define(mod, name, e.Deserialize<T>(Options) ?? throw new JsonException("null"));

    // kiln.vfx read this section at boot, as the game lists VFX once before mod code runs. Here it becomes a block for others to name, and the builder is told the same text.
    private static BlockHandle MakeVfx(ModBlocks mod, string name, JsonElement e)
    {
        mod.VfxRaw[name] = e.GetRawText();
        return Make<VfxBlock>(mod, name, e);
    }

    // The same for a material: kiln.vfx read the section at boot, as the game lists every material's textures once before mod code runs.
    private static BlockHandle MakeMaterial(ModBlocks mod, string name, JsonElement e)
    {
        mod.VfxRaw[name] = e.GetRawText();
        return Make<MaterialBlock>(mod, name, e);
    }

    /// <summary>Reads a mod's blocks/*.json and compiles nothing yet: each block waits, and a reference by name compiles it. So the mod's code can name its data files'
    /// blocks from its first <see cref="Blocks.Define{T}"/> on, and a data block can name a block its code defined before.</summary>
    public static void Read(ModBlocks mod, string directory)
    {
        if (mod.Data != null) return;
        var data = mod.Data = new DataFiles();
        var dir = Path.Combine(directory, "blocks");
        if (!Directory.Exists(dir)) return;
        var docs = data.Docs;
        var entries = data.Entries;
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            var rel = $"blocks/{Path.GetFileName(file)}";
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(file), Doc);
                docs.Add(doc);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name is "format" or "comment" or "$schema") continue;
                    var kind = Kinds.FirstOrDefault(k => k.Section == p.Name);
                    if (kind.Make == null)
                    {
                        Fail(mod, $"{rel}: \"{p.Name}\" is not a kind of block; there are {string.Join(", ", Kinds.Select(k => k.Section))}");
                        continue;
                    }
                    foreach (var b in p.Value.EnumerateObject())
                    {
                        var (name, value, make) = (b.Name, b.Value, kind.Make);
                        BlockHandle? done = null;
                        Func<BlockHandle> compile = () => done ??= make(mod, name, value);
                        mod.Pending[name] = compile;
                        entries.Add((rel, p.Name, name, compile));
                    }
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                Fail(mod, $"{rel}: {e.Message}");
            }
        }
    }

    /// <summary>Compiles a mod's blocks/*.json: what <see cref="Read"/> read, or reads it now. Returns how many blocks it defined. Any thread that may touch content (the tick thread).</summary>
    public static int LoadMod(ModBlocks mod, string directory)
    {
        if (mod.DataLoaded) return 0;
        Read(mod, directory);
        using var data = mod.Data!;
        mod.Data = null;
        mod.DataLoaded = true;
        var n = 0;
        foreach (var (file, section, name, compile) in data.Entries)
        {
            try
            {
                mod.Compiling.Add(name);
                compile();
                n++;
            }
            catch (JsonException e)
            {
                Fail(mod, $"{file}: {section}.{name}{(e.Path is { Length: > 1 } p ? p[1..] : "")}: {e.Message}");
            }
            catch (BlockError e)
            {
                Fail(mod, $"{file}: {e.Message}");
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
            {
                Fail(mod, $"{file}: {section}.{name}: {e.Message}");
            }
            finally
            {
                mod.Compiling.Remove(name);
                mod.Pending.Remove(name);
            }
        }
        return n;
    }

    /// <summary>Drop cached data of the old lifetime. A revoked mod owner must never be reused by the next data pass.
    /// A data-only mod keeps its service owner, so repeated reloads don't allocate owners.</summary>
    internal static void Unloaded(ModBlocks mod)
    {
        mod.Reset();
        if (mod.OwnedByMod) mod.Owner = 0;
        mod.OwnedByMod = false;
    }

    /// <summary>A mod's data files, read: what their blocks are, until each is compiled.</summary>
    internal sealed class DataFiles : IDisposable
    {
        public readonly List<JsonDocument> Docs = new();
        public readonly List<(string File, string Section, string Name, Func<BlockHandle> Compile)> Entries = new();

        public void Dispose()
        {
            foreach (var d in Docs) d.Dispose();
            Docs.Clear();
        }
    }

    private static void Fail(ModBlocks mod, string message)
    {
        lock (mod.Errors) mod.Errors.Add(message);
        mod.ReportDirty = true;
        BlocksBridge.Log(LogLevel.Error, $"blocks: {mod.Id}: {message}");
    }
}

/// <summary>Started by Kiln.Content when a loaded mod has a blocks/ folder, or by the first <see cref="Blocks.Define{T}"/>:
/// compiles the data files of mods as they load, on the tick thread.</summary>
internal static class BlocksStartup
{
    private static readonly HashSet<string> Done = new();
    private static IDisposable? _tick, _reloads;
    private static readonly object Gate = new();

    static BlocksStartup() => Touch();

    public static void Touch()
    {
        lock (Gate) _tick ??= BlocksBridge.AddTick("kiln blocks data", OnTick);
    }

    /// <summary>The mod's code is defining blocks: its data files' names are there for it to use. They compile when named, the rest at the next pass.</summary>
    public static void ReadData(ModBlocks mod)
    {
        if (mod.DataLoaded || mod.Data != null) return;
        var dir = BlocksBridge.ModDirectory(mod.Id);
        if (dir.Length > 0) BlockJson.Read(mod, dir);
    }

    private static void OnTick(ulong step)
    {
        if (step % 10 != 3) return;
        // from the service's own tick, so that the subscription is the service's: a mod's would end with the mod
        _reloads ??= Events.On<ModReloadedEvent>(e =>
        {
            void Reloaded(string id)
            {
                Done.Remove(id);
                var mod = Blocks.AllMods.FirstOrDefault(m => m.Id == id);
                // A new OnLoad that defined blocks already reset/read its data (DataLoaded is still false).
                // Otherwise this is the previous lifetime, even if unload and reload fell between data passes.
                if (mod != null && (!mod.OwnedByMod || mod.DataLoaded))
                {
                    Reskins.Unloaded(id);
                    BlockJson.Unloaded(mod);
                }
            }
            Reloaded(e.Id);
            foreach (var dependent in e.Cascade) Reloaded(dependent);
        });
        BlockRuntime.WriteReports();
        BlockRuntime.SubscribeSignals();
        AbilityRuntime.Subscribe();
        PlayerTriggers.Subscribe();
        // An unloaded mod's data files compile again when it is loaded again (a reload clears it through ModReloadedEvent), and its material
        // replacements go (Vfx/Reskins.cs).
        foreach (var m in Mods.All)
            if (!m.IsLoaded && m.State != ModState.Pending)
            {
                Done.Remove(m.Id);
                Reskins.Unloaded(m.Id);
                var old = Blocks.AllMods.FirstOrDefault(b => b.Id == m.Id);
                if (old != null && (old.DataLoaded || old.Data != null || old.OwnedByMod)) BlockJson.Unloaded(old);
            }
        foreach (var m in Mods.All)
        {
            if (!m.IsLoaded || m.Directory.Length == 0 || Done.Contains(m.Id) || !Directory.Exists(Path.Combine(m.Directory, "blocks"))) continue;
            Done.Add(m.Id);
            var mod = Blocks.ModFor(m.Id, () => BlocksBridge.ServiceOwner($"blocks:{m.Id}"));
            if (mod.Owner == 0) mod.Owner = BlocksBridge.ServiceOwner($"blocks:{m.Id}");
            var n = BlockJson.LoadMod(mod, m.Directory);
            BlocksBridge.Log(LogLevel.Info, $"blocks: {m.Id}: {n} block(s) from blocks/*.json, {mod.Errors.Count} error(s)");
        }
    }
}
