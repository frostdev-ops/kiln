// SPDX-License-Identifier: MIT
// The sync test (docs/kiln/sync.md "Tests"). One mod, side both:
//  - on a dedicated server: content changes only the server has (code layers in Content.OnReady), a server config
//    and a common config with [Synced] entries, and the control channel sync-test:ctl for synctest.py's fake clients;
//  - on a client: checks what the server's sync did to this game's registries and configs (PASS/FAIL lines in
//    kiln.log, a HUD line), and that it all goes away on leave.
using System.Globalization;
using Kiln;

namespace SyncTest;

public enum Difficulty
{
    Easy,
    Normal,
    Hard,
}

[Comment("Server rules of the sync test. [Synced] entries reach Kiln clients while they are connected.")]
public sealed class ServerRules
{
    [Comment("How many homes a player may set."), Range(1, 50), Synced]
    public int MaxHomes { get; set; } = 3;

    [Comment("Damage multiplier."), Range(0, 10), Synced]
    public double DamageFactor { get; set; } = 1.0;

    [Synced]
    public Difficulty Mode { get; set; } = Difficulty.Normal;

    [Comment("Not synced: stays on the server.")]
    public string Motd { get; set; } = "welcome";

    [Comment("Synced, but a running client only takes it after the mod reloads."), Synced, RequiresRestart, Range(1, 65535)]
    public int Port { get; set; } = 8080;

    public RulesSection Rules { get; set; } = new();

    [Comment("Lab: give each Kiln player this many Material_T1_Wood_Logs (one stack) 15 s after joining (0 = off).\n" +
             "Not synced. For the client check of stacks above the vanilla max (BigStacks on the server only)."), Range(0, 9999)]
    public int GiveOnJoin { get; set; }
}

[Synced, Comment("A synced section: every entry inside is synced.")]
public sealed class RulesSection
{
    public bool Pvp { get; set; }

    [Range(1, 3600)]
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class CommonSettings
{
    [Comment("Synced from a server's common config."), Synced]
    public bool Feature { get; set; }

    [Comment("Not synced."), Range(0, 100)]
    public int LocalOnly { get; set; } = 5;
}

[Comment("The player's own settings (never synced). Edit them in the Kiln menu: Mods > sync-test > Config.")]
public sealed class ClientPrefs
{
    [Comment("Show the sync-test HUD line.")]
    public bool ShowHud { get; set; } = true;

    [Comment("Where the HUD line goes.")]
    public HudAnchor Anchor { get; set; } = HudAnchor.TopRight;

    [Comment("A name."), ConfigKey("nickname")]
    public string Nick { get; set; } = "KilnTest";

    [Comment("How often the HUD values are read."), Range(1, 600)]
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    [Range(0.25, 4)]
    public double Scale { get; set; } = 1.0;

    public List<int> Numbers { get; set; } = [1, 2, 3];
}

/// <summary>sync-test:ctl message 0 (to the server): a test command.</summary>
public sealed record Control(string Command) : INetMessage<Control>
{
    public void Encode(NetWriter w) => w.String(Command);
    public static Control Decode(ref NetReader r) => new(r.String(1024));
}

/// <summary>sync-test:ctl message 2 (to the server): bytes; answered with "blob &lt;length&gt; &lt;crc32&gt;" (fragment checks).</summary>
public sealed record Blob(byte[] Data) : INetMessage<Blob>
{
    public void Encode(NetWriter w) => w.Bytes(Data);
    public static Blob Decode(ref NetReader r) => new(r.Bytes(1 << 20));
}

/// <summary>sync-test:ctl message 1 (to the client): the answer.</summary>
public sealed record Reply(string Text) : INetMessage<Reply>
{
    public void Encode(NetWriter w) => w.String(Text);
    public static Reply Decode(ref NetReader r) => new(r.String(4096));
}

public sealed class SyncTestMod : Mod
{
    // The server-only content change (dedicated server): the client must get it through the sync.
    internal const string ServerPack = """
        { "items": { "Material_T1_Resin": { "maxStackSize": 777 } },
          "recipes": { "Tool_Torch01_T0_PK": { "inputs": [ { "item": "Material_T1_Twigs", "count": 3 },
                                                           { "item": "Material_T1_Wood_Logs", "count": 2 } ],
                                               "outputs": { "mul": 2 } },
                       "Tool_basic_FishingRod_T0_PK": { "craftingDuration": 2 },
                       "Consumable_Tool_Campfire_PK": { "inputs": [ { "item": "Material_T1_Wood_Logs", "count": 5 },
                                                                    { "item": "Material_T1_Twigs", "count": 3 } ] },
                       "Ammo_Arrow_T0_Grassland_Wooden_PKSU": { "outputs": { "mul": 2.5 } } } }
        """;

