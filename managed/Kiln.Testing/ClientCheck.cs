// SPDX-License-Identifier: MIT
// Guided client checks: a check is an async method of a mod, marked [ClientCheck], that sets a scene, tells the player
// what to do and look for, asserts what code can read by itself, and waits for the player's verdict (pass, fail with a
// note, or skip). The runner (ClientChecks.cs) lists the checks of every mod that registered them, runs them on the tick
// thread and appends each verdict to kiln/state/client-checks.json. Docs: docs/kiln/modding/testing.md, "Guided checks".
namespace Kiln.Testing;

/// <summary>Marks a guided client check: an async method of a mod taking one <see cref="CheckContext"/> and returning
/// <see cref="Task"/> or <see cref="ValueTask"/>. Static, or an instance method of the mod's <see cref="Mod"/> class (run
/// on the live instance), or of another class with a parameterless constructor.</summary>
/// <example><code>
/// [ClientCheck("blocks.frost-flash", "Frost flash on hit", Area = "Blocks", Since = "0.2.0-dev.26",
///              Tags = [ClientCheckTags.Visual], Needs = ClientCheckNeeds.Hosted, Order = 10)]
/// public async Task Frost_flash(CheckContext c)
/// {
///     // set the scene with the services the mod already uses (players, items, spawn, time)
///     c.Instruct("Hit the dummy three times. Does a blue flash appear on each hit?");
///     await c.Until(() => hits >= 3, TimeSpan.FromMinutes(2), "three hits");
///     c.Expect(hits == 3, "three hits counted", $"counted {hits}");
///     await c.Verdict();
/// }
/// </code></example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ClientCheckAttribute : Attribute
{
    /// <summary>Marks an async method <c>Task M(CheckContext c)</c> as a guided check with this id and title.</summary>
    /// <param name="id">Unique within the mod: lower-case letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, at most 64
    /// characters, by custom <c>area.slug</c>.</param>
    /// <param name="title">What the list shows.</param>
    public ClientCheckAttribute(string id, string title)
    {
        Id = id;
        Title = title;
    }

    /// <summary>The check's id, unique within the mod (<c>area.slug</c>).</summary>
    public string Id { get; }

    /// <summary>What the check list shows.</summary>
    public string Title { get; }

    /// <summary>The group the list shows the check under (default: the mod's name).</summary>
    public string? Area { get; init; }

    /// <summary>The Kiln or mod release that added what the check covers, e.g. <c>0.2.0-dev.26</c>.</summary>
    public string? Since { get; init; }

    /// <summary>Tags: the names in <see cref="ClientCheckTags"/>, or free ones for grouping. <see cref="ClientCheckTags.Cheats"/>
    /// changes how the runner starts the check.</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>Which session the check needs; in another one it is refused with the reason shown.</summary>
    public ClientCheckNeeds Needs { get; init; } = ClientCheckNeeds.Any;

    /// <summary>Lower runs first in "run all"; equal orders keep the mod's load order, then class, then declaration
    /// order.</summary>
    public int Order { get; init; }

    /// <summary>A sentence the runner shows before the body runs; the body starts only after the player answers yes
    /// to it with its own button (never the pass key, never a default). No yes: the check does not start and has no
    /// entry. A check tagged <see cref="ClientCheckTags.GuardProof"/> must have one.</summary>
    public string? Confirm { get; init; }
}

/// <summary>Tag names of <see cref="ClientCheckAttribute.Tags"/>.</summary>
public static class ClientCheckTags
{
    /// <summary>The check uses level, experience, skill-point or knowledge commands (or anything else that changes what
    /// the game may report to Steam as an achievement). The runner starts it only while the achievement guard of the
    /// player's own game client is latched on and protecting; see docs/kiln/modding/testing.md, "Checks tagged cheats".</summary>
    public const string Cheats = "cheats";

    /// <summary>The player judges something they see.</summary>
    public const string Visual = "visual";

    /// <summary>The player judges something they hear.</summary>
    public const string Audio = "audio";

    /// <summary>Needs a second player. Never runs together with <see cref="Cheats"/>.</summary>
    public const string TwoPlayers = "two-players";

    /// <summary>The check that shows the achievement guard working on this game build and Kiln version. Until its newest
    /// run on a build passed, every other <see cref="Cheats"/> check is refused there. Needs
    /// <see cref="ClientCheckAttribute.Confirm"/>.</summary>
    public const string GuardProof = "guard-proof";

    /// <summary>Needs a Windows PC.</summary>
    public const string Windows = "windows";

    /// <summary>Needs a Steam Deck.</summary>
    public const string SteamDeck = "steam-deck";

    /// <summary>Done (partly) in the Kiln launcher, outside the game.</summary>
    public const string Launcher = "launcher";
}

/// <summary>The session a check needs (<see cref="ClientCheckAttribute.Needs"/>).</summary>
public enum ClientCheckNeeds
{
    /// <summary>Any session: hosted, joined or a dedicated server driven through the control folder.</summary>
    Any = 0,

    /// <summary>A world this game client hosts (single player or co-op host).</summary>
    Hosted = 1,

    /// <summary>A game client joined to a server (the body runs on the server).</summary>
    Joined = 2,
}

/// <summary>Where a check runs, as written into its ledger entry.</summary>
public enum ClientCheckSession
{
    /// <summary>This game client hosts the world: the body and the screen are in one process.</summary>
    Hosted = 0,

