// SPDX-License-Identifier: MIT
// NoClip: Kiln's own per-player switch for PlayerRules.NoClip, its /noclip command, what the runtime knows about a
// player's NoClip, and the pure decisions (which requests are used up, whether a place is open space).
namespace Kiln;

/// <summary>
/// NoClip: a player's character flies through terrain, blocks and props (<see cref="PlayerRules.NoClip"/>). This class
/// is the switch Kiln itself keeps for a player (what <c>/noclip</c> uses) and the place to ask how it stands. A mod
/// can also put <see cref="PlayerRules.NoClip"/> into a rule set of its own.
/// </summary>
/// <remarks>
/// Server side; tick thread. Off on every server until its <c>rules.json</c> sets <c>allowNoClip</c>, and only for a
/// player whose game runs Kiln with the same hook (the movement is predicted there). It is a tool for admins and for
/// debugging: it grants no invulnerability, and the Shroud, water and enemies act on the character as always.
/// <para>What ends it: the switch (or the rule set) going off; the player's death; a teleport of the player (fast
/// travel, a bed, a command, a mod); the player leaving; the world closing. After a death or a teleport it has to be
/// asked for again. When it ends while the character is inside something, Kiln first moves the character back to the
/// last place where it was in open space, and only then lets collision return: the game does not push a character out
/// of solid ground.</para>
/// </remarks>
public static class NoClip
{
    /// <summary>The capability: this game build has the collision hook and character flight's hooks.</summary>
    public const string Capability = "rules.noclip";

    /// <summary>The command's permission node (level Admin; a hosted world's host and the console have it).</summary>
    public const string PermissionNode = "kiln.command.noclip";

    private const string Label = "noclip";
    private static readonly Dictionary<EntityId, RuleSet> Sets = new();
    // players whose switch is being turned on right now: applying the rule set posts events, and a handler that calls
    // Set for the same player must not make a second set
    private static readonly Dictionary<EntityId, bool> Turning = new();  // value: an "off" came in meanwhile

    /// <summary>True when this build can do it (the capability <see cref="Capability"/>). The server's
    /// <c>rules.json</c> and the player's game still decide for each player (<see cref="Set"/> says why not).</summary>
    public static bool Available => Capabilities.Is(Capability);

