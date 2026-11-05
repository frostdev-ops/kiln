// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Kiln quest engine ops (Kiln.Quests' <see cref="Quests"/>; docs/kiln/services/quests.md "Kiln.Admin"). The vanilla
/// journal has its own ops (quests_list, quest_state: Ops.Knowledge.cs); these are the quests mods define. Targets: "world",
/// a player (entity number or name), or "synthetic:&lt;name&gt;" (a test holder without an entity).</summary>
internal static partial class Ops
{
    internal static bool RegisterQuestsSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterQuests(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"quest ops off: Kiln.Quests is not available ({e.GetType().Name})");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterQuests(Bridge bridge, Logger log)
    {
        Quests.EnsureStarted();
        bridge.Register("modquests_status", _ =>
        {
            var s = Quests.Status;
            var (requests, written) = Quests.WaypointCounters;
            return new JsonObject
            {
                ["started"] = s.Started, ["serverSide"] = s.ServerSide, ["quests"] = s.Quests, ["dialogues"] = s.Dialogues, ["npcs"] = s.Npcs,
                ["conversations"] = s.Conversations, ["transitions"] = s.Transitions, ["actions"] = s.Actions, ["actionFailures"] = s.ActionFailures,
                ["waypoints"] = s.Waypoints, ["waypointRequests"] = requests, ["waypointsWritten"] = written,
                ["loadErrors"] = new JsonArray([.. s.LoadErrors.Select(e => (JsonNode)e)]),
                ["recentActions"] = new JsonArray([.. Quests.RecentActions.TakeLast(32).Select(e => (JsonNode)e)]),
            };
        });
        bridge.Register("modquests_list", _ => new JsonArray([.. Quests.Definitions.Select(q => (JsonNode)new JsonObject
        {
            ["id"] = q.FullId, ["mod"] = q.ModId, ["name"] = q.Name.For("en"), ["scope"] = q.Scope.ToString(), ["journal"] = q.Journal.ToString(),
            ["main"] = q.Main, ["auto"] = q.Auto, ["repeatable"] = q.Repeatable, ["giver"] = q.Giver,
            ["steps"] = new JsonArray([.. q.Steps.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id, ["text"] = s.Text.For("en"),
                ["objectives"] = new JsonArray([.. s.Objectives.Select(o => (JsonNode)new JsonObject { ["kind"] = o.Kind.ToString(), ["count"] = o.Count })]),
            })]),
        })]));
        // {target, quest?}: one quest (also when never started) or every record of the holder
        bridge.Register("modquest_state", req =>
        {
            var h = QuestTarget(req);
            if (req["quest"] is JsonValue v && v.TryGetValue<string>(out var id))
            {
                var q = Quests.Find(id) ?? throw new AdminError($"no quest \"{id}\"");
                var p = Quests.Progress(q.FullId, h);
                return new JsonObject
                {
                    ["target"] = h.Key, ["quest"] = q.FullId, ["status"] = Quests.StatusOf(q.FullId, h).ToString(), ["canStart"] = Quests.CanStart(q.FullId, h),
                    ["progress"] = p is null ? null : ProgressJson(p),
                };
            }
            return new JsonObject
            {
                ["target"] = h.Key,
                ["quests"] = new JsonArray([.. Quests.ProgressOf(h).Select(p => (JsonNode)ProgressJson(p))]),
            };
        });
        // {target, quest, status: start|complete|fail|reset|advance} or {target, quest, step}
        bridge.RegisterAsync("modquest_set", async req =>
        {
            var h = QuestTarget(req);
            var id = req["quest"] is JsonValue v && v.TryGetValue<string>(out var s0) ? s0 : throw new AdminError("quest is required");
            var q = Quests.Find(id) ?? throw new AdminError($"no quest \"{id}\"");
            QuestResult r;
            if (req["step"] is JsonValue sv && sv.TryGetValue<string>(out var step)) r = Quests.SetStep(q.FullId, h, step);
            else
            {
                var status = req["status"] is JsonValue st && st.TryGetValue<string>(out var s1) ? s1.ToLowerInvariant() : throw new AdminError("status or step is required");
                r = status switch
                {
                    "start" => Quests.Start(q.FullId, h, true),
                    "complete" or "done" => Quests.Complete(q.FullId, h),
                    "fail" or "failed" => Quests.Fail(q.FullId, h, "admin"),
                    "reset" or "none" => Quests.Reset(q.FullId, h),
                    "advance" => await Quests.Advance(q.FullId, h, null, true),
                    _ => throw new AdminError($"status \"{status}\": start, complete, fail, reset or advance"),
                };
            }
            return new JsonObject { ["ok"] = r.Ok, ["reason"] = r.Reason, ["status"] = r.Status.ToString(), ["quest"] = q.FullId, ["target"] = h.Key };
        });
        // {signal, target?, amount?}
        bridge.Register("modquest_signal", req =>
        {
            var name = req["signal"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new AdminError("signal is required");
            var amount = req["amount"] is JsonValue a && a.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= 1 and <= 1_000_000 ? (int)d : 1;
            Quests.Signal(name, req["target"] is null ? null : QuestTarget(req), amount);
            return new JsonObject { ["signal"] = name, ["amount"] = amount };
        });
        bridge.Register("modquests_reload", _ => new JsonObject { ["fromFiles"] = Quests.Reload(), ["total"] = Quests.Definitions.Count, ["loadErrors"] = Quests.Status.Errors });
        log.Info("quest engine ops on (modquests_status, modquests_list, modquest_state, modquest_set, modquest_signal, modquests_reload)");
        return true;
    }

    private static QuestHolder QuestTarget(JsonObject req)
    {
        var who = (req["target"] ?? req["entity"]) switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<double>(out var d) && d >= 1 && d <= 1023 && d == Math.Floor(d) => ((uint)d).ToString(),
            _ => throw new AdminError("target is required: \"world\", a player entity or name, or \"synthetic:<name>\""),
        };
        try
        {
            return QuestHolder.Parse(who);
        }
        catch (ArgumentException e)
        {
            throw new AdminError(e.Message);
        }
    }

    private static JsonObject ProgressJson(QuestProgress p)
    {
        var counters = new JsonObject();
        foreach (var (k, v) in p.Counters) counters[k] = v;
        return new JsonObject
        {
            ["quest"] = p.Quest.FullId, ["status"] = p.Status.ToString(), ["step"] = p.StepId, ["stepIndex"] = p.StepIndex, ["counters"] = counters, ["completions"] = p.Completions,
        };
    }
}
