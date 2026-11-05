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
        Log.Info($"assets-test done: {_pass} PASS, {_fail} FAIL");
    }

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
