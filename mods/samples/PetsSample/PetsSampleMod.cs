// SPDX-License-Identifier: MIT
// Kiln.Pets from a mod (docs/kiln/modding/pets.md). Kiln keeps the records, bodies, leases, protection, revive and the
// owner's bar; this mod makes the choices:
//   hound     Kiln's safe defaults: players protected, a recovery anchor when downed, a free 3 s revive.
//   duelist   (blocks/pets.json) fights other players' duelists when both owners agreed (/pet duel), vanishes when downed,
//             costs 3 twigs to revive.
//   A hound downed while its owner stands next to it stays as an anchor; one downed far away is released (PetDowningEvent).
//   A revive within 30 s of the last one of the same owner is refused (PetRevivingEvent).
// Commands: /pet give [hound|duelist], /pet list, /pet summon|dismiss|stay|follow|revive|release <n>,
// /pet duel <player name>, /pet peace.
using Kiln;

namespace PetsSample;

public sealed class PetsSampleMod : Mod
{
    private readonly Dictionary<CharacterKey, DateTime> _lastRevive = [];
    private readonly HashSet<(CharacterKey, CharacterKey)> _duelAsks = [];

    public override void OnLoad()
    {
        Blocks.Define("hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf", Name = "Hound" });

        Events.On<PetDowningEvent>(e =>
        {
            if (e.Pet.Definition != "pets-sample:hound" || !Capabilities.Is("kiln.players")) return;
            // this mod's choice for its hounds: far from the owner, a downed hound goes for good
            var owner = Players.All.FirstOrDefault(p => CharacterData.KeyOf(p.Entity) == e.Pet.Owner);
            if (owner?.Position is { } at && e.Pet.RecoveryAnchor is { } spot && at.DistanceTo(spot) > 40) e.Outcome = PetDownedOutcome.Release;
        });
        Events.On<PetRevivingEvent>(e =>
        {
            if (_lastRevive.TryGetValue(e.Owner, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(30)) e.Cancel();
        });
        Events.On<PetRevivedEvent>(e => _lastRevive[e.Owner] = DateTime.UtcNow);
        Events.On<PetDownedEvent>(e => Log.Info($"{e.Change.After.Label} of {e.Owner} went down ({e.Change.Cause}): {e.Change.After.Life}, {e.Change.After.Presence}"));
    }

    // Kiln's rows say what is there: a command refuses with the row's reason instead of throwing deep inside
    private static CommandException Off(string row) => new($"{row} is off: {Capabilities.Why(row)}");

    private static (PlayerRef Who, CharacterKey Key) Owner(CommandContext c)
    {
        if (c.Source.Player.IsNone) throw new CommandException("only a player in the world can do this");
        var key = CharacterData.KeyOf(c.Source.Player);
        if (key.IsNone || key.IsAccountOnly) throw new CommandException("your character is not known on this build");
        return (PlayerRef.Of(c.Source.Player), key);
    }

    private static PetSnapshot Nth(CharacterKey key, int n) =>
        Pets.Of(key).ElementAtOrDefault(n - 1) ?? throw new CommandException($"you have no pet {n} (/pet list)");

    private static string Line(PetResult r) => r.Ok ? $"{r.Pet!.Label}: {r.Pet.Life}, {r.Pet.Presence}, {r.Pet.Health}/{r.Pet.HealthMax}" : $"refused: {r.Reason}";

    [Command("pet give", Description = "a new pet: hound or duelist", Permission = CommandPermission.Admin)]
    private async Task Give(CommandContext c, string kind = "hound")
    {
        if (!Capabilities.Is("pets.records")) throw Off("pets.records");
        var (_, key) = Owner(c);
        c.Reply(Line(await Pets.Grant(key, $"pets-sample:{kind}", PetRequest.New())));
    }

    [Command("pet list", Description = "your pets")]
    private void List(CommandContext c)
    {
        if (!Capabilities.Is("pets.records")) throw Off("pets.records");
        var (_, key) = Owner(c);
        var n = 0;
        foreach (var p in Pets.Of(key))
            c.Reply($"{++n}. {p.Label} ({p.Definition}): {p.Life}, {p.Presence}, {p.Health}/{p.HealthMax}, {p.RequestedOrder}{(p.WantsSummoned ? ", out" : "")}");
        if (n == 0) c.Reply("no pets (/pet give)");
    }

