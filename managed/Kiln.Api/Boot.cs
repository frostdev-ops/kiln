// SPDX-License-Identifier: MIT
// Mod.OnBoot's phase (chapter 08): a mod whose kiln.mod.json says "boot": true (or "required") stages plain-data block definitions before
// the game is entered (Kiln.Blocks' BootDefinitions); Kiln prepares them with the mods' files and publishes one generation, so a first start
// already has the content. The native side is kiln.boot (native/core/abi/services/kiln_boot.h); Kiln.Host runs the authors.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>Where a boot author runs, as the boot graph resolved it (a game client authors for every role it may take).</summary>
[Flags]
public enum BootRoles
{
    /// <summary>A dedicated server.</summary>
    DedicatedServer = 1,
    /// <summary>The server world a game client hosts.</summary>
    HostedServer = 2,
    /// <summary>The game client.</summary>
    Client = 4,
}

/// <summary>A mod's part in this process's boot generation (<see cref="Mods.BootState"/>).</summary>
public enum BootModState
{
    /// <summary>The mod takes no part (not a boot mod, or no boot phase in this process).</summary>
    NotParticipating = 0,
    /// <summary>Its OnBoot finished; the generation is not committed yet.</summary>
    Staged = 1,
    /// <summary>Its definitions are in the published generation.</summary>
    Committed = 2,
    /// <summary>Its OnBoot failed: none of its definitions or files are in.</summary>
    ExcludedCallback = 3,
    /// <summary>A mod it requires is out: none of its definitions or files are in.</summary>
    ExcludedDependency = 4,
    /// <summary>A collector refused one of its definitions: none of them are in.</summary>
    RejectedPrepare = 5,
    /// <summary>Nothing was published because of another mod's failure (the start stopped).</summary>
    GenerationAborted = 6,
    /// <summary>Enabled after the boot phase: its boot content needs a restart.</summary>
    MissedPhase = 7,
    /// <summary>Published, then its content faulted while the game bound it.</summary>
    ActivationFault = 8,
}

/// <summary>One record of the game's data as the offline view read it.</summary>
/// <param name="ResourceId">The resource it came from.</param>
/// <param name="TypeHash">The resource type's hash.</param>
/// <param name="Part">The resource part.</param>
/// <param name="Value">The whole record as camelCase JSON (detached: valid after the frame).</param>
public sealed record BootDataValue(Guid ResourceId, uint TypeHash, int Part, JsonElement Value);

/// <summary>What <see cref="IReadOnlyGameData.Read"/> found. Not <see cref="Ok"/>: no such record, a type the view cannot decode, the budget is
/// spent, or the boot-author frame is closed (<c>PhaseClosed</c>).</summary>
/// <param name="Ok">True when <see cref="Data"/> holds the record.</param>
/// <param name="Reason">Why not, in plain words; null when <see cref="Ok"/>.</param>
/// <param name="Data">The record.</param>
public sealed record BootDataResult(bool Ok, string? Reason, BootDataValue? Data) : IKilnResult;

/// <summary>The game's data before the game is entered: a bounded offline view, never the live registries.</summary>
public interface IReadOnlyGameData
{
    /// <summary>One record: <c>item:&lt;debugName&gt;</c>, <c>template:&lt;debugName&gt;</c>, <c>skill:0x&lt;8 hex digits&gt;</c>,
    /// <c>material:&lt;GameMaterials name or CellN&gt;</c>, <c>resource:&lt;GUID&gt;/&lt;8 hex type hash&gt;/&lt;part&gt;</c>.</summary>
    /// <exception cref="ArgumentException">The key is not one of the five forms.</exception>
    /// <exception cref="KilnUnavailableException"><c>mods.boot.game_data</c> is off.</exception>
    [BootThreadOnly, RequiresCapability("mods.boot.game_data")]
    BootDataResult Read(string key);
}

/// <summary>What <see cref="Mod.OnBoot"/> gets: the mod's boot-author frame. Stage definitions with Kiln.Blocks'
/// <c>BootDefinitions.For(boot)</c>; read the game's data with <see cref="GameData"/>.</summary>
public sealed class BootContext
{
    internal BootContext(string modId, ulong generation, BootRoles roles, IReadOnlyGameData gameData)
    {
        ModId = modId;
        Generation = generation;
        Roles = roles;
        GameData = gameData;
    }

    /// <summary>The author's mod id.</summary>
    public string ModId { get; }

    /// <summary>The process's boot generation.</summary>
    public ulong Generation { get; }

