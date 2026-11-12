// SPDX-License-Identifier: MIT
// Task t3: the player half that a lab can show without a client. A simulated player (KILN_PLAYERS_SIMULATE with
// fixtures/player-visits.log, KILN_CHARACTERS_SIMULATE=1) joins, leaves and rejoins in another slot through kiln.players'
// real event path, with a CharacterId from kiln.characters' simulation. It has no entity and so no inventory: what this
// proves is that the store's join and leave paths run for it, that a character's records (seeded into the world's
// generation by fixtures/seed-character.py before the boot) stay parked across a leave, a rejoin, an autosave and a
// restart, keep their values, are listed, and attach to nothing. What happens to items in a real player's inventory is
// in ClientChecks.cs (guided checks). How to run: fixtures/run-players.sh.
using Kiln;

namespace InstancesTest;

/// <summary>The value the t3 checks keep on an item (the lab's seeded records and the guided checks).</summary>
public sealed class T3Mark
{
    public string Label { get; set; } = "";
    public int N { get; set; }
}

public sealed partial class InstancesTestMod
{
    // The two characters of fixtures/player-visits.log (one Steam account, as character-test's), and the records
    // fixtures/seed-character.py writes for them: A has two equal swords in flat slots 10 and 11, B one in slot 3.
    internal const ulong T3Account = 0x1111222233334444;
    internal static readonly Guid T3CharA = Guid.Parse("aaaaaaaa-0000-4000-8000-0000000000a1");
    internal static readonly Guid T3CharB = Guid.Parse("bbbbbbbb-0000-4000-8000-0000000000b2");
    private static readonly (Guid Character, int Slot, string Label, int N)[] T3Seeded =
        [(T3CharA, 10, "A-10", 10), (T3CharA, 11, "A-11", 11), (T3CharB, 3, "B-3", 3)];

    internal InstanceKey<T3Mark> T3Key = null!;
    internal ulong T3Step;
    private readonly List<string> _t3Sim = [];
    private int _t3SimFail;

    internal bool T3Simulated => Players.Available && Players.Diagnostics.Status is { Simulated: true };

    [LabOnLoad("t3.setup")]
    private void T3Setup()
    {
        T3Key = InstanceData.Key<T3Mark>("t3.mark");
        Context.Tick.Pre += T3OnPre;
        if (!Players.Available) return;
        Players.Left += T3OnLeft;   // the guided checks read the leave's order from T3Log
        if (!T3Simulated) return;
        // slot 0 plays A first; at A's leave Birch takes slot 0 with B and A comes back in slot 1 (fixture order)
        var a = PlayerCharacters.Simulate(0, T3Account, T3CharA);
        PlayerCharacters.Simulate(1, T3Account, T3CharA);
        Info($"t3 sim: characters simulated ({a}): slot 0 and 1 -> A until A leaves");
        Players.Joined += T3SimJoined;
        Players.Left += T3SimLeft;
    }

    private void T3OnPre(in TickInfo t) => T3Step = t.Step;

    private void T3OnLeft(Player p) => T3Log.Add((T3Step, "left", p.CharacterId.ToString(), $"{p.Name} slot {p.Slot}"));

    private void T3SimJoined(Player p)
    {
        _t3Sim.Add($"joined {p.Name} slot {p.Slot} character {p.CharacterId}");
        T3SimState($"step {T3Step}: {p.Name} joined (slot {p.Slot}, entity {p.Entity}, character {p.CharacterId})");
    }

    private void T3SimLeft(Player p)
    {
        _t3Sim.Add($"left {p.Name} slot {p.Slot} character {p.CharacterId}");
        T3SimState($"step {T3Step}: {p.Name} left (slot {p.Slot}, character {p.CharacterId})");
        if (p.Slot == 0) PlayerCharacters.Simulate(0, T3Account, T3CharB);   // the fixture's next player in slot 0 is Birch
    }

    /// <summary>The store's view of the seeded records now: logged, and every difference from the seed counts.</summary>
    private void T3SimState(string when)
    {
        var parked = InstanceData.Parked(T3Key);
        var all = InstanceData.All(T3Key);
        var lines = parked.Select(p => $"{p.Character.Id.ToString()[..8]} slot {p.FlatSlot} {p.State} {p.Value?.Label}={p.Value?.N} ({p.Reason})");
        Info($"t3 sim {when}: {all.Count} on items, {parked.Count} parked: {string.Join("; ", lines)}");
        var ok = all.Count == 0 && T3Seeded.All(s => parked.Count(p => p.Character.Id == s.Character && p.FlatSlot == s.Slot &&
                                                                       p.Value is { } v && v.Label == s.Label && v.N == s.N) == 1);
        if (!ok) _t3SimFail++;
    }

