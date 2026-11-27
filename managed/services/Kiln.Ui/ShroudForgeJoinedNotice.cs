// SPDX-License-Identifier: MIT
// A ShroudForge mod tried to change the world's blocks in a game that joined a server: one Kiln toast per joined session.
//
// Why: a joined client's world is the server's (or the hosting player's) to change, in ShroudForge as under Kiln.
// ShroudForge answers such a mod with its own texts ("original registered voxel grid contract is unavailable" for the
// grid, "voxel write was rejected before changing the world" for a write), which say nothing about why, and the player
// sees nothing at all. netjunk, 2026-11-27: Altar Cleanup (Nexus 417) on a client joined to a dedicated server waited
// 30 s and gave up, with nothing on screen.
//
// kiln-shroudforge sees it (native/plugins/shroudforge/src/joined_notice.h: its writer's refusal, and the mod's own
// ShroudForge log line), writes one kiln.log line naming the mod, and puts {session, mod, at} under "joinedWorldEdit" in
// kiln/state/shroudforge.json. This watcher reads that file every 2 s (a few KB) on its own thread while ShroudForge
// runs hosted, and toasts once for each new joined session; in a start without a status of its own it stops after two
// minutes. ShroudForge's own texts reach the mod unchanged.
using System.Text.Json;

namespace Kiln;

internal static class ShroudForgeJoinedNotice
{
    /// <summary>How long the toast stays.</summary>
    internal const double Seconds = 12;

    /// <summary>What kiln-shroudforge says: the client world's session it happened in and the mod ("" when unknown).</summary>
    internal readonly record struct Edit(long Session, string Mod);

    /// <summary>The joined world edit in shroudforge.json (pure: tests); null for none, or for a status that is not a hosted
    /// ShroudForge's.</summary>
    internal static Edit? Read(JsonElement? doc)
    {
        if (doc is not { ValueKind: JsonValueKind.Object } d) return null;
        if (!d.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "hosted") return null;
        if (!d.TryGetProperty("joinedWorldEdit", out var e) || e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("session", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out var session) || session <= 0)
            return null;
        var mod = e.TryGetProperty("mod", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        return new Edit(session, mod);
    }

    /// <summary>A status that can no longer show a joined world edit: ShroudForge does not run hosted in this start.</summary>
    internal static bool Final(JsonElement? doc)
    {
        if (doc is not { ValueKind: JsonValueKind.Object } d) return false;
        string? S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return S("mode") is "self-hosted" or "off" or "not-installed" or "failed" || S("side") == "server" || S("runtime") == "stopped";
    }

    /// <summary>Whether the watcher is done after this look (pure: tests): the status can no longer show a joined world
    /// edit, or no status of this start came in its first two minutes (no file, or an earlier start's: kiln-shroudforge
    /// writes its own at Kiln's plugin step, before Kiln.Ui starts, so it does not run here). A read that failed (the file
    /// shared while it is replaced) is looked at again.</summary>
    internal static bool Done(ModCatalog.StateFile f, int polls) =>
        Final(f.Current ? f.Doc : null) || (polls > 60 && !f.Current && f.Error is null);

    /// <summary>The toast, in Kiln's language.</summary>
    internal static string Text() => KilnText.T("shroudforge.joined_world_edit");

    /// <summary>The sessions already toasted (one poller).</summary>
    internal sealed class Gate
    {
        internal long Last;
    }

    /// <summary>One look at the status file: toasts when it names a joined session not toasted yet.</summary>
    internal static void Check(Gate gate, JsonElement? doc, Action<string, ToastKind, double> toast)
    {
        if (Read(doc) is not { } e || e.Session == gate.Last) return;
        gate.Last = e.Session;
        toast(Text(), ToastKind.Warning, Seconds);
        UiEngine.Log(LogLevel.Info, "shown: a ShroudForge mod" + (e.Mod.Length > 0 ? $" ({e.Mod})" : "") +
                                    " tried to change blocks in a joined game (kiln.log's ShroudForge line says why)");
    }

    private static int _started;

    /// <summary>Starts the watcher once (a game client on Windows).</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || !OperatingSystem.IsWindows()) return;
        new Thread(Watch) { IsBackground = true, Name = "kiln shroudforge joined" }.Start();
    }

    private static void Watch()
    {
        try
        {
            var src = ModCatalog.Sources();
            var gate = new Gate();
            for (var i = 0; ; i++)
            {
                var f = ModCatalog.ReadState(src, "shroudforge.json");
                var doc = f.Current ? f.Doc : null;
                Check(gate, doc, UiEngine.AddToast);
                if (Done(f, i)) return;
                Thread.Sleep(2000);
            }
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Warn, "ShroudForge joined-game notice: " + e.Message + " (kiln.log still says it)");
        }
    }
}
