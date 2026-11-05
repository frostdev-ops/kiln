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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonDocumentOptions Doc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("statuses", Make<StatusBlock>), ("effects", Make<EffectBlock>), ("spells", Make<SpellBlock>), ("weapons", Make<WeaponBlock>), .. ItemJson.Kinds,
    ];

    internal static BlockHandle Make<T>(ModBlocks mod, string name, JsonElement e) where T : class, IBlock =>
        Compiler.Define(mod, name, e.Deserialize<T>(Options) ?? throw new JsonException("null"));

    /// <summary>Compiles a mod's blocks/*.json. Returns how many blocks it defined. Any thread that may touch content (the tick thread).</summary>
    public static int LoadMod(ModBlocks mod, string directory)
    {
        var dir = Path.Combine(directory, "blocks");
        if (!Directory.Exists(dir)) return 0;
        var docs = new List<JsonDocument>();
        var entries = new List<(string File, string Section, string Name, Func<BlockHandle> Compile)>();
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
                    if (p.Name == "vfx")
                    {
                        // The game enumerates VFX once, before mod code runs, so kiln.vfx reads this section natively at boot. Nothing here
                        // builds it: without that service these effects don't exist, which deserves a line.
                        if (!BlocksBridge.HasService("kiln.vfx", 1))
                            BlocksBridge.Log(LogLevel.Info, $"blocks: {mod.Id}: {rel}: the \"vfx\" section is not built here: kiln.vfx is not running (a dedicated server has no VFX, or it is turned off)");
                        continue;
                    }
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
        var n = 0;
        foreach (var (file, section, name, compile) in entries)
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
        foreach (var d in docs) d.Dispose();
        return n;
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
    private static IDisposable? _tick;
    private static readonly object Gate = new();

    static BlocksStartup() => Touch();

    public static void Touch()
    {
        lock (Gate) _tick ??= BlocksBridge.AddTick("kiln blocks data", OnTick);
    }

    private static void OnTick(ulong step)
    {
        if (step % 10 != 3) return;
        BlockRuntime.WriteReports();
        BlockRuntime.SubscribeSignals();
        foreach (var m in Mods.All)
        {
            if (!m.IsLoaded || m.Directory.Length == 0 || Done.Contains(m.Id) || !Directory.Exists(Path.Combine(m.Directory, "blocks"))) continue;
            Done.Add(m.Id);
            var mod = Blocks.ModFor(m.Id, () => BlocksBridge.ServiceOwner($"blocks:{m.Id}"));
            var n = BlockJson.LoadMod(mod, m.Directory);
            BlocksBridge.Log(LogLevel.Info, $"blocks: {m.Id}: {n} block(s) from blocks/*.json, {mod.Errors.Count} error(s)");
        }
    }
}
