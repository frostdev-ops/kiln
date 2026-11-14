// SPDX-License-Identifier: MIT
// 16c-weapon-follow-ups: the guided checks of a weapon's follow-up routes and of the moves cases the dossier left for a player (ids 16c-b to
// 16c-f). The fixture is the data-only mod mods/tests/weapon-routes-fixture (a sword with routes, a shield, a staff charge); 16c-e uses the Own Sword
// of mods/tests/weapon-moves-test. What the code can see is asserted: the sequences the character enters, read each tick. What only a player can
// judge (the motion, the recovery, the hands) is asked. 16c-b and 16c-d open blocks.move_followups.
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

public sealed partial class ClientPassMod
{
    private const string RoutesFixture = "weapon-routes-fixture", MovesLab = "weapon-moves-test", FollowUpsRow = "blocks.move_followups";
    private const int FollowUpsOrder = 2000 + (28270 - 28000) * 10;

    /// <summary>The sequences the applied generation of a routed weapon plays, by slot/link, from the mod's blocks report. Empty: none is applied.</summary>
    private static Dictionary<string, uint> RouteClones(string mod, string block)
    {
        var clones = new Dictionary<string, uint>();
        if (Blocks.Lowered(block, mod) is not { } text || JsonNode.Parse(text)?["movesReady"]?["clones"] is not JsonObject live) return clones;
        foreach (var (key, guid) in live)
            if (Guid.TryParse((string?)guid, out var g)) clones[key] = Abilities.IdOf(g);
        return clones;
    }

