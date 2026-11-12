// SPDX-License-Identifier: MIT
// The guided client checks of a weapon's moves: what only a player can see. Each sets the scene (the weapon in the bag, a training doll in front), says what
// to do and what to look for, asserts what the server can read by itself (the sequences the player's character enters, the moment the hurt event came) and
// waits for the player's verdict. ClientChecks.Register lists them (OnLoad); proof's runner runs them. The chain and the heavy/block checks use the Own Sword,
// whose moves name the test mod's own player states (assets/player-states.json): the instructions describe the clips the test cooks.
using Kiln;
using Kiln.Testing;

namespace WeaponMovesTest;

public sealed partial class WeaponMovesTestMod
{
    private const string Since = "0.2.0-dev.26";

    /// <summary>The weapons in the player's bag and a training doll a few metres in front of them; both go away when the check ends.</summary>
    private async Task<bool> Scene(CheckContext c, params string[] items)
    {
        if (!c.Expect(!c.Player.IsNone, "a player runs the check", c.Session.ToString())) return false;
        foreach (var item in items)
        {
            var given = await Items.Give(c.Player, item, 1);
            c.Expect(given.Status == ItemsStatus.Done, $"{item} is in the bag", given.ToString());
        }
        var at = World.Position(c.Player)!.Value;
        var doll = Build.PlaceProp(Doll, new WorldPosition(at.X + 2.5, at.Y, at.Z), options: new BuildOptions { Flags = BuildFlags.InBases | BuildFlags.Transient });
        c.Expect(!doll.IsNone, "a training doll stands 2.5 m east of you", doll.ToString());
        c.OnEnd(async () =>
        {
            if (!doll.IsNone && World.Exists(doll)) Spawn.Destroy(doll);
            foreach (var item in items)
                if (Content.Items.Get(item) is { } e) await Items.Take(c.Player, new ItemId(e.Id), 1);
        });
        return true;
    }

    /// <summary>The sequences the player's character enters while <paramref name="until"/> is false, in order, each once per start.</summary>
    private static async Task<List<uint>> Entered(CheckContext c, Func<List<uint>, bool> until, TimeSpan timeout, string what)
    {
        var seen = new List<uint>();
        long last = 0;
        var deadline = DateTime.UtcNow + timeout;
        while (!until(seen) && DateTime.UtcNow < deadline)
        {
            if (Abilities.SequenceOf(c.Player) is { Sequence: not 0 } s && s.TriggerTime != last)
            {
                last = s.TriggerTime;
                seen.Add(s.Sequence);
            }
            await c.Ticks();
        }
        c.Expect(until(seen), what, $"entered [{string.Join(" -> ", seen.Select(Name))}]");
        return seen;
    }

    [ClientCheck("blocks.moves-chain", "Moves: a weapon's chain plays its own two swings in order", Area = "Blocks", Since = Since,
        Tags = [ClientCheckTags.Visual], Needs = ClientCheckNeeds.Any, Order = 10)]
    private async Task Chain_plays_its_own_swings(CheckContext c)
    {
        if (!await Scene(c, G("own-sword"))) { await c.Verdict(); return; }
        uint one = Abilities.IdOf(CloneGuid("own-sword", "primary.1")), two = Abilities.IdOf(CloneGuid("own-sword", "primary.2"));
        c.Instruct("Take the Own Sword from your bag in hand and give the training doll three light attacks in a row. Its swings are states of the test mod's own " +
                   "(wm-slash-1 and wm-slash-2) playing the clips the test cooks: the first a forehand, the sword coming from high on your right across to the left " +
                   "while the left arm swings from across the body out to the left; the second the same swing mirrored and a little faster, a backhand from the left across to the right. Pass if the " +
                   "first swing is that forehand, the second that backhand, the third the forehand again, and none of them is the game's own sword swing.");
        var seen = await Entered(c, s => s.Count >= 3, TimeSpan.FromMinutes(3), "three swings");
        var swings = seen.Where(s => s == one || s == two).Take(3).ToList();
        c.Expect(swings.SequenceEqual(new[] { one, two, one }), "the game played link 1, link 2, link 1", string.Join(" -> ", swings.Select(Name)));
        await c.Verdict();
    }

