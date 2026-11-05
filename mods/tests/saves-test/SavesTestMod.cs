// SPDX-License-Identifier: MIT
// kiln.saves lab test. Prints "saves-test: PASS ..." / "FAIL ..." / "INFO ..." lines to kiln.log, and
// "saves-test: READY <what>" when the driver should stop (SIGINT) or kill (-9) the server.
// Phases over several boots (kept in kiln/data/saves-test/state.json):
//   1  fresh: events around an autosave and a requested save, WorldData / PlayerData / EntityData (persistent barrel,
//      session-only enemy, destroyed barrel), Saves.Backup("t1"), then new tokens + a save.        READY stop
//   2  after SIGINT: the shutdown save's handler data is what loaded, player + entity data back.  READY kill
//   3  after kill -9 (x3): the loaded data is exactly the generation of the world file on disk.    READY kill / next
//   4  stage the restore of backup t1.                                                             READY stop
//   5  after the restore: world file + mod data are t1's; three more saves.                         READY stop
//   6  (the driver put an older file from savegame/backup/ in place) its mod data generation loaded.  done
// Every Saving handler bumps a counter in WorldData and appends {revision, counter, token, payload sha} to
// ledger.jsonl (a plain file of its own): after any restart, the data must equal the ledger entry of the world file's
// revision. Delete kiln/data/saves-test/ to start over.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace SavesTest;

public sealed class Probe
{
    public string Token { get; set; } = "";
    public long Counter { get; set; }
    public uint StagedFor { get; set; }
    public string Payload { get; set; } = "";
    public string PayloadSha { get; set; } = "";
    public List<int> Numbers = [];  // a field (IncludeFields)
}

