// SPDX-License-Identifier: MIT
// kiln.playerstate lab test, no client. Prints "playerstate-test: PASS ..." / "FAIL ..." / "INFO ..." lines to kiln.log. Everything it
// spawns is removed at the end. Needs KILN_PLAYERSTATE_LAB=1 for the consume tests (they skip with an INFO line without it).
using System.Text;
using Kiln;

namespace PlayerStateTest;

public sealed class PlayerStateTestMod : Mod
{
    // The game's sequences, FNV-1a of these GUIDs is what actors report.
    private static readonly Guid Greet = new("13fd798d-033a-494b-95e8-76fe817202b5");      // Ability_Player_Emote_Greet
    private static readonly Guid Glide = new("9c7cee31-58bc-4ae7-b963-c779a0f94dfc");      // Movement_Tool_HangGlider_Start
    private static readonly Guid Dodge = new("01ff63b6-56f4-483b-bf86-64639a062265");      // Ability_Movement_Dodge
    private static readonly Guid Sleep = new("5bd75f9b-d11f-428c-be3a-3c84653953cf");      // Interaction_Sleep
    private static readonly Guid Sit = new("b5e65ebc-c1fc-4fef-a127-d133512fa701");        // Interaction_Sit
    private static readonly Guid Hook = new("2da510ec-0cf3-4953-b9c7-b4a8ced3648e");       // Movement_Tool_HookPull_Self
    private static readonly Guid Parry = new("2b856cb6-c2d2-4e93-88d4-d07ee44c613d");      // Ability_Player_Parry_AllWeapons
    private static readonly Guid DoubleJump = new("e7b5dbb5-db37-4efa-ba2e-8e94c3f35fba"); // Ability_Movement_Skill_JumpDoubleSecond
    private static readonly Guid Fists = new("06cb777d-8ee2-4e2c-bb2b-c5626cc01b9e");      // Attack_Player_Unarmed_Fists_LightChain01
    private static readonly Guid UseFood = new("90dc9374-acbf-4213-b01c-71ea7e9c6565");    // UseItem_Food_Buff
    private static readonly Guid UseDrink = new("04c763f3-f4a6-481f-9c13-db20718f131c");   // UseItem_Food_Liquid_Buff
    private static readonly Guid UsePotion = new("0c55f8b3-165c-4333-9f61-8680b27ba3c0");  // UseItem_Potion_Health

    private const uint Mushroom = 0x23d06ec5;  // Food_T1_raw_mushroom_Mushroom_brown
    private const uint Milk = 0x1a73be52;      // Food_T5_raw_hydration_MountaingoatMilk
    private const uint Potion = 0xdcb1dded;    // Consumable_Potion_Health_T4
    private const uint Glider = 0x3d4a78dd;    // Tool_Glider_T1
    private const uint Sword = 0x4c0d3e02;     // Weapon_T1_1H_Sword_Craft

    private int _pass, _fail;
    private readonly List<EntityId> _spawned = new();
    private readonly List<PlayerStateChangedEvent> _states = new();
    private readonly List<PlayerActionEvent> _actions = new();
    private readonly List<PlayerConsumedEvent> _consumed = new();
    private readonly List<PlayerActionEndedEvent> _ended = new();
    private readonly List<PlayerJumpEvent> _jumps = new();
    private readonly List<PlayerGlideEvent> _glides = new();
    private readonly List<PlayerEquipmentChangedEvent> _equip = new();
    private readonly List<PlayerHotbarChangedEvent> _hotbar = new();
    private readonly List<PlayerClientActionEvent> _client = new();
    private Func<PlayerClientActionEvent, bool>? _cancelClientIf;
    private long _stateEventsTotal;
    private Func<PlayerActionEvent, bool>? _cancelIf;
    private bool _record = true;
    private nuint _world;

    [Subscribe] private void OnState(PlayerStateChangedEvent e) { _stateEventsTotal++; if (_record) _states.Add(e); }

    [Subscribe]
    private void OnClientAction(PlayerClientActionEvent e)
    {
        if (_record) _client.Add(e);
        if (_cancelClientIf?.Invoke(e) == true) e.Cancel();
    }

    [Subscribe] private void OnConsumed(PlayerConsumedEvent e) { if (_record) _consumed.Add(e); }
    [Subscribe] private void OnEnded(PlayerActionEndedEvent e) { if (_record) _ended.Add(e); }
    [Subscribe] private void OnJump(PlayerJumpEvent e) { if (_record) _jumps.Add(e); }
    [Subscribe] private void OnGlide(PlayerGlideEvent e) { if (_record) _glides.Add(e); }
    [Subscribe] private void OnEquip(PlayerEquipmentChangedEvent e) { if (_record) _equip.Add(e); }
    [Subscribe] private void OnHotbar(PlayerHotbarChangedEvent e) { if (_record) _hotbar.Add(e); }

    [Subscribe]
    private void OnAction(PlayerActionEvent e)
    {
        if (_record) _actions.Add(e);
        if (_cancelIf?.Invoke(e) == true) e.Cancel();
    }

    // The way a mod would write it.
    private readonly List<string> _eaten = new();

    [Subscribe]
    private void OnEat(PlayerConsumeEvent e)
    {
        if (e.Item == "Food_T1_raw_mushroom_Mushroom_brown") _eaten.Add("mushroom");
        else _eaten.Add(e.Item.Name);
    }

    public override void OnLoad()
    {
        // kiln/data/playerstate-test/watch: no suite, a line in kiln.log for everything a real player does.
        if (File.Exists(Path.Combine(Context.DataDirectory, "watch"))) StartWatch();
        // kiln/data/playerstate-test/failsafe: for a lab whose deployed sigdb had a pattern broken on purpose
        else if (File.Exists(Path.Combine(Context.DataDirectory, "failsafe"))) _ = FailSafe();
        else if (Client.IsDedicatedServer) _ = Run();
        else Info("client: the automatic suite runs on a dedicated server only");
    }

    // ---- watching a real player (the manual test) ----
    private bool _noisy;
    private string _cancel = "off";

    private void W(string s) => Log.Info("WATCH " + s);

