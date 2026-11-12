// SPDX-License-Identifier: MIT
using System.Diagnostics;

namespace Kiln.Testing;

/// <summary>One read of an achievement guard (the runner's copy of <see cref="SteamGuardStatus"/>).</summary>
internal readonly record struct GuardStatus(bool Available, bool HookInstalled, bool TargetVerified, bool Active, long Dropped,
    long Passed, string Reason)
{
    public bool Protecting => Available && HookInstalled && TargetVerified && Active;

    public override string ToString() =>
        !Available ? $"not available ({Reason})"
        : Protecting ? $"protecting, {Dropped} dropped, {Passed} passed"
        : $"not protecting (hook {(HookInstalled ? "in" : "not in")}, target {(TargetVerified ? "verified" : "not verified")}, " +
          $"{(Active ? "on" : "off")}; {Reason}), {Dropped} dropped, {Passed} passed";
}

/// <summary>The seam between the runner and the achievement guard of this game client (tests use a fake).</summary>
internal interface IAchievementGuard
{
    /// <summary>The guard's state now; null when it did not arrive. May throw.</summary>
    GuardStatus? Status();

    /// <summary>Turns the guard on until the game exits; true when it protects now. May throw.</summary>
    bool Latch(string reason);

    /// <summary>Turns the guard on while <paramref name="hold"/> is not disposed; true when it protects now.</summary>
    bool TryHold(string reason, out IDisposable? hold);
}

/// <summary>The real guard: <see cref="SteamGuard"/> (Kiln.Client, service kiln.steamguard).</summary>
internal sealed class SteamAchievementGuard : IAchievementGuard
{
    public GuardStatus? Status()
    {
        var s = SteamGuard.Status;
        return new GuardStatus(s.Available, s.HookInstalled, s.TargetVerified, s.Active, s.Dropped, s.Passed, s.Reason ?? "");
    }

    public bool Latch(string reason) => SteamGuard.Latch(reason);

    public bool TryHold(string reason, out IDisposable? hold) => SteamGuard.TryHold(reason, out hold);
}

/// <summary>A joining client's own answer for a <c>cheats</c> check, sent with its start request: what its guard said
/// after its latch and what its own ledger holds for its game build.</summary>
internal sealed record Attestation
{
    /// <summary>The client's latch returned true.</summary>
    public bool Latched { get; init; }

    /// <summary>The status the client read after the latch; null when none arrived there.</summary>
    public GuardStatus? Status { get; init; }

    /// <summary>The newest guard-proof verdict in the client's ledger for its game sha256 and Kiln version: pass, fail,
    /// skip, none, unreadable (the ledger could not be read) or unknown (the client's game sha256 is not known).</summary>
    public string Proof { get; init; } = "none";

    /// <summary>The client's game sha256 and Kiln version the proof was looked up for.</summary>
    public string? Sha256 { get; init; }
    public string? Kiln { get; init; }

    /// <summary>When the client read it (UTC).</summary>
    public DateTime Taken { get; init; }

    /// <summary>The player answered the runner's throwaway-character question with yes on the client for this start.</summary>
    public bool Throwaway { get; init; }

    /// <summary>A line on why the client could not answer, when it could not.</summary>
    public string? Problem { get; init; }
}

/// <summary>A check refused at its start: which line of the rule failed and why.</summary>
internal sealed record Refusal(string Line, string Reason)
{
    public override string ToString() => Line.Length == 0 ? Reason : $"{Line}: {Reason}";
}

/// <summary>Where a start request came from.</summary>
internal enum CheckOrigin
{
    /// <summary>The runner's screen or keys in this process.</summary>
    Local,
    /// <summary><c>kiln/control/checks/</c>.</summary>
    Control,
    /// <summary>A joining client, over the network.</summary>
    Remote,
}

/// <summary>
/// The rule for checks tagged <c>cheats</c>. Such a check starts only when every line holds, each read at that moment:
/// <list type="number">
/// <item>The session is hosted (started in this game), or joined with the joining client's own answer in the start
/// request. From the control folder: refused (nobody at the game answered). With <c>two-players</c>: refused.</item>
/// <item>The guard of the client whose player the check changes answered <c>Latch</c> true.</item>
/// <item>Its status after the latch says protecting (available, hook installed, target verified, active).</item>
/// <item>The check is tagged <c>guard-proof</c> (and has a Confirm text), or the newest guard-proof entry in that
/// client's ledger for the same game sha256 and Kiln version is a pass.</item>
/// </list>
/// Any other state refuses: no guard, a status that did not arrive or came late, a read that throws, a latch that
/// returned false, a ledger that could not be read.
/// </summary>
internal static class CheatsRule
{
    /// <summary>A status read slower than this counts as late.</summary>
    internal static TimeSpan StatusDeadline = TimeSpan.FromSeconds(1);

    /// <summary>A joining client's answer older (or newer, by the clocks) than this counts as late.</summary>
    internal static TimeSpan AttestationDeadline = TimeSpan.FromSeconds(30);

