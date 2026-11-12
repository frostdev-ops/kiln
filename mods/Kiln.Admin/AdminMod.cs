// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Serves external admin tools (file bridge) and the Kiln Launcher (KAP/1 over TCP on 127.0.0.1) from
/// inside the server, with one op table. Ops are added per Kiln service as they become available on this build;
/// unavailable ones answer with a clear error instead of failing silently.</summary>
public sealed class AdminMod : Mod
{
    private const int PollEverySteps = 2;    // ~80 ms
    private const int StateEverySteps = 25;  // ~1 s
    private Bridge _bridge = null!;
    private TcpAdmin? _tcp;
    private ulong _step;

    public override void OnLoad()
    {
        var config = ReadConfig();
        _bridge = new Bridge(ResolveDirectory(config), Log);
        _bridge.EnsureDirectories();
        if (_bridge.ForeignState() is { } age)
        {
            Log.Warn($"another admin mod writes {Path.Combine(_bridge.Directory, "state.json")} too (last {age:0.0} s ago, not in Kiln.Admin's form). " +
                "Each request in that folder is answered by whichever of the two reads it first, and state.json changes between their two forms. " +
                "With Kiln's ShroudForge support this is usually the Emberwatch Lua mod (mods/emberwatch-admin), whose work Kiln.Admin does: " +
                "remove that mod, or give Kiln.Admin a folder of its own with \"bridge\" in kiln/data/kiln-admin/config.json. " +
                "The Kiln Launcher is not affected (it talks to Kiln.Admin over KAP).");
        }
        _bridge.Register("ping", _ => new JsonObject { ["pong"] = true, ["clock"] = _step / 24.5, ["kiln"] = Game.KilnVersion });
        _bridge.Register("status", _ => Status());
        Ops.DataDirectory = Context.DataDirectory;
        Ops.TickScheduler = Context.Scheduler;  // the writes after a guard request hop onto the tick thread (OpsTick.cs)
        Ops.AdminVersion = Context.Info.Version;
        Ops.LoadedAt = Environment.TickCount64;
        Ops.RegisterAll(_bridge, Log);
        Ops.RegisterAdmin(_bridge, Log);
        Ops.RegisterCreaturesSafe(_bridge, Log);
        Ops.RegisterGameDataSafe(_bridge, Log);
        Ops.RegisterBuildSafe(_bridge, Log);
        Ops.RegisterProgressionSafe(_bridge, Log);
        Ops.RegisterKnowledgeSafe(_bridge, Log);
        Ops.RegisterQuestsSafe(_bridge, Log);
        Ops.RegisterWorldStatusSafe(_bridge, Log);
        Ops.RegisterRulesSafe(_bridge, Log);
        StartTcp(config);
        Context.Tick.Pre += OnPre;
        Log.Info($"file bridge at {_bridge.Directory}; ops: {string.Join(", ", _bridge.OpNames)}");
    }

    public override void OnUnload()
    {
        Context.Tick.Pre -= OnPre;
        var reason = Lifecycle.Phase == ServerPhase.Stopping ? "shutdown" : "unload";
        _tcp?.Stop(reason);
        _tcp = null;
        Ops.Tcp = null;
        _bridge.Abandon();
    }

    private void OnPre(in TickInfo tick)
    {
        _step = tick.Step;
        _tcp?.Drain();
        if (tick.Step % PollEverySteps == 0) _bridge.Poll();
        if (tick.Step % StateEverySteps == 0)
        {
            var text = Bridge.Serialize(State());
            _bridge.WriteStateText(text);
            _tcp?.PushState(text);
        }
    }

    /// <summary>KAP/1: <c>"tcp": {"enabled": true, "port": 0}</c> (the default). Port 0: the server's query port number
    /// on TCP, then +1000 ... +9000, then any free port; endpoint.json says which one it got.</summary>
    private void StartTcp(JsonObject? config)
    {
        var tcp = config?["tcp"] as JsonObject;
        if (tcp?["enabled"] is JsonValue en && en.TryGetValue<bool>(out var enabled) && !enabled)
        {
            Log.Info("KAP/1 is off (config.json tcp.enabled = false)");
            return;
        }
        var port = tcp?["port"] is JsonValue pv && pv.TryGetValue<int>(out var p) && p is >= 0 and < 65536 ? p : 0;
        var candidates = new List<int>();
        if (port == 0 && QueryPort() is { } q)
            for (var i = 0; i < 10; i++) candidates.Add(q + i * 1000);
        try
        {
            var t = new TcpAdmin(_bridge, Log, Context.DataDirectory, Ops.ServerIdent, Game.KilnVersion, Context.Info.Version);
            if (!t.Start(port, candidates)) return;
            _tcp = t;
            Ops.Tcp = t;
            Log.Info($"KAP/1 on 127.0.0.1:{t.Port} (endpoint {t.EndpointFile}, token {Path.Combine(Context.DataDirectory, AdminToken.FileName)})");
        }
        catch (Exception e)
        {
            Log.Error(e, "KAP/1 listener");
        }
    }

    private int? QueryPort()
    {
        try
        {
            var cfg = Path.Combine(ServerDirectory(), "enshrouded_server.json");
            if (File.Exists(cfg) && JsonNode.Parse(File.ReadAllText(cfg))?["queryPort"] is JsonValue v && v.TryGetValue<int>(out var q) && q is > 0 and < 56536)
                return q;
        }
        catch (Exception)
        {
        }
        return null;
    }

    private JsonObject Status()
    {
        var features = new JsonObject();
        foreach (var f in new[] { "tick", "tick.wait_jobs", "log.mirror" }) features[f] = Game.HasFeature(f);
        return new JsonObject { ["kiln"] = Game.KilnVersion, ["features"] = features, ["ops"] = new JsonArray([.. _bridge.OpNames.Select(o => (JsonNode)o)]) };
    }

    private JsonObject State()
    {
        var st = new JsonObject { ["clock"] = _step / 24.5, ["kiln"] = Game.KilnVersion, ["ready"] = true };
        Ops.AddState(st);
        return st;
    }

    private JsonObject? ReadConfig()
    {
        var path = Path.Combine(Context.DataDirectory, "config.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (JsonException e)
        {
            Log.Warn($"{path}: {e.Message} (using defaults)");
            return null;
        }
    }

    private string ServerDirectory() => Path.GetFullPath(Path.Combine(Context.Info.Directory, "..", "..", ".."));

    /// <summary>Default: the directory external tools expect (&lt;server&gt;/shroudforge/exports/emberwatch);
    /// kiln/data/kiln-admin/config.json {"bridge": "&lt;dir&gt;"} overrides it.</summary>
    private string ResolveDirectory(JsonObject? config)
    {
        if (config?["bridge"] is JsonValue v && v.TryGetValue<string>(out var dir) && dir.Length > 0) return dir;
        return Path.Combine(ServerDirectory(), "shroudforge", "exports", "emberwatch");
    }
}