    private int _t3Restored, _t3LostMine;

    [Subscribe]
    private void T3OnRestored(InstanceDataRestoredEvent e)
    {
        if (e.Mod != Context.Info.Id || e.Key != "t3.mark") return;
        _t3Restored++;
        T3Log.Add((T3Step, "restored", e.Item.ToString(), $"{e.Match}: {e.Detail}"));
        Info($"t3 event: restored t3.mark on {e.Item} ({e.Match}): {e.Detail}");
    }

    [Subscribe]
    private void T3OnLost(InstanceDataLostEvent e)
    {
        if (e.Mod != Context.Info.Id || !e.Keys.Contains("t3.mark")) return;
        _t3LostMine++;
        T3Log.Add((T3Step, "lost", e.LastRef.ToString(), $"{e.Reason}: {e.Detail}"));
        Info($"t3 event: lost t3.mark of {e.Item} last {e.LastRef} ({e.Reason}): {e.Detail}");
    }

    [Subscribe]
    private void T3OnParked(InstanceDataParkedEvent e)
    {
        if (e.Mod != Context.Info.Id || !e.Keys.Contains("t3.mark")) return;
        T3Log.Add((T3Step, "parked", e.Character.Id.ToString(), $"{e.Reason} slot {e.FlatSlot}: {e.Detail}"));
        Info($"t3 event: parked t3.mark of {e.Item} in {e.Holder} slot {e.FlatSlot} ({e.Reason}): {e.Detail}");
    }

    [Subscribe]
    private void T3OnSaving(WorldSavingEvent e)
    {
        T3Log.Add((T3Step, "saving", e.Revision.ToString(), e.Reason.ToString()));
        if (T3Simulated) T3SimState($"step {T3Step}: saving revision {e.Revision} ({e.Reason})");
    }

    /// <summary>What the t3 checks saw, in order: (step, kind, who, detail). Kinds: restored, lost, parked, saving, left.</summary>
    internal readonly List<(ulong Step, string Kind, string Who, string Detail)> T3Log = [];

    [LabCheck(300, "t3.players")]
    private async Task T3Players()
    {
        if (!T3Simulated)
        {
            Info("t3.players: not a simulated run (KILN_PLAYERS_SIMULATE unset): skipped");
            return;
        }
        T3SimState($"step {T3Step}: before the visits");
        var parked = InstanceData.Parked(T3Key);
        Check(parked.Count == T3Seeded.Length, $"t3.players: the seeded records of two characters are read from the generation ({parked.Count} of {T3Seeded.Length})");
        // the fixture: A joins (slot 0), leaves; B joins slot 0; A rejoins in slot 1; both stay
        var done = await Until(() => _t3Sim.Count(s => s.StartsWith("joined")) >= 3, 25 * 60 * 6);
        await Steps(25 * 5);
        Check(done, $"t3.players: the visits ran: {string.Join(", ", _t3Sim)}");
        Check(_t3Sim.Count >= 4 && _t3Sim[0].Contains("slot 0") && _t3Sim[0].EndsWith(T3CharA.ToString()) &&
              _t3Sim[1].StartsWith("left") && _t3Sim[2].Contains("slot 0") && _t3Sim[2].EndsWith(T3CharB.ToString()) &&
              _t3Sim[3].Contains("slot 1") && _t3Sim[3].EndsWith(T3CharA.ToString()),
            "t3.players: A joins in slot 0 and leaves, B takes slot 0, A comes back in slot 1, each with its CharacterId");
        T3SimState($"step {T3Step}: after the visits");
        Check(_t3SimFail == 0, $"t3.players: at every join, leave and save the store held all {T3Seeded.Length} records, waiting, with their values, and none on an item ({_t3SimFail} differences)");
        Check(_t3Restored == 0 && _t3LostMine == 0, $"t3.players: nothing restored, nothing lost ({_t3Restored} restored, {_t3LostMine} lost events)");
        Check(InstanceData.All(T3Key).Count == 0, "t3.players: no item carries a t3.mark value");
        var save = await Saves.Request();
        T3SimState($"step {T3Step}: after a save with both players in game");
        Check(save.Success && _t3SimFail == 0, $"t3.players: a save with both simulated players in game keeps the records ({save})");
        Info("t3: READY end");
    }
}
