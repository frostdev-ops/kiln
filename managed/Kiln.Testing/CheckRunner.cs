// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Reflection;

namespace Kiln.Testing;

/// <summary>The game build of this process, from <c>kiln/state/boot.json</c>.</summary>
internal sealed record GameInfo(string Side, string? Sha256, bool Verified, string Kiln);

/// <summary>New lines of kiln.log since a mark.</summary>
internal interface ILogTail
{
    long Mark();
    IReadOnlyList<string> Since(long mark, int maxBytes);
}

/// <summary>What the runner needs from the process (a fake in tests).</summary>
internal interface IRunnerEnv
{
    GameInfo Game { get; }

    /// <summary>A game client (not a dedicated server).</summary>
    bool IsClient { get; }

    /// <summary>This client hosts the world it is in (the server world runs here).</summary>
    bool Hosting { get; }

    EntityId LocalPlayer { get; }

    /// <summary>The name of the character <paramref name="player"/> plays, read on the server side; null when it cannot
    /// be read here (<paramref name="why"/> says why).</summary>
    string? CharacterName(EntityId player, out string why);

    /// <summary>Changes when the world this process runs (or is joined to) changes; 0 with no world.</summary>
    uint WorldSession { get; }

    /// <summary>The achievement guard of this process; null when there is none to ask.</summary>
    IAchievementGuard? Guard { get; }

    ILogTail Log { get; }

    DateTime Now { get; }

    void Write(LogLevel level, string text);
}

/// <summary>A request to start a check. <c>Controller</c> is who may answer it: null for this process (screen, keys,
/// control folder), else the joining client's peer.</summary>
internal sealed record StartRequest(string Mod, string Id, CheckOrigin Origin, Attestation? Attestation = null, EntityId Player = default,
    object? Controller = null);

internal enum RunEventKind
{
    /// <summary>The check was refused; <see cref="RunEvent.Refusal"/> says why. No entry.</summary>
    Refused,
    /// <summary>The check waits for yes or no to its Confirm text.</summary>
    Confirming,
    /// <summary>The player answered no (or the run was stopped) before the body started. No entry.</summary>
    Declined,
    /// <summary>The body started.</summary>
    Started,
    /// <summary>The instruction, the phase or the assertions changed.</summary>
    Changed,
    /// <summary>The check ended: with <see cref="RunEvent.Entry"/> when a verdict was written, without when stopped.</summary>
    Finished,
}

internal sealed record RunEvent(RunEventKind Kind, string Mod, string Id, CheckOrigin Origin, object? Controller, string Message,
    Refusal? Refusal = null, LedgerEntry? Entry = null, ViewCurrent? Current = null);

/// <summary>An answer to the running check.</summary>
internal enum AnswerKind
{
    Pass,
    Fail,
    Skip,
    Yes,
    No,
    Stop,
}

/// <summary>
/// The guided check runner. Its state lives on the tick thread: <see cref="Step"/> runs there every server-world step
/// (Kiln's service tick), and check bodies run there in their mod's context. Other threads (the screen, key presses,
/// the network on a client) hand work over with <see cref="Post"/>; the screen reads <see cref="View"/>, an immutable
/// snapshot.
/// </summary>
internal sealed class CheckRunner
{
    /// <summary>After a verdict, how long a body that asked for it may take to finish before it is canceled.</summary>
    internal static TimeSpan FinishGrace = TimeSpan.FromSeconds(10);

    /// <summary>How long one clean-up may take.</summary>
    internal static TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    internal const string DroppedAssertion = "the guard dropped at least one report";

    internal const int MaxLogLines = 200, MaxLogBytes = 32 * 1024, MaxLine = 1000, MaxNote = 300, MaxAssertions = 64;

    private readonly IRunnerEnv _env;
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly Queue<StartRequest> _queue = new();
    private readonly Dictionary<string, Refusal> _lastRefusal = new(StringComparer.Ordinal);
    private Run? _run;
    private IDisposable? _hold;
    private bool _holdTried;
    private string? _warning;
    private string? _changedCharacter;
    private uint _changedIn;
    private string? _message;
    private GuardStatus? _guard;
    private DateTime _guardRead;
    private bool _dirty = true;
    private bool _catalogueDirty = true;
    private RunnerView _view = RunnerView.Empty;

    public CheckRunner(IRunnerEnv env, Ledger ledger, Scrubber scrubber)
    {
        _env = env;
        Ledger = ledger;
        Scrubber = scrubber;
        if (ledger.Load() is { } moved) env.Write(LogLevel.Warn, "checks: " + moved);
    }

    public Ledger Ledger { get; }
    public Scrubber Scrubber { get; }

