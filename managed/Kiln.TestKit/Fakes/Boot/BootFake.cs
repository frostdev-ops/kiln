// SPDX-License-Identifier: MIT
// The kit's boot phase (chapter 08, Mod.OnBoot): runs a boot mod's OnBoot as Kiln.Host does at a start, on an instance of its own, with
// the staging of BootDefinitions and the lowering of Kiln.Blocks (BootPhase.LowerStaged), and records the outcome as the process's boot
// generation, so the mod then loads in the harness with its boot content counted as committed. The native kiln.boot is not modelled:
// its table answers KILN_ERR_UNAVAILABLE (no deferred mode), the game's data comes from what the test scripts.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kiln.Host;

namespace Kiln.TestKit;

/// <summary>One harness's boot phase: the game-data records the test scripted and the runs so far.</summary>
internal sealed class BootFakeState
{
    public readonly object Lock = new();
    public readonly Dictionary<string, BootDataValue> Records = new(StringComparer.Ordinal);
    public readonly Dictionary<string, BootModReport> Reports = new(StringComparer.Ordinal);
    public readonly Dictionary<string, IReadOnlyList<BootStaged>> Committed = new(StringComparer.Ordinal);
    public readonly List<BootRun> Runs = [];
}

internal sealed unsafe class BootFake : IKitFake
{
    internal const string Service = "kiln.boot";
    internal static readonly string[] Features = ["mods.boot", "mods.boot.game_data"];
    internal const string PhaseRow = "mods.boot.phase";
    private static KilnBootApi* _table;

    string IKitFake.Service => Service;

    public void Register()
    {
        // a table, so that Kiln.Blocks finds kiln.boot (it reports mods.boot offline without one); every call answers as a process
        // without a boot phase does
        KitBackend.Instance.RegisterTable(Service, Create());
        FakeTables.Features(Features);
        FakeTables.Session(PhaseRow, false, "no boot-author frame is open");
    }

    public void Start(KilnHarness harness) => BootGeneration.Record(0, [], new Dictionary<string, IReadOnlyList<BootStaged>>());

    public void BeforeStep(KilnHarness harness, ulong step) { }

    public void End(KilnHarness harness) => BootGeneration.Record(0, [], new Dictionary<string, IReadOnlyList<BootStaged>>());

    public void AddCoverage(List<CoverageRow> rows) => rows.Add(new CoverageRow("Boot phase (Mod.OnBoot)", CoverageKind.RecordingFake,
        [Service, .. Features, PhaseRow],
        "KilnHarness.BootPhase.Run runs a boot mod's OnBoot as Kiln.Host does at a start: once, on an instance of its own, with BootDefinitions' " +
        "staging (canonical JSON, DuplicateDefinition, NotInBootKinds, PhaseClosed, Blocks.Define refused) and Kiln.Blocks' lowering of what it " +
        "staged (a definition that does not compile, BootReferenceOutside, ShadowedByFile), and records the outcome as the process's boot " +
        "generation: a committed mod then loads in the harness (its definitions' runtime parts, BootDefinitionsCommittedEvent), a failed one is " +
        "refused RestartRequiredBoot. boot.GameData answers from KilnHarness.BootPhase.SetRecord. Not modelled: the native kiln.boot (its table " +
        "answers unavailable), the collectors' prepare and the generation's registrations (the kit's fakes do not get the boot generation's " +
        "items, clones or nodes), several mods' drop and dependency rules, the exit code of a stopped start"));

    public static BootFakeState StateOf(KilnHarness harness) => KitFakes.State<BootFakeState>(harness);