    /// <summary>The body runs on the server; the instruction, the keys and the verdict are on the joining client.</summary>
    Joined = 1,

    /// <summary>A dedicated server driven through <c>kiln/control/checks/</c>, with no screen.</summary>
    Server = 2,
}

/// <summary>The player's verdict on a check.</summary>
public enum ClientCheckVerdict
{
    /// <summary>What the check asked for happened.</summary>
    Pass = 0,
    /// <summary>It did not; the player adds one line saying what was seen.</summary>
    Fail = 1,
    /// <summary>Not judged this time (the scene could not be set, or the player passed on it).</summary>
    Skip = 2,
}

/// <summary>What a running check gets. Every member is for the tick thread, where the body runs; awaits on it come back
/// there.</summary>
public sealed class CheckContext
{
    private readonly ICheckRun _run;

    internal CheckContext(ICheckRun run, ModContext mod, string id, string title, ClientCheckSession session, EntityId player,
        CancellationToken cancellation)
    {
        _run = run;
        Mod = mod;
        Id = id;
        Title = title;
        Session = session;
        Player = player;
        Cancellation = cancellation;
    }

    /// <summary>The check's id.</summary>
    public string Id { get; }

    /// <summary>The check's title.</summary>
    public string Title { get; }

    /// <summary>The context of the mod the check belongs to.</summary>
    public ModContext Mod { get; }

    /// <summary>The mod's log (<c>kiln/logs/kiln.log</c>). The lines written while the check runs go into its entry.</summary>
    public Logger Log => Mod.Log;

    /// <summary>Where the check runs.</summary>
    public ClientCheckSession Session { get; }

    /// <summary>The entity of the player who runs the check: the local player when hosted, the joining player's entity
    /// on the server when joined, <see cref="EntityId.None"/> from the control folder or before the player spawned.</summary>
    public EntityId Player { get; }

    /// <summary>Signalled when the check ends early: the player stopped the run, gave a verdict before the body asked for
    /// one, or the mod unloads. Awaits on this context then throw <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>Shows <paramref name="text"/> as the current instruction: in the list and on the HUD line, until the
    /// next instruction or the end of the check.</summary>
    public void Instruct(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _run.Instruct(text);
    }

    /// <summary>Records an automatic assertion next to the player's verdict. It does not throw and does not change the
    /// verdict.</summary>
    /// <param name="condition">The result.</param>
    /// <param name="what">What was asserted, e.g. "three hits counted".</param>
    /// <param name="detail">What was seen, e.g. "counted 2".</param>
    /// <returns><paramref name="condition"/>.</returns>
    public bool Expect(bool condition, string what, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(what);
        _run.Expect(condition, what, detail);
        return condition;
    }

    /// <summary>Waits for the player's verdict: pass, fail (with a one-line note) or skip. The first verdict is the
    /// check's; a later call returns it at once. A body that returns without calling this is asked for one by the
    /// runner.</summary>
    public Task<ClientCheckVerdict> Verdict() => _run.Verdict();

    /// <summary>Registers <paramref name="cleanup"/> to run when the check ends, however it ends (verdict, stop,
    /// exception, unload): on the tick thread, last registered first, each on its own exception guard.</summary>
    public void OnEnd(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _run.OnEnd(() =>
        {
            cleanup();
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc cref="OnEnd(Action)"/>
    public void OnEnd(Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _run.OnEnd(cleanup);
    }

    /// <summary>Continues on the tick thread after <paramref name="count"/> server-world steps.</summary>
    public async Task Ticks(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Cancellation.ThrowIfCancellationRequested();
            await Mod.Scheduler.NextTick();
        }
        Cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>Checks <paramref name="condition"/> once per step until it holds or <paramref name="timeout"/> passes.
    /// A timeout records a failed assertion named <paramref name="what"/> and returns false; it does not end the
    /// check.</summary>
    public async Task<bool> Until(Func<bool> condition, TimeSpan timeout, string what)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrEmpty(what);
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                _run.Expect(false, what, $"timed out after {timeout.TotalSeconds:0.#} s");
                return false;
            }
            await Ticks();
        }
        return true;
    }

    /// <summary>Waits wall time, coming back on the tick thread.</summary>
    public async Task Delay(TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until) await Ticks();
    }
}

/// <summary>Registers a mod's guided checks with the runner.</summary>
public static class ClientChecks
{
    /// <summary>Lists the <see cref="ClientCheckAttribute"/> methods of <paramref name="mod"/>'s assemblies with the
    /// runner. Call it from <see cref="Mod.OnLoad"/>; the checks leave the list when the mod unloads. A second call for
    /// the same instance does nothing. Tick thread.</summary>
    /// <returns>How many checks were found (with a shape problem or not: those are listed as refused).</returns>
    /// <exception cref="ArgumentException"><paramref name="mod"/> has no context yet (call it from OnLoad or later).</exception>
    public static int Register(Mod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (mod.Context is null) throw new ArgumentException("the mod has no context yet: call ClientChecks.Register from OnLoad", nameof(mod));
        return CheckRegistry.Register(mod);
    }
}

/// <summary>What a running check reports to the runner (the runner's side of <see cref="CheckContext"/>).</summary>
internal interface ICheckRun
{
    void Instruct(string text);
    void Expect(bool condition, string what, string? detail);
    Task<ClientCheckVerdict> Verdict();
    void OnEnd(Func<Task> cleanup);
}