    /// <summary>Turns Kiln's NoClip switch for <paramref name="player"/> on or off. On again after a death or a teleport
    /// asks anew. Off moves the character back to its last free position first when it is inside something (the result
    /// says so; <see cref="PlayerNoClipEndedEvent"/> follows when collision is back).</summary>
    /// <returns><see cref="NoClipResult.Ok"/> false with a reason when it is refused: the server's <c>rules.json</c>
    /// does not allow it, the player's game has no Kiln or has not confirmed the hook, or another rule set turns NoClip
    /// off for the player.</returns>
    /// <exception cref="ArgumentException"><paramref name="player"/> is none.</exception>
    /// <exception cref="KilnUnavailableException"><see cref="Capability"/> is off (turning it off never throws this).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static NoClipResult Set(EntityId player, bool on)
    {
        Game.AssertTickThread();
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        var name = NameOf(player);
        Sets.TryGetValue(player, out var own);
        if (own is { Active: false })
        {
            Sets.Remove(player);
            own = null;
        }
        if (!on)
        {
            if (Turning.ContainsKey(player))
            {
                // asked from a handler while the switch is being turned on: it is ended when that is done
                Turning[player] = true;
                return new(true, false, null, $"NoClip off for {name}.");
            }
            if (own is null)
            {
                var still = RulesRuntime.Fresh(player).NoClip;
                return new(true, still, null, still ? $"NoClip for {name} comes from another rule set, not from this switch." : $"NoClip is not on for {name}.");
            }
            Sets.Remove(player);
            own.Dispose();  // resolves at once: the runtime starts the move back when the character is inside something
            var after = RulesRuntime.Fresh(player).NoClip;
            var moving = RulesRuntime.NoClipOf(player).Restoring;
            if (!after && !moving) Tell(player, "NoClip is off: collision is back.");
            return new(true, after, null, after
                ? $"NoClip switch off for {name}, but another rule set keeps it on."
                : moving
                    ? $"NoClip off for {name}: the character was inside something and is being put back at its last free position."
                    : $"NoClip off for {name}.");
        }
        if (!Available) throw new KilnUnavailableException(Capability, Capabilities.Why(Capability));
        if (!RulesRuntime.Limits.AllowNoClip)
            return new(false, false, "policy", $"NoClip stays off for {name}: this server's rules.json does not allow it (allowNoClip).");
        var client = Rules.ClientOf(player);
        if (client is not (RulesClient.Kiln or RulesClient.Host or RulesClient.Simulated))
            return new(false, false, "client", client switch
            {
                RulesClient.Pending => $"{name}'s game is still joining; try again in a moment.",
                RulesClient.KilnWithoutRules => $"{name}'s Kiln is too old for NoClip (it has no {Rules.ChannelName} channel).",
                _ => $"{name}'s game has no Kiln: NoClip moves the character in the player's game too, so it needs Kiln there.",
            });
        if (!RulesRuntime.CanPredictNoClip(player))
            return new(false, false, "client", $"NoClip stays off for {name}: their game has not confirmed the collision hook (an older Kiln, or a game build without it).");
        if (!Turning.TryAdd(player, false))
            return new(true, RulesRuntime.Fresh(player).NoClip, null, $"NoClip is being turned on for {name}.");
        var offAsked = false;
        // Kiln's rule set, whoever asked: it outlives the mod or tool that turned it on
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            if (own is null) Sets[player] = own = Rules.Apply(player, new PlayerRules { NoClip = true }, label: Label);
            else own.Update(own.Rules);  // asks anew after a death or a teleport
        }
        finally
        {
            ModContext.CurrentMod = outer;
            Turning.Remove(player, out offAsked);
        }
        if (offAsked)
        {
            // a handler of this very change asked for off: it wins, being the later word
            Sets.Remove(player);
            own?.Dispose();
            return new(true, RulesRuntime.Fresh(player).NoClip, null, $"NoClip for {name} was turned off again while it was being turned on.");
        }
        if (!RulesRuntime.Fresh(player).NoClip)
        {
            Sets.Remove(player);
            own.Dispose();
            return new(false, false, "denied", $"NoClip stays off for {name}: another rule set turns it off for them.");
        }
        Tell(player, "NoClip is on: you fly through terrain and walls. Jump climbs, sneak sinks, sprint is faster. It ends when you die or teleport; /noclip off ends it.");
        return new(true, true, null, $"NoClip on for {name}.");
    }

    /// <summary>How NoClip stands for <paramref name="player"/> now. Never throws for a missing capability: without
    /// the hook nothing is on.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    public static NoClipState Of(EntityId player)
    {
        Game.AssertTickThread();
        var on = Rules.Of(player).NoClip;
        RulesNative.NoClipOf(player.Value, out var n);
        var (free, restoring) = RulesRuntime.NoClipOf(player);
        return new NoClipState(player, on, n.Active != 0, n.Skipped, free, restoring);
    }

    // The runtime's word that NoClip ended for a player and collision is back.
    internal static void Ended(EntityId player, NoClipEnd reason, WorldPosition? restoredTo)
    {
        RulesRuntime.Log(LogLevel.Info, $"player {player}: NoClip ended ({reason})" + (restoredTo is { } p ? $", back at {p}" : ""));
        if (reason == NoClipEnd.Death) Tell(player, "NoClip ended because you died. Turn it on again to go on.");
        else if (reason == NoClipEnd.Teleport) Tell(player, "NoClip ended because you were teleported. Turn it on again to go on.");
        else if (restoredTo is not null) Tell(player, "NoClip is off. You were inside something, so you are back at the last place where you were free.");
        if (Events.HasSubscribers<PlayerNoClipEndedEvent>()) Events.Post(new PlayerNoClipEndedEvent(player, reason, restoredTo));
    }

    internal static void Forget()
    {
        Sets.Clear();
        Turning.Clear();
    }

    private static string NameOf(EntityId e)
    {
        try
        {
            return Players.Find(e)?.Name is { Length: > 0 } n ? n : $"player {e}";
        }
        catch (Exception)
        {
            return $"player {e}";
        }
    }

    private static void Tell(EntityId player, string text)
    {
        try
        {
            Chat.Tell(player, text);
        }
        catch (Exception)
        {
            // no chat (an offline world): the command's reply says it
        }
    }

    // ---------------------------------------------------------------------------------------- /noclip --------

    private static int _registered;

    /// <summary>Adds <c>/noclip</c> (once; Kiln's command, node <see cref="PermissionNode"/>, level Admin).</summary>
    internal static void RegisterCommand()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        Commands.Register(Commands.Literal("noclip").Requires(CommandPermission.Admin, PermissionNode)
            .Describe("NoClip: fly through terrain and walls (the server's rules.json allows it with allowNoClip)")
            .Executes(c => c.Reply(Run(c, Self(c), null)))
            .Then(Commands.Literal("on").Executes(c => c.Reply(Run(c, Self(c), true)))
                .Then(Commands.Argument("player", Arg.Player()).Executes(c => c.Reply(Run(c, c.Get<Player>("player").Entity, true)))))
            .Then(Commands.Literal("off").Executes(c => c.Reply(Run(c, Self(c), false)))
                .Then(Commands.Argument("player", Arg.Player()).Executes(c => c.Reply(Run(c, c.Get<Player>("player").Entity, false)))))
            .Then(Commands.Literal("status").Executes(c => c.Reply(Status()))),
            CommandPermission.Admin);
    }

    private static EntityId Self(CommandContext c) =>
        c.Source.IsPlayer && !c.Source.Player.IsNone ? c.Source.Player : throw new CommandException("name a player: /noclip on|off <player>");

    private static string Run(CommandContext c, EntityId player, bool? on)
    {
        NoClipResult r;
        try
        {
            r = Set(player, on ?? !Rules.Of(player).NoClip);
        }
        catch (KilnUnavailableException e)
        {
            c.Fail($"NoClip is not available here: {e.Reason ?? "this game build has no collision hook"}");
            return "";
        }
        if (!r.Ok) c.Fail(r.Message);
        return r.Message;
    }

    /// <summary>One line: whether it works here, whether the server allows it, and who has it.</summary>
    internal static string Status()
    {
        var who = new List<string>();
        try
        {
            foreach (var p in Players.All)
                if (Rules.Of(p.Entity).NoClip) who.Add(p.Name);
        }
        catch (Exception)
        {
            // no kiln.players
        }
        return (Available ? "NoClip works on this build" : "NoClip does not work here (" + Capabilities.Why(Capability) + ")") +
               (RulesRuntime.Limits.AllowNoClip ? ", rules.json allows it" : ", rules.json does not allow it (allowNoClip)") +
               (who.Count > 0 ? "; on for " + string.Join(", ", who) : "; on for nobody") + ".";
    }
}