    private static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (KilnBootApi*)NativeMemory.AllocZeroed((nuint)sizeof(KilnBootApi));  // process lifetime, never freed
        t->Size = (uint)sizeof(KilnBootApi);
        t->Version = 1;
        t->Status = &Status;
        t->BeginOwner = &BeginOwner;
        t->Stage = &Stage;
        t->FinishOwner = &FinishOwner;
        t->ReadResource = &ReadResource;
        t->Commit = &Commit;
        t->OwnerState = &OwnerState;
        t->Issues = &Issues;
        t->Collector = &Collector;
        t->ActivePlan = &ActivePlan;
        _table = t;
        return (nint)t;
    }

    private const int Unavailable = 3;  // KILN_ERR_UNAVAILABLE: no deferred mode in this process

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Status(KilnBootCommitResultV1* r) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BeginOwner(ulong o, KilnStr id, uint roles, ulong* gen) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Stage(ulong o, ulong gen, KilnStr kind, KilnStr key, KilnStr json, KilnBootStageResultV1* r) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FinishOwner(ulong o, ulong gen, uint accept) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadResource(ulong o, KilnStr query, byte* json, nuint capacity, nuint* required) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Commit(ulong host, ulong gen, KilnBootCommitResultV1* r) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OwnerState(KilnStr id, KilnBootOwnerStateV1* s) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Issues(ulong gen, KilnBootIssueV1* issues, uint capacity, uint stride, uint* required) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Collector(ulong o, KilnStr id, void* fn, void* user) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* ActivePlan(KilnStr id, ulong* gen) => null;

    /// <summary>OnBoot of the mod in <paramref name="directory"/>, as Kiln.Host's boot phase runs it (BootPhase.cs): the outcome, recorded.</summary>
    internal static BootRun Run(KilnHarness harness, string directory, bool commit)
    {
        var state = StateOf(harness);
        var m = Manifest.Read(directory);
        if (m.Boot == ManifestBoot.None)
            throw new InvalidOperationException($"mod {m.Id} has no \"boot\" in its kiln.mod.json: it has no boot phase (OnBoot is not called)");
        var mods = harness.Manager;
        var data = new KitGameData(state);
        var bootOn = Capabilities.Is("mods.boot");
        var boot = new BootContext(m.Id, 1, mods.BootRoles(m), data) { BootOn = bootOn };
        data.Frame = boot;
        var issues = new List<BootIssue>();
        var lowered = new List<string>();
        BootModState outcome;
        string? reason = null;
        var owner = mods.Host.OwnerCreate("boot-author:" + m.Id);
        FakeTables.Session(PhaseRow, true, "");
        try
        {
            if (!bootOn)
            {
                outcome = BootModState.ExcludedCallback;
                reason = $"mods.boot is off: {Capabilities.Why("mods.boot")}; its OnBoot does not run";
                issues.Add(new BootIssue(m.Id, "", "", "callback", "BootCapOff", reason));
            }
            else if (!mods.RunBootAuthor(m, directory, owner, boot, out var why)
                     || (boot.Staged.Count > 0 && Lower(m, directory, boot, issues, lowered, out why) == false))
            {
                outcome = BootModState.ExcludedCallback;
                reason = why;
                issues.Add(new BootIssue(m.Id, "", "", "callback", "CallbackFailed", why));
            }
            else outcome = commit ? BootModState.Committed : BootModState.Staged;
        }
        finally
        {
            boot.Open = false;
            FakeTables.Session(PhaseRow, false, "no boot-author frame is open");
            mods.Host.OwnerRevoke(owner);
        }
        var staged = boot.Staged.Select(s => new BootRunDefinition(s.Kind, s.Key, s.Json)).ToList();
        var ok = outcome is BootModState.Committed or BootModState.Staged;
        var run = new BootRun(m.Id, outcome, reason, staged, ok ? lowered : [], issues);
        lock (state.Lock)
        {
            state.Runs.Add(run);
            if (commit)
            {
                state.Reports[m.Id] = new BootModReport(m.Id, 1, outcome, run.Definitions, issues);
                if (ok) state.Committed[m.Id] = boot.Staged.Where(s => lowered.Contains($"{s.Kind}/{s.Key}")).ToList();
                else state.Committed.Remove(m.Id);
                BootGeneration.Record(1, state.Reports.Values, state.Committed);
            }
        }
        if (commit && ok) mods.SetBootFingerprint(m.Id, ModManager.BootFingerprint(m, directory));
        return run;
    }

    private static bool Lower(Manifest m, string directory, BootContext boot, List<BootIssue> issues, List<string> lowered, out string why)
    {
        if (BootPhase.LowerStaged(m, directory, boot.Staged, out why) is not { } answer) return false;
        foreach (var issue in answer["issues"]!.AsArray())
            issues.Add(new BootIssue(m.Id, (string)issue!["kind"]!, (string)issue["key"]!, "freeze", (string)issue["code"]!, (string)issue["reason"]!));
        lowered.AddRange(answer["definitions"]!.AsArray().Select(d => (string)d!));
        return true;
    }

    /// <summary>boot.GameData in the kit: the records the test scripted (KilnHarness.BootPhase.SetRecord), else NotFound.</summary>
    private sealed class KitGameData(BootFakeState state) : IReadOnlyGameData
    {
        private static readonly Regex Query = new(@"^(item:.+|template:.+|skill:0x[0-9a-fA-F]{8}|material:.+|resource:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/[0-9a-fA-F]{8}/[0-9]+)$",
            RegexOptions.CultureInvariant);

        public BootContext? Frame { get; set; }

        public BootDataResult Read(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (Frame is { Open: false }) return new BootDataResult(false, "PhaseClosed: the boot-author frame is closed; the game's data is read inside OnBoot only", null);
            if (FakeTables.Off("mods.boot.game_data")) throw new KilnUnavailableException("mods.boot.game_data", "switched off by the test");
            if (!Query.IsMatch(key)) throw new ArgumentException($"\"{key}\" is not a game-data query (item:, template:, skill:0x..., material:, resource:)", nameof(key));
            lock (state.Lock)
                return state.Records.TryGetValue(key, out var v)
                    ? new BootDataResult(true, null, v)
                    : new BootDataResult(false, $"NotFound: no record {key} in the game's data (the test scripted none: KilnHarness.BootPhase.SetRecord)", null);
        }
    }
}