    private void StartWatch()
    {
        _record = false;
        W($"on: {PlayerStates.Status}; side {(Client.IsDedicatedServer ? "dedicated" : Client.IsRemote ? "joined" : "host or menu")}");
        Events.On<PlayerStateChangedEvent>(e =>
        {
            if (!_noisy && PlayerStates.SourceOf(e.State) is PlayerStateSource.Ability or PlayerStateSource.Input) return;
            W($"{e.Player.Entity} {(e.Entered ? "+" : "-")}{e.State} ({e.Source}.{e.Name}) origin {e.Snapshot.Origin}");
        });
        Events.On<PlayerActionEvent>(e =>
        {
            var c = e as PlayerConsumeEvent;
            W($"{e.Player.Entity} action {e.SequenceName} trigger {e.Trigger} input {e.Input} item {e.Item}{(c is null ? "" : $" CONSUME {c.Kind} buffs [{string.Join(",", c.Buffs.Select(b => $"0x{b.Id:x8} {b.Slot}"))}]")} canCancel {e.CanCancel}");
            if (_cancel == "all" || (_cancel == "eat" && c is not null) || (_cancel == "glide" && e.Trigger == PlayerStateId.ActionGlider))
            {
                e.Cancel();
                W($"{e.Player.Entity} canceled {e.SequenceName}");
            }
        });
        Events.On<PlayerConsumedEvent>(e => W($"{e.Player.Entity} consumed {e.Item} {e.Kind}, {e.Left} left"));
        Events.On<PlayerActionEndedEvent>(e => W($"{e.Player.Entity} ended {e.Action.SequenceName} after {e.Duration.TotalSeconds:F2} s canceled {e.Canceled}"));
        Events.On<PlayerJumpEvent>(e => W($"{e.Player.Entity} jump {e.Type} count {e.Count} double {e.IsDoubleJump}"));
        Events.On<PlayerGlideEvent>(e => W($"{e.Player.Entity} glide {(e.Started ? "start" : "end")} glider {e.Glider} after {e.Duration.TotalSeconds:F1} s"));
        Events.On<PlayerEquipmentChangedEvent>(e => W($"{e.Player.Entity} equipment {e.Slot}: {e.Before} -> {e.Now}"));
        Events.On<PlayerHotbarChangedEvent>(e => W($"{e.Player.Entity} hotbar {e.Before} -> {e.Now}, held {e.HeldBefore} -> {e.Held}"));
        Events.On<PlayerClientActionEvent>(e =>
        {
            W($"{e.Player.Entity} client action {e.Name} v{e.Version} canCancel {e.CanCancel}");
            if (_cancel == "equip" && e.Name == "changeEquipmentAction")
            {
                e.Cancel();
                W($"{e.Player.Entity} canceled {e.Name}");
            }
        });
        try
        {
            RegisterWatchCommand();
        }
        catch (Exception ex)
        {
            W("no /pst command here: " + ex.Message);
        }
    }

    private void RegisterWatchCommand()
    {
        Commands.Register("pst", ctx =>
        {
            var what = ctx.Count > 0 ? ctx.Args[0].ToLowerInvariant() : "state";
            var p = ctx.Source.IsPlayer ? ctx.Source.Player : Players.All.FirstOrDefault()?.Entity ?? EntityId.None;
            if (what == "cancel") { _cancel = ctx.Count > 1 ? ctx.Args[1].ToLowerInvariant() : "off"; ctx.Reply("cancel: " + _cancel); return; }
            if (what == "noisy") { _noisy = !_noisy; ctx.Reply("noisy: " + _noisy); return; }
            var s = PlayerStates.Read(p);
            var line = s is null ? "unreadable" : $"origin {s.Origin}, {string.Join(" ", s.Active.Where(x => PlayerStates.SourceOf(x) is not (PlayerStateSource.Ability or PlayerStateSource.Input)).Select(x => x.ToString()))}; health {s.Health}/{s.HealthMax} stamina {s.Stamina}/{s.StaminaMax} mana {s.Mana}/{s.ManaMax} shroud {s.ShroudTimeLeft} oxygen {s.Oxygen}/{s.OxygenMax}; running {s.Action?.SequenceName ?? "nothing"}";
            var q = PlayerStates.EquipmentOf(p);
            W($"{p} state: {line}; held {q?.Held}, bar {q?.HotbarIndex}; buffs {string.Join(",", PlayerStates.BuffsOf(p).Select(b => $"0x{b.Id:x8} {b.Slot} {b.Remaining?.TotalSeconds:F0}s"))}");
            ctx.Reply(line);
        });
    }

    private void Info(string s) => Log.Info("playerstate-test: INFO " + s);
    private void Check(bool ok, string s)
    {
        if (ok) { _pass++; Log.Info("playerstate-test: PASS " + s); }
        else { _fail++; Log.Warn("playerstate-test: FAIL " + s); }
    }
    private async Task Steps(int n) { for (var i = 0; i < n; i++) await Context.Scheduler.NextTick(); }

    private async Task Run()
    {
        try
        {
            var hook = Hooks.Detour("GetComponent", c => { if (_world == 0) _world = (nuint)c[1]; return c.CallOriginal(); });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            hook.Remove();
            TestTable();
            var p = await SpawnPlayer();
            if (!p.IsNone)
            {
                await TestRead(p);
                await TestStatesAndActions(p);
                await TestConsume(p);
                await TestFakeInput(p);
                await TestEquipment(p);
                await TestRealInput(p);
                await TestCost(p);
                await TestOtherThread(p);
            }
        }
        catch (Exception ex)
        {
            Check(false, "the test threw: " + ex);
        }
        foreach (var e in _spawned) Spawn.Destroy(e);
        _record = false;
        Log.Info($"playerstate-test done: {_pass} PASS, {_fail} FAIL");
    }