    /// <summary>Started, refused, finished...: the control folder and the network listen (tick thread).</summary>
    public event Action<RunEvent>? Events;

    /// <summary>The latest snapshot for the screen (any thread).</summary>
    public RunnerView View => Volatile.Read(ref _view);

    /// <summary>The check that runs now, if any (tick thread).</summary>
    internal Run? Current => _run;

    internal bool Busy => _run is not null || _queue.Count > 0;

    /// <summary>Hands <paramref name="work"/> to the tick thread (the next <see cref="Step"/>). Any thread.</summary>
    public void Post(Action work) => _inbox.Enqueue(work);

    /// <summary>The registry changed: the list and the ledger's catalogue are rebuilt at the next step. Any thread.</summary>
    public void CatalogueChanged() => Post(() =>
    {
        _catalogueDirty = true;
        _dirty = true;
    });

    /// <summary>One step of the runner (tick thread).</summary>
    public void Step()
    {
        while (_inbox.TryDequeue(out var work))
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                _env.Write(LogLevel.Error, $"checks: {e}");
            }
        }
        if (_catalogueDirty)
        {
            _catalogueDirty = false;
            if (Ledger.MergeCatalogue(CheckRegistry.All.Select(LedgerCheck.Of)) && !Ledger.Write())
                _env.Write(LogLevel.Warn, $"checks: the ledger was not written: {Ledger.WriteError}");
        }
        if (_run is { } run) Advance(run);
        if (_run is null) StartNext();
        if (_env.Now - _guardRead > TimeSpan.FromSeconds(1)) ReadGuard();
        if (_changedCharacter is not null && _env.WorldSession != _changedIn) _dirty = true;
        if (_dirty) Publish();
    }

    /// <summary>Queues one check (tick thread). Its outcome comes as events.</summary>
    public void Request(StartRequest r)
    {
        if (r.Controller is not null && _run is { } busy && !Equals(busy.Request.Controller, r.Controller))
        {
            Emit(new RunEvent(RunEventKind.Refused, r.Mod, r.Id, r.Origin, r.Controller, "another check is running",
                new Refusal("", $"another check is running ({busy.Case.Key})")));
            return;
        }
        _queue.Enqueue(r);
        _dirty = true;
        if (_run is null) StartNext();
    }

    /// <summary>Queues every check whose mod, id, title, area or a tag contains <paramref name="filter"/> (tick
    /// thread). Returns how many.</summary>
    public int RequestAll(string? filter, CheckOrigin origin, object? controller = null, EntityId player = default)
    {
        var n = 0;
        foreach (var c in CheckRegistry.All)
        {
            if (filter is { Length: > 0 } f && !Matches(c, f)) continue;
            _queue.Enqueue(new StartRequest(c.Mod, c.Id, origin, null, player, controller));
            n++;
        }
        _dirty = true;
        if (_run is null) StartNext();
        return n;
    }

    internal static bool Matches(CheckCase c, string f) =>
        c.Mod.Contains(f, StringComparison.OrdinalIgnoreCase) || c.Id.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        c.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || c.Area.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        c.Tags.Any(t => t.Equals(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>An answer to the running check (tick thread). Returns null when taken, else why not.
    /// <paramref name="guardAtEnd"/> is a joining client's guard status sent with its verdict.</summary>
    public string? Answer(AnswerKind kind, string? note = null, object? controller = null, GuardStatus? guardAtEnd = null)
    {
        if (kind == AnswerKind.Stop)
        {
            var queued = _queue.Count;
            if (controller is not null) RemoveQueued(controller);
            else _queue.Clear();
            if (_run is not { } r0) return queued > 0 ? null : "no check is running";
            if (!Equals(r0.Request.Controller, controller)) return "the running check was started by someone else";
            r0.Stopped = true;
            if (r0.Phase == RunPhase.Confirming)
            {
                Decline(r0, "stopped before it started");
                return null;
            }
            r0.Cancel();
            r0.Phase = RunPhase.Finishing;
            _dirty = true;
            return null;
        }
        if (_run is not { } run) return "no check is running";
        if (!Equals(run.Request.Controller, controller)) return "the running check was started by someone else";
        switch (kind)
        {
            case AnswerKind.Yes or AnswerKind.No when run.Phase != RunPhase.Confirming:
                return "the running check does not wait for a yes or no";
            case AnswerKind.No:
                Decline(run, "the player answered no");
                return null;
            case AnswerKind.Yes:
                if (run.Questions.TryDequeue(out var answered) && ReferenceEquals(answered, CheatsRule.ThrowawayQuestion)) run.ThrowawayConfirmed = true;
                if (run.Questions.Count > 0) Ask(run);
                else Begin(run, afterConfirm: true);
                return null;
        }
        if (run.Phase == RunPhase.Confirming) return "answer the check's question first (yes or no)";
        if (run.Verdict is not null) return "this check has its verdict";
        run.Verdict = kind switch { AnswerKind.Pass => ClientCheckVerdict.Pass, AnswerKind.Fail => ClientCheckVerdict.Fail, _ => ClientCheckVerdict.Skip };
        run.Note = Scrubber.Line(note, MaxNote);
        run.GuardAtEnd = guardAtEnd;
        run.FinishBy = _env.Now + FinishGrace;
        run.VerdictTcs.TrySetResult(run.Verdict.Value);
        // a verdict before the body asked for one ends the body (the player bailed out, or judged early)
        if (!run.AskedVerdict && run.Body is { IsCompleted: false }) run.Cancel();
        run.Phase = RunPhase.Finishing;
        _dirty = true;
        Emit(Event(RunEventKind.Changed, run, $"{LedgerEntry.VerdictName(run.Verdict.Value)} given"));
        return null;
    }

    private void RemoveQueued(object controller)
    {
        var keep = _queue.Where(q => !Equals(q.Controller, controller)).ToList();
        _queue.Clear();
        foreach (var q in keep) _queue.Enqueue(q);
    }

    /// <summary>The session a request runs in, or why it cannot run here.</summary>
    private (ClientCheckSession Session, string? Problem) SessionOf(StartRequest r) => r.Origin switch
    {
        CheckOrigin.Remote => (ClientCheckSession.Joined, null),
        _ when _env.IsClient => _env.Hosting
            ? (ClientCheckSession.Hosted, null)
            : (ClientCheckSession.Hosted, "this game is not hosting a world: host one (single player or co-op) to run checks here, or join a server"),
        CheckOrigin.Control => (ClientCheckSession.Server, null),
        _ => (ClientCheckSession.Server, "a dedicated server runs checks only from kiln/control/checks/"),
    };

    private static string? NeedsProblem(CheckCase c, ClientCheckSession session) => c.Needs switch
    {
        ClientCheckNeeds.Hosted when session != ClientCheckSession.Hosted =>
            $"needs a world this game hosts; this session is {LedgerEntry.SessionName(session)}",
        ClientCheckNeeds.Joined when session != ClientCheckSession.Joined =>
            $"needs a game joined to a server; this session is {LedgerEntry.SessionName(session)}",
        _ => null,
    };

    private void StartNext()
    {
        while (_run is null && _queue.TryDequeue(out var r))
        {
            _dirty = true;
            var found = CheckRegistry.Find(r.Mod, r.Id);
            if (found is not { } f)
            {
                Refuse(r, null, new Refusal("", $"no check {r.Mod}/{r.Id} is loaded"));
                continue;
            }
            var c = f.Case;
            if (c.Problem is { } problem)
            {
                Refuse(r, c, new Refusal("", problem));
                continue;
            }
            var (session, sessionProblem) = SessionOf(r);
            if (sessionProblem is not null)
            {
                Refuse(r, c, new Refusal("", sessionProblem));
                continue;
            }
            if (NeedsProblem(c, session) is { } needs)
            {
                Refuse(r, c, new Refusal("", needs));
                continue;
            }
            if (CheatsRule.Static(c, session, r.Origin) is { } refusal)
            {
                Refuse(r, c, refusal);
                continue;
            }
            if (!f.Mod.Context.Alive)
            {
                Refuse(r, c, new Refusal("", "the mod is unloading"));
                continue;
            }
            HoldGuard();
            var run = new Run(this, f.Mod, c, r, session);
            _run = run;
            // the runner's own question first (a cheats check where the character's name cannot be read), then the check's
            if (c.Cheats && session == ClientCheckSession.Hosted && CheatsRule.ReadName(_env, PlayerOf(run), out _) is null)
                run.Questions.Enqueue(CheatsRule.ThrowawayQuestion);
            if (c.Confirm is { } confirm) run.Questions.Enqueue(confirm);
            if (run.Questions.Count > 0)
            {
                Ask(run);
                return;
            }
            Begin(run, afterConfirm: false);
        }
        if (_run is null && _queue.Count == 0) ReleaseHold();
    }

    /// <summary>While the runner is active it holds the guard (cheats or not); a hold that fails only stops cheats
    /// checks, which read the guard themselves.</summary>
    private void HoldGuard()
    {
        if (_hold is not null || _holdTried || _env.Guard is not { } guard) return;
        _holdTried = true;
        try
        {
            if (guard.TryHold("guided checks are running", out var hold)) _hold = hold;
            else _env.Write(LogLevel.Info, "checks: the achievement guard could not be held for this run (checks tagged cheats stay refused)");
        }
        catch (Exception e)
        {
            _env.Write(LogLevel.Warn, $"checks: holding the achievement guard threw {e.GetType().Name}: {e.Message}");
        }
    }

    private void ReleaseHold()
    {
        _holdTried = false;
        if (_hold is null) return;
        try
        {
            _hold.Dispose();  // a latch a cheats check took stays on until the game exits
        }
        catch (Exception e)
        {
            _env.Write(LogLevel.Warn, $"checks: ending the guard hold threw {e.Message}");
        }
        _hold = null;
    }

    private void Refuse(StartRequest r, CheckCase? c, Refusal refusal)
    {
        var key = r.Mod + "/" + r.Id;
        _lastRefusal[key] = refusal;
        _message = $"refused {key}: {refusal}";
        _env.Write(LogLevel.Warn, $"checks: refused {key} ({LedgerEntry.SessionName(SessionOf(r).Session)}, from {r.Origin.ToString().ToLowerInvariant()}): {refusal}");
        _dirty = true;
        Emit(new RunEvent(RunEventKind.Refused, r.Mod, r.Id, r.Origin, r.Controller, refusal.ToString(), refusal));
    }

    private void Decline(Run run, string why)
    {
        _env.Write(LogLevel.Info, $"checks: {run.Case.Key} not started: {why}");
        _message = $"{run.Case.Key} not started: {why}";
        _run = null;
        _dirty = true;
        Emit(Event(RunEventKind.Declined, run, why));
    }

    private void Ask(Run run)
    {
        run.Phase = RunPhase.Confirming;
        run.Instruction = run.Questions.Peek();
        _dirty = true;
        Emit(Event(RunEventKind.Confirming, run, "waiting for yes or no: " + run.Instruction));
    }

    private EntityId PlayerOf(Run run) =>
        run.Request.Origin == CheckOrigin.Remote ? run.Request.Player : run.Session == ClientCheckSession.Hosted ? _env.LocalPlayer : EntityId.None;

    /// <summary>Reads the rule (for cheats) and starts the body.</summary>
    private void Begin(Run run, bool afterConfirm)
    {
        var c = run.Case;
        Refusal? refusal;
        GuardStatus? before = null;
        var player = PlayerOf(run);
        string? throwaway = null;
        refusal = run.Session == ClientCheckSession.Joined
            ? CheatsRule.Joined(c, run.Request.Attestation, _env.Now)
            : CheatsRule.Hosted(c, run.Request.Origin, _env.Guard, Ledger.ReadFile, _env.Game, out before);
        // line 5 last: the lines before it say first what is wrong with the session and the guard
        if (refusal is null && c.Cheats)
            refusal = CheatsRule.Character(_env, player,
                run.Session == ClientCheckSession.Joined ? run.Request.Attestation?.Throwaway == true : run.ThrowawayConfirmed, out throwaway);
        if (run.Session == ClientCheckSession.Joined && c.Cheats && run.Request.Attestation?.Status is { } s) before = s;
        if (refusal is not null)
        {
            _run = null;
            Refuse(run.Request, c, refusal);
            return;
        }
        _lastRefusal.Remove(c.Key);
        run.GuardBefore = before;
        run.Phase = RunPhase.Running;
        run.Instruction = "";
        run.LogMark = SafeMark();
        if (throwaway is not null) run.Add(true, throwaway, null);
        run.Started = _env.Now;
        var context = new CheckContext(run, run.Mod.Context, c.Id, c.Title, run.Session, player, run.Cancellation);
        _env.Write(LogLevel.Info, $"checks: start {c.Key} ({LedgerEntry.SessionName(run.Session)}{(c.Cheats ? ", cheats: guard latched and protecting" : "")})");
        _message = $"running {c.Key}";
        _dirty = true;
        if (c.Cheats)
        {
            _changedCharacter = ChangedCharacter;
            _changedIn = _env.WorldSession;
        }
        Emit(Event(RunEventKind.Started, run, afterConfirm ? "started after yes" : "started"));
        run.Body = Invoke(run, context);
    }

    private long SafeMark()
    {
        try
        {
            return _env.Log.Mark();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Calls the check method in its mod's context, so its awaits come back to the tick thread as that mod.</summary>
    private static Task Invoke(Run run, CheckContext context)
    {
        var m = run.Case.Method!;
        var mod = run.Mod.Context;
        var previousSync = SynchronizationContext.Current;
        var previousMod = ModContext.CurrentMod;
        SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
        ModContext.CurrentMod = mod;
        try
        {
            var type = m.DeclaringType!;
            run.Target = m.IsStatic ? null : type.IsInstanceOfType(run.Mod.Instance) ? run.Mod.Instance : Activator.CreateInstance(type, nonPublic: true);
            return m.Invoke(run.Target, BindingFlags.DoNotWrapExceptions, null, [context], null) switch
            {
                Task t => t,
                ValueTask vt => vt.AsTask(),
                _ => Task.CompletedTask,
            };
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
        finally
        {
            ModContext.CurrentMod = previousMod;
            SynchronizationContext.SetSynchronizationContext(previousSync);
        }
    }

    private static Task InvokeCleanup(Run run, Func<Task> cleanup)
    {
        var mod = run.Mod.Context;
        var previousSync = SynchronizationContext.Current;
        var previousMod = ModContext.CurrentMod;
        SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
        ModContext.CurrentMod = mod;
        try
        {
            return cleanup() ?? Task.CompletedTask;
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
        finally
        {
            ModContext.CurrentMod = previousMod;
            SynchronizationContext.SetSynchronizationContext(previousSync);
        }
    }

    private void Advance(Run run)
    {
        if (!run.Mod.Context.Alive && run.Phase == RunPhase.Confirming)
        {
            Decline(run, "its mod unloaded");
            return;
        }
        if (run.Phase == RunPhase.Confirming) return;
        if (!run.Mod.Context.Alive)
        {
            // its scheduler drops the body's continuations and its clean-ups cannot run as it: nothing to wait for
            run.Stopped = true;
            run.Cancel();
            _env.Write(LogLevel.Warn, $"checks: {run.Case.Key} ended: its mod unloaded (no entry)");
            Complete(run);
            return;
        }
        if (run.Phase is RunPhase.Running or RunPhase.Asking)
        {
            if (run.Body is { IsCompleted: true } done && !run.BodyEnded)
            {
                run.BodyEnded = true;
                if (done.IsFaulted && done.Exception?.GetBaseException() is { } e && e is not OperationCanceledException)
                {
                    run.Add(false, "the check's code ran to its end", $"{e.GetType().Name}: {e.Message}");
                    run.Instruction = "The check's code stopped with an error (see the assertions). Pass, fail or skip?";
                }
                else if (run.Instruction.Length == 0)
                    run.Instruction = "The check ended without asking. Pass, fail or skip?";
                run.Phase = RunPhase.Asking;
                _dirty = true;
                Emit(Event(RunEventKind.Changed, run, "waiting for the verdict"));
            }
            return;
        }
        // finishing: wait for the body, then the clean-ups (last registered first), then write
        if (run.Body is { IsCompleted: false } && !run.LeftBehind)
        {
            if (_env.Now < run.FinishBy) return;
            if (!run.Canceled)
            {
                run.Cancel();
                run.FinishBy = _env.Now + FinishGrace;
                return;
            }
            run.LeftBehind = true;
            run.Add(false, "the check's code finished after its verdict", $"still running {FinishGrace.TotalSeconds:0} s after it was canceled; left behind");
        }
        while (true)
        {
            if (run.CleanupTask is { } ct)
            {
                if (!ct.IsCompleted && _env.Now < run.CleanupBy) return;
                if (!ct.IsCompleted) run.Add(false, "a clean-up finished", $"still running after {CleanupTimeout.TotalSeconds:0} s; left behind");
                else if (ct.IsFaulted && ct.Exception?.GetBaseException() is { } e)
                    run.Add(false, "a clean-up ran", $"{e.GetType().Name}: {e.Message}");
                run.CleanupTask = null;
            }
            if (!run.TryTakeCleanup(out var next)) break;
            run.CleanupTask = InvokeCleanup(run, next);
            run.CleanupBy = _env.Now + CleanupTimeout;
        }
        Complete(run);
    }

    private void Complete(Run run)
    {
        _run = null;
        _dirty = true;
        run.Finished = true;
        if (run.Target is not null && !ReferenceEquals(run.Target, run.Mod.Instance) && run.Target is IDisposable d)
        {
            try
            {
                d.Dispose();
            }
            catch (Exception e)
            {
                _env.Write(LogLevel.Warn, $"checks: {run.Case.Key}: dispose threw {e.Message}");
            }
        }
        if (run.Stopped || run.Verdict is not { } verdict)
        {
            _message = $"{run.Case.Key} stopped (no entry)";
            _env.Write(LogLevel.Info, $"checks: {run.Case.Key} stopped before its verdict (no entry)");
            Emit(Event(RunEventKind.Finished, run, "stopped before its verdict: no entry"));
            return;
        }
        var c = run.Case;
        LedgerGuard? guard = null;
        var note = run.Note;
        if (c.Cheats)
        {
            GuardStatus? after = run.Session == ClientCheckSession.Joined ? run.GuardAtEnd : _env.Guard is { } g ? CheatsRule.ReadStatus(g).Status : null;
            if (c.GuardProof && ProofOverride(run, after, verdict, note) is { } failed)
            {
                verdict = ClientCheckVerdict.Fail;
                note = failed;
            }
            var warning = GuardWarning(run.GuardBefore, after);
            if (after is { } a) guard = LedgerGuard.Of(a);
            else guard = new LedgerGuard();
            if (warning is not null)
            {
                _warning = $"{c.Key}: {warning}";
                _env.Write(LogLevel.Error, $"checks: achievement guard warning for {c.Key}: {warning}");
                note = (note.Length > 0 ? note + " " : "") + "[guard warning: " + warning + "]";
            }
        }
        var (log, dropped) = CaptureLog(run.LogMark);
        var g0 = _env.Game;
        var entry = new LedgerEntry
        {
            Mod = c.Mod, ModVersion = c.ModVersion, Id = c.Id, Title = c.Title, Area = c.Area, Since = c.Since, Tags = c.Tags.ToArray(),
            Kiln = g0.Kiln, Game = new LedgerGame { Side = g0.Side, Sha256 = g0.Sha256, Verified = g0.Verified },
            Session = LedgerEntry.SessionName(run.Session), Verdict = LedgerEntry.VerdictName(verdict), Note = note,
            Time = _env.Now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), Assertions = run.Assertions.ToArray(), Log = log,
            LogDropped = dropped, Guard = guard,
        };
        Ledger.MergeCatalogue([LedgerCheck.Of(c)]);
        var written = Ledger.Append(entry);
        _message = written ? $"{c.Key}: {entry.Verdict} written" : $"{c.Key}: {entry.Verdict} NOT written: {Ledger.WriteError}";
        _env.Write(written ? LogLevel.Info : LogLevel.Error,
            $"checks: {c.Key} {entry.Verdict.ToUpperInvariant()}{(note.Length > 0 ? ": " + note : "")}" +
            $" ({run.Assertions.Count(a => !a.Ok)} of {run.Assertions.Count} assertion(s) failed)" + (written ? "" : $"; the ledger was not written: {Ledger.WriteError}"));
        Emit(Event(RunEventKind.Finished, run, _message) with { Entry = entry });
    }

    /// <summary>The guard-proof check shows the guard working only when its dropped counter went up while the body ran.
    /// When it did not, the recorded verdict is fail whatever the player answered (the one verdict the runner
    /// overrides), and the note says why. Returns that note, or null when the counter went up.</summary>
    private string? ProofOverride(Run run, GuardStatus? after, ClientCheckVerdict answered, string note)
    {
        long? b = run.GuardBefore?.Dropped, a = after?.Dropped;
        var up = b is { } x && a is { } y && y > x;
        run.Add(up, DroppedAssertion, $"{b?.ToString() ?? "unread"} before, {a?.ToString() ?? "unread"} after");
        if (up) return null;
        _env.Write(LogLevel.Warn, $"checks: {run.Case.Key}: the guard's dropped counter did not go up; recorded as fail (the player answered {LedgerEntry.VerdictName(answered)})");
        return $"the guard's dropped counter did not go up ({b?.ToString() ?? "unread"} before, {a?.ToString() ?? "unread"} after): " +
               "the guard did not see an achievement report" +
               (answered != ClientCheckVerdict.Fail ? $"; the player answered {LedgerEntry.VerdictName(answered)}" : "") +
               (note.Length > 0 ? "; note: " + note : "");
    }

    /// <summary>The line that stays after a cheats check started, until the game exits or the world is left.</summary>
    internal const string ChangedCharacter =
        "A cheats check changed this character. Delete it before you play on: loaded again without the guard it can unlock Steam achievements.";

    /// <summary>When the guard let a report through or stopped protecting while a cheats check ran.</summary>
    internal static string? GuardWarning(GuardStatus? before, GuardStatus? after)
    {
        if (after is not { } a) return "the guard's status could not be read when the check ended";
        if (before is { } b && a.Passed > b.Passed)
            return $"the guard passed {a.Passed - b.Passed} achievement report(s) to Steam during the check";
        if (!a.Protecting) return "the guard was not protecting when the check ended: " + a;
        return null;
    }

    private (string[] Lines, int Dropped) CaptureLog(long mark)
    {
        if (mark < 0) return ([], 0);
        IReadOnlyList<string> raw;
        try
        {
            raw = _env.Log.Since(mark, 1 << 20);
        }
        catch (Exception e)
        {
            return ([$"(kiln.log could not be read: {Scrubber.Line(e.Message, 200)})"], 0);
        }
        var lines = new List<string>();
        var bytes = 0;
        var dropped = 0;
        foreach (var l in raw)
        {
            var s = Scrubber.Line(l, MaxLine);
            if (s.Length == 0) continue;
            if (lines.Count >= MaxLogLines || bytes + s.Length > MaxLogBytes)
            {
                dropped++;
                continue;
            }
            lines.Add(s);
            bytes += s.Length;
        }
        return (lines.ToArray(), dropped);
    }

    private void ReadGuard()
    {
        _guardRead = _env.Now;
        GuardStatus? s = null;
        if (_env.Guard is { } g)
        {
            try
            {
                s = g.Status();
            }
            catch
            {
                s = null;
            }
        }
        if (s != _guard)
        {
            _guard = s;
            _dirty = true;
        }
    }

    private RunEvent Event(RunEventKind kind, Run run, string message) =>
        new(kind, run.Case.Mod, run.Case.Id, run.Request.Origin, run.Request.Controller, message, Current: CurrentOf(run));

    private void Emit(RunEvent e)
    {
        _dirty = true;
        if (Events is not { } events) return;
        foreach (Action<RunEvent> h in events.GetInvocationList())
        {
            try
            {
                h(e);
            }
            catch (Exception x)
            {
                _env.Write(LogLevel.Error, $"checks: an event handler threw {x}");
            }
        }
    }

    internal void MarkDirty() => _dirty = true;

    internal void MarkDirtyFromAnyThread() => Post(() => _dirty = true);

    private static string PhaseName(RunPhase p) => p switch
    {
        RunPhase.Confirming => "confirming",
        RunPhase.Running => "running",
        RunPhase.Asking => "asking",
        _ => "finishing",
    };

    private static ViewCurrent CurrentOf(Run r) => new(r.Case.Mod, r.Case.Id, r.Case.Title, r.Case.Area, PhaseName(r.Phase), r.Instruction,
        r.Phase == RunPhase.Confirming ? r.Instruction : null, r.Assertions.ToArray(), LedgerEntry.SessionName(r.Session), r.Case.Cheats);

    /// <summary>The refusal the list shows for a check that is not running: what is known now, without touching the guard.</summary>
    private string? Preview(CheckCase c, ClientCheckSession session, string? sessionProblem, Func<string> proof)
    {
        if (c.Problem is { } p) return p;
        if (_lastRefusal.TryGetValue(c.Key, out var last)) return last.ToString();
        if (sessionProblem is not null) return null;  // said once for the whole list
        if (NeedsProblem(c, session) is { } n) return n;
        if (CheatsRule.Static(c, session, CheckOrigin.Local) is { } r) return r.ToString();
        if (!c.Cheats || session == ClientCheckSession.Joined) return null;
        if (_guard is not { Available: true } g) return $"{CheatsRule.Line2}: the achievement guard is not there" + (_guard is { } x ? $" ({x.Reason})" : "");
        if (c.GuardProof) return null;
        var state = proof();
        return CheatsRule.Proof(c, () => throw new InvalidOperationException(), _env.Game.Sha256, _env.Game.Kiln, state)?.ToString();
    }

    /// <summary>The list for a session (the joined channel sends it with <see cref="ClientCheckSession.Joined"/>). Tick thread.</summary>
    internal List<ViewItem> Items(ClientCheckSession session, string? sessionProblem)
    {
        var newest = Ledger.Newest();
        string? proof = null;  // read once per list, only when a cheats check needs it
        string Proof() => proof ??= CheatsRule.ProofState(Ledger.ReadFile, _env.Game.Sha256, _env.Game.Kiln);
        return CheckRegistry.All
            .OrderBy(c => c.Area, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Order)
            .Select(c =>
            {
                newest.TryGetValue(c.Key, out var e);
                return new ViewItem(c.Mod, c.Id, c.Title, c.Area, c.Tags, LedgerCheck.NeedsName(c.Needs), e?.Verdict, e?.Note,
                    Preview(c, session, sessionProblem, Proof), c.Confirm, c.Cheats);
            }).ToList();
    }

    private void Publish()
    {
        _dirty = false;
        if (_changedCharacter is not null && _env.WorldSession != _changedIn) _changedCharacter = null;  // the world was left
        var (session, problem) = SessionOf(new StartRequest("", "", _env.IsClient ? CheckOrigin.Local : CheckOrigin.Control));
        var items = Items(session, problem);
        var g = _guard;
        var label = _env.IsClient ? (_env.Hosting ? "hosted" : "menu") : "server";
        var view = new RunnerView(label, items, _run is { } r ? CurrentOf(r) : null,
            g is { } s ? s.ToString() : _env.Guard is null ? "not there" : "status did not arrive", g?.Protecting ?? false,
            g?.Dropped ?? 0, g?.Passed ?? 0, _warning, problem ?? _message, _queue.Count, Ledger.WriteError ?? (Ledger.MovedAside is { } m ? $"an unreadable ledger was moved to {Path.GetFileName(m)}" : null))
        {
            Changed = _changedCharacter,
        };
        Volatile.Write(ref _view, view);
    }

    internal enum RunPhase
    {
        Confirming,
        Running,
        Asking,
        Finishing,
    }

    /// <summary>One check from start to entry. Its members are touched on the tick thread; <see cref="CheckContext"/>'s
    /// calls take the lock in case a body left the tick thread.</summary>
    internal sealed class Run : ICheckRun
    {
        private readonly CheckRunner _runner;
        private readonly CancellationTokenSource _cts = new();
        private readonly Stack<Func<Task>> _cleanups = new();
        private readonly object _lock = new();

        public Run(CheckRunner runner, CheckRegistry.Registered mod, CheckCase c, StartRequest request, ClientCheckSession session)
        {
            _runner = runner;
            Mod = mod;
            Case = c;
            Request = request;
            Session = session;
        }

        public CheckRegistry.Registered Mod { get; }
        public CheckCase Case { get; }
        public StartRequest Request { get; }
        public ClientCheckSession Session { get; }
        public RunPhase Phase { get; set; }
        public string Instruction { get; set; } = "";
        public List<LedgerAssertion> Assertions { get; } = new();
        public TaskCompletionSource<ClientCheckVerdict> VerdictTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Cancellation => _cts.Token;
        public Task? Body { get; set; }
        public object? Target { get; set; }
        public bool AskedVerdict { get; private set; }
        public bool BodyEnded { get; set; }
        public bool Stopped { get; set; }
        public bool Canceled { get; private set; }
        public bool LeftBehind { get; set; }
        public Queue<string> Questions { get; } = new();
        public bool ThrowawayConfirmed { get; set; }
        public bool Finished { get; set; }
        public ClientCheckVerdict? Verdict { get; set; }
        public string Note { get; set; } = "";
        public GuardStatus? GuardBefore { get; set; }
        public GuardStatus? GuardAtEnd { get; set; }
        public long LogMark { get; set; } = -1;
        public DateTime Started { get; set; }
        public DateTime FinishBy { get; set; }
        public Task? CleanupTask { get; set; }
        public DateTime CleanupBy { get; set; }

        public void Cancel()
        {
            Canceled = true;
            VerdictTcs.TrySetCanceled();
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException)
            {
                // a registration of the body threw; the body sees the cancellation anyway
            }
        }

        public bool TryTakeCleanup(out Func<Task> next)
        {
            lock (_lock) return _cleanups.TryPop(out next!);
        }

        public void Add(bool ok, string what, string? detail)
        {
            lock (_lock)
            {
                if (Assertions.Count >= MaxAssertions) return;
                Assertions.Add(new LedgerAssertion
                {
                    What = _runner.Scrubber.Line(what, MaxNote), Ok = ok, Detail = detail is null ? null : _runner.Scrubber.Line(detail, MaxNote),
                });
            }
            _runner.MarkDirty();
        }

        void ICheckRun.Instruct(string text)
        {
            if (Finished) return;
            lock (_lock) Instruction = text;
            _runner.MarkDirty();
            if (Game.IsTickThread) _runner.Emit(_runner.Event(RunEventKind.Changed, this, "instruction"));
            else _runner.Post(() => _runner.Emit(_runner.Event(RunEventKind.Changed, this, "instruction")));
        }

        void ICheckRun.Expect(bool condition, string what, string? detail)
        {
            if (Finished) return;
            Add(condition, what, detail);
        }

        Task<ClientCheckVerdict> ICheckRun.Verdict()
        {
            if (Verdict is { } v) return Task.FromResult(v);
            if (Finished || Canceled) return Task.FromCanceled<ClientCheckVerdict>(new CancellationToken(true));
            if (!AskedVerdict)
            {
                AskedVerdict = true;
                if (Phase == RunPhase.Running) Phase = RunPhase.Asking;
                _runner.MarkDirty();
                if (Game.IsTickThread) _runner.Emit(_runner.Event(RunEventKind.Changed, this, "waiting for the verdict"));
                else _runner.Post(() => _runner.Emit(_runner.Event(RunEventKind.Changed, this, "waiting for the verdict")));
            }
            return VerdictTcs.Task;
        }

        void ICheckRun.OnEnd(Func<Task> cleanup)
        {
            lock (_lock) _cleanups.Push(cleanup);
        }
    }
}