    /// <summary>Every role the mod may take in this process.</summary>
    public BootRoles Roles { get; }

    /// <summary>The offline view of the game's data.</summary>
    public IReadOnlyGameData GameData { get; }

    /// <summary>The frame is open: OnBoot has not returned.</summary>
    internal bool Open { get; set; } = true;

    /// <summary><c>mods.boot</c> was on when the frame opened (BootDefinitions refuses without it).</summary>
    internal bool BootOn { get; init; } = true;

    /// <summary>What the author staged, canonical JSON, in order: Kiln.Host lowers it into the generation.</summary>
    internal List<BootStaged> Staged { get; } = [];

    /// <summary>kiln.boot's stage for this frame (null offline): (kind, key, canonical JSON) -> (staged, reason).</summary>
    internal Func<string, string, string, (bool Ok, string? Reason)>? Stage { get; init; }
}

/// <summary>One staged definition (canonical JSON, the block's own camelCase form).</summary>
internal sealed record BootStaged(string Kind, string Key, string Json);

/// <summary>One issue of the boot generation.</summary>
/// <param name="ModId">The mod it is about (empty: none).</param>
/// <param name="Kind">The definition's kind (plural, as in blocks/*.json), or empty.</param>
/// <param name="Key">The definition's key, or empty.</param>
/// <param name="Phase">Where it happened: stage, callback, prepare, commit.</param>
/// <param name="Code">What happened: DuplicateDefinition, CallbackFailed, Dependency, PrepareRefused, ShadowedByFile, ...</param>
/// <param name="Reason">In plain words.</param>
public sealed record BootIssue(string ModId, string Kind, string Key, string Phase, string Code, string Reason);

/// <summary>A mod's boot state, its committed definitions (<c>kind/key</c>) and its issues.</summary>
/// <param name="ModId">The mod.</param>
/// <param name="Generation">The process's boot generation (0: none).</param>
/// <param name="State">Its part in it.</param>
/// <param name="Definitions">What it staged and the generation holds, as <c>kind/key</c>.</param>
/// <param name="Issues">Its issues.</param>
public sealed record BootModReport(string ModId, ulong Generation, BootModState State, IReadOnlyList<string> Definitions, IReadOnlyList<BootIssue> Issues);

/// <summary>The boot-author frame of this thread (null outside OnBoot): Blocks.Define is refused inside it.</summary>
internal static class BootFrame
{
    [ThreadStatic] internal static BootContext? Current;
}

/// <summary>The process's committed boot generation, as Kiln.Host recorded it after the commit; read by Kiln.Blocks when a committed
/// boot mod's runtime instance loads (its runtime parts, the change event).</summary>
internal static class BootGeneration
{
    private static readonly object Gate = new();
    private static Dictionary<string, BootModReport> _reports = new(StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<BootStaged>> _committed = new(StringComparer.Ordinal);

    internal static ulong Number { get; private set; }

    internal static void Record(ulong generation, IEnumerable<BootModReport> reports, IReadOnlyDictionary<string, IReadOnlyList<BootStaged>> committed)
    {
        lock (Gate)
        {
            Number = generation;
            _reports = reports.ToDictionary(r => r.ModId, StringComparer.Ordinal);
            _committed = new Dictionary<string, IReadOnlyList<BootStaged>>(committed, StringComparer.Ordinal);
        }
    }

    internal static void Set(BootModReport report)
    {
        lock (Gate) _reports = new Dictionary<string, BootModReport>(_reports, StringComparer.Ordinal) { [report.ModId] = report };
    }

    internal static BootModReport? Report(string modId)
    {
        lock (Gate) return _reports.GetValueOrDefault(modId);
    }

    internal static IReadOnlyList<BootStaged> Committed(string modId)
    {
        lock (Gate) return _committed.GetValueOrDefault(modId) ?? [];
    }

    internal static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
}

/// <summary>The view that answers while no reader exists (chapter 08 S4): <c>mods.boot.game_data</c> is off, so <see cref="Read"/> names it.</summary>
internal sealed class UnavailableGameData(BootContext? frame = null) : IReadOnlyGameData
{
    internal BootContext? Frame { get; set; } = frame;

    public BootDataResult Read(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Frame is { Open: false }) return new BootDataResult(false, "PhaseClosed: the boot-author frame is closed; the game's data is read inside OnBoot only", null);
        throw new KilnUnavailableException("mods.boot.game_data", "no offline game-data snapshot is there for this game build");
    }
}