    /// <summary>The sequences the player's character enters for <paramref name="seconds"/>, in order, each start once.</summary>
    private static async Task<List<uint>> RouteEntered(CheckContext c, double seconds)
    {
        var seen = new List<uint>();
        long last = 0;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (Abilities.SequenceOf(c.Player) is { Sequence: not 0 } s && s.TriggerTime != last)
            {
                last = s.TriggerTime;
                seen.Add(s.Sequence);
            }
            await c.Ticks();
        }
        return seen;
    }

    private static bool Followed(List<uint> seen, uint from, uint to)
    {
        for (var i = 0; i + 1 < seen.Count; i++)
            if (seen[i] == from && seen[i + 1] == to) return true;
        return false;
    }

    [ClientCheck("16c-b", "Follow-ups: a block hands over to the weapon's own parry and light attack", Area = "Blocks", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Any, Order = FollowUpsOrder + 0)]
    public async Task FollowUpRoutes(CheckContext c)
    {
        if (!await Need(c, RoutesFixture) || !await NeedOpen(c, FollowUpsRow) || !await NeedPlayer(c)) return;
        var clones = RouteClones(RoutesFixture, "route-sword");
        if (!c.Expect(clones.Count == 4, "the Route Sword's moves are applied as one generation", $"clones [{string.Join(", ", clones.Keys)}]")) { await c.Verdict(); return; }
        await GiveBlock(c, RoutesFixture, "route-sword");
        c.Instruct("Equip the Route Sword and find a weak enemy that attacks you (any will do). For the next two minutes: " +
                   "1. Hold block and let it hit you just as you start to block, so that the blow is parried: your character goes into its parry. " +
                   "2. Hold block and press the light attack while blocking: your character goes into the sword's first light swing, and a second press continues the chain. " +
                   "3. Dodge out of a swing: the dodge works as with any sword. 4. Let go of everything: your character stands and walks normally again.");
        var seen = await RouteEntered(c, 120);
        c.Log.Info($"client-pass: 16c-b entered [{string.Join(" -> ", seen.Select(s => $"0x{s:x8}"))}]; clones [{string.Join(", ", clones.Select(k => $"{k.Key}=0x{k.Value:x8}"))}]");
        c.Expect(seen.Contains(clones["block/1"]), "the block entered is the sword's own generation clone", $"{seen.Count} sequence starts");
        c.Expect(Followed(seen, clones["block/1"], clones["parry/1"]), "a parried blow led from the block into the sword's own parry", "block/1 -> parry/1");
        c.Expect(Followed(seen, clones["block/1"], clones["primary/1"]), "the light attack while blocking led into the sword's first light swing", "block/1 -> primary/1");
        c.Expect(Followed(seen, clones["primary/1"], clones["primary/2"]), "the light chain went from its first link to its second", "primary/1 -> primary/2");
        c.Instruct("Did the parry play when (and only when) a blow was parried, did the light attack come out of the block, and did your character " +
                   "return to normal standing and walking each time, with no frozen pose?");
        await c.Verdict();
    }

    [ClientCheck("16c-c", "Moves: a staff charge plays its cast and releases one shot", Area = "Blocks", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Any, Order = FollowUpsOrder + 1)]
    public async Task StaffCastRelease(CheckContext c)
    {
        if (!await Need(c, RoutesFixture) || !await NeedPlayer(c)) return;
        await GiveBlock(c, RoutesFixture, "research-cast", 20);
        c.Instruct("Equip any staff and load the Research Cast charges. 1. Tap cast once. 2. Hold the cast, then release. 3. Start a cast and dodge " +
                   "before the shot leaves, then cast again at once. Each completed cast sends exactly one ice shot, a little under a second after it " +
                   "starts; the dodged cast sends none; mana and charges go down by one cast each. Your arm and the staff return to rest after every " +
                   "cast. Was it one shot per completed cast, with no second shot and no arm or staff left frozen in the cast pose?");
        await c.Verdict();
    }

    [ClientCheck("16c-d", "Follow-ups: held block, parry timing and shield recovery", Area = "Blocks", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Any, Order = FollowUpsOrder + 2)]
    public async Task HeldBlockAndParry(CheckContext c)
    {
        if (!await Need(c, RoutesFixture) || !await NeedOpen(c, FollowUpsRow) || !await NeedPlayer(c)) return;
        var clones = RouteClones(RoutesFixture, "route-sword");
        if (!c.Expect(clones.Count == 4, "the Route Sword's moves are applied as one generation", $"clones [{string.Join(", ", clones.Keys)}]")) { await c.Verdict(); return; }
        await GiveBlock(c, RoutesFixture, "route-sword");
        await GiveBlock(c, RoutesFixture, "research-shield");
        c.Instruct("Equip the Route Sword and the Research Shield, near a weak enemy. First, for 20 seconds: hold block the whole time, walk forward, " +
                   "back and sideways, and take a few blocked hits without parrying. Start holding block now.");
        var held = await RouteEntered(c, 20);
        var blocks = held.Count(s => s == clones["block/1"]);
        c.Log.Info($"client-pass: 16c-d held block entered [{string.Join(" -> ", held.Select(s => $"0x{s:x8}"))}]");
        c.Expect(blocks is >= 1 and <= 3, "the held block was entered once per press, not again and again", $"{blocks} block starts in 20 s");
        c.Instruct("Now, for one minute: block too early (well before the blow), block at the right moment (a parry), and block too late. Then take the " +
                   "shield off, put it back on, and parry once more.");
        var seen = await RouteEntered(c, 60);
        c.Log.Info($"client-pass: 16c-d parries entered [{string.Join(" -> ", seen.Select(s => $"0x{s:x8}"))}]");
        c.Expect(Followed(seen, clones["block/1"], clones["parry/1"]), "a timed parry led from the block into the sword's own parry", "block/1 -> parry/1");
        c.Instruct("While you held block: did the guard stay up steadily (no restarting), with sword and shield in the right hands? Did only the " +
                   "well-timed block give the parry, and did the too-early and too-late ones end normally? After putting the shield back on, was " +
                   "everything the same?");
        await c.Verdict();
    }

    [ClientCheck("16c-e", "Moves: a dropped weapon with moves keeps them after pickup and rejoin", Area = "Blocks", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Any, Order = FollowUpsOrder + 3)]
    public async Task DroppedWeaponKeepsMoves(CheckContext c)
    {
        if (!await Need(c, MovesLab) || !await NeedPlayer(c)) return;
        await GiveBlock(c, MovesLab, "own-sword");
        c.Instruct("1. Drop the Own Sword on the ground and look at it: it lies there with its own model and icon. 2. Pick it up, equip it, and do the " +
                   "light attack twice, the heavy attack and a block: they are its own motions, as in the moves checks. 3. Drop it again, leave the " +
                   "world, come back, pick it up and repeat. 4. Do the same once with an ordinary sword of the game. Did the Own Sword keep its " +
                   "model, its moves and its durability through the drops and the rejoin, with no \"unknown item\" and no stand-in model?");
        await c.Verdict();
    }

    [ClientCheck("16c-f", "Moves: a cooked clip under a new id against one under a game id", Area = "Blocks", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = FollowUpsOrder + 4)]
    public async Task ClipIdentityOverride(CheckContext c)
    {
        // its fixture (a cooked clip served under a new GUID and, after a restart, under a game GUID) is an optional extension of the package and is
        // not built: the check asks for skip until a mod with this id exists
        if (!await Need(c, "clip-override-fixture")) return;
        c.Instruct("The fixture for this check is not part of this build. Press skip.");
        await c.Verdict();
    }
}
