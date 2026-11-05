// SPDX-License-Identifier: MIT
// Kiln.Skills network part: channel "kiln:skills" v1 (not required: vanilla clients and clients without Kiln.Skills
// still join; they use /talents). A Kiln client asks for a tree's state and sends unlock / lock / respec requests; the
// server answers with the player's state and pushes it after every change. On a hosting client the screen calls the
// server part directly on the server world's tick thread.
namespace Kiln;

/// <summary>Client to server: <see cref="Op"/> 0 = state, 1 = unlock, 2 = lock, 3 = respec.</summary>
public sealed record TalentRequest(int Op, string Tree, string Node);

/// <summary>Server to client: a player's state in one tree.</summary>
public sealed record TalentStateMessage(string Tree, int Total, int Spent, int Available, Dictionary<string, int> Levels,
    bool Ok, string Message, long Revision, int CharacterLevel);

internal static class TalentNet
{
    internal const string ChannelName = "kiln:skills";
    private static NetChannel? _channel;

    /// <summary>The client's view of its trees (any thread reads; the main thread writes).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TalentStateMessage> Client = new();

    public static void Start()
    {
        if (_channel is not null || !Network.Available) return;
        try
        {
            _channel = Network.Channel(ChannelName, "1", required: false, maxMessageBytes: 16 * 1024)
                .RegisterJson<TalentRequest>(OnRequest, NetDirection.ToServer)
                .RegisterJson<TalentStateMessage>(OnState, NetDirection.ToClient)
                .RegisterJson<GameShadowMessage>((m, _) => GameShadow.OnMessage(m), NetDirection.ToClient);
            Network.Disconnected += _ =>
            {
                // a session's state doesn't outlive it: a stale table reads as unlocked on the next server's screen
                GameShadow.ForgetClientSide();
                Client.Clear();
            };
            if (Kiln.Client.Available && Kiln.Client.IsClient) Kiln.Client.Tick += static (in TickInfo _) => GameShadow.ClientTick();
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"channel kiln:skills: {ex.Message}");
        }
    }

    internal static bool ServerSide => !Kiln.Client.Available || Kiln.Client.IsDedicatedServer || Kiln.Client.IsHosting;

    // ---- server ----
    private static void OnRequest(TalentRequest m, NetContext ctx)
    {
        if (ctx.Player.IsNone) return;
        ctx.Reply(Handle(m, Skills.HolderOf(ctx.Player)));
    }

    internal static TalentStateMessage Handle(TalentRequest m, TalentHolder h)
    {
        TalentResult? r = null;
        string message = "";
        Skills.Entry e;
        try
        {
            e = Skills.Need(m.Tree);
        }
        catch (KeyNotFoundException)
        {
            return new TalentStateMessage(m.Tree, 0, 0, 0, new(), false, "the server does not have this talent tree", 0, 0);
        }
        try
        {
            r = m.Op switch
            {
                1 => Skills.Unlock(m.Tree, m.Node, h),
                2 => Skills.Lock(m.Tree, m.Node, h),
                3 => Skills.Respec(m.Tree, h),
                _ => null,
            };
            if (r is { Ok: false }) message = r.Reason;
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }
        return StateMessage(e, h, r?.Ok ?? true, message);
    }

    internal static TalentStateMessage StateMessage(Skills.Entry e, TalentHolder h, bool ok, string message)
    {
        var s = Skills.StateOf(e, h);
        var p = Skills.PointsOf(e, h, s);
        return new TalentStateMessage(e.Tree.Id, p.Total, p.Spent, p.Available, new Dictionary<string, int>(s.Levels), ok,
            message, s.Revision, s.LastLevel);
    }

    /// <summary>After a change made by code or a command: the player's client gets the new state.</summary>
    internal static void Push(Skills.Entry e, TalentHolder h)
    {
        if (h.Entity.IsNone) return;
        try
        {
            var msg = StateMessage(e, h, true, "");
            if (Kiln.Client.Available && Kiln.Client.IsHosting) Client[e.Tree.Id] = msg;  // the host's own screen
            _channel?.SendTo(h.Entity, msg);
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"push {e.Tree.Id}: {ex.Message}");
        }
    }

    /// <summary>False when it could not go (the peer isn't ready): the caller tries again.</summary>
    internal static bool SendShadow(EntityId player, GameShadowMessage m)
    {
        try
        {
            return _channel?.SendTo(player, m) == true;
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"shadow levels to {player}: {ex.Message}");
            return false;
        }
    }

    // ---- client ----
    private static void OnState(TalentStateMessage m, NetContext ctx)
    {
        Client[m.Tree] = m;
        if (!m.Ok && m.Message.Length > 0) Hud.Toast(m.Message, ToastKind.Warning, 3);
    }

    /// <summary>From the screen (any thread): asks the server. False when there is no way to (not connected, no Kiln).</summary>
    internal static bool Send(TalentRequest r)
    {
        if (Kiln.Client.Available && Kiln.Client.IsHosting)
        {
            Skills.Entry? e;
            lock (Skills.Gate) Skills.Registry.TryGetValue(r.Tree, out e);
            if (e is null) return false;
            // a mod's tree runs in its mod (its scheduler); a data-only tree (owner Kiln) in the skills service tick
            if (e.Owner?.Scheduler is { } sched) sched.Post(() => HandleHost(r));
            else HostQueue.Enqueue(r);
            if (r.Op != 0 && HostWorldStopped) Predict(e, r);
            return true;
        }
        return _channel is not null && _channel.SendToServer(r);
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<TalentRequest> HostQueue = new();
    private static long _hostTickAt;

    /// <summary>The pause menu of a game hosted offline stops the worlds: nothing runs on the tick thread until the player
    /// resumes (kiln.log, hosted sessions: no step for as long as Play/2 lasts). So a request made there can't apply yet: the
    /// character's data, the effects and the attributes all belong to the tick thread.</summary>
    private static bool HostWorldStopped => Environment.TickCount64 - Volatile.Read(ref _hostTickAt) > 300;

    /// <summary>What the player asked for, on the screen right away, marked as waiting. The request itself stays queued, and its
    /// answer replaces this at the first step after the pause. Only what the screen can tell will work: the server still
    /// decides (a refund another node needs comes back as a refusal).</summary>
    private static void Predict(Skills.Entry e, TalentRequest r)
    {
        if (!Client.TryGetValue(r.Tree, out var s)) return;
        var levels = new Dictionary<string, int>(s.Levels, StringComparer.Ordinal);
        var spent = s.Spent;
        switch (r.Op)
        {
            case 1 when e.Tree.Node(r.Node) is { } n:
            {
                var lv = levels.GetValueOrDefault(n.Id);
                var linked = n.Requires.Count == 0 || (n.RequireAll ? n.Requires.All(q => levels.GetValueOrDefault(q) > 0) : n.Requires.Any(q => levels.GetValueOrDefault(q) > 0));
                if (lv >= n.MaxLevel || s.Total - spent < n.Cost || !linked) return;
                levels[n.Id] = lv + 1;
                spent += n.Cost;
                break;
            }
            case 2 when e.Tree.Node(r.Node) is { } n:
            {
                var lv = levels.GetValueOrDefault(n.Id);
                if (lv <= 0) return;
                if (lv == 1) levels.Remove(n.Id);
                else levels[n.Id] = lv - 1;
                spent -= n.Cost;
                break;
            }
            case 3:
                levels.Clear();
                spent = 0;
                break;
            default:
                return;
        }
        Client[r.Tree] = s with { Levels = levels, Spent = spent, Available = s.Total - spent, Ok = true, Message = TalentUi.Frame.Paused.ToString() };
    }

    /// <summary>Hosting: keeps the screen's copy current, so a tree first opened from the pause menu has its state.</summary>
    internal static void RefreshHost(Skills.Entry e)
    {
        if (!Kiln.Client.Available || !Kiln.Client.IsHosting) return;
        var host = HostPlayer();
        if (!host.IsNone) Client[e.Tree.Id] = StateMessage(e, Skills.HolderOf(host), true, "");
    }

    /// <summary>From the skills service tick: the hosting player's requests for data-only trees.</summary>
    internal static void DrainHost()
    {
        Volatile.Write(ref _hostTickAt, Environment.TickCount64);
        while (HostQueue.TryDequeue(out var r)) HandleHost(r);
    }

    private static void HandleHost(TalentRequest r)
    {
        var host = HostPlayer();
        if (host.IsNone) return;
        var reply = Handle(r, Skills.HolderOf(host));
        Client[r.Tree] = reply;
        if (!reply.Ok && reply.Message.Length > 0) Hud.Toast(reply.Message, ToastKind.Warning, 3);
    }

    /// <summary>The hosting player in the server world: slot 0, else the only player.</summary>
    private static EntityId HostPlayer()
    {
        if (!Players.Available) return default;
        var all = Players.All.Where(p => p.InGame).ToArray();
        return all.FirstOrDefault(p => p.Slot == 0)?.Entity ?? (all.Length == 1 ? all[0].Entity : default);
    }
}

