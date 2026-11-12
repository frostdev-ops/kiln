// SPDX-License-Identifier: MIT
// Moves on player states by name (Kiln.PlayerAnim, docs/kiln/modding/animations.md "Player states"): the test mod's own states (assets/player-states.json,
// clips cooked by build-assets.py), the game's states by name, and the names that cannot be a move's state, each reported by block and slot. Then, on a
// spawned player character driven by the attack, heavy and block inputs, the state the server's graph was given (ServerAnimationGraphInput+0x22c, as
// mods/tests/player-anim-test reads it) while each move plays.
using System.Text.Json.Nodes;
using Kiln;

namespace WeaponMovesTest;

public sealed partial class WeaponMovesTestMod
{
    private const ulong BlockInput = 1UL << 30;   // keen::ecs::PlayerInputType.Block

    // the test's own states, their kind, and the move of own-sword that names each
    internal static readonly (string State, string Suffix, PlayerAnimStateKind Kind)[] OwnStates =
        [("wm-slash-1", "primary.1", PlayerAnimStateKind.State), ("wm-slash-2", "primary.2", PlayerAnimStateKind.State),
         ("wm-cleave", "heavy", PlayerAnimStateKind.State), ("wm-guard", "block", PlayerAnimStateKind.State)];

    // the game's state each of the test's states is placed like: what the move plays where the test's clips are not cooked (its state is then not live)
    private static readonly Dictionary<string, string> LikeOf = new()
    {
        ["wm-slash-1"] = "Attack_Sword_Shield_01", ["wm-slash-2"] = "Attack_Sword_Shield_02",
        ["wm-cleave"] = "SM_Attack_Sword_Shield_FullBody/Attack_Heavy", ["wm-guard"] = "Block_FullBody",
    };

    private static bool Live(string state) => PlayerAnimStates.Find(Me, state) is { Status: PlayerAnimStateStatus.Live };

    // game states by name, on game-name-sword: (the name written, the clone's suffix)
    private static readonly (string Name, string Suffix)[] GameNames =
        [("SM_Attack_Hammer_FullBody/Attack_Axe_01", "primary.1"), ("Attack_Axe_02", "primary.2"), ("Attack_Greatsword_Heavy", "heavy")];

    // the names refused-sword gives, its slot's path, the clone's suffix and the like whose state the move keeps, and a word of the reason
    private static readonly (string Name, string Path, string Suffix, string Like, string Reason)[] StateRefusals =
    [
        ("No_State_Has_This_Name", "moves.primary[0].animation", "primary.1", Light1, "is no state of mod"),
        ("Attack_Jump_Uppercut", "moves.primary[1].animation", "primary.2", Light2, "several machines"),
        ("SM_Block_Locomotion/Idle_Block", "moves.heavy.animation", "heavy", SwordHeavy, "entered by no id"),
        ("SM_Jump_Attack_FullBody/Jump_Attack_Start_FullBody", "moves.block.animation", "block", GameBlock, "ids ("),
        ("wm-nowhere", "moves.dodgeAttack.animation", "dodgeAttack", "Attack_Player_Melee_Special_DodgeAttack_Sword", "refused"),
    ];

    private static uint IdOf(Guid g) => Fnv(g);