    internal const string Line1 = "line 1", Line2 = "line 2", Line3 = "line 3", Line4 = "line 4";

    /// <summary>What can be refused without touching the guard (the list shows it before a start; the start checks it
    /// again first).</summary>
    public static Refusal? Static(CheckCase c, ClientCheckSession session, CheckOrigin origin)
    {
        if (c.GuardProof && !c.Cheats)
            return new Refusal(Line4, "a guard-proof check must also be tagged cheats");
        if (!c.Cheats) return null;
        if (origin == CheckOrigin.Control || session == ClientCheckSession.Server)
            return new Refusal(Line1, "a cheats check never starts from the control folder: nobody at the game answered for its guard");
        if (c.Has(ClientCheckTags.TwoPlayers))
            return new Refusal(Line1, "a cheats check that needs two players is always refused (the other player's guard cannot answer)");
        if (c.GuardProof && c.Confirm is null) return new Refusal(Line4, "a guard-proof check needs a Confirm text, and this one has none");
        return null;
    }

    /// <summary>The whole rule for a start in this process (hosted): latches the guard when lines 1 holds. Returns null
    /// when the check may start; <paramref name="after"/> is then the status read after the latch.</summary>
    public static Refusal? Hosted(CheckCase c, CheckOrigin origin, IAchievementGuard? guard, Func<LedgerRead> readLedger, GameInfo game,
        out GuardStatus? after)
    {
        after = null;
        if (Static(c, ClientCheckSession.Hosted, origin) is { } r) return r;
        if (!c.Cheats) return null;
        if (guard is null) return new Refusal(Line2, "the achievement guard is not there (Kiln.Client has no kiln.steamguard)");
        bool latched;
        try
        {
            latched = guard.Latch($"guided check {c.Key} (cheats)");
        }
        catch (Exception e)
        {
            return new Refusal(Line2, $"the guard's latch threw {e.GetType().Name}: {e.Message}");
        }
        if (!latched) return new Refusal(Line2, "the guard's latch returned false" + StatusHint(guard));
        var (status, problem) = ReadStatus(guard);
        if (problem is not null) return new Refusal(Line3, problem);
        if (!status!.Value.Protecting) return new Refusal(Line3, "the guard is not protecting after the latch: " + status.Value);
        after = status;
        return Proof(c, readLedger, game.Sha256, game.Kiln);
    }

    /// <summary>The rule for a joined start, on the server: the joining client's answer stands for lines 2 to 4.</summary>
    public static Refusal? Joined(CheckCase c, Attestation? a, DateTime now)
    {
        if (Static(c, ClientCheckSession.Joined, CheckOrigin.Remote) is { } r) return r;
        if (!c.Cheats) return null;
        if (a is null) return new Refusal(Line1, "the joining client's guard status did not come with the start request");
        if (a.Taken == default || (now - a.Taken).Duration() > AttestationDeadline)
            return new Refusal(Line1, $"the joining client's guard status is late (read {(now - a.Taken).TotalSeconds:0} s before the start; " +
                                      "more than 30 s, or the two clocks disagree)");
        if (a.Problem is { Length: > 0 } p) return new Refusal(Line2, "the joining client could not answer for its guard: " + p);
        if (!a.Latched) return new Refusal(Line2, "the joining client's latch returned false" + (a.Status is { } s0 ? $" ({s0.Reason})" : ""));
        if (a.Status is not { } s) return new Refusal(Line3, "the joining client's guard status after its latch did not arrive");
        if (!s.Protecting) return new Refusal(Line3, "the joining client's guard is not protecting after its latch: " + s);
        if (c.GuardProof) return null;
        return ProofRefusal(a.Proof, a.Sha256, a.Kiln);
    }

    /// <summary>Line 4 against this process's ledger: the newest guard-proof entry for this build and Kiln version.</summary>
    public static Refusal? Proof(CheckCase c, Func<LedgerRead> readLedger, string? sha256, string kiln, string? state = null)
    {
        if (c.GuardProof) return null;
        return ProofRefusal(state ?? ProofState(readLedger, sha256, kiln), sha256, kiln);
    }

    /// <summary>pass, fail, skip, none, unreadable or unknown (no sha256): the newest guard-proof verdict for the build.</summary>
    public static string ProofState(Func<LedgerRead> readLedger, string? sha256, string kiln)
    {
        if (string.IsNullOrEmpty(sha256)) return "unknown";
        LedgerRead r;
        try
        {
            r = readLedger();
        }
        catch (Exception)
        {
            return "unreadable";
        }
        if (r.State == LedgerReadState.Unreadable) return "unreadable";
        if (r.State == LedgerReadState.Missing) return "none";
        var newest = r.Document!.Entries.LastOrDefault(e =>
            e.Has(ClientCheckTags.GuardProof) && e.Has(ClientCheckTags.Cheats) && e.Game.Side == "client" && string.Equals(e.Game.Sha256, sha256, StringComparison.OrdinalIgnoreCase) &&
            e.Kiln == kiln);
        if (newest is { Verdict: "pass" } && !newest.Assertions.Any(a => a.What == CheckRunner.DroppedAssertion && a.Ok)) return "fail";
        return newest?.Verdict ?? "none";
    }

