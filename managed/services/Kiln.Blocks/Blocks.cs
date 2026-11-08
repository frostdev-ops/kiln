// SPDX-License-Identifier: MIT
// The face of Kiln.Blocks: Blocks.Define in C#, blocks/*.json in data, both the same objects. Compiling is in Compiler.cs.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A block is wrong: which block, which field and why. Thrown by <see cref="Blocks.Define{T}"/>; a data file logs it.</summary>
public sealed class BlockError(string block, string? field, string problem)
    : ArgumentException($"block {block}{(string.IsNullOrEmpty(field) ? "" : $", {field}")}: {problem}")
{
    public string Block { get; } = block;
    public string? Field { get; } = field;
    public string Problem { get; } = problem;
}

/// <summary>
/// Building blocks: say what you mean, Kiln makes the game's resources. Call in <c>OnLoad</c>:
/// <code>
/// var chilled = Blocks.Define("chilled", new StatusBlock { Name = "Chilled", Seconds = 8 });
/// var burst = Blocks.Define("frost-burst", new EffectBlock { Damage = Damage.Ice(40), Applies = chilled });
/// Blocks.Define("frostbolt", new SpellBlock
/// {
///     Name = "Frostbolt",
///     Cast = new CastBlock { ManaCost = 30 },
///     Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [burst] } }] },
///     Recipe = new RecipeBlock { Makes = 10, Needs = { [GameItems.Material_T1_Twigs] = 2 } },
/// });
/// Blocks.Define("frost-edge", new WeaponBlock { Name = "Frost Edge", Class = WeaponClass.Sword, OnHit = [burst] });
/// </code>
/// The ids come from your mod id and the block's name: never write a GUID, and keep the names (a rename says <c>Was</c>).
/// docs/kiln/modding/blocks.md.
/// </summary>
public static class Blocks
{
    private static readonly ConcurrentDictionary<string, ModBlocks> Mods_ = new();

    /// <summary>The services blocks compile to are there on this build.</summary>
    public static bool Available => NewContent.Available && Content.GameDataAvailable && Assets.Available;

    /// <summary>Defines a block as the calling mod's. Compiling happens now: what is wrong throws <see cref="BlockError"/> here,
    /// and the mod does not load with a half-defined block.</summary>
    public static BlockHandle<T> Define<T>(string name, T block) where T : class, IBlock
    {
        ArgumentNullException.ThrowIfNull(block);
        var mod = CallerMod();
        return Compiler.Define(mod, name, block);
    }

    /// <summary>Defines a VFX as the calling mod's. The handle goes in a <c>Vfx</c> field, and to <see cref="Vfx"/>'s calls. The game lists VFX once at start, so one defined here is
    /// there from the next start; see <see cref="VfxBlockHandle.Status"/>. A block in a data file's "vfx" section is there at once.</summary>
    public static VfxBlockHandle Define(string name, VfxBlock block) => (VfxBlockHandle)Define<VfxBlock>(name, block);

    /// <summary>Defines a material as the calling mod's. Its <see cref="BlockHandle.Name"/> goes in a recipe's <c>material</c> and in <see cref="ParticleBlock.Material"/>. The game lists every
    /// material's textures once at start, so one defined here is there from the next start; see <see cref="MaterialBlockHandle.Status"/>. A block in a data file's "materials" section is there at once.</summary>
    public static MaterialBlockHandle Define(string name, MaterialBlock block) => (MaterialBlockHandle)Define<MaterialBlock>(name, block);

    /// <summary>Changes a game material in place, as the calling mod's. The game reads its material tables once at start, so a tweak new to this game is there from the next start;
    /// see <see cref="TableBlockHandle{T}.Status"/>. One in a data file's "materialTweaks" section is there at once.</summary>
    public static TableBlockHandle<MaterialTweakBlock> Define(string name, MaterialTweakBlock block) => (TableBlockHandle<MaterialTweakBlock>)Define<MaterialTweakBlock>(name, block);

    /// <summary>Defines a new building material as the calling mod's. The game reads its material tables once at start, so one new to this game is there from the next start;
    /// see <see cref="TableBlockHandle{T}.Status"/>. One in a data file's "buildingMaterials" section is there at once.</summary>
    public static TableBlockHandle<BuildingMaterialBlock> Define(string name, BuildingMaterialBlock block) => (TableBlockHandle<BuildingMaterialBlock>)Define<BuildingMaterialBlock>(name, block);

