// SPDX-License-Identifier: MIT
// Blob catalog: every content blob the game serves, and where each one comes from (docs/kiln/services/assets.md, "The
// content blob catalog"). The C# equivalent of ShroudForge's file-content listing and EML's get_all_contents, as the live
// catalog (what is served now), not a listing of the archive on disk.
//   /blobs                 how many blobs, by origin, and how many a mod overrides
//   /blobs mods            the blobs mods registered (registrations only: quick, and visibly incomplete)
//   /blobs json [n]        the first n entries (default 20) as JSON (schema/blob-catalog.schema.json) in this mod's data folder
// What to take from it: a snapshot never changes, so page through it at leisure and dispose it; a refusal for a budget is
// a result, "not ready yet" is an exception naming the capability; the origins say who contributes a blob, the selected
// origin and owner say whose bytes the game reads now; the change event tells you when a new snapshot would differ.
using System.Text.Json;
using Kiln;

namespace BlobCatalog;

public sealed class BlobCatalogMod : Mod
{
    private const string CatalogCapability = "assets.blob_catalog";

    public override void OnLoad()
    {
        Log.Info($"blob-catalog: the catalog is {(Capabilities.Is(CatalogCapability) ? "available" : $"not available ({Capabilities.Why(CatalogCapability)})")}");
        Events.On<AssetsCatalogChangedEvent>(e => Log.Info($"blob-catalog: {e}"));  // a mod's subscriptions end with it
    }

    [Command("blobs", Description = "the content blob catalog: counts by origin; 'mods' for the registrations only; 'json [n]' writes a page")]
    private void Blobs(CommandContext c, string? what = null, int count = 20)
    {
        if (!Capabilities.Is(CatalogCapability))
            throw new CommandException($"no blob catalog here ({CatalogCapability} is off: {Capabilities.Why(CatalogCapability)})");
        var mode = what == "mods" ? BlobCatalogMode.RegistrationsOnly : BlobCatalogMode.RequireComplete;
        BlobCatalogOpenResult opened;
        try
        {
            opened = Assets.OpenBlobCatalog(mode);
        }
        catch (KilnUnavailableException e)
        {
            // the default mode before the game has opened its content index: say so, the registrations still work
            throw new CommandException($"not yet: {e.Message} (try '/blobs mods')");
        }
        if (!opened.Ok) throw new CommandException($"the catalog was not opened: {opened.Reason}");
        using var snapshot = opened.Snapshot!;
        if (what == "json")
        {
            WriteJson(c, snapshot, Math.Clamp(count, 1, BlobCatalogSnapshot.MaxPage));
            return;
        }
        var byOrigin = new Dictionary<BlobOrigins, int>();
        int overridden = 0, total = 0;
        for (ulong at = 0; at < snapshot.Total;)
        {
            var page = snapshot.ReadPage(at);
            foreach (var e in page)
            {
                foreach (var bit in new[] { BlobOrigins.Game, BlobOrigins.Manifest, BlobOrigins.Managed, BlobOrigins.NativeApi })
                    if ((e.Origins & bit) != 0) byOrigin[bit] = byOrigin.GetValueOrDefault(bit) + 1;
                if (e.Overridden) overridden++;
                total++;
            }
            at += (ulong)page.Count;
        }
        var info = snapshot.Info;
        c.Reply($"{total} blobs ({(info.Status == BlobCatalogStatus.Complete ? "the game's and every mod's" : "mods' registrations only")}, " +
                $"generation {info.Generation}): " + string.Join(", ", byOrigin.Select(kv => $"{kv.Key} {kv.Value}")) + $"; {overridden} overridden by a mod");
    }

    private void WriteJson(CommandContext c, BlobCatalogSnapshot snapshot, int count)
    {
        // The records serialize in the schema's shape (uint64 as decimal strings): no options needed.
        var page = snapshot.ReadPage(0, count);
        var file = Path.Combine(Context.DataDirectory, "catalog-page.json");
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(file, JsonSerializer.Serialize(new { info = snapshot.Info, first = "0", entries = page }, new JsonSerializerOptions { WriteIndented = true }));
        c.Reply($"{page.Count} of {snapshot.Total} entries written to {file}");
    }
}