/// <summary>/talents for every player (vanilla clients too) and admins.</summary>
internal static class TalentCommands
{
    private static bool _done;

    public static void Start()
    {
        if (_done) return;
        _done = true;
        try
        {
            Commands.Register("talents", Run, usage: "[tree] [unlock|lock <node> | respec | grant <points> [player]]",
                description: "Kiln talent trees: your points and nodes; unlock or give back a node");
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"command /talents: {ex.Message}");
        }
    }

    private static void Run(CommandContext c)
    {
        var trees = Skills.Trees;
        if (trees.Count == 0)
        {
            c.Reply("no talent trees on this server");
            return;
        }
        var player = c.Source.Player;
        if (c.Count == 0)
        {
            foreach (var t in trees)
                c.Reply(c.Source.IsPlayer ? $"{t.Id} ({t.Name}): {Skills.Points(t.Id, Skills.HolderOf(player))}" : $"{t.Id} ({t.Name}): {t.Nodes.Count} nodes");
            return;
        }
        var tree = trees.FirstOrDefault(t => t.Id.Equals(c.Args[0], StringComparison.OrdinalIgnoreCase) ||
                                              t.Id.EndsWith(":" + c.Args[0], StringComparison.OrdinalIgnoreCase));
        if (tree is null)
        {
            c.Reply($"no tree \"{c.Args[0]}\" (/talents lists them)");
            return;
        }
        var op = c.Count > 1 ? c.Args[1].ToLowerInvariant() : "";
        if (op == "grant")
        {
            if (!c.Source.IsPlayer || Players.Find(player)?.IsAdmin == true || c.Source.Kind != CommandSourceKind.Player)
            {
                var n = int.TryParse(c.ArgOrDefault(2), out var v) ? v : 1;
                var target = c.Count > 3 ? Players.Find(c.Args[3])?.Entity ?? default : player;
                if (target.IsNone)
                {
                    c.Reply("no such player");
                    return;
                }
                c.Reply($"{tree.Id}: {Skills.Grant(tree.Id, target, n)}");
            }
            else c.Reply("only admins can grant talent points");
            return;
        }
        if (!c.Source.IsPlayer || player.IsNone)
        {
            c.Reply("only players have talents");
            return;
        }
        var h = Skills.HolderOf(player);
        TalentResult? r = op switch
        {
            "unlock" or "take" or "+" => Skills.Unlock(tree.Id, c.Arg(2), h),
            "lock" or "refund" or "-" => Skills.Lock(tree.Id, c.Arg(2), h),
            "respec" or "reset" => Skills.Respec(tree.Id, h),
            _ => null,
        };
        if (r is not null)
        {
            c.Reply($"{tree.Name}: {r}");
            return;
        }
        var s = Skills.State(tree.Id, h);
        c.Reply($"{tree.Name}: {Skills.Points(tree.Id, h)}");
        foreach (var n in tree.Nodes)
            c.Reply($"  {n.Id}: {n.Name} {s.Levels.GetValueOrDefault(n.Id)}/{n.MaxLevel}, cost {n.Cost}" +
                    (n.Requires.Count > 0 ? $", after {string.Join("/", n.Requires)}" : ""));
    }
}
