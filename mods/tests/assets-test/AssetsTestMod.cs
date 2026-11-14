// SPDX-License-Identifier: MIT
using Kiln;

namespace AssetsTest;

/// <summary>
/// kiln.assets lab test (no player). On a dedicated server it checks the resource service and content reader, which
/// both run there: read a vanilla ItemInfo by GUID, register a new resource and read it back, add a content blob and
/// read it back, override a resource for the session, and dump. Icons and textures are client-only (client checklist).
/// </summary>
public sealed class AssetsTestMod : Mod
{
    // Bronze Sword (Weapon_T1_1H_SwordEpic_01): objectId GUID, loaded as keen::ItemInfo part 0 on both sides.
    private static readonly Guid BronzeSword = Guid.Parse("1d622bd9-8170-41b1-822e-d3984b386d95");
    private int _pass, _fail;

    public override void OnLoad()
    {
        Log.Info($"assets-test: OnLoad (assets {(Assets.Available ? "available" : "MISSING")})");
        if (!Assets.Available) { Log.Info("assets-test done: 0 PASS, 0 FAIL (service unavailable)"); return; }
        _ = RunAsync();
        // Client session helper: where the server world runs in this process (a hosting client), give each player once
        // the items the visual checks need (kiln/data/assets-test/kits.txt remembers who got them). A dedicated
        // server lab gets the kit too only if a player joins, which the lab never has.
        if (Players.Available) Players.Joined += p => _ = GiveKit(p);
    }

    // Item ids are FNV-1a of each item's GUID (the game's rule): vanilla by debug name, new items by id.
    private const uint Emberseal = 0x68548fc5, Glacier = 0x723a87fa, FrostMail = 0x3d76341a, FrostShard = 0xfaa382cf,
                       FrostEdge = 0xdc853d25;

    private async Task GiveKit(Player p)
    {
        var key = p.SteamId != 0 ? p.SteamId.ToString() : p.Name;
        var file = Path.Combine(Context.DataDirectory, "kits.txt");
        var done = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();
        if (done.Contains(key)) { Log.Info($"assets-test: kit for {p} already given"); return; }
        for (var i = 0; i < 20; i++) await Context.Scheduler.NextTick();  // let the player's inventory settle
        var r = new List<string>
        {
            (await Items.Give(p.Entity, "Weapon_T1_1H_SwordEpic_01", 1, new Gear(1, ItemRarity.Common), GiveOptions.Notify)).ToString(),
            (await Items.Give(p.Entity, "Consumable_Grenade_Explosive_PowderBall_T1", 3, options: GiveOptions.Notify)).ToString(),
            (await Items.Give(p.Entity, new ItemId(Emberseal), 2, options: GiveOptions.Notify)).ToString(),
            (await Items.Give(p.Entity, new ItemId(Glacier), 1, new Gear(1, ItemRarity.Epic), GiveOptions.Notify)).ToString(),
            (await Items.Give(p.Entity, new ItemId(FrostMail), 1, new Gear(1, ItemRarity.Rare), GiveOptions.Notify)).ToString(),
            (await Items.Give(p.Entity, new ItemId(FrostShard), 5, options: GiveOptions.Notify)).ToString(),
            (await Items.Give(p.Entity, new ItemId(FrostEdge), 1, new Gear(1, ItemRarity.Rare), GiveOptions.Notify)).ToString(),
            (await Items.Give(p.Entity, "Material_T1_Twigs", 6, options: GiveOptions.Notify)).ToString(),
        };
        Log.Info($"assets-test: client kit for {p}: {string.Join("; ", r)}");
        Directory.CreateDirectory(Context.DataDirectory);
        File.AppendAllLines(file, new[] { key });
    }

    private void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private async Task RunAsync()
    {
        try
        {
            // The resource system is up well before the world; a couple of ticks make sure of it.
            for (var i = 0; i < 4; i++) await Context.Scheduler.NextTick();
            Run();
        }
        catch (Exception ex)
        {
            Check(false, $"test aborted: {ex}");
        }
        try
        {
            FullCatalog();
        }
        catch (Exception ex)
        {
            Check(false, $"assets.full-catalog aborted: {ex}");
        }
        Log.Info($"assets-test done: {_pass} PASS, {_fail} FAIL");
    }

