// SPDX-License-Identifier: MIT
// Lab bot players driven from lab scripts (tools/labbot/labbot.py): the control channel labbot:control v1 over kiln.net's
// lab transport. A lab peer sends Request (id 0, one JSON object with "op" and "seq"); the mod answers Reply (id 1, JSON
// with the same "seq", "ok", and "error" when not ok). Ops:
//   status                       LabBots.Status()
//   join {name, group?, index?}  LabBots.Join: group "admin" (default), "least" or "index" (with index): {id}
//   info {id} / list             one bot / every remembered bot
//   leave {id, drop?}            a logout, or a drop
//   say {id, text}               a chat line typed by the bot (kiln.chat's inject: chat subscribers and commands see it
//                                as the bot's own line); "told": the newest told line number before it
//   command {id, line}           Commands.Execute as the bot (no prefix): its result and the output of a handler that
//                                finished at once (an async handler's later replies: told), and "told" as for say
//   teleport {id, x, y, z}       Teleport.To the bot's entity; the reply comes when the teleport ended
//   position {id}                where the bot's entity is
//   character {id}               the bot's CharacterKey (what per-character data such as pets is keyed by)
//   players                      Players.All with positions
//   told {id, since?, all?}      the server's chat lines to that bot (a command's replies, Chat.Tell): every new newest
//                                inbox line is kept with its reader mask (Chat.Diagnostics.LastLine, read in Pre and Post),
//                                so two lines added in one step show only the second; "since" = a line number from an
//                                earlier answer
// Using items (since dev.32 labbot-2; docs/re/labbot.md "Using an item"):
//   give {id, item, count?}      Items.Give to the bot (the game's own inventory add): item by name or id; answers when
//                                the give ended: status, done (give and use act only while the bot is still in game in
//                                the same entity: BotPeers.Still; an error when it left while they waited)
//   inventory {id}               the bot's non-empty slots: slot, item, name, count, category
//   look {id, at?:[x,y,z] | yaw?, pitch?, eye?, distance?}
//                                the bot's client camera (LabBots.SetCamera): at its eyes (its position + eye metres,
//                                default 1.7) looking at `at` or along yaw/pitch degrees (yaw 0 = +Z, 90 = +X; a
//                                positive pitch looks down; "pitch" with "at" replaces the line of sight's)
//   ready {id, ready?}           the bot's client says it is ready for gameplay (LabBots.SetReady; default true)
//   use {id, item | slot, look...?, wait?}  uses the item the way the game does for a hotbar use (Abilities.UseItem: the
//                                item's own sequence with that slot as the used item): says ready, sets the camera when the
//                                look fields are given, waits while the game answers NotReady or Busy (at most `wait` s,
//                                default 30: a bot that just became ready plays the game's first-time spawn intro,
//                                Player_Spawn_Initial_FirstTime_PodWakeup, 17.7 s, StateFlag Spawning), then uses;
//                                answers result (ItemUseResult), slot, item, count before; a thrown item (an animal bait)
//                                starts at the camera and flies along it
//   buffs {id}                   the bot's buffs (PlayerStates.BuffsOf): id, slot, level, seconds left
//   spawn {template, x, y, z, yaw?}  Spawn.Entity (transient: never saved): {entity}; answers when it is there
//   near {x, y, z, radius, component?}  entities with that component (default PickupItem) within radius: entity,
//                                template, name, position, distance
//   component {entity, name}     whether the entity has the component, its size and bytes (hex, at most 1024)
//   destroy {entity}             Spawn.Destroy
// Lab only: LabBots answers KilnUnavailableException off a lab server; the mod then answers every op with that error.
// Requests count only from a lab peer (ControlPeers): one from a player's game connection is dropped unanswered. Every
// other request is answered, with the error when its op threw (Replies).
// A bot that kiln.net would refuse as a client without Kiln is let in (BotPeers; NetVanillaClientEvent).
using System.Numerics;
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
    private readonly HashSet<uint> _refusedPeers = [];

    // the server's chat lines as they appear (newest inbox line, polled in Pre and Post): number, reader mask, text
    private readonly List<(long N, uint Mask, uint Connection, string Text)> _lines = [];
    private long _lineNo;
    private ChatLine? _last;

    private void PollLines()
    {
        ChatLine? l;
        try { l = Chat.Diagnostics.LastLine(); }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException) { return; }
        if (l is not { } line || (_last is { } prev && prev.Text == line.Text && prev.Connection == line.Connection && prev.Account == line.Account))
            return;
        _last = line;
        _lines.Add((++_lineNo, line.ReaderMask, line.Connection, line.Text));
        if (_lines.Count > 400) _lines.RemoveRange(0, _lines.Count - 400);
    }

    public override void OnLoad()
    {
        _chan = Network.Channel("labbot:control", "1", required: false)
            .Register<Request>((m, ctx) => Handle(m.Json, ctx.Sender), NetDirection.ToServer)
            .Register<Reply>(null, NetDirection.ToClient);
        Context.Tick.Pre += (in TickInfo _) => PollLines();
        Context.Tick.Post += (in TickInfo _) => PollLines();
        Events.On<NetVanillaClientEvent>(LetBotsIn);
        string state;
        try
        {
            var s = LabBots.Status();
            state = s.Available ? $"available ({s.Groups} user groups)" : $"not available yet: {s.Why}";
        }
        catch (KilnUnavailableException e) { state = "off: " + e.Message; }
        Log.Info($"labbot-test: control channel labbot:control v1 open; LabBots {state}; capability {Capabilities.Get(LabBots.Capability)}");
    }

    // kiln.net judges a bot a client without Kiln (it sends no Kiln frame) and refuses it where the lab's mods state
    // clients "kiln" or "mod" (or requireKiln, or modSync refuse): a bot has no client to crash, so it goes in (BotPeers)
    private void LetBotsIn(NetVanillaClientEvent e)
    {
        if (!e.Kick) return;
        IReadOnlyList<LabBotInfo> bots;
        try { bots = LabBots.All(); }
        catch (KilnUnavailableException) { return; }
        if (!BotPeers.IsBot(e.Peer.Id, bots)) return;
        e.Kick = false;
        Log.Info($"labbot-test: bot '{e.Peer.Name}' (0x{e.Peer.Id:x}) let in: kiln.net would refuse it as a client without Kiln");
    }

    private void Handle(string json, NetPeer sender)
    {
        if (ControlPeers.Refusal(sender) is { } refused)
        {
            // never answered (a player learns nothing from it), logged once per connection
            if (_refusedPeers.Add(sender.Id)) Log.Warn($"labbot-test: request refused: {refused}");
            return;
        }
        JsonObject rq;
        try { rq = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException e)
        {
            Send(sender, new JsonObject { ["ok"] = false, ["error"] = "bad request: " + e.Message });
            return;
        }
        long seq = 0;
        var res = Replies.Run(() =>
        {
            seq = rq["seq"]?.GetValue<long>() ?? 0;
            var op = rq["op"]?.GetValue<string>() ?? "";
            return op switch
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
                "told" => Told(rq),
                "give" => GiveBot(rq, sender, seq),
                "inventory" => Inventory(rq),
                "look" => Look(rq),
                "ready" => Ready(rq),
                "use" => Use(rq, sender, seq),
                "buffs" => Buffs(rq),
                "spawn" => SpawnAt(rq, sender, seq),
                "near" => Near(rq),
                "component" => ComponentOf(rq),
                "destroy" => new JsonObject { ["destroyed"] = Spawn.Destroy(new EntityId(U32(rq, "entity"))) },
                _ => Error($"unknown op '{op}'"),
            };
        });
        if (res.ContainsKey("pending")) return;  // the op answers when it ends
        res["seq"] = seq;
        Send(sender, res);
    }

    private void Send(NetPeer peer, JsonObject o)
    {
        o.TryAdd("ok", true);
        _chan?.SendTo(peer, new Reply(o.ToJsonString()));
    }

    private static JsonObject Error(string why) => Replies.Error(why);

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

    // the newest captured line number before a bot's own line: every reply to it is numbered after (tools/labbot's
    // wait_told starts there, so a reply to an earlier command never answers for this one)
    private long ToldCursor()
    {
        PollLines();
        return _lineNo;
    }

    private JsonObject Say(JsonObject rq)
    {
        var b = InGame(rq);
        var text = rq["text"]?.GetValue<string>() ?? throw new ArgumentException("no text");
        var told = ToldCursor();
        Chat.Diagnostics.Inject(b.Connection, text);
        Log.Info($"labbot-test: bot {b.Id} '{b.Name}' says: {text}");
        return new JsonObject { ["id"] = b.Id, ["told"] = told };
    }

    private JsonObject Command(JsonObject rq)
    {
        var b = InGame(rq);
        var line = rq["line"]?.GetValue<string>() ?? throw new ArgumentException("no line");
        var told = ToldCursor();
        var r = Commands.Execute(CommandSource.ForPlayer(b.Entity, b.Name), line);
        Log.Info($"labbot-test: bot {b.Id} '{b.Name}' ran {line}: {r}");
        return new JsonObject
        {
            ["id"] = b.Id, ["found"] = r.Found, ["denied"] = r.Denied, ["done"] = r.Ok, ["error"] = r.Error,
            ["output"] = new JsonArray(r.Output.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()), ["told"] = told,
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
            var res = await Replies.RunAsync(async () =>
            {
                var r = await Teleport.To(b.Entity, to);
                var o = r.Ok ? new JsonObject { ["id"] = b.Id } : Error($"teleport refused: {r.Reason}");
                if (World.TryGetPosition(b.Entity, out var p)) o["position"] = Pos(p);
                return o;
            });
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

    // lines a bot's reader was to get: its reader is its machine index (a bit set in the mask = that reader is done or
    // not addressed; a line seen before the game sent it still has the bot's bit clear). A line told to one player carries
    // that player's connection (lab p4: "[Server] refused: ..." with the bot's connection and only bit 16 clear).
    private JsonObject Told(JsonObject rq)
    {
        var b = Bot(Id(rq)) ?? throw new ArgumentException($"no bot {Id(rq)}");
        var since = rq["since"]?.GetValue<long>() ?? 0;
        var all = rq["all"]?.GetValue<bool>() ?? false;  // every captured line, whoever it was for
        var reader = (int)(b.Machine & 0x7f);
        var lines = _lines.Where(x => x.N > since && x.Text.Length > 0 && (all || (x.Mask & (1u << reader)) == 0))
            .Select(x => (JsonNode)new JsonObject { ["n"] = x.N, ["text"] = x.Text, ["mask"] = x.Mask, ["connection"] = x.Connection }).ToArray();
        return new JsonObject { ["id"] = b.Id, ["lines"] = new JsonArray(lines), ["last"] = _lineNo };
    }

    // ---- using items (dev.32 labbot-2) ----
    private static uint U32(JsonObject rq, string k) => rq[k]?.GetValue<uint>() ?? throw new ArgumentException($"no {k}");
    private static double D(JsonObject rq, string k) => rq[k]?.GetValue<double>() ?? throw new ArgumentException($"no {k}");

    // an item named or numbered: "Food_T2_Dog_Bait", "0xb59dfc2c", "3047029804". kiln.items reads the game's item
    // registry into its catalog at the first ask after a world started, a moment later (lab u1: the first give right
    // after a bot's join found no name yet): asked again for up to 5 s
    private async Task<ItemInfo> ItemOf(JsonObject rq)
    {
        var text = rq["item"]?.ToString() ?? throw new ArgumentException("no item");
        for (var i = 0; i < 300; i++)
        {
            if (Items.Lookup(text) is { } info) return info;
            await Context.Scheduler.NextTick();
        }
        throw new ArgumentException($"no item '{text}' in kiln.items' catalog");
    }

    // answers when the async op ends (as teleport)
    private void Later(NetPeer sender, long seq, Func<Task<JsonObject>> run)
    {
        _ = Go();

        async Task Go()
        {
            var res = await Replies.RunAsync(run);
            res["seq"] = seq;
            Send(sender, res);
        }
    }

    // an op that waited acts only on the same bot in game (its entity is its player slot's: after a leave the next player
    // in that slot, another bot or a player's game, has it)
    private static void StillThere(LabBotInfo b, string op)
    {
        if (!BotPeers.Still(b, LabBots.Info(b.Id)))
            throw new InvalidOperationException($"bot {b.Id} '{b.Name}' is no longer in game: {op} not done");
    }

    private JsonObject GiveBot(JsonObject rq, NetPeer sender, long seq)
    {
        var b = InGame(rq);
        var count = rq["count"]?.GetValue<int>() ?? 1;
        Later(sender, seq, async () =>
        {
            var item = await ItemOf(rq);
            StillThere(b, "give");
            var r = await Items.Give(b.Entity, item.Id, count);
            Log.Info($"labbot-test: bot {b.Id} '{b.Name}' given {count} x {item.Name}: {r.Status} {r.Done} {r.Message}");
            var o = new JsonObject { ["id"] = b.Id, ["item"] = item.Name, ["status"] = r.Status.ToString(), ["done"] = r.Done, ["message"] = r.Message };
            if (!r.Succeeded) o["ok"] = false;
            if (!r.Succeeded) o["error"] = $"give {item.Name}: {r.Status} {r.Message}";
            return o;
        });
        return new JsonObject { ["pending"] = true };
    }

    private static JsonArray SlotsOf(EntityId e)
    {
        var inv = Items.Read(e) ?? throw new InvalidOperationException("no inventory");
        return new JsonArray(inv.Slots.Where(x => !x.IsEmpty).Select(x => (JsonNode)new JsonObject
        {
            ["slot"] = x.Slot.Value.ToString("x"), ["item"] = x.Item.Value, ["name"] = Items.Lookup(x.Item.Value.ToString())?.Name ?? "",
            ["count"] = x.Count, ["category"] = x.Category.ToString(),
        }).ToArray());
    }

    private static JsonObject Inventory(JsonObject rq)
    {
        var b = InGame(rq);
        return new JsonObject { ["id"] = b.Id, ["slots"] = SlotsOf(b.Entity) };
    }

    // the camera at the bot's eyes, looking at "at" or along yaw/pitch (degrees; the game's view is the rotation applied
    // to +Z: yaw about +Y, a positive pitch looks down, as PlayerAim reads it back)
    private static JsonObject? LookFrom(JsonObject rq, LabBotInfo b, bool required)
    {
        var hasAt = rq["at"] is JsonArray;
        var hasYaw = rq.ContainsKey("yaw");
        if (!hasAt && !hasYaw)
            return required ? throw new ArgumentException("look needs at:[x,y,z] or yaw (and pitch)") : null;
        if (!World.TryGetPosition(b.Entity, out var feet)) throw new InvalidOperationException("the bot has no position");
        var eye = feet with { Y = feet.Y + (rq["eye"]?.GetValue<double>() ?? 1.7) };
        double yaw, pitch;
        if (hasAt)
        {
            var at = (JsonArray)rq["at"]!;
            double dx = at[0]!.GetValue<double>() - eye.X, dy = at[1]!.GetValue<double>() - eye.Y, dz = at[2]!.GetValue<double>() - eye.Z;
            yaw = Math.Atan2(dx, dz);
            pitch = Math.Atan2(-dy, Math.Sqrt(dx * dx + dz * dz));
        }
        else
        {
            yaw = D(rq, "yaw") * Math.PI / 180;
            pitch = 0;
        }
        if (rq.ContainsKey("pitch")) pitch = D(rq, "pitch") * Math.PI / 180;
        var distance = (float)(rq["distance"]?.GetValue<double>() ?? 0);
        LabBots.SetCamera(b.Id, eye, Quaternion.CreateFromYawPitchRoll((float)yaw, (float)pitch, 0), distance);
        return new JsonObject
        {
            ["camera"] = Pos(eye), ["yaw"] = Math.Round(yaw * 180 / Math.PI, 2), ["pitch"] = Math.Round(pitch * 180 / Math.PI, 2),
            ["distance"] = distance,
        };
    }

    private JsonObject Look(JsonObject rq)
    {
        var b = InGame(rq);
        var o = LookFrom(rq, b, required: true)!;
        o["id"] = b.Id;
        Log.Info($"labbot-test: bot {b.Id} '{b.Name}' looks: {o.ToJsonString()}");
        return o;
    }

    private static JsonObject Ready(JsonObject rq)
    {
        var b = InGame(rq);
        var ready = rq["ready"]?.GetValue<bool>() ?? true;
        LabBots.SetReady(b.Id, ready);
        return new JsonObject { ["id"] = b.Id, ["ready"] = ready };
    }

    private JsonObject Use(JsonObject rq, NetPeer sender, long seq)
    {
        var b = InGame(rq);
        LabBots.SetReady(b.Id);
        var look = LookFrom(rq, b, required: false);
        Later(sender, seq, async () =>
        {
            var inv = Items.Read(b.Entity) ?? throw new InvalidOperationException("the bot has no inventory");
            InventorySlot slot;
            if (rq["slot"] is { } st)
            {
                var want = Convert.ToUInt64(st.ToString(), 16);
                slot = inv.Slots.FirstOrDefault(x => x.Slot.Value == want) ?? throw new ArgumentException($"no slot {st}");
            }
            else
            {
                var item = await ItemOf(rq);
                slot = inv.Slots.FirstOrDefault(x => x.Item == item.Id && x.Count > 0) ?? throw new ArgumentException($"the bot has no {item.Name}");
            }
            // the bot's client said it is ready: the game marks its actor ready in a step or two (actor_is_ready_for_gameplay)
            // and then plays a new character's first-time spawn intro (Player_Spawn_Initial_FirstTime_PodWakeup, 17.7 s,
            // StateFlag Spawning: Busy; lab u3), so the first use after ready waits for it
            var r = ItemUseResult.NotReady;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            var tries = 0;
            while (r is ItemUseResult.NotReady or ItemUseResult.Busy && waited.Elapsed.TotalSeconds < UseWaitSeconds(rq))
            {
                await Context.Scheduler.NextTick();
                StillThere(b, "use");
                r = Abilities.UseItem(b.Entity, slot.Slot);
                tries++;
            }
            var name = Items.Lookup(slot.Item.Value.ToString())?.Name ?? slot.Item.ToString();
            Log.Info($"labbot-test: bot {b.Id} '{b.Name}' uses {name} from slot {slot.Slot.Value:x} ({slot.Count}): {r}" +
                     (look is null ? "" : $", camera {look.ToJsonString()}"));
            var o = new JsonObject
            {
                ["id"] = b.Id, ["result"] = r.ToString(), ["slot"] = slot.Slot.Value.ToString("x"), ["item"] = slot.Item.Value, ["name"] = name,
                ["countBefore"] = slot.Count,
            };
            if (look is not null) o["look"] = look;
            o["tries"] = tries;
            o["waited"] = Math.Round(waited.Elapsed.TotalSeconds, 2);
            if (r != ItemUseResult.Started)
            {
                // what kept it: the bot's states as kiln.playerstate reads them
                var states = TryStates(b.Entity);
                o["states"] = states;
                o["ok"] = false;
                o["error"] = $"use {name}: {r} after {tries} tries in {waited.Elapsed.TotalSeconds:0.0} s (states: {states})";
                Log.Info($"labbot-test: bot {b.Id} '{b.Name}': {o["error"]}");
            }
            return o;
        });
        return new JsonObject { ["pending"] = true };
    }

    private static double UseWaitSeconds(JsonObject rq) => rq["wait"]?.GetValue<double>() ?? 30;

    private static string TryStates(EntityId e)
    {
        try
        {
            var snap = PlayerStates.Read(e);
            if (snap is null) return "unknown";
            var action = snap.Action is { } a ? $"; action {a}" : "";
            return string.Join(",", snap.Active) + action;
        }
        catch (Exception x) when (x is KilnUnavailableException or InvalidOperationException)
        {
            return "unreadable: " + x.Message;
        }
    }

    private static JsonObject Buffs(JsonObject rq)
    {
        var b = InGame(rq);
        return new JsonObject
        {
            ["id"] = b.Id,
            ["buffs"] = new JsonArray(PlayerStates.BuffsOf(b.Entity).Select(x => (JsonNode)new JsonObject
            {
                ["id"] = "0x" + x.Id.ToString("x8"), ["slot"] = x.Slot, ["level"] = x.Level, ["seconds"] = x.Remaining?.TotalSeconds,
            }).ToArray()),
        };
    }

    private JsonObject SpawnAt(JsonObject rq, NetPeer sender, long seq)
    {
        var template = rq["template"]?.GetValue<string>() ?? throw new ArgumentException("no template");
        var at = new WorldPosition(D(rq, "x"), D(rq, "y"), D(rq, "z"));
        var yaw = (float)(rq["yaw"]?.GetValue<double>() ?? 0);
        Later(sender, seq, async () =>
        {
            var e = await Spawn.Entity(template, at, yaw, new SpawnOptions { Flags = SpawnFlags.Transient });
            Log.Info($"labbot-test: spawned {template} at {at}: entity {e}");
            return new JsonObject { ["entity"] = e.Value, ["template"] = template };
        });
        return new JsonObject { ["pending"] = true };
    }

    private static JsonObject Near(JsonObject rq)
    {
        var at = new WorldPosition(D(rq, "x"), D(rq, "y"), D(rq, "z"));
        var radius = D(rq, "radius");
        var component = rq["component"]?.GetValue<string>() ?? "PickupItem";
        var list = new List<(double D, JsonObject O)>();
        foreach (var e in World.Find(component))
        {
            if (!World.TryGetPosition(e, out var p)) continue;
            var d = p.DistanceTo(at);
            if (d > radius) continue;
            var info = Spawn.Inspect(e);
            list.Add((d, new JsonObject
            {
                ["entity"] = e.Value, ["template"] = info?.Template?.ToString(), ["name"] = info?.TemplateName, ["position"] = Pos(p),
                ["distance"] = Math.Round(d, 2),
            }));
        }
        return new JsonObject { ["entities"] = new JsonArray(list.OrderBy(x => x.D).Select(x => (JsonNode)x.O).ToArray()) };
    }

    private static JsonObject ComponentOf(JsonObject rq)
    {
        var e = new EntityId(U32(rq, "entity"));
        var name = rq["name"]?.GetValue<string>() ?? throw new ArgumentException("no name");
        var bytes = World.ReadBytes(e, name);
        var o = new JsonObject { ["entity"] = e.Value, ["name"] = name, ["exists"] = World.Exists(e), ["has"] = bytes is not null };
        if (bytes is not null)
        {
            o["size"] = bytes.Length;
            o["hex"] = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 1024)));
        }
        return o;
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