    private ModConfig<ServerRules>? _server;
    private ModConfig<CommonSettings>? _common;
    private ModConfig<ClientPrefs>? _prefs;
    private NetChannel? _ctl;
    private int _pass, _fail;
    private string _hud = "sync-test: waiting";
    private HudLine? _hudLine;
    private ulong _nextHud;
    // this game's own values, read before any sync (client)
    private int _woodOriginal, _resinOriginal;
    private string? _torchOriginal;
    private bool _synced;

    private void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")} ({_pass} pass, {_fail} fail so far)");
    }

    private static bool Dedicated => !Kiln.Client.IsClient;

    private static uint Fnv(byte[] data)
    {
        var h = 2166136261u;
        foreach (var b in data) h = (h ^ b) * 16777619u;
        return h == 0 ? 1 : h;
    }

    public override void OnLoad()
    {
        _server = Config.Register<ServerRules>(ConfigKind.Server);
        _common = Config.Register<CommonSettings>(ConfigKind.Common);
        _prefs = Config.Register<ClientPrefs>(ConfigKind.Client);
        _ctl = Network.Channel("sync-test:ctl", "1", required: false)
            .Register<Control>(OnControl, NetDirection.ToServer)
            .Register<Reply>((m, _) => Log.Info($"server says: {m.Text}"), NetDirection.ToClient)
            .Register<Blob>((m, ctx) => ctx.Reply(new Reply($"blob {m.Data.Length} {Fnv(m.Data)}")), NetDirection.ToServer);
        Log.Info($"sync-test: {(Dedicated ? "dedicated server" : $"client ({Kiln.Client.State})")}; server config maxHomes {_server.Value.MaxHomes}" +
                 $"{(_server.IsFromServer("maxHomes") ? " (from the server)" : "")}, common feature {_common.Value.Feature}");
        if (Content.Available)
            Content.OnReady(_ =>
            {
                if (Dedicated)
                {
                    var r = Content.Apply(ServerPack, "sync-test server pack");
                    Log.Info($"server content: {r.Applied} change(s), {r.Errors.Count} error(s){(r.Errors.Count > 0 ? ": " + string.Join("; ", r.Errors) : "")}");
                }
                else if (!_synced)
                {
                    ReadOriginals();
                }
            });
        Context.Tick.Pre += OnTick;
        Context.Tick.Pre += OnServerTick;
    }

    // ---- server: the lab give (a stack above the vanilla max for the client check) ----

    private readonly List<(NetPeer Peer, DateTime At)> _gives = new();
    private readonly HashSet<ulong> _given = new();

    [Subscribe]
    private void OnPeerReady(NetPeerReadyEvent e)
    {
        if (!Dedicated || _server!.Value.GiveOnJoin <= 0 || e.Peer.IsLab) return;
        _gives.Add((e.Peer, DateTime.UtcNow.AddSeconds(15)));
        Log.Info($"lab give: {_server.Value.GiveOnJoin} Material_T1_Wood_Logs to {e.Peer.Name} in 15 s");
    }

    private void OnServerTick(in TickInfo tick)
    {
        if (!Dedicated || _gives.Count == 0 || tick.Step % 25 != 0) return;
        var now = DateTime.UtcNow;
        foreach (var g in _gives.Where(g => now >= g.At).ToList())
        {
            _gives.Remove(g);
            if (g.Peer.State != NetPeerState.Ready || g.Peer.Player.IsNone) continue;
            if (!_given.Add(g.Peer.AccountId == 0 ? g.Peer.Id : g.Peer.AccountId)) continue;
            _ = Give(g.Peer, _server!.Value.GiveOnJoin);  // catches what it can throw
        }
    }

    private async Task Give(NetPeer peer, int count)
    {
        try
        {
            var r = await Items.Give(peer.Player, "Material_T1_Wood_Logs", count, options: GiveOptions.Notify);
            var wood = Content.Items.Get("Material_T1_Wood_Logs");
            Log.Info($"lab give to {peer.Name} (entity {peer.Player}): {r.Status} {r.Done}/{r.Requested} in {r.Changes.Count} slot(s) " +
                     $"[{string.Join(", ", r.Changes.Select(x => $"{x.Slot}+{x.Amount}"))}]; server wood max {wood?.MaxStack}");
        }
        catch (Exception e)
        {
            Log.Warn($"lab give to {peer.Name} failed: {e.Message}");
        }
    }

    private void ReadOriginals()
    {
        _woodOriginal = (int)(Content.Items.Get("Material_T1_Wood_Logs")?.Original("maxStackSize").Integer ?? 0);
        _resinOriginal = (int)(Content.Items.Get("Material_T1_Resin")?.Original("maxStackSize").Integer ?? 0);
        _torchOriginal ??= Inputs("Tool_Torch01_T0_PK");
        Log.Info($"client: this game's own values: wood max {_woodOriginal}, resin max {_resinOriginal}, torch {_torchOriginal}");
    }

    private static string Inputs(string recipe)
    {
        var r = Content.Recipes.Get(recipe);
        return r is null ? "?" : string.Join(" + ", r.Inputs.Select(s => s.ToString())) + " -> " + string.Join(" + ", r.Outputs.Select(s => s.ToString()));
    }

    // ---- client checks ----

    [Subscribe]
    private void OnContentSynced(ContentSyncedEvent e)
    {
        if (Dedicated) return;
        if (!Content.Ready)
        {
            // the client world (and its registries) went first: kiln.content put everything back when it stopped
            Log.Info($"client: content sync {(e.Restored ? "taken off" : "applied")} while this game's registries are not loaded " +
                     $"({e.Applied} field(s)); kiln.content restores them when the world stops");
            return;
        }
        var wood = Content.Items.Get("Material_T1_Wood_Logs")!;
        var resin = Content.Items.Get("Material_T1_Resin")!;
        var torch = Inputs("Tool_Torch01_T0_PK");
        var rod = Content.Recipes.Get("Tool_basic_FishingRod_T0_PK")!.CraftingDuration;
        if (!e.Restored)
        {
            _synced = true;
            Log.Info($"client: content from the server: {e.Applied} change(s), {e.Reset} reset, {e.Errors.Count} error(s); wood max {wood.MaxStack} " +
                     $"(own {wood.Original("maxStackSize")}), resin {resin.MaxStack}, torch {torch}, rod {rod.TotalSeconds}s");
            Check("client.content.applied", e.Errors.Count == 0 && e.Applied > 0, $"{e.Applied} change(s), errors: {string.Join("; ", e.Errors.Take(3))}");
            // 777 from sync-test's server pack, or BigStacks' raise when BigStacks re-applied after it: never this game's own
            Check("client.content.resin", resin.MaxStack > resin.Original("maxStackSize").Integer,
                $"Material_T1_Resin max {resin.MaxStack} (server pack 777 or BigStacks', own {resin.Original("maxStackSize")})");
            Check("client.content.torch", torch.Contains("Material_T1_Twigs x3") && torch.Contains("Material_T1_Wood_Logs x2"), torch);
            Check("client.content.rod-duration", Math.Abs(rod.TotalSeconds - 2) < 0.01, $"{rod.TotalSeconds}s");
            Log.Info($"client: wood max {wood.MaxStack} (BigStacks on the server gives 1000; without it {wood.Original("maxStackSize")})");
        }
        else
        {
            Check("client.content.restored.resin", resin.MaxStack == resin.Original("maxStackSize").Integer, $"resin {resin.MaxStack}");
            Check("client.content.restored.wood", wood.MaxStack == wood.Original("maxStackSize").Integer, $"wood {wood.MaxStack}");
            Check("client.content.restored.torch", _torchOriginal is null || torch == _torchOriginal, $"{torch} (own {_torchOriginal})");
            Check("client.content.restored.rod", Math.Abs(rod.TotalSeconds - Content.Recipes.Get("Tool_basic_FishingRod_T0_PK")!.Original("craftingDuration").Number) < 0.01, $"{rod.TotalSeconds}s");
        }
        Refresh(force: true);
    }

    [Subscribe]
    private void OnConfigSynced(ConfigSyncedEvent e)
    {
        if (Dedicated || e.ModId != Context.Info.Id) return;
        var s = _server!.Value;
        Log.Info($"client: config {e.Config.Id} {(e.Restored ? "back to local" : "from the server")}: changed {string.Join(", ", e.Changed)}" +
                 $"{(e.Errors.Count > 0 ? "; refused " + string.Join("; ", e.Errors) : "")}; maxHomes {s.MaxHomes}, mode {s.Mode}, pvp {s.Rules.Pvp}, " +
                 $"cooldown {s.Rules.Cooldown}, motd '{s.Motd}', port {s.Port}; server values: {string.Join(", ", e.Config.ServerValues)}");
        if (e.Config != _server) return;
        if (!e.Restored)
        {
            Check("client.config.from-server", _server.IsFromServer("maxHomes") && _server.IsFromServer("rules.cooldown"), string.Join(", ", _server.ServerValues));
            Check("client.config.not-synced-local", !_server.IsFromServer("motd"), $"motd '{s.Motd}'");
            Check("client.config.read-only", !_server.TrySet("maxHomes", 2, out var why) && why.Contains("server"), why);
            var file = _server.Entries.First(x => x.Path == "maxHomes");
            Check("client.config.file-keeps-local", file.FromServer && file.FileValue is int fv && fv == ReadFileValue(), $"running {file.Value}, file {file.FileValue}");
        }
        else
        {
            Check("client.config.restored", _server.ServerValues.Count == 0 && s.MaxHomes == ReadFileValue(), $"maxHomes {s.MaxHomes}");
        }
        Refresh(force: true);
    }

    private int ReadFileValue() => _server!.Entries.First(x => x.Path == "maxHomes").FileValue is int v ? v : -1;

    [Subscribe]
    private void OnConfigChanged(ConfigChangedEvent e)
    {
        if (e.ModId != Context.Info.Id) return;
        Log.Info($"config {e.Config.Id} changed ({e.Source}): {string.Join(", ", e.Changed)}{(e.PendingRestart.Count > 0 ? "; after restart: " + string.Join(", ", e.PendingRestart) : "")}");
    }

    // ---- HUD (client) ----

    private void OnTick(in TickInfo tick)
    {
        if (Dedicated || !Ui.Available) return;
        if (tick.Step < _nextHud) return;
        _nextHud = tick.Step + (ulong)Math.Max(30, _prefs!.Value.Interval.TotalSeconds * 60);
        Refresh(force: false);
    }

    private void Refresh(bool force)
    {
        if (Dedicated || !Ui.Available) return;
        var p = _prefs!.Value;
        if (!p.ShowHud)
        {
            _hudLine?.Dispose();
            _hudLine = null;
            return;
        }
        try
        {
            var wood = Content.Ready ? Content.Items.Get("Material_T1_Wood_Logs")?.MaxStack ?? 0 : 0;
            var resin = Content.Ready ? Content.Items.Get("Material_T1_Resin")?.MaxStack ?? 0 : 0;
            var st = ContentSync.Status;
            _hud = $"sync-test: wood max {wood} | resin max {resin} | homes {_server!.Value.MaxHomes}{(_server.IsFromServer("maxHomes") ? " (server)" : "")} | " +
                   $"content {st.State} ({st.Applied}) | {_pass} pass {_fail} fail";
        }
        catch (Exception e)
        {
            _hud = "sync-test: " + e.Message;
        }
        _hudLine ??= Hud.Show("sync-test.status", () => _hud, p.Anchor, Color.Orange);
        _hudLine.Anchor = p.Anchor;
    }

    // ---- server: control from synctest.py ----

    private void OnControl(Control c, NetContext ctx)
    {
        string reply;
        try
        {
            reply = Run(c.Command.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception e)
        {
            reply = "error: " + e.Message;
        }
        Log.Info($"control from {ctx.Sender.Name}: {c.Command} -> {reply}");
        ctx.Reply(new Reply(reply));
    }

    private string Run(string[] a)
    {
        switch (a.FirstOrDefault())
        {
            case "set-config" when a.Length >= 3:
                return _server!.TrySet(a[1], string.Join(' ', a.Skip(2)), out var e1) ? $"ok {a[1]} = {_server.Get(a[1])}" : "error: " + e1;
            case "set-common" when a.Length >= 3:
                return _common!.TrySet(a[1], string.Join(' ', a.Skip(2)), out var e2) ? $"ok {a[1]} = {_common.Get(a[1])}" : "error: " + e2;
            case "set-sync" when a.Length >= 3:
                var sync = Config.Find("sync") ?? throw new InvalidOperationException("no sync config");
                return sync.TrySet(a[1], a[2], out var e3) ? $"ok {a[1]} = {sync.Get(a[1])}" : "error: " + e3;
            case "set-item" when a.Length >= 4:
                var item = Content.Items.Get(a[1]) ?? throw new KeyNotFoundException(a[1]);
                item.Set(a[2], double.Parse(a[3], CultureInfo.InvariantCulture));
                return $"ok {item.Name}.{a[2]} = {item.Get(a[2])}";
            case "get-item" when a.Length >= 3:
                return $"ok {(Content.Items.Get(a[1]) ?? throw new KeyNotFoundException(a[1])).Get(a[2]).AsDouble.ToString(CultureInfo.InvariantCulture)}";
            case "set-recipe" when a.Length >= 4:
                var rec = Content.Recipes.Get(a[1]) ?? throw new KeyNotFoundException(a[1]);
                rec.Set(a[2], double.Parse(a[3], CultureInfo.InvariantCulture));
                return $"ok {rec.Name}.{a[2]} = {rec.Get(a[2])}";
            case "config-roundtrip":
                return ConfigSync.CheckRoundTrip(_server!);
            case "roundtrip":
                var rt = ContentSync.CheckRoundTrip();
                return (rt.Ok ? "ok " : "error: ") + rt;
            case "revert-content":
                return $"ok {Content.Revert()} field(s) put back";
            case "apply-content":
                var r = Content.Apply(ServerPack, "sync-test server pack");
                return $"ok {r.Applied} change(s)";
            case "status":
                return $"content {ContentSync.Serving?.ToString() ?? "none"}; config {ConfigSync.Serving?.ToString() ?? "none"}; " +
                       $"maxHomes {_server!.Value.MaxHomes}; changed {Content.Status()?.Changed}";
            default:
                return "error: commands: set-config <path> <value>, set-common <path> <value>, set-sync <key> <value>, " +
                       "set-item <name> <field> <value>, revert-content, apply-content, status";
        }
    }

    public override void OnUnload()
    {
        _hudLine?.Dispose();
        Log.Info($"sync-test unloading: {_pass} PASS, {_fail} FAIL");
    }
}