    [Command("pet summon", Description = "bring pet n out (a downed one: its recovery anchor)")]
    private async Task Summon(CommandContext c, int n) { if (!Capabilities.Is("pets.records")) throw Off("pets.records"); if (!Capabilities.Is("pets.summon")) throw Off("pets.summon"); var (who, key) = Owner(c); c.Reply(Line(await Pets.Summon(who, Nth(key, n).Id, PetRequest.New()))); }

    [Command("pet dismiss", Description = "put pet n away")]
    private async Task Dismiss(CommandContext c, int n) { if (!Capabilities.Is("pets.records")) throw Off("pets.records"); if (!Capabilities.Is("pets.summon")) throw Off("pets.summon"); var (who, key) = Owner(c); c.Reply(Line(await Pets.Dismiss(who, Nth(key, n).Id, PetRequest.New()))); }

    [Command("pet stay", Description = "pet n stays")]
    private async Task Stay(CommandContext c, int n) { if (!Capabilities.Is("pets.records")) throw Off("pets.records"); if (!Capabilities.Is("pets.follow")) throw Off("pets.follow"); var (who, key) = Owner(c); c.Reply(Line(await Pets.Command(who, Nth(key, n).Id, PetOrder.Stay, EntityRef.None, PetRequest.New()))); }

    [Command("pet follow", Description = "pet n follows")]
    private async Task Follow(CommandContext c, int n) { if (!Capabilities.Is("pets.records")) throw Off("pets.records"); if (!Capabilities.Is("pets.follow")) throw Off("pets.follow"); var (who, key) = Owner(c); c.Reply(Line(await Pets.Command(who, Nth(key, n).Id, PetOrder.Follow, EntityRef.None, PetRequest.New()))); }

    [Command("pet revive", Description = "revive pet n: stand at its recovery anchor")]
    private async Task Revive(CommandContext c, int n) { if (!Capabilities.Is("pets.records")) throw Off("pets.records"); if (!Capabilities.Is("pets.downed")) throw Off("pets.downed"); var (who, key) = Owner(c); c.Reply(Line(await Pets.Revive(who, Nth(key, n).Id, PetRequest.New()))); }

    [Command("pet release", Description = "release pet n for good")]
    private async Task Release(CommandContext c, int n) { if (!Capabilities.Is("pets.records")) throw Off("pets.records"); var (who, key) = Owner(c); c.Reply(Line(await Pets.Release(who, Nth(key, n).Id, PetRequest.New()))); }

    [Command("pet duel", Description = "agree to a duel with a player: both of you ask, then your duelists may fight")]
    private void Duel(CommandContext c, string player)
    {
        if (!Capabilities.Is("pets.fight")) throw Off("pets.fight");
        if (!Capabilities.Is("kiln.players")) throw Off("kiln.players");
        var (_, key) = Owner(c);
        var other = Players.Find(player) ?? throw new CommandException($"no player {player}");
        var otherKey = CharacterData.KeyOf(other.Entity);
        // this mod's consent rule: hostile only when both asked; Kiln enforces the pair in every hit and order
        _duelAsks.Add((key, otherKey));
        if (!_duelAsks.Contains((otherKey, key)))
        {
            c.Reply($"asked {other.Name} for a duel; they agree with /pet duel <your name>");
            return;
        }
        Pets.SetHostile(key, otherKey, true);
        c.Reply($"duel on with {other.Name}: your duelists may fight");
    }

    [Command("pet peace", Description = "end your duels")]
    private void Peace(CommandContext c)
    {
        if (!Capabilities.Is("pets.fight")) throw Off("pets.fight");
        var (_, key) = Owner(c);
        foreach (var (a, b) in _duelAsks.Where(x => x.Item1 == key || x.Item2 == key).ToList())
        {
            Pets.SetHostile(a, b, false);
            _duelAsks.Remove((a, b));
        }
        c.Reply("no duels");
    }
}