    /// <summary>Defines a new terrain material as the calling mod's. The game reads its material tables once at start, so one new to this game is there from the next start;
    /// see <see cref="TableBlockHandle{T}.Status"/>. One in a data file's "terrainMaterials" section is there at once.</summary>
    public static TableBlockHandle<TerrainMaterialBlock> Define(string name, TerrainMaterialBlock block) => (TableBlockHandle<TerrainMaterialBlock>)Define<TerrainMaterialBlock>(name, block);

    /// <summary>Defines a loot table as the calling mod's. The handle goes in a creature's <c>Drops</c>, and rolls, drops and gives by itself: <see cref="LootTableHandle.Roll"/>. It asks nothing of players' games.</summary>
    public static LootTableHandle Define(string name, LootTableBlock block) => (LootTableHandle)Define<LootTableBlock>(name, block);
    /// <summary>Defines a quest as the calling mod's: <c>Quests.Define</c> gets what it lowers to. The handle's <see cref="QuestHandle.FullId"/> is what <c>Quests.Start</c> takes. The game builds
    /// its journal at start, so a quest defined here is in it from the next start (a quest in a data file's "quests" section too, unless the mod ships <c>quests/generated/&lt;mod&gt;.json</c>).</summary>
    public static QuestHandle Define(string name, QuestBlock block) => (QuestHandle)Define<QuestBlock>(name, block);

    /// <summary>Defines a dialogue as the calling mod's. The handle goes where a <see cref="DialogueDefinition"/> does (<c>new NpcBlock { Talk = handle }</c>); a block names it by its name (<c>TalkName</c>).</summary>
    public static DialogueHandle Define(string name, DialogueBlock block) => (DialogueHandle)Define<DialogueBlock>(name, block);

    /// <summary>A block a mod defined (its id, or null: the calling mod), or null.</summary>
    public static BlockHandle? Find(string name, string? mod = null)
    {
        var id = mod ?? BlocksBridge.CallerModId;
        return id != null && Mods_.TryGetValue(id, out var m) && m.Handles.TryGetValue(name, out var h) ? h : null;
    }

    /// <summary>What a block became, as JSON: the items, recipes, templates, buffs and programs, with the values given to them.
    /// The same document whichever way the block was written.</summary>
    public static string? Lowered(string name, string? mod = null)
    {
        var id = mod ?? BlocksBridge.CallerModId;
        return id != null && Mods_.TryGetValue(id, out var m) && m.Lowered.TryGetValue(name, out var j) ? j.ToJsonString(new() { WriteIndented = true }) : null;
    }

    /// <summary>The mods with blocks, each with its block names and the errors of its data files.</summary>
    public static IReadOnlyDictionary<string, (IReadOnlyList<string> Blocks, IReadOnlyList<string> Errors)> Report() =>
        Mods_.ToDictionary(m => m.Key, m => ((IReadOnlyList<string>)m.Value.Handles.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(), (IReadOnlyList<string>)m.Value.Errors.ToList()));

    /// <summary>What is not wrong but may not be what was meant: a block renamed without <c>Was</c>.</summary>
    public static IReadOnlyList<string> Warnings(string? mod = null)
    {
        var id = mod ?? BlocksBridge.CallerModId;
        if (id == null || !Mods_.TryGetValue(id, out var m)) return [];
        lock (m.Warnings) return m.Warnings.ToList();
    }

    /// <summary>Writes every program blocks made to <paramref name="directory"/> (<c>&lt;guid&gt;.bin</c> + <c>.txt</c>, for
    /// <c>tools/impact/impact.py dis</c> and <c>lint</c>). Returns how many.</summary>
    public static int DumpPrograms(string directory)
    {
        Directory.CreateDirectory(directory);
        var n = 0;
        foreach (var m in Mods_.Values)
            foreach (var (guid, (key, blob, listing)) in m.Programs)
            {
                File.WriteAllBytes(Path.Combine(directory, $"{guid}.bin"), blob);
                File.WriteAllText(Path.Combine(directory, $"{guid}.txt"), $"{m.Id} {key}\n{listing}");
                n++;
            }
        return n;
    }

    internal static IEnumerable<ModBlocks> AllMods => Mods_.Values;

