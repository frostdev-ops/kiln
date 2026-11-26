// SPDX-License-Identifier: MIT
// Lab test of the skill respec paying its Runes from linked sources (GitHub #150), on a dedicated server. The payer is a
// spawned player character (1_Player, no client) standing in a base: a lab has no logged-in player, so the server runs with
// KILN_STATIONS_CRAFT_LAB=1, which lets kiln-items give such a character (an id outside the player range, never a real
// player) respec links of its own (the private kiln_stations_lab_respec_link) and the base's Magic chests as the game hands
// them to a player with the right. Everything else is the game's own: the node goes into the character's tree as a save
// holds it (GameTree.LabPutInSave), and the unlearn and the reset go through the game's input handler (GameTree.GameRequest),
// which calls the respec payer; each case reads Kiln's own payment counters (kiln_stations_craft_stats), so "nothing moved"
// is Kiln's verdict, not a payment that never ran:
//  1. no link: the payer's InventoryPay call reaches Kiln (RespecPaid), Kiln stands aside, the game refuses (no Runes);
//  2. Runes only in the linked chest: paid from it, the node is gone;
//  3. one Rune in the bag: the bag first, the link for the rest;
//  4. the bag covers it: the game's own payment, the link untouched;
//  5. short even with the link: refused, nothing moved, the node stays;
//  6. one Rune in a Magic chest of the base: the chest before the link;
//  7. the paid reset (points spent 2): paid from the link;
//  8. a reset with nothing spent: free, the payment is never asked, the link untouched.
// The join check's own free reset (pay 0) never calls the payer (docs/re/respec.md section 1); a character without a client
// never passes that check, so it is static evidence only.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kiln;

namespace RespecTest;

public sealed class RespecTestMod : Mod
{
    private const string MagicChest = "Prop_Decoration_T1_Storage_24_Magic", Chest = "Prop_Decoration_T1_Storage_24",
        PlayerTemplate = "1_Player";
    private const uint Node = 0xc01ee111;  // a vanilla node next to a root, cost 2 (docs/re/progression.md section 3)
    private const int Cost = 2;
    private static readonly ItemId Rune = new(0x41a792dc);  // Material_T1_Token_WeaponUpgrade_Rune01, the payer's item
    private static readonly BuildOptions InBases = new() { Flags = BuildFlags.InBases };
    private int _pass, _fail;
    private readonly List<EntityId> _placed = new(), _spawned = new();

