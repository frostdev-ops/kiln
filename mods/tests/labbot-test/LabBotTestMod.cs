// SPDX-License-Identifier: MIT
// Lab bot players driven from lab scripts (tools/labbot/labbot.py): the control channel labbot:control v1 over kiln.net's
// lab transport. A lab peer sends Request (id 0, one JSON object with "op" and "seq"); the mod answers Reply (id 1, JSON
// with the same "seq", "ok", and "error" when not ok). Ops:
//   status                       LabBots.Status()
//   join {name, group?, index?}  LabBots.Join: group "admin" (default), "least" or "index" (with index): {id}
//   info {id} / list             one bot / every remembered bot
//   leave {id, drop?}            a logout, or a drop
//   say {id, text}               a chat line typed by the bot (kiln.chat's inject: chat subscribers and commands see it
//                                as the bot's own line)
//   command {id, line}           Commands.Execute as the bot (no prefix): its result and the output of a handler that
//                                finished at once (an async handler's later replies are in kiln.log only)
//   teleport {id, x, y, z}       Teleport.To the bot's entity; the reply comes when the teleport ended
//   position {id}                where the bot's entity is
//   character {id}               the bot's CharacterKey (what per-character data such as pets is keyed by)
//   players                      Players.All with positions
// Lab only: LabBots answers KilnUnavailableException off a lab server; the mod then answers every op with that error.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace LabBotTest;

/// <summary>id 0: a request from a lab script.</summary>
public sealed record Request(string Json) : INetMessage<Request>
{
    public void Encode(NetWriter w) => w.String(Json);
    public static Request Decode(ref NetReader r) => new(r.String(65536));
}

/// <summary>id 1: the answer to a request (same "seq").</summary>
public sealed record Reply(string Json) : INetMessage<Reply>
{
    public void Encode(NetWriter w) => w.String(Json);
    public static Reply Decode(ref NetReader r) => new(r.String(65536));
}

public sealed class LabBotTestMod : Mod
{
    private NetChannel? _chan;

    public override void OnLoad()
    {
        _chan = Network.Channel("labbot:control", "1", required: false)
            .Register<Request>((m, ctx) => Handle(m.Json, ctx.Sender), NetDirection.ToServer)
            .Register<Reply>(null, NetDirection.ToClient);
        string state;
        try
        {
            var s = LabBots.Status();
            state = s.Available ? $"available ({s.Groups} user groups)" : $"not available yet: {s.Why}";
        }
        catch (KilnUnavailableException e) { state = "off: " + e.Message; }
        Log.Info($"labbot-test: control channel labbot:control v1 open; LabBots {state}; capability {Capabilities.Get(LabBots.Capability)}");
    }

    private void Handle(string json, NetPeer sender)
    {
        JsonObject rq;
        try { rq = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException e)
        {
            Send(sender, new JsonObject { ["ok"] = false, ["error"] = "bad request: " + e.Message });
            return;
        }
        var seq = rq["seq"]?.GetValue<long>() ?? 0;
        var op = rq["op"]?.GetValue<string>() ?? "";
        JsonObject res;
        try
        {
            res = op switch
            {
                "status" => Status(),
                "join" => Join(rq),
                "info" => Bot(Id(rq)) is { } b ? new JsonObject { ["bot"] = Describe(b) } : Error($"no bot {Id(rq)}"),
                "list" => new JsonObject { ["bots"] = new JsonArray(LabBots.All().Select(b => (JsonNode)Describe(b)).ToArray()) },
                "leave" => Leave(rq),
                "say" => Say(rq),
                "command" => Command(rq),
                "teleport" => TeleportBot(rq, sender, seq),
                "position" => Position(rq),
                "character" => Character(rq),
                "players" => PlayersNow(),
                _ => Error($"unknown op '{op}'"),
            };
        }
        catch (Exception e) when (e is KilnUnavailableException or ArgumentException or InvalidOperationException or JsonException
                                      or FormatException or NotSupportedException)
        {
            res = Error($"{e.GetType().Name}: {e.Message}");
        }
        if (res.ContainsKey("pending")) return;  // the op answers when it ends
        res["seq"] = seq;
        Send(sender, res);
    }

    private void Send(NetPeer peer, JsonObject o)
    {
        o.TryAdd("ok", true);
        _chan?.SendTo(peer, new Reply(o.ToJsonString()));
    }

    private static JsonObject Error(string why) => new() { ["ok"] = false, ["error"] = why };

    private static int Id(JsonObject rq) => rq["id"]?.GetValue<int>() ?? throw new ArgumentException("no id");

    private static LabBotInfo? Bot(int id) => LabBots.Info(id);

    private static LabBotInfo InGame(JsonObject rq)
    {
        var b = Bot(Id(rq)) ?? throw new ArgumentException($"no bot {Id(rq)}");
        if (!b.InGame) throw new InvalidOperationException($"bot {b.Id} '{b.Name}' is not in game ({b.Phase}: {b.Reason})");
        return b;
    }

