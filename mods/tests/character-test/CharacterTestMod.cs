// SPDX-License-Identifier: MIT
// Lab test of the character layer (no player): prints "character-test: PASS ..." / "FAIL ..." lines to kiln.log.
//   boot 1: kiln.characters; writes account-keyed data the old way (through the same public API old builds used).
//   boot 2: the migration: the first character gets it, once; two characters of one account are separate.
//   boot 3 (restart): the data came back, per character.
// A run with KILN_PLAYERS_SIMULATE + KILN_CHARACTERS_SIMULATE=1 checks Player.CharacterId and the services' holders on two
// "players" of one Steam account. charnet.py drives the channel kiln:character through the control channel below.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace CharacterTest;

public sealed class Note
{
    public string Text { get; set; } = "";
    public int Count { get; set; }
}

public sealed record CtlRequest(string Op, Guid Character, ulong Account, string? Name, string? Text);
public sealed record CtlReply(string Op, bool Ok, string Data);

public sealed class CharacterTestMod : Mod
{
    internal const ulong LegacyAccount = 0x7600000000000001;
    private const string Errand = "character-test:errand", Watch = "character-test:world_watch", Tree = "character-test:tree";
    private static readonly CharacterKey A = new(0x1111222233334444, Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly CharacterKey B = new(0x1111222233334444, Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"));
    private int _pass, _fail;
    private NetChannel? _ctl;
    private string StatePath => Path.Combine(Context.DataDirectory, "boot.txt");

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"character-test: {(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task<bool> WaitFor(Func<bool> done, int steps = 600)
    {
        for (var i = 0; i < steps; i++)
        {
            if (done()) return true;
            await Context.Scheduler.NextTick();
        }
        return done();
    }

    public override void OnLoad()
    {
        _ctl = Network.Channel("character-test:ctl", "1", required: false, maxMessageBytes: 256 * 1024)
            .RegisterJson<CtlRequest>(OnCtl, NetDirection.ToServer)
            .RegisterJson<CtlReply>((_, _) => { }, NetDirection.ToClient);
        var simulated = PlayerCharacters.Status?.Simulated == true;
        if (simulated)
        {
            PlayerCharacters.Simulate(0, 0x1111222233334444, A.Id);
            PlayerCharacters.Simulate(1, 0x1111222233334444, B.Id);
        }
        _ = Guarded(simulated);
    }

    private async Task Guarded(bool simulated)
    {
        try
        {
            await WaitFor(() => Skills.Find(Tree) is not null && Quests.Find(Errand) is not null);
            if (simulated) await Simulated();
            else await Boots();
        }
        catch (Exception e)
        {
            Check(false, $"threw: {e}");
        }
        Log.Info($"character-test: done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Boots()
    {
        var boot = File.Exists(StatePath) && int.TryParse(File.ReadAllText(StatePath), out var b) ? b + 1 : 1;
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(StatePath, boot.ToString());
        Log.Info($"character-test: boot {boot}");
        await WaitFor(() => Saves.Status is { State: "running" or "saving" });
        var st = PlayerCharacters.Status;
        Check(st is { Resolved: true, RecordOffset: 0x50 }, $"kiln.characters found the CharacterId at record +0x50 ({st})");
        await WaitFor(() => PlayerCharacters.Status is { Records: true, Scans: > 0 });
        Check(PlayerCharacters.Status is { Records: true, Known: 0 }, $"it reads the 16 records; no player, no character ({PlayerCharacters.Status})");
        Check(CharacterData.KeyOf(new EntityId(1)).IsNone, "no player: no key");
        Check(CharacterKey.TryParse(A.Text, out var parsed) && parsed == A, $"a key reads back from its text {A.Text}");
        Check(new CharacterKey(5, Guid.Empty).IsAccountOnly && new CharacterKey(5, Guid.Empty).Text == "0000000000000005.account", "an account-only key is told apart");
        Check(CharacterData.Status is { Started: true, ChannelOpen: true }, $"the transport runs ({CharacterData.Status})");
        switch (boot)
        {
            case 1:
                Seed();
                break;
            case 2:
                Migrate();
                break;
            default:
                Restart();
                break;
        }
        var r = await Saves.Request();
        Check(r.Success, $"the world saved ({r.Revision})");
    }

    /// <summary>Boot 1: account-keyed data, written through the public API that wrote it before characters.</summary>
    private void Seed()
    {
        var old = QuestHolder.OfAccount(LegacyAccount);
        Check(Quests.Start(Errand, old).Ok, "seed: a quest book under the account");
        Quests.Signal("character-test.one", old);
        Check(Quests.Progress(Errand, old)?.StepId == "two", $"seed: its first step is done ({Quests.Progress(Errand, old)})");
        var oldTalents = new TalentHolder(LegacyAccount, EntityId.None);
        Check(Skills.Unlock(Tree, "a", oldTalents).Ok, "seed: a talent under the account");
        Check(Skills.Unlock(Tree, "a", oldTalents).Ok, "seed: its second level");
        PlayerData.Get<Note>(LegacyAccount, "note").Text = "from the account";
    }

    /// <summary>Boot 2: the first character to ask gets the account's data; the second starts empty; they are separate.</summary>
    private void Migrate()
    {
        var old = QuestHolder.OfAccount(LegacyAccount);
        var qa = QuestHolder.OfCharacter(A, LegacyAccount);
        var qb = QuestHolder.OfCharacter(B, LegacyAccount);
        var pa = Quests.Progress(Errand, qa);
        Check(pa is { Status: QuestStatus.Active, StepId: "two" }, $"migration: the first character got the account's quest where it stood ({pa})");
        Check(Quests.Progress(Errand, qb) is null, "migration: the second character of the account has no such quest");
        Check(Quests.Progress(Errand, old) is null, "migration: the account's book is empty now (moved, not copied)");
        var ta = Skills.HolderOf(A, LegacyAccount);
        var tb = Skills.HolderOf(B, LegacyAccount);
        Check(Skills.LevelOf(Tree, "a", ta) == 2, $"migration: the first character got the account's talents (level {Skills.LevelOf(Tree, "a", ta)})");
        Check(Skills.LevelOf(Tree, "a", tb) == 0, "migration: the second character has none");
        Check(Skills.LevelOf(Tree, "a", new TalentHolder(LegacyAccount, EntityId.None)) == 0, "migration: the account's tree state is empty now");
        Check(CharacterData.MigrateFromPlayerData(A, "note", LegacyAccount), "migration API: moves a mod's PlayerData entry");
        Check(!CharacterData.MigrateFromPlayerData(B, "note", LegacyAccount), "migration API: only once");
        Check(!CharacterData.MigrateFromPlayerData(A, "note", LegacyAccount), "migration API: not over what the character has");
        Check(CharacterData.Get<Note>(A, "note").Text == "from the account" && !PlayerData.Contains(LegacyAccount, "note"), "migration API: the value moved");

        // separate
        Quests.Signal("character-test.two", qa);
        Check(Quests.StatusOf(Errand, qa) == QuestStatus.Done, "two characters: the first finishes the quest");
        Check(Quests.Start(Errand, qb).Ok && Quests.StatusOf(Errand, qb) == QuestStatus.Active, "two characters: the second starts it on its own");
        Check(Quests.Progress(Errand, qb)?.StepId == "one" && Quests.StatusOf(Errand, qa) == QuestStatus.Done, "two characters: separate progress");
        Check(Quests.Start(Watch, qa).Ok, "a world quest starts");
        Check(Quests.StatusOf(Watch, qb) == QuestStatus.Active, "a world quest is the world's: the second character sees it active");
        Check(Skills.Unlock(Tree, "a", tb).Ok && Skills.LevelOf(Tree, "a", tb) == 1 && Skills.LevelOf(Tree, "a", ta) == 2, "two characters: separate talents");

        // the data API
        CharacterData.Get<Note>(B, "note").Count = 7;
        CharacterData.Carried.Get<Note>(A, "bag").Text = "travels";
        Check(CharacterData.Keys(B).Contains("note") && !CharacterData.Carried.Keys(B).Contains("bag"), "keys: per character, per kind");
        Check(CharacterData.TryGet<Note>(B, "note", out var n) && n.Count == 7, "TryGet");
        Check(!CharacterData.TryGet<Note>(B, "nothing", out _), "TryGet of nothing");
        Check(CharacterData.Characters.Contains(A) && CharacterData.Characters.Contains(B), "Characters lists both");
        CharacterData.Set(B, "gone", new Note());
        Check(CharacterData.Remove(B, "gone") && !CharacterData.Contains(B, "gone"), "Set, Remove, Contains");
        try
        {
            CharacterData.Get<Note>(CharacterKey.None, "x");
            Check(false, "no character is refused");
        }
        catch (ArgumentException)
        {
            Check(true, "no character is refused");
        }
    }

    /// <summary>Boot 3: the restart kept everything, per character.</summary>
    private void Restart()
    {
        var qa = QuestHolder.OfCharacter(A, LegacyAccount);
        var qb = QuestHolder.OfCharacter(B, LegacyAccount);
        Check(Quests.StatusOf(Errand, qa) == QuestStatus.Done, "restart: the first character's quest is still done");
        Check(Quests.Progress(Errand, qb) is { Status: QuestStatus.Active, StepId: "one" }, "restart: the second character's is still at its first step");
        Check(Quests.Progress(Errand, QuestHolder.OfAccount(LegacyAccount)) is null, "restart: nothing went back to the account");
        Check(Skills.LevelOf(Tree, "a", Skills.HolderOf(A)) == 2 && Skills.LevelOf(Tree, "a", Skills.HolderOf(B)) == 1, "restart: talents per character");
        Check(CharacterData.Get<Note>(A, "note").Text == "from the account" && CharacterData.Get<Note>(B, "note").Count == 7, "restart: mod data per character");
        Check(CharacterData.Carried.Get<Note>(A, "bag").Text == "travels", "restart: carried data");
    }

    /// <summary>The players of the fixture: one Steam account, two characters (slots 0 and 1), through the real event path.</summary>
    private async Task Simulated()
    {
        const ulong steam = 76561190000000001;
        var old = QuestHolder.OfAccount(steam);
        if (Quests.Progress(Errand, old) is null) Quests.Start(Errand, old);
        var joined = new List<Player>();
        Players.Joined += p => joined.Add(p);
        Check(await WaitFor(() => joined.Count == 2, 4000), $"both players of the fixture joined: {string.Join(", ", joined)}");
        var p0 = joined.First(p => p.Slot == 0);
        var p1 = joined.First(p => p.Slot == 1);
        Check(p0.SteamId == steam && p1.SteamId == steam, "one Steam account");
        Check(p0.CharacterId == A.Id && p1.CharacterId == B.Id, $"Player.CharacterId tells them apart: {p0.CharacterId}, {p1.CharacterId}");
        Check(CharacterData.KeyOf(p0.Entity) == A && CharacterData.KeyOf(p1.Entity) == B, "CharacterData.KeyOf follows");
        var q0 = QuestHolder.Of(p0);
        var q1 = QuestHolder.Of(p1);
        Check(q0.Character == A && q1.Character == B && q0.Key != q1.Key, $"QuestHolder.Of: {q0.Key}, {q1.Key}");
        Check(Quests.Progress(Errand, q0) is { Status: QuestStatus.Active }, "the first character that asks gets the Steam account's book");
        Check(Quests.Progress(Errand, q1) is null, "the second does not");
        Check(Quests.Start(Errand, q1).Ok, "the second starts the quest itself");
        Quests.Signal("character-test.one", q0);
        Check(Quests.Progress(Errand, q0)?.StepId == "two" && Quests.Progress(Errand, q1)?.StepId == "one", "signals reach one character's book");
        var t0 = Skills.HolderOf(p0.Entity);
        var t1 = Skills.HolderOf(p1.Entity);
        Check(t0.Character == A && t1.Character == B, $"Skills.HolderOf: {t0}, {t1}");
        Check(Skills.Unlock(Tree, "a", t0).Ok && Skills.LevelOf(Tree, "a", t0) == 1 && Skills.LevelOf(Tree, "a", t1) == 0, "talents: per character");
        var left = new List<Player>();
        Players.Left += p => left.Add(p);
        Check(await WaitFor(() => left.Count == 2, 4000), "both left");
        Check(left.First(p => p.Slot == 1).CharacterId == B.Id, "a player who left still names the character");
        Check(CharacterData.LastOf(steam) != CharacterKey.None, $"the Steam id's last character is known ({CharacterData.LastOf(steam)})");
    }

    private void OnCtl(CtlRequest m, NetContext ctx)
    {
        var key = new CharacterKey(m.Account, m.Character);
        try
        {
            switch (m.Op)
            {
                case "unlock":
                {
                    var r = Skills.Unlock(Tree, m.Name!, Skills.HolderOf(key));
                    ctx.Reply(new CtlReply(m.Op, r.Ok, r.ToString()));
                    break;
                }
                case "state":
                {
                    var s = Skills.State(Tree, Skills.HolderOf(key));
                    ctx.Reply(new CtlReply(m.Op, true, JsonSerializer.Serialize(new { s.Levels, s.Granted, Points = Skills.Points(Tree, Skills.HolderOf(key)).Available })));
                    break;
                }
                case "carried":
                {
                    ctx.Reply(new CtlReply(m.Op, true, JsonSerializer.Serialize(new { Keys = CharacterData.Carried.Keys(key), Revision = CharacterData.Carried.Revision(key) })));
                    break;
                }
                case "note-set":
                    CharacterData.Carried.Get<Note>(key, "bag").Text = m.Text ?? "";
                    ctx.Reply(new CtlReply(m.Op, true, ""));
                    break;
                case "note-get":
                    ctx.Reply(new CtlReply(m.Op, CharacterData.Carried.TryGet<Note>(key, "bag", out var note), note?.Text ?? ""));
                    break;
                case "quest-start":
                {
                    var r = Quests.Start(Errand, QuestHolder.OfCharacter(key));
                    ctx.Reply(new CtlReply(m.Op, r.Ok, r.Reason));
                    break;
                }
                case "quest-signal":
                    Quests.Signal(m.Name!, QuestHolder.OfCharacter(key));
                    ctx.Reply(new CtlReply(m.Op, true, ""));
                    break;
                case "quest-status":
                    ctx.Reply(new CtlReply(m.Op, true, $"{Quests.StatusOf(Errand, QuestHolder.OfCharacter(key))}/{Quests.Progress(Errand, QuestHolder.OfCharacter(key))?.StepId}"));
                    break;
                case "world-id":
                    ctx.Reply(new CtlReply(m.Op, true, Saves.WorldId ?? ""));
                    break;
                case "save":
                    _ = SaveAndAnswer(m, ctx);
                    break;
                case "status":
                    ctx.Reply(new CtlReply(m.Op, true, CharacterData.Status.ToString()));
                    break;
                default:
                    ctx.Reply(new CtlReply(m.Op, false, "unknown op"));
                    break;
            }
        }
        catch (Exception e)
        {
            ctx.Reply(new CtlReply(m.Op, false, e.Message));
        }
    }

    private static async Task SaveAndAnswer(CtlRequest m, NetContext ctx)
    {
        var r = await Saves.Request();
        ctx.Reply(new CtlReply(m.Op, r.Success, r.Revision.ToString()));
    }
}
