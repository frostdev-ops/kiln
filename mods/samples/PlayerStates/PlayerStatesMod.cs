// SPDX-License-Identifier: MIT
// Sample: player states. assets/player-states.json adds four states to the player's animation graph (kiln.playeranim merges
// them at boot, on the server and on every player's game); this code makes the game's sequences name them and puts those
// copies into the sample sword's slots, and holds the guided client checks (Kiln.Testing). README.md says what each file is.
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Testing;

namespace PlayerStatesSample;

public sealed class PlayerStatesMod : Mod
{
    public const string ModId = "player-states-sample";
    public const string Sword = "Kiln_Sample_RiposteSword";
    private const string Area = "Player animations";
    private const string Since = "0.2.0-dev.25";

    /// <summary>Which of the sword's sequences gets a copy that plays which state: the game's sequence (by name), the state,
    /// the copy's GUID. The game's sword attack names Attack_Sword_Shield_01, its heavy attack Attack_Heavy, its block Block_FullBody;
    /// each state of ours is placed like that state (assets/player-states.json), so the copy only has to name ours instead.</summary>
    public static readonly (string Game, string State, Guid Copy)[] Moves =
    [
        ("Attack_Player_1H_Single_LightChain1", "swing", Guid.Parse("4c0d7e21-9a3b-4f6e-8d15-2b7c9e0a6f41")),
        ("Attack_Player_Melee_Heavy_Sword_Diagonal", "heavy", Guid.Parse("4c0d7e21-9a3b-4f6e-8d15-2b7c9e0a6f42")),
        ("Ability_Block", "guard", Guid.Parse("4c0d7e21-9a3b-4f6e-8d15-2b7c9e0a6f43")),
    ];

    /// <summary>The staff cast the overlay check plays: a copy of a game cast whose second overlay names the state "cast".</summary>
    public const string CastOf = "Attack_Player_Staff_Overlay_ChainLightning";
    public static readonly Guid CastCopy = Guid.Parse("4c0d7e21-9a3b-4f6e-8d15-2b7c9e0a6f44");

    private bool _castNamesOurs;

    public override void OnLoad()
    {
        foreach (var (game, state, copy) in Moves)
        {
            var c = new GameDataClone { Guid = copy, Name = $"{ModId}.{state}", Base = game };
            c.Fields["events[SetAnimationEvent#0].animationName"] = PlayerAnimStates.GuidOf(ModId, state).ToString();
            Define(c);
        }
        var cast = new GameDataClone { Guid = CastCopy, Name = $"{ModId}.cast", Base = CastOf };
        cast.Fields["events[SetAnimationOverlayEvent#1].overlayName"] = PlayerAnimStates.GuidOf(ModId, "cast").ToString();
        Define(cast);
        Content.OnReady(_ => PutMoves());
        var n = ClientChecks.Register(this);
        foreach (var s in new[] { "swing", "heavy", "guard", "cast" })
        {
            var info = PlayerAnimStates.Find(ModId, s);
            Log.Info($"state {s}: {(info is null ? "not known (no service on this side)" : $"{info.Status}{(string.IsNullOrEmpty(info.Reason) ? "" : ": " + info.Reason)}")}");
        }
        Log.Info($"player graph {PlayerAnimStates.Graph}, hash {PlayerAnimStates.GraphHash}; {n} guided check(s) registered");
    }

    private void Define(GameDataClone c)
    {
        try
        {
            var d = Content.Sequences.Define(c);
            Log.Info($"sequence {d.Name}: a copy of {d.Base}, {d.State}{(d.Error is null ? "" : ": " + d.Error)}");
        }
        catch (ArgumentException e) { Log.Warn($"sequence {c.Name}: refused: {e.Message}"); }
    }

    /// <summary>Runs each time the game's data has been applied: the sword's slots that hold the game's sequences get our copies.</summary>
    private void PutMoves()
    {
        if (Content.ItemData.Get(Sword) is not { } sword)
        {
            Log.Warn($"{Sword} is not in the game's data, so it keeps the game's moves");
            return;
        }
        foreach (var (game, state, copy) in Moves)
        {
            var from = Content.Sequences.Get(game)?.Guid;
            var swapped = 0;
            for (var slot = 0; slot < 32 && from is { } f; slot++)
            {
                Guid now;
                try { now = sword.Get($"sequences[{slot}].sequence").Guid; }
                catch (KeyNotFoundException) { break; }
                if (now != f && now != copy) continue;
                if (now != copy) sword.Set($"sequences[{slot}].sequence", copy.ToString());
                swapped++;
            }
            Log.Info($"{Sword}: {swapped} slot(s) of {game} play the state {state}");
        }
        // kiln.gamedata of this build may refuse an overlayName in a copy (the OverlayId check): then the copy plays the game's own shoot
        var castCopy = Content.Sequences.Get(CastCopy);
        _castNamesOurs = castCopy is not null && castCopy.Get("events[SetAnimationOverlayEvent#1].overlayName").Guid == PlayerAnimStates.GuidOf(ModId, "cast");
        if (!_castNamesOurs) Log.Warn("the staff cast's copy does not name the state cast on this build: the overlay check will say so");
    }