    private static JsonObject Describe(LabBotInfo b)
    {
        var o = new JsonObject
        {
            ["id"] = b.Id, ["name"] = b.Name, ["phase"] = b.Phase.ToString(), ["slot"] = b.Slot, ["machine"] = b.Machine,
            ["connection"] = b.Connection, ["entity"] = b.Entity.Value, ["recordState"] = b.RecordState,
            ["permissions"] = b.Permissions, ["group"] = b.Group, ["savesAcked"] = b.SavesAcked, ["account"] = b.Account.ToString("x16"),
            ["character"] = b.CharacterId.ToString(), ["joinedStep"] = b.JoinedStep, ["inGameStep"] = b.InGameStep, ["reason"] = b.Reason,
        };
        if (b.InGame && World.TryGetPosition(b.Entity, out var p)) o["position"] = Pos(p);
        return o;
    }

    private static JsonArray Pos(WorldPosition p) => [p.X, p.Y, p.Z];

    private static JsonObject Status()
    {
        var s = LabBots.Status();
        return new JsonObject { ["available"] = s.Available, ["bots"] = s.Bots, ["groups"] = s.Groups, ["publishes"] = s.Publishes, ["why"] = s.Why };
    }

    private JsonObject Join(JsonObject rq)
    {
        var name = rq["name"]?.GetValue<string>() ?? throw new ArgumentException("no name");
        var group = (rq["group"]?.GetValue<string>() ?? "admin") switch
        {
            "admin" => LabBotGroup.Admin,
            "least" => LabBotGroup.Least,
            "index" => LabBotGroup.Index,
            var g => throw new ArgumentException($"group '{g}' is admin, least or index"),
        };
        var id = LabBots.Join(name, group, rq["index"]?.GetValue<int>() ?? 0);
        Log.Info($"labbot-test: bot {id} '{name}' joins ({group})");
        return new JsonObject { ["id"] = id };
    }

    private JsonObject Leave(JsonObject rq)
    {
        var id = Id(rq);
        LabBots.Leave(id, rq["drop"]?.GetValue<bool>() ?? false);
        return new JsonObject { ["id"] = id };
    }

    private JsonObject Say(JsonObject rq)
    {
        var b = InGame(rq);
        var text = rq["text"]?.GetValue<string>() ?? throw new ArgumentException("no text");
        Chat.Diagnostics.Inject(b.Connection, text);
        Log.Info($"labbot-test: bot {b.Id} '{b.Name}' says: {text}");
        return new JsonObject { ["id"] = b.Id };
    }

    private JsonObject Command(JsonObject rq)
    {
        var b = InGame(rq);
        var line = rq["line"]?.GetValue<string>() ?? throw new ArgumentException("no line");
        var r = Commands.Execute(CommandSource.ForPlayer(b.Entity, b.Name), line);
        Log.Info($"labbot-test: bot {b.Id} '{b.Name}' ran {line}: {r}");
        return new JsonObject
        {
            ["id"] = b.Id, ["found"] = r.Found, ["denied"] = r.Denied, ["done"] = r.Ok, ["error"] = r.Error,
            ["output"] = new JsonArray(r.Output.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        };
    }

    private JsonObject TeleportBot(JsonObject rq, NetPeer sender, long seq)
    {
        var b = InGame(rq);
        var to = new WorldPosition(rq["x"]!.GetValue<double>(), rq["y"]!.GetValue<double>(), rq["z"]!.GetValue<double>());
        _ = Finish();
        return new JsonObject { ["pending"] = true };

        async Task Finish()
        {
            JsonObject res;
            try
            {
                var r = await Teleport.To(b.Entity, to);
                res = r.Ok ? new JsonObject { ["id"] = b.Id } : Error($"teleport refused: {r.Reason}");
                if (World.TryGetPosition(b.Entity, out var p)) res["position"] = Pos(p);
            }
            catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or ArgumentException)
            {
                res = Error($"{e.GetType().Name}: {e.Message}");
            }
            res["seq"] = seq;
            Send(sender, res);
        }
    }

    private static JsonObject Position(JsonObject rq)
    {
        var b = InGame(rq);
        return World.TryGetPosition(b.Entity, out var p) ? new JsonObject { ["id"] = b.Id, ["position"] = Pos(p) } : Error("no position");
    }

    private static JsonObject Character(JsonObject rq)
    {
        var b = InGame(rq);
        var key = CharacterData.KeyOf(b.Entity);
        return new JsonObject { ["id"] = b.Id, ["key"] = key.ToString(), ["known"] = !key.IsNone && !key.IsAccountOnly };
    }

    private static JsonObject PlayersNow() =>
        new()
        {
            ["players"] = new JsonArray(Players.All.Select(p =>
            {
                var o = new JsonObject { ["name"] = p.Name, ["entity"] = p.Entity.Value, ["account"] = p.AccountId.ToString("x16"), ["admin"] = p.IsAdmin };
                if (p.Position is { } at) o["position"] = Pos(at);
                return (JsonNode)o;
            }).ToArray()),
        };
}
