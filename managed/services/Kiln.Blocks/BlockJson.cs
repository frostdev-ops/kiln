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

    // The limits of a mod's own blocks/ folder, as in native/services/vfx/generated.h.
    internal const int MaxEntries = 4096, MaxFiles = 256, MaxFileBytes = 16 << 20;
    internal const long MaxTotalBytes = 64L << 20;

    private static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("statuses", Make<StatusBlock>), ("effects", Make<EffectBlock>), ("spells", Make<SpellBlock>), ("weapons", Make<WeaponBlock>), ("vfx", MakeVfx), ("materials", MakeMaterial), ("screenEffects", Make<ScreenEffectBlock>), ("sounds", Make<SoundBlock>), .. WorldJson.Kinds, .. ItemJson.Kinds, .. AbilityJson.Kinds, .. FurnitureJson.Kinds, .. NpcJson.Kinds, .. QuestBlockJson.Kinds, .. UiJson.Kinds,
        ("affixes", Make<AffixBlock>), ("affixPools", Make<AffixPoolBlock>),
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
        // A mod's folder is input from the internet. The bounds are the ones kiln.vfx's boot reader has for the same
        // files (native/services/vfx/generated.h), so both sides refuse the same folders: the listing stops at
        // MaxEntries (never the whole listing of a folder with millions of names), at most MaxFiles JSON files of
        // MaxFileBytes each and MaxTotalBytes in all, and no file that is reached through a link or lies outside the
        // mod's folder (BakedFiles.Read). Nesting is bounded by the JSON reader itself (64).
        var names = new List<string>();
        try
        {
            var seen = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                if (++seen > MaxEntries)
                {
                    Fail(mod, $"blocks/ holds more than {MaxEntries} entries: none of its block files are read");
                    return;
                }
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileName(path));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(mod, $"blocks/ could not be listed: {e.Message}");
            return;
        }
        if (names.Count > MaxFiles)
        {
            Fail(mod, $"blocks/ holds {names.Count} JSON files, more than {MaxFiles}: none are read");
            return;
        }
        long total = 0;
        foreach (var fileName in names.OrderBy(x => x, StringComparer.Ordinal))
        {
            var rel = $"blocks/{fileName}";
            try
            {
                var text = BakedFiles.Read(directory, rel, MaxFileBytes);
                if (text is null)
                {
                    Fail(mod, $"{rel} is not read: it is larger than {MaxFileBytes >> 20} MiB, reached through a link, or could not be opened");
                    continue;
                }
                total += text.Length;
                if (total > MaxTotalBytes)
                {
                    Fail(mod, $"{rel} and the files after it are not read: blocks/ holds more than {MaxTotalBytes >> 20} MiB of block files");
                    break;
                }
                var doc = JsonDocument.Parse(text, Doc);
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
                    // a section is an object of block name -> block; anything else costs that section, not the file's others or the later files
                    if (p.Value.ValueKind != JsonValueKind.Object)
                    {
                        var shape = p.Value.ValueKind switch { JsonValueKind.Array => "a list", JsonValueKind.String => "a string", JsonValueKind.Number => "a number", _ => p.Value.GetRawText() };
                        Fail(mod, $"{rel}: \"{p.Name}\" is {shape}, not an object of block name -> block: none of its blocks are read");
                        continue;
                    }
                    foreach (var b in p.Value.EnumerateObject())
                    {
                        var (name, value, make, section) = (b.Name, b.Value, kind.Make, p.Name);
                        BlockHandle? done = null;
                        Func<BlockHandle> compile = () => done ??= CompileOne(mod, section, name, value, make);
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

    /// <summary>How many blocks <see cref="Read"/> would make an entry for in a mod's blocks/*.json, for progress.json's count only: the same
    /// files, order, bounds and sections (<see cref="Kinds"/>), no side effect, no log line. A file it cannot read or that has another
    /// shape counts 0; any failure of the whole, out of memory included, is the caller's to catch (BlocksStartup: no total).</summary>
    internal static ulong CountEntries(string directory)
    {
        var dir = Path.Combine(directory, "blocks");
        if (!Directory.Exists(dir)) return 0;
        var names = new List<string>();
        var seen = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            if (++seen > MaxEntries) return 0;
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileName(path));
        }
        if (names.Count > MaxFiles) return 0;
        ulong n = 0;
        long total = 0;
        foreach (var fileName in names.OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                var text = BakedFiles.Read(directory, $"blocks/{fileName}", MaxFileBytes);
                if (text is null) continue;
                if ((total += text.Length) > MaxTotalBytes) break;
                using var doc = JsonDocument.Parse(text, Doc);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && Kinds.Any(k => k.Section == p.Name))
                        foreach (var _ in p.Value.EnumerateObject()) n++;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return n;
    }

    /// <summary>Compiles a mod's blocks/*.json: what <see cref="Read"/> read, or reads it now. Returns how many blocks it defined. Any thread that may touch content (the tick thread).</summary>
    public static int LoadMod(ModBlocks mod, string directory, Action<string, string>? each = null)
    {
        if (mod.DataLoaded) return 0;
        Read(mod, directory);
        using var data = mod.Data!;
        mod.Data = null;
        mod.DataLoaded = true;
        var n = 0;
        foreach (var (file, section, name, compile) in data.Entries)
        {
            each?.Invoke(section, name);
            try
            {
                mod.Compiling.Add(name);
                Compiler.AffixDataPass(mod, name, compile(), file);
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
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // anything else of this one block is its error too: the mod's other blocks and files still load
                Fail(mod, $"{file}: {section}.{name}: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                mod.Compiling.Remove(name);
                mod.Pending.Remove(name);
            }
        }
        return n;
    }

    /// <summary>One block's compile, also when code names it first. The compilers say what is wrong with BlockError (and
    /// the exceptions LoadMod names); an exception of a kind nobody foresaw (a NullReferenceException of a field left
    /// null) becomes that block's BlockError, never the end of the mod's other blocks and files.</summary>
    private static BlockHandle CompileOne(ModBlocks mod, string section, string name, JsonElement value, Func<ModBlocks, string, JsonElement, BlockHandle> make)
    {
        try
        {
            return make(mod, name, value);
        }
        catch (Exception e) when (e is not (JsonException or ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException or OutOfMemoryException))
        {
            throw new BlockError($"{section}.{name}", null, $"could not be compiled: {e.GetType().Name}: {e.Message}");
        }
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
        CompileLoaded();
    }

    private static bool Due(ModEntry m) =>
        m.IsLoaded && m.Directory.Length > 0 && !Done.Contains(m.Id) && Directory.Exists(Path.Combine(m.Directory, "blocks"));

    // progress.json's step "content" for the start: the blocks of every mod due at the first pass, counted once (best effort), and
    // carried over the passes until every one of those mods was attempted (a mod whose files throw stops a pass, and the mods after
    // it compile at the next one). Then "game" begins. Tick thread only.
    private static bool _counted, _finished;
    private static ulong _done, _total;  // _total 0: no count (unknown)

    /// <summary>The count of one mod's data blocks; a test replaces it.</summary>
    internal static Func<ModEntry, ulong> Counter = CountFor;

    private static ulong CountFor(ModEntry m)
    {
        // read already (its code named its data files): the cached entries give it for nothing
        var cached = Blocks.AllMods.FirstOrDefault(b => b.Id == m.Id);
        if (cached is { DataLoaded: false, Data: { } data }) return (ulong)data.Entries.Count;
        return BlockJson.CountEntries(m.Directory);
    }

    /// <summary>Compiles the data files of every loaded mod not done yet, one mod after the other: marked done, then compiled (a mod whose
    /// files throw stops the pass there, as it always did). Reporting can never come before or prevent a compile: the count is best
    /// effort under every failure (then the step has no total).</summary>
    internal static void CompileLoaded()
    {
        if (!Mods.All.Any(Due)) return;
        if (!_counted)
        {
            _counted = true;
            try
            {
                foreach (var m in Mods.All.Where(Due).ToList()) _total += Counter(m);
            }
            catch
            {
                _total = 0;  // out of memory or anything else: no total, and the compile goes on
            }
        }
        try
        {
            foreach (var m in Mods.All)
            {
                if (!Due(m)) continue;
                Done.Add(m.Id);
                var mod = Blocks.ModFor(m.Id, () => BlocksBridge.ServiceOwner($"blocks:{m.Id}"));
                if (mod.Owner == 0) mod.Owner = BlocksBridge.ServiceOwner($"blocks:{m.Id}");
                var n = BlockJson.LoadMod(mod, m.Directory, (section, name) => Report($"{m.Id}: {section} {name}"));
                BlocksBridge.Log(LogLevel.Info, $"blocks: {m.Id}: {n} block(s) from blocks/*.json, {mod.Errors.Count} error(s)");
            }
        }
        finally
        {
            // every mod of the start attempted (none due any more): the content step is over, "game" begins
            if (!_finished && !Mods.All.Any(Due))
            {
                _finished = true;
                if (StartProgress.Step("content", "", "", _done, _total == 0 ? 0 : Math.Max(_total, _done)) == StartProgress.Ok)
                    StartProgress.Step("game", StartProgress.GameLabel(BlocksBridge.IsClient));
            }
        }
    }

    private static void Report(string detail)
    {
        if (_finished) return;
        try
        {
            if (_total == 0)
            {
                _done++;
                StartProgress.Step("content", "", detail);
                return;
            }
            _total = Math.Max(_total, _done + 1);  // a count that came out short never runs backwards
            StartProgress.Step("content", "", detail, _done++, _total);
        }
        catch
        {
        }
    }

    /// <summary>A test starts the process's start again.</summary>
    internal static void ResetStartForTests() => (_counted, _finished, _done, _total) = (false, false, 0, 0);
}