    private void StatesData()
    {
        foreach (var name in new[] { "blocks.weapon_moves", "blocks.animated_held", "anim.player_states" })
        {
            var c = Capabilities.Get(name);
            Info($"capability {name}: {c.State}, {c.Reason} \"{c.ReasonText}\" (source {c.Source})");
        }
        Check(Capabilities.Get("blocks.weapon_moves").State == CapabilityState.Available && Capabilities.Get("blocks.animated_held").State == CapabilityState.Available,
            $"the capability rows blocks.weapon_moves and blocks.animated_held are available: {Capabilities.Get("blocks.weapon_moves").State}, {Capabilities.Get("blocks.animated_held").State}");
        var errors = Blocks.Report().GetValueOrDefault(Me).Errors ?? [];

        // 1. a mod state by name
        foreach (var (state, suffix, kind) in OwnStates)
        {
            var info = PlayerAnimStates.Find(Me, state);
            var want = BlockIds.Derive(Me, "state", state);
            if (_wrong && state == "wm-slash-1") want = BlockIds.Derive(Me, "state", "x");
            var clone = Seq($"{G("own-sword")}.{suffix}");
            var named = clone.Get("events[SetAnimationEvent#0].animationName").Guid;
            if (HeldCooked)
                Check(info is { Status: PlayerAnimStateStatus.Live } && info.Kind == kind && info.Guid == want && named == want,
                    $"own-sword.{suffix} plays the mod's state \"{state}\": its SetAnimationEvent names {named}, derive(mod, \"state\", \"{state}\") = {want}; PlayerAnimStates.Find says {info?.Status} {info?.Kind}, id 0x{info?.Id:x8} in {info?.Machine}");
            else
            {
                // not cooked here: the state's clip is not served, so the state does not load, the move is reported and keeps its like's state
                var line = errors.FirstOrDefault(e => e.Contains("weapon own-sword,", StringComparison.Ordinal) && e.Contains($"\"{state}\"", StringComparison.Ordinal));
                var like = suffix switch { "primary.1" => Light1, "primary.2" => Light2, "heavy" => SwordHeavy, _ => GameBlock };
                Check(info is { Status: not PlayerAnimStateStatus.Live } && line != null && named == StateOf(Seq(like)) && !(_wrong && state == "wm-slash-1"),
                    $"not cooked here: own-sword.{suffix}'s state \"{state}\" is {info?.Status} ({info?.Reason}), reported ({line ?? "no line"}), the move keeps {like}'s state");
            }
        }
        if (HeldCooked)
            Check(!errors.Any(e => e.Contains("weapon own-sword,", StringComparison.Ordinal)), $"own-sword's names were all found: no problem line for it [{string.Join(" | ", errors.Where(e => e.Contains("own-sword", StringComparison.Ordinal)))}]");

        // 3. a game state by name
        foreach (var (name, suffix) in GameNames)
        {
            var found = PlayerAnimStates.FindGameStates(name);
            var named = Seq($"{G("game-name-sword")}.{suffix}").Get("events[SetAnimationEvent#0].animationName").Guid;
            var want = found.Count == 1 && found[0].Ids.Count == 1 ? found[0].Ids[0] : 0u;
            if (_wrong && suffix == "heavy") want ^= 1;
            Check(want != 0 && IdOf(named) == want,
                $"game-name-sword.{suffix}: \"{name}\" resolves to {named}, id 0x{IdOf(named):x8}; FindGameStates: [{string.Join(", ", found.Select(f => $"{f.Machine}/{f.Name} {f.Kind} ids {string.Join("/", f.Ids.Select(i => $"0x{i:x8}"))}"))}]");
        }
        Check(!errors.Any(e => e.Contains("weapon game-name-sword,", StringComparison.Ordinal)), "game-name-sword's names were all found: no problem line for it");

        // 4. the refusals
        foreach (var (name, path, suffix, like, reason) in StateRefusals)
        {
            var line = errors.FirstOrDefault(e => e.Contains("weapon refused-sword,", StringComparison.Ordinal) && e.Contains(path, StringComparison.Ordinal));
            var clone = Seq($"{G("refused-sword")}.{suffix}");
            var kept = clone.Get("events[SetAnimationEvent#0].animationName").Guid == StateOf(Seq(like));
            var word = _wrong && suffix == "primary.1" ? "(fail-once)" : reason;
            Check(line != null && line.Contains(word, StringComparison.Ordinal) && kept, $"refused-sword {path} \"{name}\": reported by block and slot, the move keeps {like}'s state ({kept}): {line ?? "(no line)"}");
        }
        var bad = PlayerAnimStates.Find(Me, "wm-nowhere");
        // the other states load: wm-zap always (a clip the game serves), the rest where this machine cooked the test
        Check(bad is { Status: PlayerAnimStateStatus.Refused } && Live("wm-zap") && (!HeldCooked || Live("wm-slash-1")),
            $"the mod's own wm-nowhere is refused by the service, its other states load: {bad?.Status} \"{bad?.Reason}\"");
        var unexpected = errors.Where(e => e.Contains("block weapon ", StringComparison.Ordinal) && e.Contains(".animation", StringComparison.Ordinal) && !e.Contains("weapon refused-sword,", StringComparison.Ordinal)
                                           && (HeldCooked || !e.Contains("weapon own-sword,", StringComparison.Ordinal))).ToList();
        Check(unexpected.Count == 0, $"no other block of this mod has a state problem: [{string.Join(" | ", unexpected)}]");

        // 5. an overlay move on a mod's overlay state
        var zap = PlayerAnimStates.Find(Me, "wm-zap");
        var wand = Seq($"{G("own-wand")}.primary");
        var overlay = wand.Get("events[SetAnimationOverlayEvent#0].overlayName").Guid;
        var zapGuid = BlockIds.Derive(Me, "state", "wm-zap");
        // wm-zap plays a clip the game serves: it loads whether or not this machine cooked the test
        Check(zap is { Status: PlayerAnimStateStatus.Live, Kind: PlayerAnimStateKind.Overlay } && overlay == (_wrong ? Guid.Empty : zapGuid),
            $"own-wand's bolt plays the mod's overlay state wm-zap: its SetAnimationOverlayEvent#0 names {overlay} ({zapGuid}); Find says {zap?.Status} {zap?.Kind} id 0x{zap?.Id:x8}");
    }

