// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal static partial class Ops
{
    static partial void RegisterChatTime(Bridge bridge, Logger log)
    {
        if (Time.Available)
        {
            bridge.Register("time_get", _ => TimeState());
            // {hours | clock:"HH:MM", mode?: "game"} -> new state
            bridge.Register("time_set", req =>
            {
                var mode = req["mode"]?.GetValue<string>() == "game" ? TimeSetMode.GameReset : TimeSetMode.Forward;
                if (req["clock"]?.GetValue<string>() is { } clock)
                {
                    if (!Time.TryParse(clock, out _)) throw new AdminError("clock must look like 19:45");
                    Time.Set(clock, mode);
                }
                else
                {
                    var h = Number(req, "hours");
                    if (h < 0 || h >= 24) throw new AdminError("hours must be 0 to <24");
                    Time.Set(h, mode);
                }
                return TimeState();
            });
            bridge.Register("weather_reset", _ =>
            {
                Time.ResetWeather();
                return new JsonObject { ["reset"] = true };
            });
        }
        if (Chat.Available)
        {
            bridge.Register("broadcast", req =>
            {
                var text = Text(req);
                return new JsonObject { ["delivered"] = Chat.Broadcast(text) };
            });
            bridge.Register("tell", req =>
            {
                var player = RequirePlayer(req);
                return new JsonObject { ["delivered"] = Chat.Tell(player.Entity, Text(req)) };
            });
            // Runs a registered chat command as the dashboard (admin): {line: "/time 12"} or {line: "time 12"}.
            bridge.Register("command", req =>
            {
                var line = req["line"]?.GetValue<string>()?.Trim() ?? throw new AdminError("line required");
                var r = Commands.Execute(CommandSource.Remote("dashboard", admin: true), line);
                if (!r.Found) throw new AdminError($"unknown command '{r.Name}'");
                if (!r.Ok) throw new AdminError(r.Error ?? "command failed");
                return new JsonObject { ["output"] = new JsonArray([.. r.Output.Select(o => (JsonNode)o)]) };
            });
            // The dashboard console: {line, steamId?} -> the whole result (output kept on errors too). With steamId the
            // line runs as that player (their permissions), else as the dashboard (admin).
            bridge.Register("command_run", req =>
            {
                var line = req["line"]?.GetValue<string>()?.Trim() ?? throw new AdminError("line required");
                if (line.Length == 0 || line.Length > 1000) throw new AdminError("line must be 1 to 1000 characters");
                var src = req["steamId"] is not null ? CommandSource.ForSteamId(SteamId(req)) : CommandSource.Remote("dashboard", admin: true);
                var r = Commands.Execute(src, line);
                log.Info($"dashboard command{(req["steamId"] is not null ? " as " + req["steamId"] : "")}: {line} -> {(r.Ok ? "ok" : r.Error)}");
                return new JsonObject
                {
                    ["name"] = r.Name, ["found"] = r.Found, ["ok"] = r.Ok, ["denied"] = r.Denied, ["canceled"] = r.Canceled,
                    ["parseError"] = r.ParseError, ["error"] = r.Error, ["output"] = new JsonArray([.. r.Output.Select(o => (JsonNode)o)]),
                };
            });
            bridge.Register("commands", _ =>
                new JsonArray([.. Commands.List().Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["usage"] = c.Usage, ["description"] = c.Description })]));
            // Player admin rights for chat commands: the player's user group may kick/ban.
            if (Players.Available)
                Commands.SetAdminCheck(src => src.Kind != CommandSourceKind.Player || Players.Find(src.Player)?.Can(PlayerPermissions.KickBan) == true);
        }
    }

    private static string Text(JsonObject req)
    {
        var text = req["text"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(text)) throw new AdminError("text required");
        if (text.Length > 500) throw new AdminError("text is longer than 500 characters");
        return text;
    }

    private static JsonObject TimeState()
    {
        var s = Time.State;
        return new JsonObject
        {
            ["hours"] = s.Hours, ["clock"] = s.Clock, ["day"] = s.IsDay,
            ["dayMinutes"] = s.DayLength.TotalMinutes, ["nightMinutes"] = s.NightLength.TotalMinutes,
            ["weather"] = s.WeatherAvailable,
        };
    }
}