    // ------------------------------------------------------------------------------------------------ guided client checks

    /// <summary>The state id the server's graph input holds for <paramref name="player"/> (ServerAnimationGraphInput+0x22c), 0 when unknown.</summary>
    private static uint StateOf(EntityId player)
    {
        var b = player.IsNone ? null : World.ReadBytes(player, "ServerAnimationGraphInput");
        return b is { Length: >= 0x230 } ? BitConverter.ToUInt32(b, 0x22c) : 0;
    }

    private static bool Live(string state) => PlayerAnimStates.Find(ModId, state)?.Status == PlayerAnimStateStatus.Live;

    private static uint IdOf(string state) => PlayerAnimStates.IdOf(PlayerAnimStates.GuidOf(ModId, state));

    private static async Task GiveSword(CheckContext c)
    {
        if (c.Player.IsNone) return;
        var r = await Items.Give(c.Player, Sword, 1);
        c.Log.Info($"{c.Id}: gave {Sword}: {r}");
    }

    [ClientCheck("anim.state-plays", "A mod's state plays on your character", Area = Area, Since = Since, Tags = [ClientCheckTags.Visual], Order = 10)]
    public async Task StatePlays(CheckContext c)
    {
        await GiveSword(c);
        c.Expect(PlayerAnimStates.Graph == PlayerAnimGraphState.Merged, "the merged player graph is served", $"{PlayerAnimStates.Graph}, hash {PlayerAnimStates.GraphHash}");
        c.Expect(Live("swing"), "the state swing is live", PlayerAnimStates.Find(ModId, "swing")?.Reason);
        c.Instruct("Put the Kiln Sample Riposte Sword in your hand and attack three times. Does your character swing with the sample's own motion "
                   + "(the sword goes up over the left shoulder), not the game's slash?");
        await c.Until(() => StateOf(c.Player) == IdOf("swing"), TimeSpan.FromMinutes(3), "the server's graph input held the state's id while the attack ran");
        await c.Verdict();
    }

    [ClientCheck("anim.state-block", "A mod's block pose", Area = Area, Since = Since, Tags = [ClientCheckTags.Visual], Order = 11)]
    public async Task StateBlock(CheckContext c)
    {
        await GiveSword(c);
        c.Expect(Live("guard"), "the state guard (like Block_FullBody) is live", PlayerAnimStates.Find(ModId, "guard")?.Reason);
        c.Instruct("With the Riposte Sword in hand, hold block and walk. Is the guard pose the sample's (the sword held flat above the head), "
                   + "and do the legs still walk?");
        await c.Until(() => StateOf(c.Player) == IdOf("guard"), TimeSpan.FromMinutes(3), "the guard's id was entered");
        await c.Verdict();
    }

    [ClientCheck("anim.second-player-sees", "Another player sees the mod's state", Area = Area, Since = Since,
                 Tags = [ClientCheckTags.TwoPlayers, ClientCheckTags.Visual], Needs = ClientCheckNeeds.Joined, Order = 12)]
    public async Task SecondPlayerSees(CheckContext c)
    {
        var own = PlayerAnimStates.GraphHash;
        var peers = Network.Peers.Where(p => !p.IsServer).ToList();
        c.Expect(peers.Count >= 2, "two players are connected", $"{peers.Count} peer(s)");
        foreach (var p in peers)
            c.Expect(p.Properties.TryGetValue("kiln.playeranim", out var h) && h == own, $"{p.Name}'s handshake carried the server's graph hash",
                     p.Properties.TryGetValue("kiln.playeranim", out var hh) ? $"{hh} against {own}" : "no kiln.playeranim in its handshake (a build without the join check)");
        c.Instruct("Player A: take the Riposte Sword and attack. Player B: watch A attack. Do you see A's own swing (over the left shoulder), in time with the hit?");
        await c.Verdict();
    }

    [ClientCheck("anim.vanilla-untouched", "The game's own moves look as before", Area = Area, Since = Since, Tags = [ClientCheckTags.Visual], Order = 13)]
    public async Task VanillaUntouched(CheckContext c)
    {
        c.Expect(PlayerAnimStates.Graph == PlayerAnimGraphState.Merged, "the merged player graph is served", PlayerAnimStates.Graph.ToString());
        if (!c.Player.IsNone)
            foreach (var item in new[] { "Weapon_T1_1H_SwordEpic_01", "Weapon_T1_2H_Staff_01", "Tool_Glider_T1" })
                c.Log.Info($"{c.Id}: gave {item}: {await Items.Give(c.Player, item, 1)}");
        var ours = new[] { "swing", "heavy", "guard", "cast" }.Select(IdOf).ToHashSet();
        c.Instruct("With the game's own Bronze Sword: walk and sprint, attack three times, block; glide from a ledge; cast the staff once. "
                   + "Does everything look as it does without mods? Answer when you have done all of it.");
        // the player's verdict ends the check: until then, watch the graph input for a state of the sample
        var verdict = c.Verdict();
        var entered = 0u;
        while (!verdict.IsCompleted)
        {
            var now = StateOf(c.Player);
            if (ours.Contains(now)) entered = now;
            await c.Ticks();
        }
        c.Expect(entered == 0, "no state of the sample was entered during the check", entered == 0 ? null : $"0x{entered:x8} was");
        await verdict;
    }