    [ClientCheck("blocks.moves-hit-timing", "Moves: the blow lands when the blade passes", Area = "Blocks", Since = Since,
        Tags = [ClientCheckTags.Visual], Needs = ClientCheckNeeds.Any, Order = 20)]
    private async Task Blow_lands_when_the_blade_passes(CheckContext c)
    {
        if (!await Scene(c, G("chain-sword"), G("heavy-greataxe"))) { await c.Verdict(); return; }
        var one = Abilities.IdOf(CloneGuid("chain-sword", "primary.1"));
        var axe = Abilities.IdOf(CloneGuid("heavy-greataxe", "heavy"));
        // the game's own moments, read from the running game
        var light = Seq(Light1).Get("events[SpawnImpact#0].time").Number;
        var chop = Seq(AxeHeavy).Get("events[SpawnImpact#0].time").Number;
        c.Instruct($"Stand next to the training doll. With the Chain Sword, give it one light attack: its blow is moved to 0.40 s into the swing (the sword's own is at " +
                   $"{light:0.##} s), and the swing is the greataxe's first diagonal (Attack_Axe_01). Then take the Heavy Greataxe and give the doll one heavy attack: its " +
                   $"blow is moved to 1.40 s (the greataxe's own is at {chop:0.##} s). Pass if each time the hit shows on the doll (the flash, the number, the sound) as the " +
                   "blade crosses it, not before and not after.");
        var hits = new List<(ulong Step, EntityId Target)>();
        using var sub = Events.On<EntityDamagedEvent>(e => { if (e.Attacker == c.Player) hits.Add((e.Step, e.Target)); });
        var seen = await Entered(c, s => s.Contains(one) && s.Contains(axe), TimeSpan.FromMinutes(3), "a light attack with the Chain Sword and a heavy attack with the Heavy Greataxe");
        await c.Ticks(60);
        c.Expect(hits.Count > 0, "the doll was hit", $"{hits.Count} hit(s)");
        await c.Verdict();
    }

    [ClientCheck("blocks.moves-heavy-block", "Moves: a heavy attack and a block show their own animation", Area = "Blocks", Since = Since,
        Tags = [ClientCheckTags.Visual], Needs = ClientCheckNeeds.Any, Order = 30)]
    private async Task Heavy_and_block_show_their_own(CheckContext c)
    {
        if (!await Scene(c, G("own-sword"))) { await c.Verdict(); return; }
        uint heavy = Abilities.IdOf(CloneGuid("own-sword", "heavy")), block = Abilities.IdOf(CloneGuid("own-sword", "block"));
        c.Instruct("Take the Own Sword in hand. Give the doll a heavy attack, then hold block for two seconds. Both are states of the test mod's own playing the clips " +
                   "it cooks: the heavy (wm-cleave) an overhead chop, the sword raised above the head and brought straight down; the block (wm-guard) the sword held " +
                   "across the body, a slow loop, while the legs keep the game's own walk when you move. Pass if the heavy is that overhead chop, not the sword's " +
                   "diagonal heavy, and the block shows the sword held across instead of the game's guard pose.");
        await Entered(c, s => s.Contains(heavy) && s.Contains(block), TimeSpan.FromMinutes(3), "the Own Sword's heavy and its block played");
        await c.Verdict();
    }

    [ClientCheck("blocks.moves-vanilla-unchanged", "Moves: a vanilla weapon of the same class is unchanged", Area = "Blocks", Since = Since,
        Tags = [ClientCheckTags.Visual], Needs = ClientCheckNeeds.Any, Order = 40)]
    private async Task Vanilla_weapon_unchanged(CheckContext c)
    {
        if (!await Scene(c, SwordStarter, G("plain-sword"))) { await c.Verdict(); return; }
        uint light = Abilities.IdOf(Seq(Light1).Guid), heavy = Abilities.IdOf(Seq(SwordHeavy).Guid), block = Abilities.IdOf(Seq(GameBlock).Guid);
        c.Instruct("With the game's own sword from your bag, then with the Plain Sword (a block with no moves), give the doll light attacks, a heavy attack and a block. " +
                   "Pass if both swing, hit and block exactly as a sword always has: the sword's own diagonals, the blow as the blade passes, the guard pose.");
        var seen = await Entered(c, s => s.Contains(light) && s.Contains(heavy) && s.Contains(block), TimeSpan.FromMinutes(4), "a light attack, a heavy attack and a block");
        var mine = Cases.Select(x => Abilities.IdOf(CloneGuid(x.Block, x.Suffix))).ToHashSet();
        c.Expect(!seen.Any(mine.Contains), "no move of this mod played", string.Join(" -> ", seen.Select(Name)));
        foreach (var name in new[] { Light1, Light2, SwordHeavy, GameBlock })
        {
            var s = Seq(name);
            c.Expect(EventsOf(s).All(e => Math.Abs(e.Time - e.Orig) < 1e-9) && Math.Abs(s.Get("length").Number - s.Original("length").AsDouble) < 1e-9, $"the game's {name} keeps its times");
        }
        await c.Verdict();
    }