    // ---- 6. the content blob catalog (0.2.0-dev.28, assets.blob_catalog) ------------------------------------------------
    // PASS assets.full-catalog only when the snapshot holds the game's whole index (its blob count as Kiln's own KFC reader
    // counts it, catalog_fixture.py), a game blob no mod registered with the game origin, whose bytes read through
    // Assets.ReadBlob hash to the SHA-256 the independent reader computed; plus a managed addition, a managed override (of a
    // game blob, with its own bytes: nothing changes for a player) and a manifest addition with their origins, an
    // unchanged open snapshot after a registration, and a visibly incomplete registrations-only view.
    private void FullCatalog()
    {
        const string cap = "assets.blob_catalog";
        Log.Info($"assets-test: {Capabilities.Why(cap)}");
        var fixtureFile = Path.Combine(Context.DataDirectory, "catalog-fixture.json");
        if (!File.Exists(fixtureFile))
        {
            Check(false, $"assets.full-catalog: no fixture at {fixtureFile} (python3 mods/tests/assets-test/catalog_fixture.py <game folder> <that file>)");
            return;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixtureFile));
        var fixture = doc.RootElement;
        var gameTotal = fixture.GetProperty("total").GetUInt64();
        var gameHash = (System.Text.Json.JsonSerializer.Deserialize<BlobCatalogEntry>(
            $"{{\"contentHash\":\"{fixture.GetProperty("hash").GetString()}\",\"origins\":[\"game\"],\"selectedOrigin\":\"game\",\"owner\":\"0\",\"sequence\":\"0\",\"overridden\":false}}")!).ContentHash;
        var gameSha = fixture.GetProperty("sha256").GetString();
        if (!Capabilities.Is(cap)) { Check(false, $"assets.full-catalog: {Capabilities.Why(cap)}"); return; }

        // registrations of three origins: a managed addition, a managed override of a game blob (its own bytes), a manifest addition
        var gameBytes = Assets.ReadBlob(gameHash);
        var managedAdd = new byte[3000];
        new Random(2813).NextBytes(managedAdd);
        var added = Assets.AddBlob(managedAdd);
        if (gameBytes != null) Assets.OverrideBlob(gameHash, gameBytes);
        var manifestDir = Path.Combine(Context.DataDirectory, "catalog-manifest");
        Directory.CreateDirectory(Path.Combine(manifestDir, "assets"));
        var manifestBytes = new byte[2000];
        new Random(2814).NextBytes(manifestBytes);
        File.WriteAllBytes(Path.Combine(manifestDir, "assets", "probe.blob"), manifestBytes);
        File.WriteAllText(Path.Combine(manifestDir, "assets", "assets.json"), "{\"format\":1,\"entries\":[{\"kind\":\"blob\",\"file\":\"probe.blob\"}]}");
        Log.Info($"assets-test: manifest: {Assets.LoadManifest(manifestDir)}");

