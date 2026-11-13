// SPDX-License-Identifier: MIT
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;

// the registry and the rule's deadlines are process-wide: one test at a time
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kiln.CheckRunnerTests;

internal sealed class FakeLog : ILogTail
{
    public readonly List<string> Lines = new();
    public long Mark() => Lines.Count;
    public IReadOnlyList<string> Since(long mark, int maxBytes) => Lines.Skip((int)mark).ToList();
}

internal sealed class FakeEnv : IRunnerEnv
{
    public const string Sha = "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";
    public const string KilnVersion = "0.2.0-dev.25";

    public GameInfo Game { get; set; } = new("client", Sha, true, KilnVersion);
    public bool IsClient { get; set; } = true;
    public bool Hosting { get; set; } = true;
    public EntityId LocalPlayer => new(7);
    public IAchievementGuard? Guard { get; set; } = new FakeGuard();
    public FakeLog LogTail { get; } = new();
    public ILogTail Log => LogTail;
    public DateTime Now { get; set; } = new(2026, 11, 12, 1, 2, 3, DateTimeKind.Utc);
    public string? Character { get; set; } = "KilnCheck runner";
    public bool CharacterThrows { get; set; }
    public uint WorldSession { get; set; } = 1;
    public IReadOnlyList<string> PlayerNames { get; set; } = [];

    public string? CharacterName(EntityId player, out string why)
    {
        if (CharacterThrows) throw new InvalidOperationException("no players service");
        why = "the test says it cannot be read";
        return Character;
    }

    public List<string> Written { get; } = new();
    public void Write(LogLevel level, string text) => Written.Add($"[{level}] {text}");
}

/// <summary>An achievement guard whose every answer the test sets.</summary>
internal sealed class FakeGuard : IAchievementGuard
{
    /// <summary>What a read gives before the latch (after it, Active is on unless <see cref="StaysOff"/>).</summary>
    public GuardStatus? Value = new(true, true, true, false, 0, 0, "ready");
    public bool LatchReturns = true;
    public bool LatchThrows;
    public bool StatusThrows;
    public TimeSpan StatusDelay;
    public bool NullAfterLatch;
    public bool StaysOff;
    public bool Latched;
    public int Latches, Holds, Releases;

    public GuardStatus? Status()
    {
        if (StatusThrows) throw new InvalidOperationException("status read failed");
        if (StatusDelay > TimeSpan.Zero) Thread.Sleep(StatusDelay);
        if (Latched && NullAfterLatch) return null;
        if (Value is { } v && (Latched || Holds > Releases) && !StaysOff) return v with { Active = true };
        return Value;
    }

    public bool Latch(string reason)
    {
        Latches++;
        if (LatchThrows) throw new InvalidOperationException("latch failed");
        Latched = LatchReturns;
        return LatchReturns;
    }

    public bool TryHold(string reason, out IDisposable? hold)
    {
        Holds++;
        hold = new Release(this);
        return true;
    }

    private sealed class Release(FakeGuard g) : IDisposable
    {
        public void Dispose() => g.Releases++;
    }
}

/// <summary>The checks of the tests (the whole test assembly is the "mod").</summary>
public sealed class CheckMod : Mod
{
    public static int CheatsRan, ProofRan, CleanupRan, TwoSecond;
    public static Action? DuringBody;

    public static void Reset()
    {
        CheatsRan = ProofRan = CleanupRan = TwoSecond = 0;
        DuringBody = null;
    }

    [ClientCheck("t.pass", "Passes on its assertions", Area = "Basics", Since = "0.2.0-test", Order = 1)]
    public async Task Pass(CheckContext c)
    {
        c.Instruct("Look at it.");
        c.Expect(true, "one is one");
        DuringBody?.Invoke();
        await c.Verdict();
    }

    [ClientCheck("t.two", "Two instructions", Area = "Basics", Order = 2)]
    public async Task Two(CheckContext c)
    {
        c.Instruct("first");
        await c.Ticks(3);
        TwoSecond++;
        c.Instruct("second");
        await c.Verdict();
    }

    [ClientCheck("t.noask", "Returns without asking", Area = "Basics", Order = 3)]
    public Task NoAsk(CheckContext c)
    {
        c.Expect(false, "two hits", "seen 1");
        return Task.CompletedTask;
    }

    [ClientCheck("t.throws", "Throws", Area = "Basics", Order = 4)]
    public async Task Throws(CheckContext c)
    {
        await c.Ticks(1);
        throw new InvalidOperationException("boom at 10.1.2.3");
    }

    [ClientCheck("t.cleanup", "Clean-up", Area = "Basics", Order = 5)]
    public async Task Cleanup(CheckContext c)
    {
        c.OnEnd(() => CleanupRan += 1);
        c.OnEnd(async () =>
        {
            await c.Mod.Scheduler.NextTick();
            CleanupRan += 10;
        });
        c.Instruct("x");
        await c.Verdict();
    }

    [ClientCheck("t.proof", "Guard proof", Area = "Cheats", Tags = [ClientCheckTags.Cheats, ClientCheckTags.GuardProof],
        Confirm = "This writes an achievement. Run it only if your Steam account already has it.", Order = 9)]
    public async Task Proof(CheckContext c)
    {
        ProofRan++;
        DuringBody?.Invoke();
        c.Instruct("Level up once.");
        await c.Verdict();
    }

    [ClientCheck("t.proof-unsafe", "Guard proof without cheats", Tags = [ClientCheckTags.GuardProof], Confirm = "Sure?")]
    public async Task UnsafeProof(CheckContext c)
    {
        ProofRan++;
        await c.Verdict();
    }