public sealed class Tag
{
    public string Token { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class SavesTestMod : Mod
{
    private const ulong FakeSteam = 76561190000000042;
    private int _pass, _fail;
    private readonly List<WorldSavingEvent> _savings = [];
    private readonly List<WorldSavedEvent> _saveds = [];
    private readonly List<string> _order = [];
    private WorldLoadedEvent? _loaded;
    private bool _churn;
    private int _phase;
    private string StatePath => Path.Combine(Context.DataDirectory, "state.json");
    private string LedgerPath => Path.Combine(Context.DataDirectory, "ledger.jsonl");

    public override void OnLoad()
    {
        Directory.CreateDirectory(Context.DataDirectory);
        // OnLoad runs before the game loads the save: WorldData already reads the sidecar of the world file on disk.
        Info($"OnLoad: Saves.Available={Saves.Available}, WorldId={Saves.WorldId}, WorldData.Loaded={WorldData.Loaded}, " +
             $"status {Saves.Status}");
        _ = RunGuarded();
    }

    public override void OnUnload() => _churn = false;

    // ---- bus handlers ----
    [Subscribe]
    private void OnSaving(WorldSavingEvent e)
    {
        _savings.Add(e);
        _order.Add($"saving:{e.Revision}:{e.Reason}");
        var p = WorldData.Get<Probe>("probe");
        p.Counter++;
        p.StagedFor = e.Revision;
        p.Numbers.Add((int)e.Revision);
        if (p.Numbers.Count > 8) p.Numbers.RemoveAt(0);
        if (_churn)
        {
            p.Payload = Convert.ToBase64String(RandomNumberGenerator.GetBytes(384 * 1024));
            p.PayloadSha = Sha(p.Payload);
        }
        PlayerData.Get<Tag>(FakeSteam, "probe").Note = $"counter {p.Counter}";
        var line = new JsonObject
        {
            ["rev"] = e.Revision, ["counter"] = p.Counter, ["token"] = p.Token, ["sha"] = p.PayloadSha,
            ["reason"] = e.Reason.ToString(), ["sync"] = e.BeforeSnapshot, ["phase"] = _phase,
        }.ToJsonString();
        using (var f = new FileStream(LedgerPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            f.Write(Encoding.UTF8.GetBytes(line + "\n"));
            f.Flush(true);
        }
        if (!_churn || p.Counter % 10 == 0)
            Info($"Saving {e.Reason} revision {e.Revision} (before snapshot: {e.BeforeSnapshot}, step {e.Step}): counter -> {p.Counter}");
    }

    [Subscribe]
    private void OnSaved(WorldSavedEvent e)
    {
        _saveds.Add(e);
        _order.Add($"saved:{e.Revision}:{e.Reason}:{e.Success}");
        if (!_churn) Info($"Saved: {e}");
    }

    [Subscribe]
    private void OnLoadedEvent(WorldLoadedEvent e)
    {
        _loaded = e;
        Info($"WorldLoaded: {e}");
    }

    // ---- driver ----
    private async Task RunGuarded()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while ((_loaded == null || !Spawn.WorldRunning) && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            Info($"world loaded and running after {sw.Elapsed.TotalSeconds:F1} s (loaded event: {_loaded != null})");
            await Wait(2);
            var st = LoadState();
            _phase = st["phase"]?.GetValue<int>() ?? 1;
            Info($"boot with phase {_phase}; world {Saves.WorldId} file revision {Header()?.rev}; data {WorldData.Loaded} " +
                 $"generation {Saves.Status?.DataGeneration}");
            switch (_phase)
            {
                case 1: await Phase1(st); break;
                case 2: await Phase2(st); break;
                case 3: await Phase3(st); break;
                case 4: Phase4(st); break;
                case 5: await Phase5(st); break;
                case 6: Phase6(st); break;
                default: Info("all phases done; delete kiln/data/saves-test to run again"); break;
            }
        }
        catch (Exception e)
        {
            Fail($"test threw: {e}");
        }
        Info($"summary: {_pass} PASS, {_fail} FAIL");
    }

    // ------------------------------------------------------------------ 1: fresh
    private async Task Phase1(JsonObject st)
    {
        if (!Check(Saves.Available, "kiln.saves is available")) return;
        var h = Header();
        Check(h != null && Saves.WorldId == h.Value.guid.ToString(), $"Saves.WorldId {Saves.WorldId} = world file GUID {h?.guid} ({Saves.WorldFile})");
        var s = Saves.Status!;
        Check(s.AutosaveInterval == TimeSpan.FromSeconds(30), $"autosave interval {s.AutosaveInterval} read from the game's code");
        Check(s.CanRequest && s.CanObserveWrites, $"request + write hooks on (status {s})");
        Info($"save dir {Saves.SaveDirectory}, data {Saves.DataDirectory}, backups {Saves.BackupDirectory}");
        Check(WorldData.Loaded == DataMatch.None, $"first run: no mod data yet ({WorldData.Loaded})");

        var token1 = Guid.NewGuid().ToString();
        var p = WorldData.Get<Probe>("probe");
        p.Token = token1;
        Check(ReferenceEquals(p, WorldData.Get<Probe>("probe")), "WorldData.Get returns the same live object");
        WorldData.Set("list", new List<string> { "a", "b" });
        Check(WorldData.TryGet<List<string>>("list", out var l) && l.Count == 2, "WorldData.Set/TryGet of a list");
        PlayerData.Get<Tag>(FakeSteam, "probe").Token = token1;
        Check(PlayerData.Players.Contains(FakeSteam), $"PlayerData.Players lists {FakeSteam}");

        // Entities: a saved prop, a never-saved enemy, a prop that gets destroyed.
        var anchor = FindAnchor();
        if (!Check(anchor is not null, $"anchor point {anchor}")) return;
        var a = anchor!.Value;
        var barrelT = Spawn.Lookup("Prop_Deco_Steppeprops_Barrel_01");
        var vukahT = Spawn.Lookup("Enemy_Vukah_Melee");
        if (!Check(barrelT != null && vukahT != null, "barrel and Vukah templates")) return;
        var bpos = new WorldPosition(a.X + 5, a.Y, a.Z + 5);
        var barrel = await Spawn.Entity(barrelT!.Id, bpos, 0, new SpawnOptions { Tag = "saves-test" });
        EntityData.Get<Tag>(barrel, "tag").Token = token1;
        Check(EntityData.IsPersistent(barrel), $"barrel {barrel} attachments are persistent (template saved by the game)");
        var vukah = await Spawn.Entity(vukahT!.Id, new WorldPosition(a.X - 8, a.Y, a.Z + 8), 0, new SpawnOptions { Tag = "saves-test" });
        EntityData.Set(vukah, "tag", new Tag { Token = token1, Note = "session only" });
        Check(!EntityData.IsPersistent(vukah), $"Vukah {vukah} attachments are session-only (DoNotSave template)");
        var doomed = await Spawn.Entity(barrelT.Id, new WorldPosition(a.X + 7, a.Y, a.Z + 5), 0, new SpawnOptions { Tag = "saves-test" });
        EntityData.Get<Tag>(doomed, "tag").Token = "doomed";
        Check(EntityData.Has(doomed, "tag"), $"barrel {doomed} has data before it is destroyed");
        Spawn.Destroy(doomed);
        await Wait(2.5);
        Check(!EntityData.Has(doomed, "tag") && !EntityData.Entities.Contains(doomed),
            $"destroyed barrel {doomed}: attachment dropped (live attachments now: {string.Join(", ", EntityData.Entities)})");
        st["barrel"] = new JsonObject { ["id"] = barrel.Value, ["template"] = barrelT.Id.ToString(), ["x"] = bpos.X, ["y"] = bpos.Y, ["z"] = bpos.Z };

        // An autosave: Saving before Saved, same revision, the file has it, Kiln's manifest is confirmed.
        var saves0 = _saveds.Count;
        var sw = Stopwatch.StartNew();
        while (!_saveds.Skip(saves0).Any(e => e.Reason == SaveReason.Autosave && e.Success) && sw.Elapsed.TotalSeconds < 45)
            await Context.Scheduler.NextTick();
        var auto = _saveds.Skip(saves0).FirstOrDefault(e => e.Reason == SaveReason.Autosave && e.Success);
        if (Check(auto != null, $"an autosave was observed within {sw.Elapsed.TotalSeconds:F1} s: {auto}"))
        {
            var saving = _savings.LastOrDefault(e => e.Revision == auto!.Revision);
            Check(saving is { Reason: SaveReason.Autosave, BeforeSnapshot: true },
                $"its WorldSavingEvent: {saving} (before snapshot {saving?.BeforeSnapshot}, step {saving?.Step})");
            var i1 = _order.IndexOf($"saving:{auto!.Revision}:Autosave");
            var i2 = _order.IndexOf($"saved:{auto.Revision}:Autosave:True");
            Check(i1 >= 0 && i2 > i1, $"order: saving at {i1}, saved at {i2} ({string.Join(" ", _order.TakeLast(4))})");
            CheckFileAndManifest(auto.Revision, "autosave");
        }

        // A requested save.
        var before = Header()?.rev ?? 0;
        var r = await Saves.Request();
        Check(r.Success && r.Reason == SaveReason.Request && r.Revision == before + 1,
            $"Saves.Request(): {r} (file was at {before})");
        Check(_savings.Any(e => e.Reason == SaveReason.Request && e.Revision == r.Revision), "WorldSavingEvent(Request) fired for it");
        CheckFileAndManifest(r.Revision, "request");

        // A backup: world file + matching sidecar generation.
        var b = await Saves.Backup("t1");
        Check(b.Revision == Header()?.rev && b.DataBundles >= 3, $"Saves.Backup(t1): {b} ({b.DataBundles} bundles) in {b.Directory}");
        var bh = ReadHeader(Path.Combine(b.Directory, "savegame", Path.GetFileName(Saves.WorldFile!)));
        Check(bh?.rev == b.Revision, $"backup's world file is revision {bh?.rev}");
        Check(Saves.Backups.Any(x => x.Directory == b.Directory), $"Saves.Backups lists it ({Saves.Backups.Count} backups)");
        var lb = Ledger(b.Revision);
        st["backup"] = new JsonObject { ["dir"] = Path.GetFileName(b.Directory), ["rev"] = b.Revision, ["token"] = token1, ["counter"] = lb?["counter"]?.GetValue<long>() ?? -1 };

        // New tokens after the backup, then one more save.
        var token2 = Guid.NewGuid().ToString();
        p.Token = token2;
        PlayerData.Get<Tag>(FakeSteam, "probe").Token = token2;
        EntityData.Get<Tag>(barrel, "tag").Token = token2;
        var r2 = await Saves.Request();
        Check(r2.Success, $"save after the backup: {r2}");
        st["token2"] = token2;
        st["phase"] = 2;
        SaveState(st);
        Ready("stop (SIGINT): phase 2 checks the shutdown save");
    }

    // ------------------------------------------------------------------ 2: after SIGINT
    private async Task Phase2(JsonObject st)
    {
        var h = Header()!.Value;
        var L = Ledger(h.rev);
        Check(WorldData.Loaded == DataMatch.Exact, $"mod data matches the world file exactly ({WorldData.Loaded}, file revision {h.rev})");
        Check(L?["reason"]?.GetValue<string>() == "Shutdown", $"the world file's revision {h.rev} is the shutdown save (ledger: {L?.ToJsonString()})");
        CheckProbe(h.rev, "after SIGINT restart");
        var token2 = st["token2"]!.GetValue<string>();
        Check(PlayerData.TryGet<Tag>(FakeSteam, "probe", out var pt) && pt.Token == token2, $"PlayerData back: token {pt?.Token}, note '{pt?.Note}'");
        Check(WorldData.TryGet<List<string>>("list", out var l) && l.SequenceEqual(["a", "b"]), "WorldData list back");
        // The barrel comes back with a new id at the same place; its attachment follows it.
        var bj = st["barrel"]!.AsObject();
        var bpos = new WorldPosition(bj["x"]!.GetValue<double>(), bj["y"]!.GetValue<double>(), bj["z"]!.GetValue<double>());
        var tmpl = Guid.Parse(bj["template"]!.GetValue<string>());
        var sw = Stopwatch.StartNew();
        EntityId found = default;
        while (sw.Elapsed.TotalSeconds < 90)
        {
            foreach (var id in Spawn.Find(tmpl, bpos, 0.5, 4))
                if (EntityData.TryGet<Tag>(id, "tag", out var t) && t.Token == token2)
                    found = id;
            if (!found.IsNone) break;
            await Wait(1);
        }
        Check(!found.IsNone, $"barrel (was {bj["id"]}) is back as entity {found} with its attachment (after {sw.Elapsed.TotalSeconds:F1} s; " +
                             $"{EntityData.Waiting} attachments still waiting)");
        Check(EntityData.Entities.Count == 1, $"only the barrel's attachment is live ({string.Join(", ", EntityData.Entities)}): the Vukah's was session-only");
        st["phase"] = 3;
        st["kills"] = 0;
        SaveState(st);
        StartChurn();
        Ready("kill (-9) any time: saves are being written back to back");
    }

    // ------------------------------------------------------------------ 3: after kill -9
    private async Task Phase3(JsonObject st)
    {
        var h = Header()!.Value;
        CheckProbe(h.rev, $"after kill -9 #{st["kills"]!.GetValue<int>() + 1}");
        CheckSidecar();
        var kills = st["kills"]!.GetValue<int>() + 1;
        st["kills"] = kills;
        if (kills < 3)
        {
            SaveState(st);
            await Wait(3);
            StartChurn();
            Ready("kill (-9) any time: saves are being written back to back");
            return;
        }
        st["phase"] = 4;
        SaveState(st);
        Info("kill -9 rounds done; restart (any way) for the restore phase");
        Ready("stop (SIGINT): phase 4 stages the restore");
    }

    // ------------------------------------------------------------------ 4: stage the restore
    private void Phase4(JsonObject st)
    {
        var b = Saves.RestoreOnNextStart("t1");
        Check(Saves.PendingRestore == Path.GetFileName(b.Directory), $"restore of {b} staged ({Saves.PendingRestore})");
        st["phase"] = 5;
        SaveState(st);
        Ready("stop (SIGINT): the restore is applied at the next start");
    }

    // ------------------------------------------------------------------ 5: after the restore
    private async Task Phase5(JsonObject st)
    {
        var bk = st["backup"]!.AsObject();
        var rev = bk["rev"]!.GetValue<uint>();
        var token1 = bk["token"]!.GetValue<string>();
        var dir = Saves.BackupDirectory!;
        var result = Directory.EnumerateFiles(dir, "restore.result-*.json").Order().LastOrDefault();
        var rj = result != null ? JsonNode.Parse(File.ReadAllText(result)) : null;
        Check(rj?["ok"]?.GetValue<bool>() == true, $"restore applied at boot: {Path.GetFileName(result)} {rj?.ToJsonString()}");
        Check(Saves.PendingRestore == null, "no restore pending any more");
        var h = Header()!.Value;
        Check(h.rev == rev, $"world file revision {h.rev} = backup t1's revision {rev}");
        Check(WorldData.Loaded == DataMatch.Exact, $"mod data generation matches the restored world file ({WorldData.Loaded}, generation {Saves.Status?.DataGeneration})");
        var p = WorldData.Get<Probe>("probe");
        Check(p.Token == token1 && p.Counter == bk["counter"]!.GetValue<long>(),
            $"WorldData is t1's: token {p.Token[..8]} (t1 {token1[..8]}), counter {p.Counter} (t1 {bk["counter"]})");
        Check(PlayerData.TryGet<Tag>(FakeSteam, "probe", out var pt) && pt.Token == token1, $"PlayerData is t1's ({pt?.Token[..8]})");
        var bj = st["barrel"]!.AsObject();
        var bpos = new WorldPosition(bj["x"]!.GetValue<double>(), bj["y"]!.GetValue<double>(), bj["z"]!.GetValue<double>());
        var tmpl = Guid.Parse(bj["template"]!.GetValue<string>());
        var sw = Stopwatch.StartNew();
        string? tok = null;
        while (sw.Elapsed.TotalSeconds < 90 && tok == null)
        {
            foreach (var id in Spawn.Find(tmpl, bpos, 0.5, 4))
                if (EntityData.TryGet<Tag>(id, "tag", out var t)) tok = t.Token;
            if (tok == null) await Wait(1);
        }
        Check(tok == token1, $"barrel attachment is t1's ({tok?[..8]})");
        // A few saves in the restored timeline, so the game's backup/ folder has older files of it.
        for (var i = 0; i < 3; i++)
        {
            var r = await Saves.Request();
            Check(r.Success, $"save {i + 1} after the restore: {r}");
            await Wait(1.5);
        }
        st["phase"] = 6;
        st["newest"] = Header()!.Value.rev;
        SaveState(st);
        Ready("stop (SIGINT), then put an older world file from savegame/backup in place");
    }

    // ------------------------------------------------------------------ 6: the game's own backup file restored
    private void Phase6(JsonObject st)
    {
        var h = Header()!.Value;
        var newest = st["newest"]!.GetValue<uint>();
        Check(h.rev != newest + 1, $"another world file is in place: revision {h.rev}, time {h.unix} (the shutdown save wrote {newest + 1})");
        Check(WorldData.Loaded == DataMatch.Exact, $"its mod data generation was kept and loaded ({WorldData.Loaded}, generation {Saves.Status?.DataGeneration})");
        CheckProbe(h.rev, "after restoring the game's own backup file");
        st["phase"] = 7;
        SaveState(st);
        Ready("done");
    }

    // ---- helpers ----
    private void StartChurn()
    {
        _churn = true;
        _ = Churn();
    }

    private async Task Churn()
    {
        var n = 0;
        var sw = Stopwatch.StartNew();
        while (_churn)
        {
            var r = await Saves.Request();
            if (!r.Success) Info($"churn: {r}");
            if (++n % 20 == 0) Info($"churn: {n} saves in {sw.Elapsed.TotalSeconds:F0} s, file revision {Header()?.rev}");
        }
    }

    private void CheckProbe(uint rev, string what)
    {
        var L = Ledger(rev);
        var p = WorldData.Get<Probe>("probe");
        var shaOk = p.Payload.Length == 0 ? p.PayloadSha == "" : Sha(p.Payload) == p.PayloadSha;
        Check(L != null && p.Counter == L["counter"]!.GetValue<long>() && p.Token == L["token"]!.GetValue<string>() &&
              p.PayloadSha == L["sha"]!.GetValue<string>() && shaOk && p.StagedFor == rev,
            $"{what}: world file revision {rev}, mod data {WorldData.Loaded} generation {Saves.Status?.DataGeneration}: counter {p.Counter} " +
            $"(ledger {L?["counter"]}), staged for {p.StagedFor}, payload {p.Payload.Length} chars sha ok {shaOk}, ledger sha match " +
            $"{p.PayloadSha == L?["sha"]?.GetValue<string>()}");
    }

    private void CheckFileAndManifest(uint rev, string what)
    {
        var h = Header();
        Check(h?.rev == rev, $"{what}: world file header revision {h?.rev} = event revision {rev}");
        var man = Path.Combine(Saves.DataDirectory!, "manifests", $"{rev:D10}-{h?.unix}.json");
        Check(File.Exists(man), $"{what}: Kiln's confirmed manifest {Path.GetFileName(man)} exists");
    }

    /// <summary>Every manifest parses and every blob it names is there with the right sha256.</summary>
    private void CheckSidecar()
    {
        var dir = Saves.DataDirectory!;
        int manifests = 0, blobs = 0, bad = 0;
        foreach (var m in Directory.EnumerateFiles(Path.Combine(dir, "manifests"), "*.json"))
        {
            manifests++;
            try
            {
                foreach (var (_, e) in JsonNode.Parse(File.ReadAllBytes(m))!["bundles"]!.AsObject())
                {
                    var sha = e!["sha256"]!.GetValue<string>();
                    var path = Path.Combine(dir, "blobs", sha[..2], sha + ".json");
                    blobs++;
                    if (!File.Exists(path) || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != sha) bad++;
                }
            }
            catch (Exception ex)
            {
                bad++;
                Info($"manifest {Path.GetFileName(m)}: {ex.Message}");
            }
        }
        var tmps = Directory.EnumerateFiles(dir, "*.tmp-*", SearchOption.AllDirectories).Count();
        Check(bad == 0, $"sidecar intact: {manifests} manifests, {blobs} blob references, {bad} bad; {tmps} leftover temp files (ignored, pruned later)");
    }

    private (Guid guid, uint rev, uint unix)? Header() => ReadHeader(Saves.WorldFile);

    private static (Guid guid, uint rev, uint unix)? ReadHeader(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[0x24];
        if (f.Read(b) != b.Length || Encoding.ASCII.GetString(b, 0, 4) != "SV2C") return null;
        return (new Guid(b.AsSpan(0x0C, 16)), BitConverter.ToUInt32(b, 0x1C), BitConverter.ToUInt32(b, 0x20));
    }

    private JsonObject? Ledger(uint rev)
    {
        if (!File.Exists(LedgerPath)) return null;
        JsonObject? hit = null;
        foreach (var line in File.ReadAllLines(LedgerPath))
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject o && o["rev"]?.GetValue<uint>() == rev) hit = o;
            }
            catch (JsonException)
            {
                // a line cut by kill -9
            }
        }
        return hit;
    }

    private WorldPosition? FindAnchor()
    {
        foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
            foreach (var id in Spawn.FindByName(filter, max: 4))
                if (Spawn.Inspect(id) is { } info)
                    return info.Position;
        return null;
    }

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private JsonObject LoadState()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private void SaveState(JsonObject st)
    {
        File.WriteAllText(StatePath + ".tmp", st.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(StatePath + ".tmp", StatePath, true);
    }

    private async Task Wait(double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) await Context.Scheduler.NextTick();
    }

    private bool Check(bool ok, string what)
    {
        if (ok) Pass(what);
        else Fail(what);
        return ok;
    }

    private void Ready(string what) => Log.Info($"saves-test: READY {what} (phase {_phase}: {_pass} PASS, {_fail} FAIL)");
    private void Pass(string s) { _pass++; Log.Info($"saves-test: PASS {s}"); }
    private void Fail(string s) { _fail++; Log.Error($"saves-test: FAIL {s}"); }
    private void Info(string s) => Log.Info($"saves-test: INFO {s}");
}