/// <summary>NoClip for one player (<see cref="NoClip.Of"/>).</summary>
/// <param name="Player">The player.</param>
/// <param name="On">The player's rules carry NoClip (<see cref="EffectiveRules.NoClip"/>).</param>
/// <param name="Active">The character is moving without collision right now: its last collision step was skipped. On
/// without Active: it is dead, gliding, on a ladder or a wall, or its game has not caught up yet.</param>
/// <param name="StepsSkipped">Collision steps skipped for the character since the world started.</param>
/// <param name="LastFree">Where the character last was in open space while NoClip was on (where it started, at first).
/// Null when NoClip is off and no move back is in flight.</param>
/// <param name="Restoring">NoClip was turned off inside something: the rules no longer carry it (<paramref name="On"/>
/// is false), but the character is being moved to <paramref name="LastFree"/> and still has no collision. Collision
/// returns when it is in open space; a move that fails is tried again.</param>
public sealed record NoClipState(EntityId Player, bool On, bool Active, ulong StepsSkipped, WorldPosition? LastFree, bool Restoring);

/// <summary>What <see cref="NoClip.Set"/> did.</summary>
/// <param name="Ok">False when turning it on was refused.</param>
/// <param name="On">Whether the player's rules carry NoClip now.</param>
/// <param name="Reason">Null when <paramref name="Ok"/>; else <c>policy</c> (the server's rules.json), <c>client</c> (the
/// player's game) or <c>denied</c> (another rule set turns it off).</param>
/// <param name="Message">One line for the one who asked.</param>
public sealed record NoClipResult(bool Ok, bool On, string? Reason, string Message) : IKilnResult;