    // ---- 2. the server's graph enters the state ----

    /// <summary>The state ids the graph is given, once each in order, while <paramref name="drive"/> starts a move and <paramref name="steps"/> steps pass.</summary>
    private async Task<List<uint>> GraphStates(EntityId p, Func<Task> drive, int steps)
    {
        var seen = new List<uint>();
        void Sample()
        {
            if (World.ReadBytes(p, "ServerAnimationGraphInput") is { Length: >= 0x230 } inp && BitConverter.ToUInt32(inp, 0x22c) is var s && (seen.Count == 0 || seen[^1] != s)) seen.Add(s);
        }
        Sample();
        var run = drive();
        while (!run.IsCompleted) { await Steps(1); Sample(); }
        await run;
        for (var i = 0; i < steps; i++) { await Steps(1); Sample(); }
        return seen;
    }

    private static string Ids(IEnumerable<uint> ids) => string.Join(" -> ", ids.Select(i => $"0x{i:x8}"));

    private async Task GraphEnters(EntityId p)
    {
        // the test's state, or where it did not load (not cooked here) the game's state it is placed like, which the move then plays
        uint Own(string s) => Live(s) ? PlayerAnimStates.Find(Me, s)!.Id : PlayerAnimStates.FindGameStates(LikeOf[s]).FirstOrDefault()?.Ids.FirstOrDefault() ?? 0;
        async Task Light(string weapon, uint[] want, string label, int presses = 1)
        {
            if (!await ToHand(p, weapon)) return;
            await Steps(20);
            var seen = await GraphStates(p, async () =>
            {
                var w = new Watch(p);
                _watch = w;
                await Press(p, Mainhand | MainhandTap, 1);
                for (var k = 1; k < presses; k++)
                {
                    // the next press while the running link's Primary opening is open, by the sequence's own clock (as ChainPlayed does)
                    for (var i = 0; i < 60 && w.Seen.Count < k; i++) await Steps(1);
                    if (w.Seen.Count < k) break;
                    var running = w.Seen[k - 1];
                    var link = Content.Sequences.Get($"0x{running.Sequence:x8}");
                    var open = link == null ? 0.6 : Openings(link).Where(o => o.Trigger == "Primary" && o.FollowUp != Guid.Empty).Select(o => o.Time).DefaultIfEmpty(0.6).Min();
                    for (var i = 0; i < 120 && _clock.GetValueOrDefault(_step) / 60.0 - running.TriggerTime / 1e9 < open - 0.1; i++) await Steps(1);
                    for (var tries = 0; tries < 4 && w.Seen.Count <= k; tries++)
                    {
                        await Press(p, Mainhand | MainhandTap, 1);
                        for (var i = 0; i < 4 && w.Seen.Count <= k; i++) await Steps(1);
                    }
                }
                for (var i = 0; i < 30 && w.Seen.Count < presses; i++) await Steps(1);
                _watch = null;
            }, 40);
            var expect = _wrong && label.StartsWith("own-sword's chain", StringComparison.Ordinal) ? want.Reverse().ToArray() : want;
            var order = seen.Where(expect.Contains).ToList();
            Check(Subsequence(order, expect), $"{label}: the graph enters {Ids(expect)} in that order; it was given {Ids(seen)}");
        }
        async Task Hold(string weapon, ulong bits, int steps, uint want, string label)
        {
            if (!await ToHand(p, weapon)) return;
            await Steps(20);
            var seen = await GraphStates(p, () => Press(p, bits, steps), 40);
            Check(seen.Contains(_wrong && label.StartsWith("own-sword's guard", StringComparison.Ordinal) ? want ^ 1 : want), $"{label}: the graph enters 0x{want:x8}; it was given {Ids(seen)}");
        }
        await Light("own-sword", [Own("wm-slash-1"), Own("wm-slash-2"), Own("wm-slash-1")], "own-sword's chain by the attack input: the mod's wm-slash-1, wm-slash-2, wm-slash-1", 3);
        await Hold("own-sword", Mainhand | MainhandHold, HeavyHold, Own("wm-cleave"), "own-sword's heavy by the held button: the mod's wm-cleave");
        await Hold("own-sword", BlockInput, 30, Own("wm-guard"), "own-sword's guard by the block input: the mod's wm-guard");
        var axe = PlayerAnimStates.FindGameStates("SM_Attack_Hammer_FullBody/Attack_Axe_01").FirstOrDefault()?.Ids.FirstOrDefault() ?? 0;
        await Light("game-name-sword", [axe], "game-name-sword's first swing by the attack input: the game's Attack_Axe_01, named by name");
        var plain = PlayerAnimStates.FindGameStates("Attack_Sword_Shield_01").FirstOrDefault()?.Ids.FirstOrDefault() ?? 0;
        await Light("plain-sword", [plain], "the control, the plain sword's first swing: the game's own Attack_Sword_Shield_01");
        if (Mods.IsLoaded(Sample))
        {
            uint S(string s) => PlayerAnimStates.Find(Sample, s)?.Id ?? 0;
            await Light($"{Sample}.turning-blade", [S("slash-1"), S("slash-2"), S("slash-1")], "the sample's three light attacks: its slash-1, slash-2, slash-1", 3);
            await Hold($"{Sample}.turning-blade", Mainhand | MainhandHold, HeavyHold, S("cleave"), "the sample's heavy: its cleave");
        }
        // Kiln Equipment (a copy with Oathsplitter's heavy on its own state), when it is loaded beside this test: its state Live, and the graph enters it
        if (Mods.IsLoaded("kiln-equipment"))
        {
            var oath = PlayerAnimStates.Find("kiln-equipment", "oath-whirl");
            Check(oath is { Status: PlayerAnimStateStatus.Live }, $"kiln-equipment's state oath-whirl: {oath?.Status} {oath?.Kind} id 0x{oath?.Id:x8} in {oath?.Machine} {oath?.Reason}");
            // with its switch no-own-animations the weapon names no state of its own: the heavy is the game's hammer whirl, in the whirl's own state
            var off = Mods.Get("kiln-equipment") is { } ke && File.Exists(Path.Combine(ke.Directory, "no-own-animations"));
            var whirl = PlayerAnimStates.FindGameStates("SM_Attack_Hammer_FullBody/Attack_Skill_Whirlwind_Hammer").FirstOrDefault()?.Ids.FirstOrDefault() ?? 0;
            if (oath is { Status: PlayerAnimStateStatus.Live })
                await Hold("kiln-equipment.oathsplitter", Mainhand | MainhandHold, HeavyHold, off ? whirl : oath.Id,
                    off ? "Oathsplitter's heavy with kiln-equipment's no-own-animations: the game's hammer whirl, Attack_Skill_Whirlwind_Hammer" : "Oathsplitter's heavy by the held button: kiln-equipment's oath-whirl");
            if (Content.Items.Get("kiln-equipment.oathsplitter") is { } o) await Items.Take(p, new ItemId(o.Id), Items.Read(p)?.Count(new ItemId(o.Id)) ?? 0);
        }
        // clear the hands for what follows
        foreach (var b in new[] { "own-sword", "game-name-sword" })
            if (Content.Items.Get(G(b)) is { } e) await Items.Take(p, new ItemId(e.Id), Items.Read(p)?.Count(new ItemId(e.Id)) ?? 0);
        if (Content.Items.Get($"{Sample}.turning-blade") is { } t) await Items.Take(p, new ItemId(t.Id), Items.Read(p)?.Count(new ItemId(t.Id)) ?? 0);
    }

    private static bool Subsequence(List<uint> seen, uint[] want)
    {
        var i = 0;
        foreach (var s in seen)
            if (i < want.Length && s == want[i]) i++;
        return want.Length > 0 && i == want.Length && want.All(w => w != 0);
    }
}
