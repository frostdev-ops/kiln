// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal static partial class Ops
{
    static partial void RegisterSaves(Bridge bridge, Logger log)
    {
        if (!Saves.Available) return;
        bridge.Register("save_status", _ => SaveState());
        // The game's own world save now; answers once the file is written.
        bridge.RegisterAsync("save_now", async _ =>
        {
            var r = await Saves.Request();
            if (!r.Success) throw new AdminError(r.Error ?? "not saved");
            return new JsonObject { ["revision"] = r.Revision, ["ms"] = Math.Round(r.Duration.TotalMilliseconds, 1) };
        });
        bridge.Register("backup_list", _ => new JsonArray([.. Saves.Backups.Select(b => (JsonNode)Backup(b))]));
        // {name} -> the backup (world file + matching mod data), made right after a fresh save.
        bridge.RegisterAsync("backup_create", async req =>
        {
            var name = req["name"]?.GetValue<string>() ?? throw new AdminError("name required");
            try
            {
                return Backup(await Saves.Backup(name));
            }
            catch (ArgumentException e)
            {
                throw new AdminError(e.Message);
            }
        });
        // {name} -> staged; applied at the next server start (restart the server to restore).
        bridge.Register("backup_restore", req =>
        {
            var name = req["name"]?.GetValue<string>() ?? throw new AdminError("name required");
            try
            {
                var b = Saves.RestoreOnNextStart(name);
                return new JsonObject { ["staged"] = Backup(b), ["note"] = "restart the server to restore" };
            }
            catch (ArgumentException e)
            {
                throw new AdminError(e.Message);
            }
        });
        bridge.Register("backup_cancel_restore", _ => new JsonObject { ["canceled"] = Saves.CancelRestore() });
    }

    private static JsonObject SaveState()
    {
        var s = Saves.Status;
        return new JsonObject
        {
            ["state"] = s?.State,
            ["world"] = s?.WorldId,
            ["revision"] = s?.Revision,
            ["lastSaved"] = s?.LastSaved?.ToString("O"),
            ["autosaveSeconds"] = s?.AutosaveInterval?.TotalSeconds,
            ["lastReason"] = s?.LastReason.ToString(),
            ["written"] = s?.Written,
            ["failed"] = s?.Failed,
            ["dataGeneration"] = s?.DataGeneration,
            ["dataMatch"] = s?.DataMatch.ToString(),
            ["pendingRestore"] = Saves.PendingRestore,
        };
    }

    private static JsonObject Backup(BackupInfo b) => new()
    {
        ["name"] = b.Name,
        ["folder"] = Path.GetFileName(b.Directory),
        ["created"] = b.Created.ToString("O"),
        ["world"] = b.WorldId,
        ["revision"] = b.Revision,
        ["bytes"] = b.Bytes,
        ["files"] = b.Files,
        ["dataBundles"] = b.DataBundles,
        ["reason"] = b.Reason.ToString(),
    };
}
