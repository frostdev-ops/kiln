// SPDX-License-Identifier: MIT
using System.Globalization;
using Kiln;

namespace TkFakes;

/// <summary>Uses the services the kit fakes the way a mod does, and logs what it sees: the tests read the log and the
/// kit's records (KilnHarness.Spawned, ...).</summary>
public sealed class TkFakesMod : Mod
{
    public override void OnLoad()
    {
        // tkf-spawn <template> [tag]: one entity 2 m in front of the player (the console: at the origin)
        Commands.Register("tkf-spawn", async ctx =>
        {
            var at = ctx.Source.IsPlayer ? Players.PositionOf(ctx.Source.Player) ?? default : default;
            var options = new SpawnOptions { Tag = ctx.Count > 1 ? ctx.Args[1] : "tk" };
            try
            {
                var e = await Spawn.Entity(ctx.Args[0], at with { X = at.X + 2 }, 90, options);
                ctx.Reply($"spawned {e.Value}");
            }
            catch (Exception x) when (x is SpawnException or NotSupportedException)
            {
                ctx.Reply($"refused: {x.GetType().Name}: {x.Message}");
            }
        });
        // tkf-try <template>: the result form
        Commands.Register("tkf-try", ctx =>
        {
            try
            {
                var r = Spawn.TryEntity(ctx.Args[0], new WorldPosition(1, 2, 3));
                ctx.Reply($"try: ok {r.Ok} entity {r.Entity.Id.Value} serial {r.Entity.HasSerial}");
            }
            catch (Exception x) when (x is ArgumentException or KilnUnavailableException)
            {
                ctx.Reply($"try: {x.GetType().Name}: {x.Message}");
            }
        });
        Commands.Register("tkf-drop", async ctx =>
        {
            var e = await Spawn.Item(uint.Parse(ctx.Args[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(ctx.Args[1], CultureInfo.InvariantCulture),
                new WorldPosition(5, 0, 5));
            ctx.Reply($"dropped {e.Value}");
        });
        Commands.Register("tkf-destroy", ctx =>
        {
            try
            {
                ctx.Reply($"destroyed {Spawn.Destroy(new EntityId(uint.Parse(ctx.Args[0], CultureInfo.InvariantCulture)))}");
            }
            catch (KilnUnavailableException x)
            {
                ctx.Reply($"destroy: unavailable {x.Capability}");
            }
        });
        Commands.Register("tkf-find", ctx =>
        {
            var near = ctx.Count > 1 ? new WorldPosition(0, 0, 0) : (WorldPosition?)null;
            var radius = ctx.Count > 1 ? double.Parse(ctx.Args[1], CultureInfo.InvariantCulture) : 0;
            ctx.Reply($"found {string.Join(",", Spawn.FindByName(ctx.Args[0], near, radius).Select(e => e.Value))} of {Spawn.EntityCount}");
        });
        Commands.Register("tkf-inspect", ctx =>
        {
            var info = Spawn.Inspect(new EntityId(uint.Parse(ctx.Args[0], CultureInfo.InvariantCulture)));
            ctx.Reply(info is null ? "inspect: none" : FormattableString.Invariant(
                $"inspect: {info.TemplateName} at {info.Position.X:0.##},{info.Position.Y:0.##},{info.Position.Z:0.##} yaw {info.YawDegrees:0} tracked {info.IsTracked} serial {info.Serial} spawntime {info.SpawnTime is not null}"));
        });
        Commands.Register("tkf-tracked", ctx =>
            ctx.Reply($"tracked: {string.Join(";", Spawn.Tracked.Select(t => $"{t.Serial} {t.TemplateName} {t.Tag} {t.Origin} live {t.IsLive}"))}"));
        // chat: a handler that logs every line and drops the ones that say "secret"; a command that tells and broadcasts
        Chat.Received += m =>
        {
            Log.Info($"chat received: #{m.Id} {m.SenderName} ({m.Sender.Value}): {m.Text} command {m.IsCommand}");
            if (m.Text.Contains("secret", StringComparison.Ordinal)) m.Cancel = true;
        };
        Commands.Register("tkf-tell", ctx =>
        {
            var to = Chat.FindPlayer(ctx.Args[0]);
            ctx.Reply($"tell: {(to is null ? "nobody" : Chat.Tell(to.Entity, ctx.Rest(1)).ToString())}");
        });
        Commands.Register("tkf-broadcast", ctx => ctx.Reply($"broadcast to {Chat.Broadcast(ctx.ArgText)}"));
        Commands.Register("tkf-say", ctx => ctx.Reply($"say: {Chat.Say(ctx.Source.Player, ctx.ArgText)}"));
        Commands.Register("tkf-chatters", ctx => ctx.Reply($"chatters: {string.Join(",", Chat.Players.Select(p => $"{p.Name}/{p.Entity.Value}/{p.Permissions}"))}"));
        // items: give and take log their result when the transaction ran (the next step); read and find answer at once
        Commands.Register("tkf-give", ctx => _ = Logged("give", Items.Give(Who(ctx), ctx.Args[1], int.Parse(ctx.Args[2], CultureInfo.InvariantCulture),
            ctx.Count > 3 && ctx.Args[3] == "gear" ? new Gear { Level = 7, Rarity = ItemRarity.Rare } : null,
            ctx.Count > 3 ? ctx.Args[3] switch { "all" => GiveOptions.AllOrNothing, "dry" => GiveOptions.DryRun, "nomerge" => GiveOptions.NoMerge, _ => GiveOptions.None } : GiveOptions.None)));
        Commands.Register("tkf-take", ctx =>
        {
            var item = Items.Lookup(ctx.Args[1]) ?? throw new CommandException($"no item {ctx.Args[1]}");
            _ = Logged("take", Items.Take(Who(ctx), item.Id, int.Parse(ctx.Args[2], CultureInfo.InvariantCulture)));
        });
        Commands.Register("tkf-read", ctx =>
        {
            var inv = Items.Read(Who(ctx));
            ctx.Reply(inv is null ? "read: none" : $"read: {inv.Slots.Count} slots: " +
                string.Join("; ", inv.Slots.Where(s => !s.IsEmpty).Select(s => $"{Items.Get(s.Item)?.Name} x{s.Count}{(s.IsGear ? $" gear L{s.Level} {s.Rarity}" : "")}")));
        });
        Commands.Register("tkf-chests", ctx => ctx.Reply($"chests: {string.Join(",", Items.FindInventories(InventoryKinds.Containers).Select(e => e.Value))}; " +
            $"players: {string.Join(",", Items.FindInventories(InventoryKinds.Players).Select(e => e.Value))}"));
        // combat: health reads, a mod's damage (logged when the hit concluded), heals and god mode
        Commands.Register("tkf-hp", ctx => ctx.Reply(Combat.GetHealth(Who(ctx)) is { } h ? $"hp: {h.Current}/{h.Max} dead {h.Dead} god {h.Invulnerable}" : "hp: none"));
        Commands.Register("tkf-damage", ctx => _ = LoggedHit("damage", Combat.Damage(Who(ctx), int.Parse(ctx.Args[1], CultureInfo.InvariantCulture), ctx.Source.Player)));
        Commands.Register("tkf-kill", ctx => _ = LoggedHit("kill", Combat.Kill(Who(ctx), ctx.Source.Player)));
        Commands.Register("tkf-heal", ctx => ctx.Reply($"healed {Combat.Heal(Who(ctx), int.Parse(ctx.Args[1], CultureInfo.InvariantCulture))}"));
        Commands.Register("tkf-sethp", ctx => ctx.Reply($"set {Combat.SetHealth(Who(ctx), int.Parse(ctx.Args[1], CultureInfo.InvariantCulture))}"));
        Commands.Register("tkf-god", ctx => Combat.SetInvulnerable(Who(ctx), ctx.Args[1] == "on"));
        Commands.Register("tkf-shield", ctx => _shielded.Add(Who(ctx)));
        // time: the clock, a set, the speed
        Commands.Register("tkf-time", ctx =>
        {
            var t = Time.State;
            ctx.Reply(FormattableString.Invariant($"time: {t.Clock} day {t.IsDay} speed {t.TimeScale:0.##} counter {t.ChangeCounter}"));
        });
        Commands.Register("tkf-settime", ctx => Time.Set(double.Parse(ctx.Args[0], CultureInfo.InvariantCulture), ctx.Count > 1 ? TimeSetMode.GameReset : TimeSetMode.Forward));
        Commands.Register("tkf-speed", ctx => Time.SetSpeed(ctx.Args[0] == "off" ? null : double.Parse(ctx.Args[0], CultureInfo.InvariantCulture)));
        Commands.Register("tkf-cleanup", ctx =>
        {
            var r = Spawn.CleanupTracked(ctx.Count > 0 ? ctx.Args[0] : null);
            ctx.Reply($"cleanup: {r.Destroyed} destroyed {r.Missing} missing {r.Kept} kept");
        });
    }

    private static EntityId Who(CommandContext ctx) =>
        ctx.Args[0] == "me" ? ctx.Source.Player : new EntityId(uint.Parse(ctx.Args[0], CultureInfo.InvariantCulture));

    private async Task Logged(string what, Task<ItemsResult> transaction)
    {
        var r = await transaction;
        Log.Info($"{what} done: {r.Status} {r.Done}/{r.Requested} changes {r.Changes.Count} steps {r.Steps} dry {r.DryRun}: {r.Message}");
    }

    private readonly HashSet<EntityId> _shielded = [];

    private async Task LoggedHit(string what, Task<HitResult> hit)
    {
        var r = await hit;
        Log.Info($"{what} done: {r.State} {r.HealthBefore}->{r.HealthAfter} killing {r.KillingBlow}");
    }

    // A shielded entity takes half damage; a hit of exactly 13 never lands.
    [Subscribe]
    private void OnHurt(EntityHurtEvent e)
    {
        Log.Info($"hurt: {e.Target.Value} by {e.Attacker.Value} for {e.Amount} lethal {e.Lethal} origin {e.Origin}");
        if (e.Amount == 13) e.Cancel();
        else if (_shielded.Contains(e.Target)) e.Amount /= 2;
    }

    [Subscribe]
    private void OnDamaged(EntityDamagedEvent e) => Log.Info($"damaged: {e.Target.Value} {e.HealthBefore}->{e.HealthAfter} by {e.Attacker.Value}");

    [Subscribe]
    private void OnDeath(EntityDeathEvent e) => Log.Info($"died: {e.Victim.Value} killer {e.Killer.Value} origin {e.Origin}");

    [Subscribe]
    private void OnRespawn(PlayerRespawnEvent e) => Log.Info($"respawned: {e.Player.Value} with {e.Health}/{e.HealthMax}");

    [Subscribe]
    private void OnDay(DayStartedEvent e) => Log.Info($"day started at {Time.Format(e.Hours)}");

    [Subscribe]
    private void OnNight(NightStartedEvent e) => Log.Info($"night started at {Time.Format(e.Hours)}");

    [Subscribe]
    private void OnTimeSet(TimeSetEvent e) => Log.Info($"time set: {Time.Format(e.Hours)} {e.Mode} by {e.By}");

    [Subscribe]
    private void OnGiven(ItemsGivenEvent e) => Log.Info($"event given: {e.Result.Done} x {Items.Get(e.Result.Item)?.Name} to {e.Result.Entity.Value} by {e.Origin}");

    [Subscribe]
    private void OnTaken(ItemsTakenEvent e) => Log.Info($"event taken: {e.Result.Done} x {Items.Get(e.Result.Item)?.Name} from {e.Result.Entity.Value} by {e.Origin}");

    [Subscribe]
    private void OnChat(ChatMessageEvent e)
    {
        if (e.Text == "shout") e.Text = "SHOUT";
    }

    [Subscribe]
    private void OnSpawned(EntitySpawnedEvent e) => Log.Info($"event spawned: {e.Entity.Value} {e.Kind} {e.TemplateName ?? e.Template?.ToString()} tag {e.Tag} by {e.Origin}");

    [Subscribe]
    private void OnDestroyed(EntityDestroyedEvent e) => Log.Info($"event destroyed: {e.Entity.Value} {e.TemplateName} {e.Reason} by {e.Origin}");
}