        var r = Assets.OpenBlobCatalog();
        Check(r.Ok && r.Snapshot != null && r.Info.Status == BlobCatalogStatus.Complete && r.Info.GameComplete && r.Info.RegistrationsComplete,
              $"catalog: a complete snapshot: {r.Info}");
        if (r.Snapshot is not { } snap) return;
        var all = new List<BlobCatalogEntry>((int)snap.Total);
        for (ulong at = 0; at < snap.Total;)
        {
            var page = snap.ReadPage(at);
            all.AddRange(page);
            at += (ulong)page.Count;
        }
        var sorted = all.Zip(all.Skip(1)).All(p => Order(p.First.ContentHash).CompareTo(Order(p.Second.ContentHash)) < 0);
        var games = (ulong)all.Count(e => (e.Origins & BlobOrigins.Game) != 0);
        Check(all.Count == (int)snap.Total && sorted, $"catalog: {all.Count} entries paged in 256s, sorted by size and hash");
        Check(games == gameTotal, $"catalog: {games} entries carry the game origin; Kiln's KFC reader counts {gameTotal} in the index");
        var g = all.FirstOrDefault(e => e.ContentHash == gameHash);
        var a = all.FirstOrDefault(e => e.ContentHash == added);
        var m = all.FirstOrDefault(e => e.ContentHash.Size == 2000 && e.Origins == BlobOrigins.Manifest);
        Check(a is { Origins: BlobOrigins.Managed, SelectedOrigin: BlobOrigins.Managed, Overridden: false }, $"catalog: the managed addition: {a}");
        Check(m is { SelectedOrigin: BlobOrigins.Manifest, Overridden: false }, $"catalog: the manifest addition: {m}");
        Check(g is { Origins: BlobOrigins.Game | BlobOrigins.Managed, SelectedOrigin: BlobOrigins.Managed, Overridden: true },
              $"catalog: the overridden game blob keeps the game origin: {g}");
        var sha = gameBytes is null ? "" : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(gameBytes));
        var untouched = all.FirstOrDefault(e => e.Origins == BlobOrigins.Game && e.SelectedOrigin == BlobOrigins.Game && e.Owner == 0 && !e.Overridden);
        var full = r.Info.Status == BlobCatalogStatus.Complete && games == gameTotal && g is not null && sha == gameSha && untouched is not null;
        Log.Info($"assets-test: ReadBlob({gameHash}) sha256 {sha}, the independent reader's {gameSha}; a game-only entry {untouched}");

        // an open snapshot never changes; a new one has a newer generation
        var second = new byte[1500];
        new Random(2815).NextBytes(second);
        var later = Assets.AddBlob(second);
        var again = snap.ReadPage(0, 1)[0] == all[0] && snap.Total == (ulong)all.Count;
        using (var newer = Assets.OpenBlobCatalog().Snapshot)
            Check(again && newer is not null && newer.Info.Generation > snap.Info.Generation && newer.Total == snap.Total + 1,
                  $"catalog: the open snapshot is unchanged after a registration; a new one: generation {newer?.Info.Generation} > {snap.Info.Generation}, {newer?.Total} entries");
        var regs = Assets.OpenBlobCatalog(BlobCatalogMode.RegistrationsOnly);
        using (var rs = regs.Snapshot)
            Check(rs is { Info: { Status: BlobCatalogStatus.Incomplete, GameComplete: false, RegistrationsComplete: true } } &&
                  rs.ReadPage(0, Math.Min(256, (int)Math.Max(1ul, rs.Total))).All(e => (e.Origins & BlobOrigins.Game) == 0),
                  $"catalog: registrations only is incomplete and names no game blob: {rs?.Info}");
        snap.Dispose();
        var closed = false;
        try { snap.ReadPage(0, 1); } catch (ObjectDisposedException) { closed = true; }
        Check(closed, "catalog: a disposed snapshot refuses reads");
        Check(full, full ? "assets.full-catalog" : $"assets.full-catalog: status {r.Info.Status}, game entries {games} of {gameTotal}, " +
                                                   $"game blob {(g is null ? "missing" : "listed")}, sha256 {(sha == gameSha ? "equal" : "differs")}");
        Assets.RemoveBlob(added);
        Assets.RemoveBlob(later);
        if (gameBytes != null) Assets.RemoveBlob(gameHash);
    }

    private static (uint, uint, uint, uint) Order(ContentHash h) => (h.Size, h.H0, h.H1, h.H2);

    private void Run()
    {
        Log.Info($"assets-test: status {Assets.Status()}");

        // 1. Read a vanilla ItemInfo resource by GUID.
        var itemKey = new AssetKey(BronzeSword, AssetKey.ItemInfoType);
        var item = Assets.ReadResource(itemKey);
        Check(item is { Length: >= 2088 }, $"read vanilla ItemInfo {BronzeSword}: {(item == null ? "null" : item.Length + " bytes")}");
        // its first 4 bytes are itemId = FNV-1a(GUID) = 0x43108313
        if (item is { Length: >= 4 })
        {
            uint id = BitConverter.ToUInt32(item, 0);
            Check(id == 0x43108313, $"ItemInfo itemId field = 0x{id:x8} (expected 0x43108313)");
        }

        // 1b. The same resource as a reflected struct: a typed view over its bytes (kiln.types + kiln.assets).
        var reflected = Assets.Reflect(itemKey);
        Check(reflected != null && reflected.Type.QualifiedName == "keen::ItemInfo" && reflected.Get<uint>("itemId") == 0x43108313,
              $"Assets.Reflect: {reflected} itemId 0x{reflected?.Get<uint>("itemId") ?? 0:x8}");
        Check(reflected != null && reflected.Get<string>("debugName") == "Weapon_T1_1H_SwordEpic_01" && reflected.Bytes.AsSpan().SequenceEqual(item),
              $"Assets.Reflect reads its text through the blob offsets: {reflected?["debugName"]}");

        // 2. Register a new resource under a fresh GUID+type and read it back.
        var newGuid = Guid.NewGuid();
        var newKey = new AssetKey(newGuid, "kiln::AssetTest", 0);
        var payload = new byte[256];
        new Random(1234).NextBytes(payload);
        Assets.Register(newKey, payload, mustBeNew: true);
        var back = Assets.ReadResource(newKey);
        Check(back != null && back.AsSpan().SequenceEqual(payload), "register a new resource and read the exact bytes back");

        // 3. Add a content blob and read it back; the derived hash records the length.
        var blob = new byte[4096];
        new Random(99).NextBytes(blob);
        var hash = Assets.AddBlob(blob);
        Check(hash.Size == (uint)blob.Length, $"blob hash size {hash.Size} == {blob.Length}");
        var listed = Assets.ListBlobs();
        Check(listed.Count == 1 && listed[0].Hash == hash && listed[0].Kind == BlobKind.Added && listed[0].Mod == "assets-test",
              $"ListBlobs lists the blob this mod added: {string.Join(", ", listed.Select(b => $"{b.Kind} {b.Hash}"))}");
        Check(Assets.ListAllBlobs().Any(b => b.Hash == hash && b.Mod == "assets-test"), "ListAllBlobs lists it too");
        // reading a blob by hash needs the content reader; skip quietly if content is off on this build
        var status = Assets.Status();
        if (status.Contains("\"content\":true"))
        {
            var read = ReadBlob(hash, blob.Length);
            Check(read != null && read.AsSpan().SequenceEqual(blob), "add a content blob and read the exact bytes back");
        }
        else
        {
            Log.Info("SKIP blob read-back: content reader off on this build");
        }

        // 3b. A blob under a hash the game lacks and the bytes don't derive: what {"kind":"blob","hash":...} does, what shader code under the
        // game's own ContentHash needs. The content map must serve it.
        if (status.Contains("\"content\":true"))
        {
            var other = new byte[4096];
            new Random(7).NextBytes(other);
            Assets.RemoveBlob(hash);                  // hash is now one nothing serves: not the game's, not Kiln's
            Check(ReadBlob(hash, other.Length) == null, "a hash the game lacks reads as nothing");
            Assets.OverrideBlob(hash, other);
            var served = ReadBlob(hash, other.Length);
            Check(served != null && served.AsSpan().SequenceEqual(other), "the content map serves a hash the game lacks, under bytes that don't derive it");
            Check(Assets.ListBlobs().Count == 1 && Assets.ListBlobs()[0].Hash == hash && Assets.ListBlobs()[0].Kind == BlobKind.Override,
                  $"ListBlobs lists the override: {string.Join(", ", Assets.ListBlobs().Select(b => $"{b.Kind} {b.Hash}"))}");
            var refused = false;
            try { Assets.OverrideBlob(hash, new byte[16]); } catch (Exception) { refused = true; }
            Check(refused, "a blob whose length isn't the hash's size is refused");
        }

        // 4. Override a vanilla resource for the session, read the override, then take it off.
        if (item != null)
        {
            var edited = (byte[])item.Clone();
            edited[8] ^= 0xFF;  // a field well past the identity header
            Assets.Override(itemKey, edited);
            var ov = Assets.ReadResource(itemKey);
            Check(ov != null && ov[8] == edited[8], "override a vanilla ItemInfo and read the edited byte back");
            Assets.RemoveResource(itemKey);
            var restored = Assets.ReadResource(itemKey);
            Check(restored != null && restored[8] == item[8], "remove the override and read the vanilla byte again");
        }

        // 5. Dump the vanilla ItemInfo to kiln/data/assets/dump/.
        var rep = Assets.Dump($"{BronzeSword}:keen::ItemInfo");
        Check(rep.Contains("wrote") && rep.Contains("bytes"), $"dump ItemInfo: {rep}");

        // clean up the test's new resource / blob
        Assets.RemoveResource(newKey);
        Assets.RemoveBlob(hash);
        Check(Assets.ListBlobs().Count == 0, "ListBlobs is empty again after RemoveBlob");
    }

    private static byte[]? ReadBlob(ContentHash hash, int size)
    {
        // Kiln.Assets has no BlobRead on the public surface (it is a debug path); use Dump then read the file.
        var rep = Assets.Dump($"blob:{hash}");
        if (!rep.Contains("wrote")) return null;
        // the dump wrote <size>-<h0>-<h1>-<h2>.blob under kiln/data/assets/dump; the report ends with its path
        var at = rep.IndexOf(" to ", StringComparison.Ordinal);
        if (at < 0) return null;
        var path = rep[(at + 4)..].Trim();
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
