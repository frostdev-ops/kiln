// SPDX-License-Identifier: MIT
// 0.2.0-dev.28, package 13b-shroudforge (chapter 13 §13b): the C# equivalents of ShroudForge's rows. Orders 3800-3899.
//   13-assets.catalog-client   the content blob catalog in a game client (holds assets.blob_catalog on the client side)
//   13-world.transform-scale   a transform-scale write as players see and touch it (holds nothing; decides the docs' row)
// The pack does not reference Kiln.Assets (ClientPass.csproj is not this package's file), so the catalog is reached through
// the shared Kiln.Assets assembly the host has loaded, by name: the same calls a mod makes.
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Kiln;
using Kiln.Testing;

namespace ClientPass;

public sealed partial class ClientPassMod
{
    private const string Dev2813bArea = "Other engines";

    /// <summary>The independent side of the catalog check: catalog_fixture.py's facts about this game's content index.</summary>
    private string CatalogFixtureFile => Path.Combine(Context.DataDirectory, "catalog-fixture.json");

    [ClientCheck("13-assets.catalog-client", "Client asset catalog acceptance", Area = Dev2813bArea, Since = Dev28,
        Needs = ClientCheckNeeds.Hosted, Order = 3800)]
    public async Task AssetsCatalogClient(CheckContext c)
    {
        if (!File.Exists(CatalogFixtureFile))
        {
            c.Instruct("This check compares Kiln's catalog with the game's content index read by Kiln's own reader. Quit, run " +
                       $"`python3 mods/tests/assets-test/catalog_fixture.py <the game's folder> \"{CatalogFixtureFile}\"` on this PC, " +
                       "start again and run this check. Press skip now.");
            await c.Verdict();
            return;
        }
        if (!await NeedOpen(c, "assets.blob_catalog")) return;
        var assets = Type.GetType("Kiln.Assets, Kiln.Assets");
        if (!c.Expect(assets is not null, "Kiln.Assets is loaded")) { await c.Verdict(); return; }
        using var fixtureDoc = JsonDocument.Parse(File.ReadAllText(CatalogFixtureFile));
        var fixture = fixtureDoc.RootElement;
        var gameTotal = fixture.GetProperty("total").GetUInt64();
        var gameHashText = fixture.GetProperty("hash").GetString()!;
        var gameSha = fixture.GetProperty("sha256").GetString();
        var call = (string name, object?[] args) => assets!.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);
        var modeType = assets!.Assembly.GetType("Kiln.BlobCatalogMode")!;
        object Mode(string name) => Enum.Parse(modeType, name);
        var entryType = assets.Assembly.GetType("Kiln.BlobCatalogEntry")!;
        dynamic gameHash = ((dynamic)JsonSerializer.Deserialize(
            $"{{\"contentHash\":\"{gameHashText}\",\"origins\":[\"game\"],\"selectedOrigin\":\"game\",\"owner\":\"0\",\"sequence\":\"0\",\"overridden\":false}}", entryType)!).ContentHash;

        // 1. the default open once the game's index is ready (at most 120 s)
        dynamic? opened = null;
        var ready = await c.Until(() =>
        {
            try { opened = call("OpenBlobCatalog", [Mode("RequireComplete")]); return true; }
            catch (TargetInvocationException e) when (e.InnerException is KilnUnavailableException) { return false; }
        }, TimeSpan.FromSeconds(120), "the default catalog open succeeds (the game's content index is readable)");
        if (!ready || opened is null) { await c.Verdict(); return; }
        c.Expect((bool)opened.Ok && opened.Snapshot is not null, "the catalog opened", (string?)opened.Reason);
        if (opened.Snapshot is null) { await c.Verdict(); return; }
        dynamic snap = opened.Snapshot;
        c.Expect(snap.Info.Status.ToString() == "Complete" && (bool)snap.Info.GameComplete && (bool)snap.Info.RegistrationsComplete,
            "a complete snapshot: the game's index and every registration", snap.Info.ToString());