    [ClientCheck("t.cheats", "Cheats", Area = "Cheats", Tags = [ClientCheckTags.Cheats], Order = 10)]
    public async Task Cheats(CheckContext c)
    {
        CheatsRan++;
        DuringBody?.Invoke();
        c.Instruct("Level up.");
        await c.Verdict();
    }

    [ClientCheck("t.proof-bare", "Guard proof without Confirm", Area = "Cheats", Tags = [ClientCheckTags.Cheats, ClientCheckTags.GuardProof], Order = 11)]
    public async Task ProofBare(CheckContext c)
    {
        ProofRan++;
        await c.Verdict();
    }

    [ClientCheck("t.cheats-two", "Cheats with two players", Area = "Cheats", Tags = [ClientCheckTags.Cheats, ClientCheckTags.TwoPlayers], Order = 12)]
    public async Task CheatsTwo(CheckContext c)
    {
        CheatsRan++;
        await c.Verdict();
    }

    [ClientCheck("t.hosted", "Needs hosted", Needs = ClientCheckNeeds.Hosted)]
    public async Task Hosted(CheckContext c) => await c.Verdict();

    [ClientCheck("t.joined", "Needs joined", Needs = ClientCheckNeeds.Joined)]
    public async Task Joined(CheckContext c) => await c.Verdict();

    [ClientCheck("t.confirm", "Asks first", Confirm = "Sure?")]
    public async Task Confirmed(CheckContext c) => await c.Verdict();

    [ClientCheck("t.early", "First by order", Order = -5)]
    public async Task Early(CheckContext c) => await c.Verdict();

    // shape problems: listed, refused
    [ClientCheck("Bad Id!", "bad id")]
    public Task BadId(CheckContext c) => Task.CompletedTask;

    [ClientCheck("t.sig", "bad signature")]
    public void BadSig() { }

    [ClientCheck("t.pass", "the same id again")]
    public Task Dup(CheckContext c) => Task.CompletedTask;
}

public sealed class OtherChecks
{
    [ClientCheck("t.other", "On another class")]
    public async Task Other(CheckContext c) => await c.Verdict();
}

public static class StaticChecks
{
    [ClientCheck("t.static", "Static")]
    public static async Task Static(CheckContext c) => await c.Verdict();
}

/// <summary>A registered <see cref="CheckMod"/> and a runner over a fresh folder.</summary>
internal sealed class Harness : IDisposable
{
    public readonly string Dir;
    public readonly FakeEnv Env = new();
    public readonly ModContext Context;
    public readonly CheckMod Mod = new();
    public readonly Scrubber Scrubber = new();
    public readonly List<RunEvent> Events = new();
    public Ledger Ledger { get; }
    public CheckRunner Runner { get; }
    public string LedgerPath => Path.Combine(Dir, "client-checks.json");
    public FakeGuard? Guard => Env.Guard as FakeGuard;
    public int Seeded;

    public Harness(Action<Harness>? before = null)
    {
        ChecksService.AutoStart = false;
        CheckRegistry.Reset();
        CheckMod.Reset();
        Dir = Path.Combine(Path.GetTempPath(), "kiln-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Context = TestHooks.NewContext("cct", "Check test", "1.2.3", Dir);
        TestHooks.Bind(Mod, Context);
        ClientChecks.Register(Mod);
        before?.Invoke(this);
        Ledger = new Ledger(LedgerPath);
        Runner = new CheckRunner(Env, Ledger, Scrubber);
        Runner.Events += Events.Add;
    }

    public void Pump(int steps = 6)
    {
        for (var i = 0; i < steps; i++)
        {
            Runner.Step();
            TestHooks.Drain(Context);
        }
        Runner.Step();
    }

    public void Start(string id, CheckOrigin origin = CheckOrigin.Local, Attestation? attestation = null, object? controller = null)
    {
        Runner.Request(new StartRequest("cct", id, origin, attestation, default, controller));
        Pump();
    }

    public RunEvent? Last(RunEventKind kind) => Events.LastOrDefault(e => e.Kind == kind);

    public LedgerEntry Answer(AnswerKind kind, string? note = null, object? controller = null)
    {
        Assert.Null(Runner.Answer(kind, note, controller));
        Pump();
        var f = Last(RunEventKind.Finished);
        Assert.NotNull(f);
        Assert.NotNull(f!.Entry);
        return f.Entry!;
    }

    public IReadOnlyList<LedgerEntry> OnDisk() => Ledger.ReadFile() is { State: LedgerReadState.Ok } r ? r.Document!.Entries : [];

    /// <summary>A ledger file with these entries before the runner starts.</summary>
    public void Seed(params LedgerEntry[] entries)
    {
        var l = new Ledger(LedgerPath);
        l.Load();
        foreach (var e in entries) Assert.True(l.Append(e));
        Seeded += entries.Length;
    }

    public static LedgerEntry ProofEntry(string verdict, string sha = FakeEnv.Sha, string kiln = FakeEnv.KilnVersion, string side = "client") => new()
    {
        Mod = "cct", ModVersion = "1.2.3", Id = "t.proof", Title = "Guard proof", Area = "Cheats",
        Tags = [ClientCheckTags.Cheats, ClientCheckTags.GuardProof], Kiln = kiln, Game = new LedgerGame { Side = side, Sha256 = sha, Verified = true },
        Session = "hosted", Verdict = verdict, Time = "2026-11-11T00:00:00Z",
        Assertions = [new LedgerAssertion { What = "the guard dropped at least one report", Ok = verdict == "pass" }],
    };

    public void Dispose()
    {
        TestHooks.Retire(Context);
        CheckRegistry.Reset();
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }
}
