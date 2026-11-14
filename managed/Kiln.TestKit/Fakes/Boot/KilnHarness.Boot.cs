// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessBootPhase? _bootPhase;

    /// <summary>The boot phase of this harness (Mod.OnBoot): run a boot mod's OnBoot as a start does, read what it staged and why it
    /// failed, and script the game's data it reads. Since 0.2.0-dev.28.</summary>
    public HarnessBootPhase BootPhase => _bootPhase ??= new HarnessBootPhase(this);
}

/// <summary>
/// The boot phase of a harness (<see cref="KilnHarness.BootPhase"/>): a unit test of a mod's <c>OnBoot</c>. Since 0.2.0-dev.28.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> <see cref="Run"/> runs the mod's <c>OnBoot</c> once, on an instance of its own in a load context of its own, as
/// Kiln.Host does before the game is entered: the frame's <c>BootDefinitions</c> stage canonical JSON with the same refusals
/// (DuplicateDefinition, NotInBootKinds, PhaseClosed; <c>Blocks.Define</c> throws inside the frame), and what it staged lowers through
/// Kiln.Blocks as at a start (a definition that does not compile, or names another mod's block or a block of the mod's data files
/// (BootReferenceOutside), fails the run; a data file's block of the same name wins, ShadowedByFile). The outcome is the process's boot
/// generation (<c>Mods.BootState</c>): a committed mod then loads with <see cref="KilnHarness.LoadFolder"/> (its definitions' runtime parts,
/// <c>BootDefinitionsCommittedEvent</c>), a failed one is refused <c>RestartRequiredBoot</c>, as in a game. <c>boot.GameData</c> answers
/// from <see cref="SetRecord"/>; the row <c>mods.boot.game_data</c> switched off makes it throw, <c>mods.boot</c> switched off refuses the
/// mod before its OnBoot.</para>
/// <para><b>Not modelled</b> (offline): the native kiln.boot and the collectors' prepare, so the boot generation's registrations (items,
/// clones, game-tree nodes) do not reach the kit's fakes; the drop of several mods and their dependents; the stopped start (exit code
/// 0x4B01) of a mod whose kiln.mod.json says "required".</para>
/// </remarks>
public sealed class HarnessBootPhase
{
    private readonly KilnHarness _harness;

    internal HarnessBootPhase(KilnHarness harness) => _harness = harness;

    /// <summary>Runs the OnBoot of the mod in <paramref name="folder"/> (a built mod folder with a kiln.mod.json that has <c>"boot"</c>)
    /// and, with <paramref name="commit"/>, makes its outcome the process's boot generation, so that <see cref="KilnHarness.LoadFolder"/>
    /// of the same folder loads it (or refuses it) as a start would. Call it before loading the mod (and, for a mod loaded at the start, before <see cref="KilnHarness.Boot"/>).</summary>
    /// <returns>What it staged, what lowered, and why it failed.</returns>
    /// <exception cref="InvalidOperationException">The manifest has no <c>"boot"</c>.</exception>
    public BootRun Run(string folder, bool commit = true)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return BootFake.Run(_harness, Path.GetFullPath(folder), commit);
    }

    /// <summary>A record <c>boot.GameData.Read(<paramref name="key"/>)</c> answers: its value as camelCase JSON.</summary>
    public void SetRecord(string key, string valueJson, Guid resourceId = default, uint typeHash = 0, int part = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(valueJson);
        using var doc = JsonDocument.Parse(valueJson);
        var s = BootFake.StateOf(_harness);
        lock (s.Lock) s.Records[key] = new BootDataValue(resourceId, typeHash, part, doc.RootElement.Clone());
    }

    /// <summary>Every run of this harness, in order.</summary>
    public IReadOnlyList<BootRun> Runs
    {
        get
        {
            var s = BootFake.StateOf(_harness);
            lock (s.Lock) return [.. s.Runs];
        }
    }
}

/// <summary>One OnBoot run of the kit (<see cref="HarnessBootPhase.Run"/>).</summary>
/// <param name="ModId">The mod.</param>
/// <param name="State">Its outcome: <c>Committed</c> (or <c>Staged</c> without commit), else <c>ExcludedCallback</c>.</param>
/// <param name="Reason">Why it failed (its OnBoot threw, a definition does not lower, mods.boot is off); null when it did not.</param>
/// <param name="Staged">What its OnBoot staged, in order: kind, key and the canonical JSON.</param>
/// <param name="Definitions">What lowered into the generation, as <c>kind/key</c> (a data file's block of the same name is left out).</param>
/// <param name="Issues">Its issues: CallbackFailed, BootCapOff, ShadowedByFile, ...</param>
public sealed record BootRun(string ModId, BootModState State, string? Reason, IReadOnlyList<BootRunDefinition> Staged, IReadOnlyList<string> Definitions,
    IReadOnlyList<BootIssue> Issues)
{
    /// <summary>True when the mod's boot content is in (Committed or Staged).</summary>
    public bool Ok => State is BootModState.Committed or BootModState.Staged;
}

/// <summary>One definition an OnBoot staged.</summary>
/// <param name="Kind">The plural kind, as in blocks/*.json (<c>statuses</c>, <c>items</c>, ...).</param>
/// <param name="Key">Its name.</param>
/// <param name="Json">The canonical JSON Kiln lowers.</param>
public sealed record BootRunDefinition(string Kind, string Key, string Json);