/// <summary>Why NoClip ended for a player (<see cref="PlayerNoClipEndedEvent"/>).</summary>
public enum NoClipEnd
{
    /// <summary>The player's rules stopped carrying it: the switch or a rule set went off, or the server's limits changed.</summary>
    RuleEnded = 0,

    /// <summary>The player died.</summary>
    Death = 1,

    /// <summary>The player was teleported (fast travel, a bed, a command, a mod).</summary>
    Teleport = 2,
}

/// <summary>NoClip ended for a player and collision is back. Posted on the tick thread; not cancelable. (That NoClip
/// started shows as <see cref="PlayerRulesChangedEvent"/>.)</summary>
public sealed class PlayerNoClipEndedEvent : KilnEvent
{
    internal PlayerNoClipEndedEvent(EntityId player, NoClipEnd reason, WorldPosition? restoredTo)
    {
        Player = player;
        Reason = reason;
        RestoredTo = restoredTo;
    }

    /// <summary>The player.</summary>
    public EntityId Player { get; }

    /// <summary>Why it ended.</summary>
    public NoClipEnd Reason { get; }

    /// <summary>Where Kiln put the character before collision returned, when it was inside something; else null.</summary>
    public WorldPosition? RestoredTo { get; }

    /// <inheritdoc/>
    public override string ToString() => $"NoClip of {Player} ended: {Reason}" + (RestoredTo is { } p ? $", back at {p}" : "");
}

/// <summary>Whether a character's place is open space, as NoClip judges it.</summary>
internal enum NoClipSpace
{
    /// <summary>The cells at the character's knees, waist and head are empty.</summary>
    Free,

    /// <summary>One of them holds matter: terrain or a block.</summary>
    Inside,

    /// <summary>The voxels there could not be read (not loaded, outside the world): not known to be free.</summary>
    Unknown,
}

/// <summary>NoClip's decisions (pure: unit-tested without a game).</summary>
internal static class NoClipMath
{
    /// <summary>The heights above the character's position (its feet) whose cells must be empty. The lowest is half a
    /// metre up: a character that stands on the ground has matter in the cell at its feet, and the game lifts one whose
    /// feet are up to about a metre deep (seen in a lab: 1.07 m). Deeper than that it falls through the ground.</summary>
    internal static readonly double[] Body = [0.5, 1.0, 1.7];

    /// <summary><paramref name="rules"/> as they count for a player: a NoClip request that a death or a teleport used
    /// up (<paramref name="spent"/> holds the set's id and version) reads as not asked. An explicit false still counts.</summary>
    public static PlayerRules WithoutSpent(PlayerRules rules, long id, int version, IReadOnlySet<(long Id, int Version)>? spent) =>
        rules.NoClip == true && spent is not null && spent.Contains((id, version)) ? rules with { NoClip = null } : rules;

    /// <summary>What kind of place <paramref name="at"/> is, from the voxels at <see cref="Body"/>.</summary>
    public static NoClipSpace Space(WorldPosition at, Func<WorldPosition, Voxel?> voxel)
    {
        var unknown = false;
        foreach (var dy in Body)
        {
            if (voxel(at with { Y = at.Y + dy }) is not { } v) unknown = true;
            else if (!v.IsAir) return NoClipSpace.Inside;
        }
        return unknown ? NoClipSpace.Unknown : NoClipSpace.Free;
    }

    /// <summary>A number for the cells <see cref="Space"/> looks at: each one's material and fill, or that it is not
    /// loaded. Equal numbers: nothing was built, dug or (un)loaded there in between.</summary>
    public static ulong Cells(WorldPosition at, Func<WorldPosition, Voxel?> voxel)
    {
        ulong all = 0;
        foreach (var dy in Body) all = all << 17 | (voxel(at with { Y = at.Y + dy }) is { } v ? v.Raw : 0x10000u);
        return all;
    }
}