    private static Refusal? ProofRefusal(string proof, string? sha256, string? kiln)
    {
        var build = $"game build {Short(sha256)} and Kiln {kiln ?? "?"}";
        return proof switch
        {
            "pass" => null,
            "fail" => new Refusal(Line4, $"the newest guard-proof check on {build} failed: the guard is not shown working here"),
            "skip" => new Refusal(Line4, $"the newest guard-proof check on {build} was skipped: run it and pass it first"),
            "unreadable" => new Refusal(Line4, "the check ledger could not be read, so no guard-proof pass can be shown"),
            "unknown" => new Refusal(Line4, "this game build's sha256 is not known, so no guard-proof pass can be matched to it"),
            _ => new Refusal(Line4, $"no guard-proof check has run on {build}: run it and pass it first"),
        };
    }

    private static string Short(string? sha) => sha is { Length: > 12 } ? sha[..12] + "…" : sha ?? "?";

    private static string StatusHint(IAchievementGuard guard)
    {
        try
        {
            return guard.Status() is { } s ? $" ({s.Reason})" : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>A status read with its own deadline: (status, null) or (null, why it does not count).</summary>
    public static (GuardStatus? Status, string? Problem) ReadStatus(IAchievementGuard guard)
    {
        var sw = Stopwatch.StartNew();
        GuardStatus? s;
        try
        {
            s = guard.Status();
        }
        catch (Exception e)
        {
            return (null, $"reading the guard's status threw {e.GetType().Name}: {e.Message}");
        }
        if (sw.Elapsed > StatusDeadline) return (null, $"the guard's status arrived late ({sw.Elapsed.TotalMilliseconds:0} ms)");
        if (s is null) return (null, "the guard's status did not arrive");
        return (s, null);
    }

    /// <summary>A cheats check changes the character for good, so it runs only on a throwaway character: one whose
    /// name starts with this (ordinal, case-sensitive).</summary>
    internal const string ThrowawayPrefix = "KilnCheck";

    internal const string Line5 = "line 5";

    /// <summary>The runner's own question before a cheats check, each time, where the character's name cannot be read.</summary>
    internal const string ThrowawayQuestion =
        "This check changes the character for good. Run it only on a throwaway character named KilnCheck… that you will delete afterwards. " +
        "Is the character you are playing now such a character?";

    internal const string FormRead = "throwaway character: name starts with KilnCheck", FormAsked = "throwaway character: confirmed by the player";

    /// <summary>The character's name, or null when it cannot be read (a read that throws counts as that).</summary>
    public static string? ReadName(IRunnerEnv env, EntityId player, out string why)
    {
        try
        {
            return env.CharacterName(player, out why);
        }
        catch (Exception e)
        {
            why = $"reading it threw {e.GetType().Name}";
            return null;
        }
    }

    /// <summary>Line 5: the character of the player the check changes is a throwaway one. Its name is read on the
    /// server side now: a readable name must start with <see cref="ThrowawayPrefix"/> (a yes does not replace that);
    /// where it cannot be read, the player must have answered <see cref="ThrowawayQuestion"/> with yes for this start
    /// (<paramref name="confirmed"/>). <paramref name="form"/> says which held, for the entry.</summary>
    public static Refusal? Character(IRunnerEnv env, EntityId player, bool confirmed, out string? form)
    {
        form = null;
        var name = ReadName(env, player, out var why);
        if (name is not null)
        {
            if (!name.StartsWith(ThrowawayPrefix, StringComparison.Ordinal))
                return new Refusal(Line5, $"cheats checks run only on a throwaway character whose name starts with {ThrowawayPrefix} (this one is '{name}')");
            form = FormRead;
            return null;
        }
        if (!confirmed)
            return new Refusal(Line5, $"this character's name cannot be read here ({why}), and the player did not confirm that it is a throwaway " +
                                      $"character named {ThrowawayPrefix}…");
        form = FormAsked;
        return null;
    }

    /// <summary>A joining client's answer, built on that client for a start request.</summary>
    public static Attestation Attest(string key, IAchievementGuard? guard, Func<LedgerRead> readLedger, GameInfo game, DateTime now)
    {
        if (guard is null) return new Attestation { Taken = now, Problem = "the achievement guard is not there", Sha256 = game.Sha256, Kiln = game.Kiln };
        bool latched;
        try
        {
            latched = guard.Latch($"guided check {key} (cheats, joined)");
        }
        catch (Exception e)
        {
            return new Attestation { Taken = now, Problem = $"the latch threw {e.GetType().Name}", Sha256 = game.Sha256, Kiln = game.Kiln };
        }
        var (status, problem) = latched ? ReadStatus(guard) : (null, null);
        return new Attestation
        {
            Latched = latched, Status = status, Problem = latched ? problem : null, Proof = ProofState(readLedger, game.Sha256, game.Kiln),
            Sha256 = game.Sha256, Kiln = game.Kiln, Taken = now,
        };
    }
}