    internal static ModBlocks ModFor(string id, Func<ulong> owner)
    {
        var m = Mods_.GetOrAdd(id, i => new ModBlocks(i, owner()));
        return m;
    }

    private static ModBlocks CallerMod()
    {
        var id = BlocksBridge.CallerModId ?? throw new InvalidOperationException("Blocks.Define must be called from mod code");
        BlocksStartup.Touch();
        var m = ModFor(id, BlocksBridge.CallerOwner);
        var owner = BlocksBridge.CallerOwner();
        // A reload has a new owner: what the old one had went with it. The mod's OnLoad and its data files make all of it again.
        if (m.Owner != owner)
        {
            if (m.OwnedByMod) m.Reset();
            m.Owner = owner;
        }
        m.OwnedByMod = true;
        BlocksStartup.ReadData(m);
        return m;
    }
}

/// <summary>What one mod defined.</summary>
internal sealed class ModBlocks(string id, ulong owner)
{
    public string Id { get; } = id;
    public ulong Owner { get; set; } = owner;
    public readonly ConcurrentDictionary<string, BlockHandle> Handles = new();
    public readonly ConcurrentDictionary<string, JsonObject> Lowered = new();
    public readonly ConcurrentDictionary<Guid, (string Key, byte[] Blob, string Listing)> Programs = new();
    public readonly List<string> Errors = new();
    public readonly List<string> Warnings = new();
    /// <summary>The text of VFX blocks read from data files, by name, until each is compiled: what the boot build read.</summary>
    public readonly Dictionary<string, string> VfxRaw = new();
    /// <summary>Templates carrying a VFX, defined once for the mod however many blocks play it.</summary>
    public readonly HashSet<Guid> Carriers = new();
    /// <summary>Game VFX named by a block, filled in when content is ready.</summary>
    public readonly List<VfxLate> VfxLates = new();
    public bool VfxPreloaded;
    /// <summary>Blocks of this mod's data files not compiled yet: a reference by name compiles them first.</summary>
    public readonly Dictionary<string, Func<BlockHandle>> Pending = new();
    /// <summary>The data files, read and not compiled yet; <see cref="DataLoaded"/>: compiled.</summary>
    public BlockJson.DataFiles? Data;
    public bool DataLoaded;
    public readonly HashSet<string> Compiling = new();
    public string? ManifestClients;
    /// <summary>The owner is the mod's own, not one of Kiln's for a mod with only data files.</summary>
    public bool OwnedByMod;
    /// <summary>A rule was added since they were last applied: they are applied again though content did not change.</summary>
    public volatile bool RulesDirty;

    /// <summary>Forgets what the mod made: it is reloading, and makes it again.</summary>
    public void Reset()
    {
        Handles.Clear();
        Lowered.Clear();
        Programs.Clear();
        LateBuilders.Clear();
        lock (Errors) Errors.Clear();
        lock (Warnings) Warnings.Clear();
        ReadyRules.Clear();
        LatePending.Clear();
        LateFailed.Clear();
        LateRules.Clear();
        Icons.Clear();
        OwnIcons.Clear();
        ReadyChecks.Clear();
        PlayerTriggers.Forget(Id);
        LootRuntime.Forget(Id);
        VfxRaw.Clear();
        VfxLates.Clear();
        Carriers.Clear();
        VfxPreloaded = false;
        Data?.Dispose();
        Data = null;
        DataLoaded = false;
        Pending.Clear();
        ReportDirty = true;
    }

    /// <summary>Something changed since the report for <c>kiln blocks</c> was written.</summary>
    public volatile bool ReportDirty;
    /// <summary>Programs that wait for values only the running game knows, by GUID.</summary>
    public readonly ConcurrentDictionary<Guid, ProgramBuilder> LateBuilders = new();
    public readonly List<Guid> LatePending = new();
    public readonly HashSet<Guid> LateFailed = new();
    public readonly List<(string Section, string Entry, string Path, Guid Program)> LateRules = new();
    public readonly List<(string Section, string Entry, GameDataRegistry Registry, string Source, string Block)> Icons = new();
    /// <summary>PNG icons of the mod's own statuses and perks.</summary>
    public readonly List<OwnIcon> OwnIcons = new();
    public bool ReadyHooked;
    public readonly List<Action<Action<string>>> ReadyChecks = new();
    public readonly List<(string Section, string Entry, JsonObject Rules)> ReadyRules = new();
}