    private enum St { Calls, Idle, OtherWorld, Mode0, NoAccess, Considered, Covered, Short, Paid, FromOutputs, Rollback, Skipped, Lab, Respec, Count }
    private static readonly string[] StatNames =
        ["calls", "idle", "other_world", "mode0", "no_access", "considered", "covered", "short", "paid", "from_outputs", "rollback", "skipped", "lab", "respec"];
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint StatsFn([In, Out] uint[] values, uint n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LinkFn(uint character, uint container);
    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern nint GetProcAddress(nint module, string name);
    private StatsFn? _stats;
    private LinkFn? _link;

    private void Info(string s) => Log.Info($"respec-test: INFO {s}");

    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"respec-test: PASS {what}"); }
        else { _fail++; Log.Error($"respec-test: FAIL {what}"); }
        return ok;
    }

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        // kiln.skills' test-only requests (GameTree.GameRequest op 3, LabPutInSave) are off without this file
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");
        _ = Run();
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task Run()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!(Spawn.WorldRunning && Build.Available && Stations.Available) && sw.Elapsed.TotalSeconds < 240)
                await Context.Scheduler.NextTick();
            await Steps(150);
            await Suite();
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned) { _link?.Invoke(e.Value, 0); Spawn.Destroy(e); }
        foreach (var e in _placed.ToArray())
            try { if (World.Exists(e)) Build.RemoveProp(e, InBases); }
            catch (Exception x) { Info($"remove {e}: {x.Message}"); }
        await Steps(3);
        Log.Info($"respec-test done: {_pass} PASS, {_fail} FAIL");
    }

    private uint[] Stats()
    {
        var v = new uint[(int)St.Count];
        _stats?.Invoke(v, (uint)v.Length);
        return v;
    }
    private static long D(uint[] before, uint[] after, St s) => (long)after[(int)s] - before[(int)s];
    private static string Delta(uint[] before, uint[] after) =>
        string.Join(", ", Enumerable.Range(0, (int)St.Count).Where(i => after[i] != before[i]).Select(i => $"{StatNames[i]} +{after[i] - before[i]}"));
    private static int Count(EntityId e) => Items.Read(e)?.Count(Rune) ?? -1;

    private async Task SetRunes(EntityId e, int n)
    {
        var have = Count(e);
        if (have > n) await Items.Take(e, Rune, have - n);
        else if (have < n) await Items.Give(e, Rune, n - have);
    }

    private async Task<EntityId> Place(string template, WorldPosition at)
    {
        EntityId e;
        try { e = Build.PlaceProp(template, at, null, 0, InBases); }
        catch (Exception x)
        {
            Info($"place {template}: {x.Message}");
            return EntityId.None;
        }
        if (!e.IsNone) _placed.Add(e);
        await Steps(10);
        return e;
    }

    private bool Learned(EntityId who) => GameTree.Query(who, Node)?.Unlocked == true;

    // One respec through the game's input handler (op 1 unlearn the node, op 2 reset): the counters' change and the Runes after.
    private async Task<(uint[] Before, uint[] After)> Respec(EntityId who, int op, string what, params EntityId[] watch)
    {
        var before = Stats();
        GameTree.GameRequest(who, op, op == 1 ? Node : 0);
        await Steps(6);
        var after = Stats();
        Info($"{what}: {(op == 1 ? "unlearn" : "reset")}; node learned {Learned(who)}; Runes {string.Join(", ", watch.Select(e => $"{e}={Count(e)}"))}; " +
             $"Kiln: {Delta(before, after)}");
        return (before, after);
    }

    // The node into the character's UnlockedSkillNodes as a save holds it (a plain node: level 0), then changeCount (+0x908)
    // bumped, so the game's player_skilltree rebuilds PlayerSkillsUi (the masks IsUnlocked and PointsSpent read) itself.
    private async Task Learn(EntityId who)
    {
        if (!Learned(who))
        {
            var put = GameTree.LabPutInSave(who, Node, 0);
            var u = World.ReadBytes(who, "UnlockedSkillNodes");
            var bumped = u is not null && World.WriteRaw(who, "UnlockedSkillNodes", 0x908, BitConverter.ToUInt32(u, 0x908) + 1);
            if (!put || !bumped) Info($"learn: LabPutInSave {put}, changeCount bumped {bumped}");
        }
        await Steps(3);
    }

    private async Task Suite()
    {
        foreach (var c in new[] { Crafting.SourcesCapability, Crafting.RespecSourcesCapability, Stations.CraftSourcesCapability })
            Check(Capabilities.Is(c), $"{c} is available ({Capabilities.Why(c)})");
        if (!Capabilities.Is(Crafting.RespecSourcesCapability)) return;
        var m = GetModuleHandleW("kiln-items.dll");
        var fs = m == 0 ? 0 : GetProcAddress(m, "kiln_stations_craft_stats");
        var fl = m == 0 ? 0 : GetProcAddress(m, "kiln_stations_lab_respec_link");
        if (fs != 0) _stats = Marshal.GetDelegateForFunctionPointer<StatsFn>(fs);
        if (fl != 0) _link = Marshal.GetDelegateForFunctionPointer<LinkFn>(fl);
        if (!Check(_stats is not null && _link is not null, "kiln-items.dll's payment counters and lab link are there")) return;

        var bases = Bases.All;
        if (!Check(bases.Count >= 1, $"Bases.All: {bases.Count} base(s)")) return;
        WorldPosition? spot = null;
        if (bases[0].Altars.Count > 0 && World.Position(bases[0].Altars[0].Entity) is { } a0) spot = new WorldPosition(a0.X + 4, a0.Y, a0.Z);
        if (!Check(spot is not null, $"a spot in the base: {spot}")) return;
        var at = spot!.Value;
        var magic = await Place(MagicChest, new WorldPosition(at.X, at.Y, at.Z + 3));
        var chest = await Place(Chest, new WorldPosition(at.X + 2.5, at.Y, at.Z + 3));
        var who = await Spawn.Entity(PlayerTemplate, new WorldPosition(at.X + 1.5, at.Y + 0.5, at.Z + 1), 0,
            new SpawnOptions { Tag = "respec-test", Flags = SpawnFlags.Transient });
        if (!who.IsNone) _spawned.Add(who);
        await Steps(40);
        var home = bases[0].Id;
        if (!Check(!magic.IsNone && !chest.IsNone && !who.IsNone && World.Has(who, "PlayerInput") && Items.Read(who) is { Initialized: true },
                $"placed: Magic chest {magic}, chest {chest}; the payer is a player character {who}")) return;
        Check(Bases.Of(who)?.Id == home && Bases.Of(magic)?.Id == home, $"the payer and the Magic chest stand in base {home}");
        // no Runes anywhere but where a case puts them: the base's Magic chests are emptied of Runes first (the lab's save copy)
        foreach (var s in Bases.Storage(home)) if (Count(s) > 0) await SetRunes(s, 0);
        await SetRunes(who, 0);
        await SetRunes(chest, 0);
        Check(Bases.Stock(home, Rune) == 0 && Count(who) == 0 && Count(chest) == 0, "no Runes in the bag, the base's Magic chests or the chest");
        // the tree: the join check skipped (the character never gets skill-point knowledge), the node as a save holds it
        Check(GameTree.GameRequest(who, 3), "the character's tree is initialized (lab op 3)");
        await Steps(3);
        await Learn(who);
        if (!Check(Learned(who) && GameTree.PointsSpent(who) == Cost, $"node 0x{Node:x8} learned, {GameTree.PointsSpent(who)} point(s) spent")) return;

        // 1. no link: Kiln sees the payer's call and stands aside; the game refuses (no Runes), the node stays
        var (b, a) = await Respec(who, 1, "1 no link", who, chest);
        Check(D(b, a, St.Respec) == 1 && D(b, a, St.Paid) == 0 && D(b, a, St.Considered) == 0 && Learned(who),
              "1. no link: the respec payer's InventoryPay call reaches Kiln's payment (respec/RespecPaid), Kiln stands aside, the game refuses");

        // the lab link: the chest is this character's respec link from here on
        Check(_link!(who.Value, chest.Value) == 1, "the lab respec link is accepted (KILN_STATIONS_CRAFT_LAB=1)");
        await Steps(3);

        // 2. Runes only in the linked chest
        await SetRunes(chest, 5);
        (b, a) = await Respec(who, 1, "2 only the link", who, chest);
        Check(!Learned(who) && Count(chest) == 5 - Cost && D(b, a, St.Paid) == 1 && D(b, a, St.Respec) == 1,
              $"2. Runes only in the linked chest: the unlearn is paid from it ({Count(chest)} left), the node is gone");

        // 3. the bag first, the link for the rest
        await Learn(who); await SetRunes(who, 1); await SetRunes(chest, 5);
        (b, a) = await Respec(who, 1, "3 bag then link", who, chest);
        Check(!Learned(who) && Count(who) == 0 && Count(chest) == 4 && D(b, a, St.Paid) == 1 && D(b, a, St.Respec) == 1,
              "3. one Rune in the bag: the bag pays first, the link the other");

        // 4. the bag covers it: the game's own payment, nothing from the link
        await Learn(who); await SetRunes(who, Cost); await SetRunes(chest, 5);
        (b, a) = await Respec(who, 1, "4 the bag covers it", who, chest);
        Check(!Learned(who) && Count(who) == 0 && Count(chest) == 5 && D(b, a, St.Covered) == 1 && D(b, a, St.Paid) == 0 && D(b, a, St.Respec) == 1,
              "4. the bag covers it: the game pays, the linked chest is untouched");

        // 5. short even with the link: refused, nothing moved
        await Learn(who); await SetRunes(who, 0); await SetRunes(chest, 1);
        (b, a) = await Respec(who, 1, "5 short", who, chest);
        Check(Learned(who) && Count(chest) == 1 && D(b, a, St.Short) == 1 && D(b, a, St.Paid) == 0 && D(b, a, St.Respec) == 1,
              "5. short even with the link: the game refuses, the node stays, nothing moved");

        // 6. a Rune in a Magic chest of the base: it pays before the link
        await Learn(who); await SetRunes(who, 0); await SetRunes(magic, 1); await SetRunes(chest, 5);
        (b, a) = await Respec(who, 1, "6 Magic chest then link", who, magic, chest);
        Check(!Learned(who) && Count(magic) == 0 && Count(chest) == 4 && D(b, a, St.Paid) == 1 && D(b, a, St.Respec) == 1,
              "6. one Rune in the base's Magic chest: it pays before the link");

        // 7. the paid reset: the points spent (2), from the link
        await Learn(who); await SetRunes(who, 0); await SetRunes(magic, 0); await SetRunes(chest, 5);
        (b, a) = await Respec(who, 2, "7 paid reset", who, chest);
        Check(!Learned(who) && Count(chest) == 5 - Cost && D(b, a, St.Paid) == 1 && D(b, a, St.Respec) == 1,
              "7. the paid reset takes the points spent from the linked chest");

        // 8. a reset with nothing spent costs nothing: the payer answers paid without a transaction, Kiln is never asked
        await SetRunes(chest, 5);
        (b, a) = await Respec(who, 2, "8 reset with nothing spent", who, chest);
        Check(D(b, a, St.Respec) == 0 && Count(chest) == 5 && GameTree.PointsSpent(who) == 0,
              "8. a reset with nothing spent is free: the payment is never asked, the linked chest is untouched");
    }
}