        // 2. page through it; the game's count, the fixture's blob, its bytes
        var all = new List<dynamic>();
        for (ulong at = 0; at < (ulong)snap.Total;)
        {
            var page = (System.Collections.IEnumerable)snap.ReadPage(at, 256);
            var n = 0;
            foreach (dynamic e in page) { all.Add(e); n++; }
            if (n == 0) break;
            at += (ulong)n;
        }
        c.Expect((ulong)all.Count == (ulong)snap.Total, "every page read", $"{all.Count} of {snap.Total}");
        var games = (ulong)all.Count(e => ((int)e.Origins & 1) != 0);
        c.Expect(games == gameTotal, "the entries with the game origin are the game's index", $"{games}; Kiln's KFC reader counts {gameTotal}");
        var g = all.FirstOrDefault(e => e.ContentHash.ToString() == gameHashText);
        c.Expect(g is not null && ((int)g.Origins & 1) != 0, "the fixture's game blob is listed with the game origin", g?.ToString());
        var bytes = (byte[]?)call("ReadBlob", [gameHash]);
        var sha = bytes is null ? "" : Convert.ToHexStringLower(SHA256.HashData(bytes));
        c.Expect(sha == gameSha, "its bytes (Assets.ReadBlob) are the game's", $"sha256 {sha}, the reader's {gameSha}");

        // 3. registrations: a managed addition and a managed override of the game blob with its own bytes (nothing changes on screen)
        var mine = new byte[3001];
        new Random(2813).NextBytes(mine);
        dynamic added = call("AddBlob", [mine])!;
        if (bytes is not null) call("OverrideBlob", [gameHash, bytes]);
        dynamic newer = ((dynamic)call("OpenBlobCatalog", [Mode("RequireComplete")])!).Snapshot;
        var all2 = new List<dynamic>();
        for (ulong at = 0; at < (ulong)newer.Total;)
        {
            var n = 0;
            foreach (dynamic e in (System.Collections.IEnumerable)newer.ReadPage(at, 256)) { all2.Add(e); n++; }
            if (n == 0) break;
            at += (ulong)n;
        }
        var a = all2.FirstOrDefault(e => e.ContentHash.ToString() == added.ToString());
        var o = all2.FirstOrDefault(e => e.ContentHash.ToString() == gameHashText);
        c.Expect((ulong)newer.Info.Generation > (ulong)snap.Info.Generation && (ulong)newer.Total == (ulong)snap.Total + 1,
            "a new snapshot after the registrations: a newer generation, one more entry", $"{newer.Info.Generation} > {snap.Info.Generation}");
        c.Expect(a is not null && (int)a.Origins == 4 && (int)a.SelectedOrigin == 4 && !(bool)a.Overridden,
            "the managed addition: managed only, not overridden", a?.ToString());
        c.Expect(bytes is null || (o is not null && (int)o.Origins == 5 && (int)o.SelectedOrigin == 4 && (bool)o.Overridden),
            "the overridden game blob keeps the game origin, adds managed, the managed bytes are served", o?.ToString());
        c.Expect((ulong)snap.Total == (ulong)all.Count && snap.ReadPage(0ul, 1)[0].Equals(all[0]),
            "the first snapshot did not change");

        // 4. registrations only, then close everything and give the blobs back
        dynamic regs = ((dynamic)call("OpenBlobCatalog", [Mode("RegistrationsOnly")])!).Snapshot;
        c.Expect(regs.Info.Status.ToString() == "Incomplete" && !(bool)regs.Info.GameComplete, "registrations only says incomplete", regs.Info.ToString());
        regs.Dispose();
        newer.Dispose();
        snap.Dispose();
        var closed = false;
        try { snap.ReadPage(0ul, 1); } catch (ObjectDisposedException) { closed = true; }
        c.Expect(closed, "a closed snapshot refuses reads");
        call("RemoveBlob", [added]);
        if (bytes is not null) call("RemoveBlob", [gameHash]);
        c.Instruct("Nothing to look at: the assertions compared Kiln's catalog in this game with the game's own index. Did the game " +
                   "run on normally while the check ran (no stutter longer than a second, no texture that went wrong)? For the " +
                   "joined half, join a Kiln server with this profile and run assets-test there (its log has PASS assets.full-catalog).");
        await c.Verdict();
    }
}