    // What a build looks like where a signature did not resolve: nothing wrong is reported, the world runs on.
    private async Task FailSafe()
    {
        try
        {
            while (!Spawn.WorldRunning) await Context.Scheduler.NextTick();
            await Steps(60);
            var s = PlayerStates.Status;
            Info($"status on this sigdb: {s}");
            var e = await SpawnPlayer();
            _actions.Clear();
            _states.Clear();
            _cancelIf = _ => true;
            Check(Abilities.PlaySequence(e, Greet), "a sequence plays");
            await Steps(10);
            _cancelIf = null;
            if (s is { Reflection: false })
            {
                Check(!PlayerStates.Available, "not available");
                Check(PlayerStates.Read(e) is null, "Read gives nothing");
                Check(PlayerStates.Check(e, PlayerStateId.Gliding) is null, "Check is null: unknown");
                Check(!PlayerStates.Is(e, PlayerStateId.Gliding), "Is is false");
                Check(_actions.Count == 0 && _states.Count == 0, "no event fires");
                Check(Abilities.SequenceOf(e)?.Sequence == Abilities.IdOf(Greet), "the game is untouched: the sequence runs");
            }
            else
            {
                Check(PlayerStates.Available, "states still work");
                var a = _actions.FirstOrDefault();
                Check(a is { CanCancel: false }, $"the action's event says it cannot be canceled: {a}");
                Check(Abilities.SequenceOf(e)?.Sequence == Abilities.IdOf(Greet), "a handler's Cancel changed nothing: the sequence runs");
                Check(a is not null && a.SequenceName == (s?.SequenceNames == true ? "Ability_Player_Emote_Greet" : ""), $"its name: '{a?.SequenceName}'");
            }
        }
        catch (Exception ex)
        {
            Check(false, "the test threw: " + ex);
        }
        foreach (var e in _spawned) Spawn.Destroy(e);
        Log.Info($"playerstate-test done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task<EntityId> SpawnPlayer()
    {
        var at = Near();
        var e = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = "playerstate-test", Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        await Steps(40);
        Check(World.Has(e, "PlayerInput") && World.Has(e, "PlayerState"), $"spawned player character {e} at {at} has PlayerInput and PlayerState");
        PlayerStates.Diagnostics.Watch(e);
        await Steps(10);
        return e;
    }

    private static WorldPosition Near()
    {
        var altars = World.Find("BaseIdComponent");
        foreach (var a in altars)
            if (World.Position(a) is { } q) return new WorldPosition(q.X + 45, q.Y + 3, q.Z + 45);
        return new WorldPosition(3845, 835, 1480);
    }

    // ---- the table comes from the running game ----
    private void TestTable()
    {
        var s = PlayerStates.Status;
        Check(s is { Reflection: true }, $"kiln.playerstate reads the game's reflection: {s}");
        Check(PlayerStates.Available, "PlayerStates.Available");
        Check(s is { Cancel: true, SequenceNames: true, ItemDetails: true }, "cancel, sequence names and item details resolved");
        Check(s?.Sources == 21, $"all 21 sources resolved ({s?.Sources})");
        var all = PlayerStates.All;
        var unnamed = all.Where(i => !i.NamedByKiln).Select(i => $"{i.Source}.{i.GameName}").ToList();
        Check(unnamed.Count == 0, $"every state of this build has a constant in Kiln ({all.Count} states, unnamed: {string.Join(",", unnamed)})");
        var missing = Enum.GetValues<PlayerStateId>().Where(v => v != PlayerStateId.None && PlayerStates.InfoOf(v) is null).ToList();
        Check(missing.Count == 0, $"every constant exists in the running game (missing: {string.Join(",", missing)})");
        Check(PlayerStates.NameOf(PlayerStateId.Gliding) == "HangGliding", "Gliding is the game's HangGliding");
        Check(PlayerStates.InfoOf(PlayerStateId.Gliding) is { Raw: 37, IsFlag: true }, "HangGliding is bit 37, by the game's own table");
        Check(PlayerStates.Find("IsSprinting") == PlayerStateId.IsSprinting, "Find by the game's name");
        Check(PlayerStates.Raw(PlayerStateSource.Actor, 37) == PlayerStateId.HangGliding, "Raw(Actor, 37) is HangGliding");
        var unknown = PlayerStates.Raw(PlayerStateSource.Actor, 63);
        Check(PlayerStates.InfoOf(unknown) is null && (ushort)unknown == 0x013F, $"a bit the game does not name keeps its raw id: 0x{(ushort)unknown:x4}");
        var bySource = all.GroupBy(i => i.Source).Select(g => $"{g.Key} {g.Count()}");
        Info("states by source: " + string.Join(", ", bySource));
    }

    // ---- reading ----
    private async Task TestRead(EntityId e)
    {
        var snap = PlayerStates.Read(e);
        Info("buffs at spawn: " + string.Join(", ", PlayerStates.BuffsOf(e).Select(b => $"0x{b.Id:x8} {b.Slot} left {b.Remaining?.TotalSeconds:F0} s")));
        Info("derived at spawn: " + string.Join(" ", snap?.Active.Where(x => (ushort)x >> 8 == 22).Select(x => x.ToString()) ?? []));
        Check(snap is not null, "PlayerStates.Read gives a snapshot of a spawned player character");
        if (snap is null) return;
        Info("active: " + string.Join(" ", snap.Active.Select(s => s.ToString())));
        Info($"vitals: health {snap.Health}/{snap.HealthMax} stamina {snap.Stamina}/{snap.StaminaMax} mana {snap.Mana}/{snap.ManaMax} heat {snap.BodyHeat}/{snap.BodyHeatMax} special {snap.SpecialAbility}/{snap.SpecialAbilityMax} shroud {snap.ShroudResistance}/{snap.ShroudResistanceMax} oxygen {snap.Oxygen}/{snap.OxygenMax} level {snap.Level} comfort {snap.Comfort}");
        Info($"movement: jumps {snap.JumpCount} water {snap.WaterDepth} canSwim {snap.CanSwim} canDive {snap.CanDive}");
        Check(snap.Origin == PlayerStateOrigin.Server, "read from the server world");
        Check(snap.RawValue(PlayerStateSource.Actor) is not null, "the Actor source is known");
        Check(snap.Check(PlayerStateId.Airborne) is not null, "Airborne is known");
        Check(snap.Is(PlayerStateId.Hungry) && !snap.Is(PlayerStateId.Fed), "a new character is Hungry, not Fed (the game's hunger marker is not food)");
        await Steps(1);
    }

    private async Task<bool> PlayAndWatch(EntityId e, Guid seq, PlayerStateId wanted, int maxSteps, string label)
    {
        _states.Clear();
        var ok = Abilities.PlaySequence(e, seq);
        Check(ok, $"{label}: PlaySequence accepted");
        var saw = false;
        var live = false;
        for (var i = 0; i < maxSteps && !(saw && live); i++)
        {
            await Steps(1);
            saw |= _states.Any(s => s.State == wanted && s.Entered);
            live |= PlayerStates.Is(e, wanted);
        }
        Check(saw, $"{label}: PlayerStateChangedEvent entered {wanted} ({PlayerStates.NameOf(wanted)})");
        Check(live, $"{label}: player.Is({wanted}) was true while it played");
        return saw;
    }

    // ---- sequences drive states and raise actions ----
    private async Task TestStatesAndActions(EntityId e)
    {
        _actions.Clear();
        _ended.Clear();
        await PlayAndWatch(e, Greet, PlayerStateId.ActionExternalAbortAndOverride, 20, "the Greet emote");
        var greet = _actions.FirstOrDefault(a => a.Action.Sequence == Abilities.IdOf(Greet));
        Check(greet is not null, "PlayerActionEvent for the emote");
        Check(greet?.SequenceName == "Ability_Player_Emote_Greet", $"the action's name comes from the game's data: {greet?.SequenceName}");
        Check(greet?.Trigger == PlayerStateId.ActionExternalAbortAndOverride, $"trigger {greet?.Trigger}");
        Check(greet?.Player.Entity == e, "the event names the player");
        Info($"emote event: {greet}");

        // the glide is a sequence with its own state
        _glides.Clear();
        var was = PlayerStates.Read(e);
        Info("before the glide: " + string.Join(" ", was?.Active.Select(s => s.ToString()) ?? []));
        var saw = await PlayAndWatch(e, Glide, PlayerStateId.Gliding, 25, "the glider");
        if (saw)
        {
            Check(_glides.Any(g => g.Started), $"PlayerGlideEvent started, glider {(_glides.FirstOrDefault()?.Glider)}");
            var player = new Player { Entity = e };
            Check(player.IsGliding, "player.IsGliding while it plays");
            var snap = PlayerStates.Read(e);
            Info("while gliding: " + string.Join(" ", snap?.Active.Select(s => s.ToString()) ?? []));
            Check(snap?.Action?.SequenceName == "Movement_Tool_HangGlider_Start", $"the running action: {snap?.Action?.SequenceName}");
            Check(snap?.Is(PlayerStateId.LocomotionHangGliding) == true, "the game's own locomotion state follows it: LocomotionHangGliding");
            Check(snap?.Is(PlayerStateId.Airborne) == true, "Airborne while gliding");
            for (var i = 0; i < 300 && (PlayerStates.Check(e, PlayerStateId.Gliding) != false); i++) await Steps(1);
            await Steps(2);
            Check(_glides.Any(g => g.Ended), $"PlayerGlideEvent ended after {_glides.LastOrDefault()?.Duration.TotalSeconds:F2} s");
            Check(_states.Any(s => s.State == PlayerStateId.Gliding && s.Left), "PlayerStateChangedEvent left Gliding");
        }
        await WaitIdle(e);
        await PlayAndWatch(e, Dodge, PlayerStateId.Dodging, 25, "a dodge");
        await PlayAndWatch(e, Sleep, PlayerStateId.Sleeping, 25, "sleeping");
        await PlayAndWatch(e, Sit, PlayerStateId.Sitting, 25, "sitting");
        await PlayAndWatch(e, Hook, PlayerStateId.Grappling, 25, "the grappling hook");
        await PlayAndWatch(e, Parry, PlayerStateId.Blocking, 25, "a parry");
        await WaitIdle(e);
        Info($"after the states: running {PlayerStates.Read(e)?.Action?.SequenceName ?? "nothing"}");

        // A real jump: actor_jump writes jumpType, jumpCount and lastJumpTime (0x1c2040); on a character with no terrain under it
        // the game refuses the jump itself, so the event's logic is tested with those fields.
        _jumps.Clear();
        var loco = Raw.Component(_world, "DynamicLocomotion", e);
        Raw.Set8(loco + 0x154, 2);   // jumpType DoubleDefault
        Raw.Set8(loco + 0x155, 2);   // jumpCount
        Raw.Set64(loco + 0xb8, (ulong)Environment.TickCount64 * 1000000);  // lastJumpTime
        for (var i = 0; i < 6 && _jumps.Count == 0; i++) await Steps(1);
        var jump = _jumps.FirstOrDefault();
        Check(jump is { Type: PlayerStateId.JumpDoubleDefault, Count: 2, IsDoubleJump: true }, $"PlayerJumpEvent from the game's jump fields: {jump?.Type}, count {jump?.Count}, double {jump?.IsDoubleJump}");
        await WaitIdle(e);

        // weapon swing
        _actions.Clear();
        Check(Abilities.PlaySequence(e, Fists), "a fist attack: PlaySequence accepted");
        for (var i = 0; i < 20 && _actions.Count == 0; i++) await Steps(1);
        Check(_actions.Any(a => a.SequenceName.StartsWith("Attack_Player_Unarmed", StringComparison.Ordinal)), $"PlayerActionEvent for the swing: {_actions.FirstOrDefault()?.SequenceName}");
        await WaitIdle(e);

        // cancel: the game's reset of a running sequence, no crash, the actor goes on
        _actions.Clear();
        _ended.Clear();
        _cancelIf = a => a.Action.Sequence == Abilities.IdOf(Greet);
        Check(Abilities.PlaySequence(e, Greet), "a Greet to cancel: PlaySequence accepted");
        for (var i = 0; i < 10 && _actions.Count == 0; i++) await Steps(1);
        await Steps(2);
        _cancelIf = null;
        var seq = Abilities.SequenceOf(e);
        Check(_actions.Any(a => a.Canceled), "a handler canceled the emote");
        Check(seq?.Sequence == 0, $"the canceled sequence is gone from the actor: 0x{seq?.Sequence:x8}");
        Check(_ended.Any(x => x.Canceled), "PlayerActionEndedEvent says canceled");
        Check(Abilities.PlaySequence(e, Fists), "the actor takes another sequence after a cancel");
        await Steps(4);
        Check(Abilities.SequenceOf(e)?.Sequence == Abilities.IdOf(Fists), "and plays it");
        await WaitIdle(e);
    }

    private async Task WaitIdle(EntityId e, int max = 240)
    {
        for (var i = 0; i < max && Abilities.SequenceOf(e)?.Sequence is not (0 or null); i++) await Steps(1);
        await Steps(3);
    }

    // ---- the real consume path: a used item in the trigger context, the game's actor_apply_buff ----
    private async Task<InventorySlot?> Give(EntityId e, uint item, int count)
    {
        var r = await Items.Give(e, new ItemId(item), count);
        await Steps(2);
        Info($"gave {count} x 0x{item:x8} to {e}: {r}");
        return Items.Read(e)?.Slots.FirstOrDefault(s => s.Item.Value == item);
    }

    private async Task TestConsume(EntityId e)
    {
        var slot = await Give(e, Mushroom, 5);
        if (slot is null) { Check(false, "the spawned player character takes a mushroom"); return; }
        if (!PlayerStates.Diagnostics.LabUseItem(e, Abilities.IdOf(UseFood), PlayerStateId.ActionPrimary, slot.Slot.Value, Mushroom))
        {
            Info("KILN_PLAYERSTATE_LAB is off: consume tests skipped");
            return;
        }
        _actions.Clear();
        _consumed.Clear();
        _eaten.Clear();
        for (var i = 0; i < 12 && _actions.Count == 0; i++) await Steps(1);
        var eat = _actions.OfType<PlayerConsumeEvent>().FirstOrDefault();
        Check(eat is not null, $"PlayerConsumeEvent when eating: {eat}");
        Check(eat is not null && eat.Item == "Food_T1_raw_mushroom_Mushroom_brown", $"e.Item compares with a GameItems name: {eat?.Item}");
        Check(eat is not null && eat.Item == Mushroom, "and with a GameItems.Ids value");
        Check(_eaten.Contains("mushroom"), "the handler written as a mod would write it ran its mushroom branch");
        Check(eat?.Kind == PlayerConsumeKind.Food && eat.IsFood, $"kind {eat?.Kind}");
        Check(eat?.SequenceName == "UseItem_Food_Buff", $"sequence {eat?.SequenceName}");
        Check(eat?.Buffs.Count == 1 && eat.Buffs[0].Slot == "Food", $"the buff it gives: {string.Join(",", eat?.Buffs.Select(b => $"0x{b.Id:x8} {b.Slot}") ?? [])}");
        Check(eat?.CanCancel == true, "CanCancel on the server");
        Check(_consumed.Count == 0, "not used up yet when the event fires");
        for (var i = 0; i < 60 && _consumed.Count == 0; i++) await Steps(1);
        var gone = _consumed.FirstOrDefault();
        Check(gone is not null, $"PlayerConsumedEvent when the item goes: {gone?.Item} {gone?.Kind}, left {gone?.Left}");
        Check(gone?.Left == 4, $"the game took one of 5 ({gone?.Left} left)");
        await Steps(4);
        var buffs = PlayerStates.BuffsOf(e);
        Info("buffs: " + string.Join(", ", buffs.Select(b => $"0x{b.Id:x8} {b.Slot} left {b.Remaining?.TotalSeconds:F0} s level {b.Level}")));
        Check(buffs.Any(b => b.Slot == "Food" && b.Id == eat?.Buffs[0].Id), "the food buff is on the player, from the game's own slots");
        await Steps(8);
        Info("state changes while eating: " + string.Join(" ", _states.Where(x => (ushort)x.State >> 8 is not (6 or 7)).Select(x => (x.Entered ? "+" : "-") + x.State)));
        Info("derived changes while eating: " + string.Join(" ", _states.Where(x => (ushort)x.State >> 8 == 22).Select(x => (x.Entered ? "+" : "-") + x.State)));
        Check(PlayerStates.Is(e, PlayerStateId.Fed), "Fed is on");
        Check(!PlayerStates.Is(e, PlayerStateId.Hungry), "Hungry is off");
        Check(_states.Any(s => s.State == PlayerStateId.Fed && s.Entered), "PlayerStateChangedEvent entered Fed");
        Check(_states.Any(s => s.State == PlayerStateId.Hungry && s.Left), "PlayerStateChangedEvent left Hungry");
        Check(_states.Any(s => s.State == PlayerStateId.UsingItem && s.Entered), "PlayerStateChangedEvent entered UsingItem");
        await WaitIdle(e);

        // canceled: no item used, no buff
        var before = Items.Read(e)?.Slots.First(s => s.Item.Value == Mushroom).Count;
        var buffCount = PlayerStates.BuffsOf(e).Count;
        _actions.Clear();
        _consumed.Clear();
        _cancelIf = a => a is PlayerConsumeEvent;
        Check(PlayerStates.Diagnostics.LabUseItem(e, Abilities.IdOf(UseFood), PlayerStateId.ActionPrimary, slot.Slot.Value, Mushroom), "eat again, to cancel");
        for (var i = 0; i < 12 && _actions.Count == 0; i++) await Steps(1);
        await Steps(40);
        _cancelIf = null;
        var after = Items.Read(e)?.Slots.First(s => s.Item.Value == Mushroom).Count;
        Check(_actions.OfType<PlayerConsumeEvent>().Any(a => a.Canceled), "the handler canceled the eat");
        Check(after == before, $"Cancel: nothing used up ({before} -> {after})");
        Check(_consumed.Count == 0, "Cancel: no PlayerConsumedEvent");
        Check(PlayerStates.BuffsOf(e).Count == buffCount, "Cancel: no new buff");
        await WaitIdle(e);

        // a drink, by the game's own liquid sequence
        var milk = await Give(e, Milk, 2);
        if (milk is not null)
        {
            _actions.Clear();
            _consumed.Clear();
            PlayerStates.Diagnostics.LabUseItem(e, Abilities.IdOf(UseDrink), PlayerStateId.ActionPrimary, milk.Slot.Value, Milk);
            for (var i = 0; i < 40 && _consumed.Count == 0; i++) await Steps(1);
            var d = _actions.OfType<PlayerConsumeEvent>().FirstOrDefault();
            Check(d?.Kind == PlayerConsumeKind.Drink && d.IsDrink, $"a drink: {d}");
            Check(_consumed.Any(c => c.Kind == PlayerConsumeKind.Drink), "a drink: PlayerConsumedEvent");
            await WaitIdle(e);
        }

        // a potion: consumed by a SpawnImpact, no buff
        var potion = await Give(e, Potion, 2);
        if (potion is not null)
        {
            _actions.Clear();
            _consumed.Clear();
            PlayerStates.Diagnostics.LabUseItem(e, Abilities.IdOf(UsePotion), PlayerStateId.ActionPrimary, potion.Slot.Value, Potion);
            for (var i = 0; i < 60 && _consumed.Count == 0; i++) await Steps(1);
            var d = _actions.OfType<PlayerConsumeEvent>().FirstOrDefault();
            Check(d?.Kind == PlayerConsumeKind.Potion, $"a potion: {d}");
            Check(_consumed.Any(c => c.Kind == PlayerConsumeKind.Potion), "a potion: PlayerConsumedEvent");
            await WaitIdle(e);
        }
    }

    // ---- fake input: what the game itself does with PlayerInput on a character with no client ----
    // The main action as a client sends a tap: held, then released with the Tap and Release bits.
    private async Task<string> Tap(EntityId e, int hold)
    {
        var pi = Raw.Component(_world, "PlayerInput", e);
        const int offset = 0x08 + 0x3a8;
        ulong Bit(PlayerStateId s) => 1ul << (int)PlayerStates.InfoOf(s)!.Raw;
        _actions.Clear();
        _states.Clear();
        _jumps.Clear();
        Raw.Or64(pi + offset, Bit(PlayerStateId.InputMainhandAction));
        await Steps(hold);
        Raw.And64(pi + offset, ~Bit(PlayerStateId.InputMainhandAction));
        Raw.Or64(pi + offset, Bit(PlayerStateId.InputMainhandAction_Tap) | Bit(PlayerStateId.InputMainhandAction_Release));
        await Steps(2);
        Raw.And64(pi + offset, ~(Bit(PlayerStateId.InputMainhandAction_Tap) | Bit(PlayerStateId.InputMainhandAction_Release)));
        await Steps(4);
        return $"actions [{string.Join(",", _actions.Select(a => $"{a.SequenceName} {a.Trigger} item {a.Item}"))}], states [{string.Join(" ", _states.Where(x => (ushort)x.State >> 8 is not (6 or 7)).Select(x => (x.Entered ? "+" : "-") + x.State))}]";
    }

    private async Task<string> Press(EntityId e, PlayerStateId input, int hold, int after)
    {
        var pi = Raw.Component(_world, "PlayerInput", e);
        const int offset = 0x08 + 0x3a8;  // PlayerInput.fromClient.digitalInput
        var bit = 1ul << (int)PlayerStates.InfoOf(input)!.Raw;
        _actions.Clear();
        _states.Clear();
        _jumps.Clear();
        Raw.Or64(pi + offset, bit);
        await Steps(hold);
        Raw.And64(pi + offset, ~bit);
        await Steps(after);
        return $"{input}: actions [{string.Join(",", _actions.Select(a => $"{a.SequenceName} {a.Trigger} item {a.Item}"))}], jumps [{string.Join(",", _jumps.Select(j => j.Type))}], states [{string.Join(" ", _states.Where(x => (ushort)x.State >> 8 is not (6 or 7)).Select(x => (x.Entered ? "+" : "-") + x.State))}]";
    }

    private async Task<bool> WaitFor(EntityId e, PlayerStateId state, int max)
    {
        for (var i = 0; i < max; i++)
        {
            if (PlayerStates.Is(e, state)) return true;
            await Steps(1);
        }
        return PlayerStates.Is(e, state);
    }

    private static string Short(PlayerSnapshot? s) => s is null ? "unreadable" :
        string.Join(" ", s.Active.Where(x => PlayerStates.SourceOf(x) is not PlayerStateSource.Input && PlayerStates.NameOf(x) is not ("Invalid" or "None" or "Holster")).Select(x => x.ToString()));

    // The game's own input path, on a character with no client: the same bits a client's input sets, read by the game's
    // control programs. Two gates are forced, nothing else: the client's "ready" flag and the terrain-loaded check.
    private async Task TestRealInput(EntityId e)
    {
        if (_world == 0) return;
        var pinput = Raw.Component(_world, "PlayerInput", e);
        Raw.Set8(pinput + 0x5b8, 1);  // PlayerInput.fromClientIsReadyForGameplay
        var ready = Symbols.FindAll("48 89 5C 24 08 57 48 83 EC 30 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 49 0F 2A 00 48 8B C2 48 8B F9 48 8D 54 24 20 48 8B C8 49 8B D8");
        foreach (var r in ready) Hooks.Detour(r, _ => 1);
        Check(await WaitFor(e, PlayerStateId.IsReadyForGameplay, 240), $"the character is ready for gameplay ({ready.Length} checks forced)");
        Info("ready: " + Short(PlayerStates.Read(e)));

        // gliding, from the input, with the glider the equip action put in its slot
        _glides.Clear();
        var line = await Press(e, PlayerStateId.InputGlider, 4, 6);
        Info("real input " + line);
        var glide = _actions.FirstOrDefault(a => a.Trigger == PlayerStateId.ActionGlider);
        Check(glide is not null, "real input: the game started the glide: PlayerActionEvent ActionGlider");
        Check(glide?.SequenceName == "Movement_Tool_HangGlider_Start", $"its sequence: {glide?.SequenceName}");
        Check(glide is not null && glide.Item == "Tool_Glider_T1", $"it says which glider: {glide?.Item}");
        Check(_states.Any(x => x.State == PlayerStateId.Gliding && x.Entered), "real input: entered Gliding");
        Check(_glides.Any(g => g.Started && g.Glider == Glider), "real input: PlayerGlideEvent with the equipped glider");
        for (var i = 0; i < 300 && PlayerStates.Check(e, PlayerStateId.Gliding) != false; i++) await Steps(1);
        await WaitIdle(e);

        // a canceled glide never takes off
        _cancelIf = a => a.Trigger == PlayerStateId.ActionGlider;
        if (!PlayerStates.Is(e, PlayerStateId.Grounded))
        {
            line = await Press(e, PlayerStateId.InputGlider, 2, 6);
            _cancelIf = null;
            Info("real input, canceled " + line);
            if (_actions.Any(a => a.Trigger == PlayerStateId.ActionGlider))
            {
                Check(_actions.Any(a => a.Trigger == PlayerStateId.ActionGlider && a.Canceled), "real input: Cancel on the glide");
                Check(!_states.Any(x => x.State == PlayerStateId.Gliding && x.Entered), "real input: a canceled glide never entered Gliding");
            }
            await WaitIdle(e);
        }
        _cancelIf = null;

        Check(await WaitFor(e, PlayerStateId.Grounded, 400), "the character reached ground");
        Info("grounded: " + Short(PlayerStates.Read(e)));
        await WaitIdle(e);

        line = await Press(e, PlayerStateId.InputJump, 2, 8);
        Info("real input " + line);
        Check(_jumps.Any(j => j.Type == PlayerStateId.JumpDefault), "real input: PlayerJumpEvent JumpDefault");
        Check(_states.Any(x => x.State == PlayerStateId.Jumping && x.Entered), "real input: entered Jumping");
        await WaitFor(e, PlayerStateId.Grounded, 200);
        await WaitIdle(e);

        line = await Press(e, PlayerStateId.InputEvade, 3, 6);
        Info("real input " + line);
        Check(_actions.Any(a => a.Trigger == PlayerStateId.ActionEvade), "real input: a dodge: PlayerActionEvent ActionEvade");
        Check(_states.Any(x => x.State == PlayerStateId.Dodging && x.Entered), "real input: entered Dodging");
        await WaitIdle(e);

        // Item use from the input does not start on a character with no client: a gate in the game's item-use check
        // (0xb1410) stays closed, and which one was not traced. The item path after the trigger is tested with
        // LabUseItem above; this logs what happens, for the day it opens.
        var bar = new EntityId(BitConverter.ToUInt32(World.ReadBytes(e, "SlotSelection")!, 0xa0));
        var inBar = Items.Read(e)?.Slots.FirstOrDefault(s => s.Item.Value == Mushroom && s.Slot.Value == SlotId.Of(bar, s.Slot.Index).Value);
        Info($"a mushroom stack in the action bar {bar}: {inBar?.Slot}");
        if (inBar is not null)
        {
            var barSlot = Raw.Component(_world, "PlayerInput", e);
            const int barAction = 0x08 + 0x60;
            Raw.Set8(barSlot + barAction + 4, (byte)inBar.Slot.Index);
            Raw.Set32(barSlot + barAction, Raw.Get32(barSlot + barAction) + 1);
            await Steps(6);
            var held = PlayerStates.EquipmentOf(e);
            Check(held is not null && held.Held == Mushroom, $"PlayerEquipment.Held follows the action bar: {held?.Held} at {held?.HotbarIndex}");
            Info("real input main action " + await Tap(e, 3));
            await WaitIdle(e);
            Raw.Set8(pinput + 0x5ac + 2, 2);  // PlayerInput.fromClientSettings.hotkeyBehaviorConsumables = OnlyUse
            Info("real input quick use " + await Press(e, (PlayerStateId)((ushort)PlayerStateId.InputQuickUse0 + inBar.Slot.Index), 3, 4));
            await WaitIdle(e);
        }
    }

    private async Task TestFakeInput(EntityId e)
    {
        if (_world == 0) { Info("fake input skipped: no world pointer"); return; }
        var size = World.ComponentSize(e, "PlayerInput");
        var pi = Raw.Component(_world, "PlayerInput", e);
        if (pi == 0) { Info("fake input skipped: no PlayerInput"); return; }
        var input = PlayerStates.InfoOf(PlayerStateId.InputJump)!.Raw;
        const int offset = 0x08 + 0x3a8;  // PlayerInput.fromClient.digitalInput, also what the source reads
        _states.Clear();
        _actions.Clear();
        _jumps.Clear();
        Raw.Or64(pi + offset, 1ul << (int)input);
        await Steps(3);
        Check(_states.Any(s => s.State == PlayerStateId.InputJump && s.Entered), "fake input: PlayerStateChangedEvent entered InputJump");
        Check(PlayerStates.Is(e, PlayerStateId.InputJump), "fake input: player.Is(InputJump)");
        await Steps(15);
        Info($"fake Jump input on a character without a client (component {size} B): actions [{string.Join(",", _actions.Select(a => a.SequenceName))}], jumps {_jumps.Count}, states [{string.Join(",", _states.Select(s => (s.Entered ? "+" : "-") + s.State))}]");
        Raw.And64(pi + offset, ~(1ul << (int)input));
        await Steps(3);
        Check(_states.Any(s => s.State == PlayerStateId.InputJump && s.Left), "fake input: left InputJump");
    }

    // ---- equipment and the action bar ----
    private async Task TestEquipment(EntityId e)
    {
        var q = PlayerStates.EquipmentOf(e);
        Check(q is not null, $"EquipmentOf reads the slots: held {q?.Held}, hotbar {q?.HotbarIndex}, glider {q?[PlayerEquipSlot.Glider]}");
        var inv = Items.Read(e);
        Info($"inventory: {inv?.Slots.Count} stacks, {string.Join(" ", inv?.Slots.Take(8).Select(s => $"0x{s.Item.Value:x8}x{s.Count}{(s.IsEquipped ? "*" : "")}") ?? [])}");
        var actions = PlayerStates.ClientActions;
        Check(actions.Count >= 45 && actions.Any(a => a.Name == "changeEquipmentAction" && a.CanCancel), $"the game's client actions, by its own field names: {actions.Count}, {actions.Count(a => a.CanCancel)} cancelable; not: {string.Join(",", actions.Where(a => !a.CanCancel).Select(a => a.Name))}");
        // give gear: the game equips what fits an empty slot
        _equip.Clear();
        var sword = await Give(e, Sword, 1);
        var glider = await Give(e, Glider, 1);
        await Steps(12);
        var r = PlayerStates.EquipmentOf(e);
        Info($"after giving: held {r?.Held}, hotbar {r?.HotbarIndex}, glider slot {r?[PlayerEquipSlot.Glider]}; events [{string.Join(",", _equip.Select(x => $"{x.Slot}:{x.Before}->{x.Now}"))}]");
        if (_world == 0 || glider is null) return;

        // the game's own equip action, as a client sends it
        var ch = Raw.Component(_world, "PlayerInput", e);
        const int action = 0x08 + 0x1c0;  // PlayerInput.fromClient.changeEquipmentAction {slot, version, equipment slot, type}
        var slotValue = glider.Slot.Value;
        Raw.Set64(ch + action, slotValue);
        Raw.Set32(ch + action + 8, Raw.Get32(ch + action + 8) + 1);
        Raw.Set8(ch + action + 0xc, 22);  // keen::EquipmentSlot.Glider
        Raw.Set8(ch + action + 0xd, 0);
        _equip.Clear();
        await Steps(12);
        // the generic event saw it, before the game ran it
        var cl = _client.FirstOrDefault(c => c.Name == "changeEquipmentAction");
        Check(cl is not null, $"PlayerClientActionEvent for the equip action: {cl}");
        Check(cl?.Payload.Length == 16 && BitConverter.ToUInt64(cl.Payload, 0) == slotValue, "with the action's bytes as the client sent them");
        var after = PlayerStates.EquipmentOf(e);
        Info($"after the equip action: glider slot {after?[PlayerEquipSlot.Glider]}; events [{string.Join(",", _equip.Select(x => $"{x.Slot}:{x.Before}->{x.Now}"))}]");
        Check(after is not null && after[PlayerEquipSlot.Glider] == Glider, "the game's equip action put the glider in its slot");
        Check(_equip.Any(x => x.Slot == PlayerEquipSlot.Glider && x.Now == Glider), "PlayerEquipmentChangedEvent for the glider");

        // canceled: the game's program never runs it
        var ch2 = Raw.Component(_world, "PlayerInput", e);
        _equip.Clear();
        _client.Clear();
        _cancelClientIf = c => c.Name == "changeEquipmentAction";
        Raw.Set64(ch2 + action, sword?.Slot.Value ?? 0);
        Raw.Set8(ch2 + action + 0xc, 21);  // keen::EquipmentSlot.HookShot: a slot that stays empty if the game skips it
        Raw.Set32(ch2 + action + 8, Raw.Get32(ch2 + action + 8) + 1);
        await Steps(12);
        _cancelClientIf = null;
        var canceled = _client.FirstOrDefault(c => c.Name == "changeEquipmentAction");
        Check(canceled is { Canceled: true, CanCancel: true }, $"a handler canceled the equip action: {canceled}");
        Check(_equip.Count == 0, $"Cancel: the game's program did not run it: {string.Join(",", _equip.Select(x => $"{x.Slot}:{x.Before}->{x.Now}"))}");
        var consumed = BitConverter.ToUInt32(World.ReadBytes(e, "ServerConsumedPlayerInput")!, 0x78);
        Check(consumed == Raw.Get32(ch2 + action + 8), $"Cancel: the action's version is marked consumed ({consumed})");

        // the game's own action bar selection
        var ab = Raw.Component(_world, "PlayerInput", e);
        const int sel = 0x08 + 0x60;  // PlayerInput.fromClient.actionbarSelectionAction {version, selectedIndex}
        _hotbar.Clear();
        _client.Clear();
        var before = PlayerStates.EquipmentOf(e)?.HotbarIndex ?? 0;
        Raw.Set8(ab + sel + 4, (byte)(before == 1 ? 2 : 1));
        Raw.Set32(ab + sel, Raw.Get32(ab + sel) + 1);
        await Steps(12);
        var h = PlayerStates.EquipmentOf(e);
        Info($"after the action bar action: hotbar {before} -> {h?.HotbarIndex}, held {h?.Held}; events [{string.Join(",", _hotbar.Select(x => $"{x.Before}->{x.Now} {x.HeldBefore}->{x.Held}"))}]");
        Check(h?.HotbarIndex != before, $"the game's action bar action moved the selection ({before} -> {h?.HotbarIndex})");
        Check(_hotbar.Count > 0, "PlayerHotbarChangedEvent");
        Check(_client.Any(c => c.Name == "actionbarSelectionAction"), "PlayerClientActionEvent for the action bar");
    }

    // ---- from another thread: the last tick's copy ----
    private async Task TestOtherThread(EntityId e)
    {
        PlayerSnapshot? first = await Task.Run(() => PlayerStates.Read(e));
        Info($"first read from another thread (nothing published yet is allowed): {(first is null ? "null" : "a copy")}");
        await Steps(4);
        var later = await Task.Run(() => PlayerStates.Read(e));
        Check(later is not null, "a later read from another thread gives the last tick's copy");
        Check(later?.Health is not null, "with vitals");
    }

    // ---- what it costs ----
    private async Task<(long Median, long P99, long Mean)> Measure(int steps = 300)
    {
        await Steps(120);  // past the JIT of what just started running
        var runs = PlayerStates.Diagnostics.PumpRuns;
        var samples = new List<long>();
        for (var i = 0; i < steps; i++)
        {
            await Steps(1);
            samples.Add(PlayerStates.Diagnostics.LastPumpNanoseconds);
        }
        samples.Sort();
        return (samples[steps / 2], samples[steps * 99 / 100], (long)samples.Average());
    }

    private async Task TestCost(EntityId first)
    {
        var all = new List<EntityId> { first };
        for (var i = 0; i < 15; i++)
        {
            var q = Near();
            var e = await Spawn.Entity("1_Player", new WorldPosition(q.X + i * 3, q.Y, q.Z + 6), 0, new SpawnOptions { Tag = "playerstate-test", Flags = SpawnFlags.Transient });
            all.Add(e);
            _spawned.Add(e);
            PlayerStates.Diagnostics.Watch(e);
        }
        await Steps(40);
        async Task<long> Cold(bool motion, bool input, bool ex)
        {
            long total = 0;
            for (var i = 0; i < 100; i++)
            {
                await Steps(1);  // right after a sim step, with the cache full of other things: as the real pump finds it
                total += PlayerStates.Diagnostics.Bench(all, motion, input, ex);
            }
            return total / 100 / all.Count;
        }
        Info($"one native read per player, in a real tick: Actor only {await Cold(false, false, false)} ns, with motion {await Cold(true, false, false)} ns, with input {await Cold(true, true, false)} ns, with everything {await Cold(true, true, true)} ns");

        Events.Unregister(this);
        await Steps(5);
        var r0 = PlayerStates.Diagnostics.PumpRuns;
        await Steps(60);
        Check(PlayerStates.Diagnostics.PumpRuns == r0, "nothing subscribed: the scan does not run");

        var sub = Events.On<PlayerConsumeEvent>(_ => { });
        var eat = await Measure();
        sub.Dispose();
        Info($"cost with {all.Count} players, only PlayerConsumeEvent subscribed, one step's pump: median {eat.Median} ns, p99 {eat.P99} ns, mean {eat.Mean} ns");

        sub = Events.On<PlayerJumpEvent>(_ => { });
        var jump = await Measure();
        sub.Dispose();
        Info($"cost with {all.Count} players, only PlayerJumpEvent subscribed, one step's pump: median {jump.Median} ns, p99 {jump.P99} ns, mean {jump.Mean} ns");

        var e0 = _stateEventsTotal;
        sub = Events.On<PlayerStateChangedEvent>(_ => _stateEventsTotal++);
        var state = await Measure();
        Info($"cost with {all.Count} players, PlayerStateChangedEvent subscribed, one step's pump: median {state.Median} ns, p99 {state.P99} ns, mean {state.Mean} ns, {_stateEventsTotal - e0} events");
        sub.Dispose();
        Events.Register(this);
        Check(state.Median > 0, "the scan ran");
    }
}

internal static unsafe class Raw
{
    public static nuint Component(nuint world, string comp, EntityId e)
    {
        ulong* r = stackalloc ulong[6];
        for (var i = 0; i < 6; i++) r[i] = 0;
        ((delegate* unmanaged<ulong*, nuint, uint, uint, ulong*>)Symbols.Get("GetComponent"))(r, world, World.ComponentHash(comp), e.Value);
        return (nuint)r[0];
    }
    public static void Or64(nuint a, ulong v) => *(ulong*)a |= v;
    public static void And64(nuint a, ulong v) => *(ulong*)a &= v;
    public static void Set64(nuint a, ulong v) => *(ulong*)a = v;
    public static uint Get32(nuint a) => *(uint*)a;
    public static void Set32(nuint a, uint v) => *(uint*)a = v;
    public static void Set8(nuint a, byte v) => *(byte*)a = v;
}