    [ClientCheck("anim.cast-overlay", "A mod's cast motion", Area = Area, Since = Since, Tags = [ClientCheckTags.Visual], Order = 14)]
    public async Task CastOverlay(CheckContext c)
    {
        c.Expect(Live("cast"), "the overlay state cast is live", PlayerAnimStates.Find(ModId, "cast")?.Reason);
        c.Expect(_castNamesOurs, "the cast's copy names the state (its overlayName)", _castNamesOurs ? null : "kiln.gamedata of this build kept the game's overlay: the copy plays the game's shoot");
        if (!c.Player.IsNone) await Items.Give(c.Player, "Weapon_T1_2H_Staff_01", 1);
        c.Instruct("Take the staff in hand and walk. The check casts the sample's cast on your character twice, a few seconds apart. "
                   + "Do the arms play the sample's cast (the arms raised high in the shoot) while the legs keep walking?");
        await c.Ticks(100);
        var seen = false;
        for (var i = 0; i < 2 && !c.Player.IsNone; i++)
        {
            c.Expect(Abilities.PlaySequence(c.Player, CastCopy, SequenceOptions.Default), $"cast {i + 1} started");
            for (var t = 0; t < 40; t++)
            {
                var b = World.ReadBytes(c.Player, "ServerAnimationGraphInput");
                if (b is not null && ContainsId(b, IdOf("cast"))) seen = true;
                await c.Ticks();
            }
            await c.Ticks(80);
        }
        c.Expect(seen, "the overlay id was set in the graph input while the cast ran");
        await c.Verdict();
    }

    private static bool ContainsId(byte[] b, uint id)
    {
        for (var o = 0; o + 4 <= b.Length; o += 4)
            if (BitConverter.ToUInt32(b, o) == id) return true;
        return false;
    }

    [ClientCheck("anim.vanilla-client", "A player without Kiln on a server with player states", Area = Area, Since = Since,
                 Tags = [ClientCheckTags.TwoPlayers], Needs = ClientCheckNeeds.Joined, Order = 15)]
    public async Task VanillaClient(CheckContext c)
    {
        NetPeer? vanilla = null;
        var sub = Events.On<NetVanillaClientEvent>(e => { if (!e.Kick) vanilla = e.Peer; });
        c.OnEnd(() => sub.Dispose());
        c.Instruct("Server: modSync set to off in kiln/config/network.json, so a player without Kiln is let in. Player B (no Kiln): join. "
                   + "Player A (Kiln and this sample): attack with the Riposte Sword. Player B: do you stay connected while A attacks, and what do you "
                   + "see A do: nothing, the game's slash, or a frozen pose?");
        await c.Until(() => vanilla is not null, TimeSpan.FromMinutes(5), "the server let a vanilla client in");
        await c.Verdict();
        c.Expect(vanilla is not null && Network.Peers.Any(p => p.Id == vanilla.Id), "the vanilla client was still connected at the verdict");
    }

    [ClientCheck("anim.differing-graph-refused", "A client with other player states is refused", Area = Area, Since = Since,
                 Tags = [ClientCheckTags.TwoPlayers], Needs = ClientCheckNeeds.Joined, Order = 16)]
    public async Task DifferingGraphRefused(CheckContext c)
    {
        string? seen = null;
        var sub = Events.On<NetClientHandshakeEvent>(e =>
        {
            var theirs = e.Peer.Properties.TryGetValue("kiln.playeranim", out var h) ? h : "(none)";
            if (theirs != PlayerAnimStates.GraphHash) seen = $"{e.Peer.Name}: theirs {theirs}, the server's {PlayerAnimStates.GraphHash}, refused {e.Refuse}: {e.Reason}";
        });
        c.OnEnd(() => sub.Dispose());
        c.Instruct("Player B: switch this sample off in your game (or remove it), start the game again and join this server. "
                   + "Are you refused with a message that names the player animations?");
        await c.Until(() => seen is not null, TimeSpan.FromMinutes(10), "a client with another graph hash tried to join");
        c.Expect(seen is not null && seen.Contains("refused True"), "the server refused it with both hashes", seen);
        await c.Verdict();
    }
}