    [ClientCheck("blocks.held-animated", "A held model with moving parts moves in the hand", Area = "Blocks", Since = Since,
        Tags = [ClientCheckTags.Visual], Needs = ClientCheckNeeds.Any, Order = 50)]
    private async Task Held_model_moves_in_the_hand(CheckContext c)
    {
        var items = HeldCases.Select(h => G(h.Block)).ToArray();
        if (!await Scene(c, items)) { await c.Verdict(); return; }
        foreach (var h in HeldCases)
            c.Expect(Content.Items.Get(G(h.Block))?.GetRef("equipment.visualEntity") == BlockIds.Derive(Me, "template", $"{h.Block}.moving"), $"{h.Block} holds its moving template");
        c.Expect(HeldCooked, "this game cooked the test mod's held models (build-assets.py)", HeldCooked ? "assets/cooked is there" : "not cooked: every weapon shows its rigid model");
        c.Instruct("Take each Held weapon from your bag in hand in turn: the Held Sword, the Held Greataxe, the Held Shield, the Held Daggers and the Held Daggers (left). " +
                   "Pass if each sits in the hand its class is held in (the sword in the right hand, the greataxe in the left, the shield on the left arm, the daggers one in " +
                   "each hand), its crystal turns and its tip sways by itself in a loop, the greataxe's glow (the game's lit axe's) sits 0.6 m up the blade and the others have " +
                   "none, no staff glow shows on any of them, and in the bag and dropped on the ground each shows its class's own item (these looks name no icon and no dropped " +
                   "model), not the moving blade.");
        await c.Verdict();
    }

    [ClientCheck("blocks.held-animated-two-players", "A held model with moving parts, seen by a player who cooked it and one who did not", Area = "Blocks", Since = Since,
        Tags = [ClientCheckTags.Visual, ClientCheckTags.TwoPlayers], Needs = ClientCheckNeeds.Joined, Order = 60)]
    private async Task Held_model_two_players(CheckContext c)
    {
        if (!await Scene(c, G("held-sword"), G("held-greataxe"))) { await c.Verdict(); return; }
        c.Expect(!HeldCooked, "the server has not cooked the test mod (it draws nothing and needs no cooked files)", HeldCooked ? "the server cooked it: move assets/cooked away and restart it" : "not cooked");
        c.Instruct("Two players join a dedicated server that has not cooked weapon-moves-test. Player 1's game has cooked it (build-assets.py; the Kiln line in player 1's " +
                   "kiln/logs/kiln.log for the mod's assets lists its cooked files and warns of none missing), player 2's has not (player 2's kiln.log warns that the mod's " +
                   "assets/cooked files are missing, and `kiln blocks weapon-moves-test` shows animatedHeld.cooked false). Each player waits until their own game has loaded the mod's blocks (the line \"blocks: weapon-moves-test: ... block(s)\" in " +
                   "their kiln.log) before judging: a state that arrives before the mod's own definition shows the server's values for a moment. Both take the Held Sword, then the Held Greataxe, in " +
                   "hand and look at each other. Pass if player 1 sees the crystal turn in both characters' hands, player 2 sees the rigid blade in the same hand (the sword " +
                   "right, the greataxe left) with the same glow on the greataxe, and neither ever sees an empty hand.");
        await c.Verdict();
    }
}
