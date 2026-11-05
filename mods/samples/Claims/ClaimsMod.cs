// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiln;

namespace Claims;

/// <summary>
/// Protected areas (kiln.gameplay sample). Each claim becomes a <see cref="ProtectionZone"/>: the game's own building,
/// terraforming, interaction, loot and crafting paths refuse the action to every player who is not an owner, and the
/// player gets a chat line saying why. Claims live in <c>kiln/data/claims/claims.json</c>; admins manage them in chat:
/// <c>/claim here &lt;name&gt; [radius]</c>, <c>/claim trust &lt;name&gt; &lt;player&gt;</c>, <c>/claim remove &lt;name&gt;</c>,
/// <c>/claim list</c>, <c>/claim reload</c>.
/// </summary>
public sealed class ClaimsMod : Mod
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private Config _config = new();
    private readonly Dictionary<uint, ClaimConfig> _byZone = new();
    private readonly List<ZoneHandle> _zones = new();
    private readonly Dictionary<(uint Player, uint Zone), DateTime> _lastTold = new();
    private CommandRegistration? _command;

    private string ConfigPath => Path.Combine(Context.DataDirectory, "claims.json");

    public override void OnLoad()
    {
        if (!Gameplay.Available)
        {
            Log.Warn("claims: kiln.gameplay is unavailable on this build; no protection");
            return;
        }
        Gameplay.Enable();
        Load();
        if (Chat.Available)
            _command = Commands.Register("claim", OnCommand, CommandPermission.Everyone,
                "/claim list | here <name> [radius] | trust <name> <player> | remove <name> | reload",
                "Protected areas (Claims sample)");
    }

    public override void OnUnload() => _command?.Dispose();

    // ------------------------------------------------------------------------------------------- config ----

    private void Load()
    {
        foreach (var z in _zones) z.Dispose();
        _zones.Clear();
        _byZone.Clear();
        Directory.CreateDirectory(Context.DataDirectory);
        if (!File.Exists(ConfigPath))
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config(), Json));
            Log.Info($"claims: wrote an empty {ConfigPath} (see claims.example.json next to the mod)");
        }
        try
        {
            _config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Json) ?? new Config();
        }
        catch (JsonException e)
        {
            Log.Error($"claims: {ConfigPath} is not valid JSON ({e.Message}); no claims loaded");
            _config = new Config();
            return;
        }
        foreach (var c in _config.Claims) Apply(c);
        Log.Info($"claims: {_zones.Count} claim(s) protected: {string.Join(", ", _config.Claims.Select(c => c.Name))}");
    }

    private void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, Json));

    private void Apply(ClaimConfig c)
    {
        if (c.Center is not { Length: 3 })
        {
            Log.Warn($"claims: claim '{c.Name}' has no center [x, y, z]; skipped");
            return;
        }
        var center = new WorldPosition(c.Center[0], c.Center[1], c.Center[2]);
        var deny = GameplayActions.None;
        foreach (var d in c.Deny)
            deny |= d.ToLowerInvariant() switch
            {
                "build" => GameplayActions.Build,
                "break" => GameplayActions.Break,
                "terraform" or "dig" => GameplayActions.Terraform,
                "interact" or "containers" => GameplayActions.Interact,
                "loot" => GameplayActions.Loot,
                "craft" => GameplayActions.Craft,
                "all" => GameplayActions.All & ~GameplayActions.Spawn,
                _ => GameplayActions.None,
            };
        if (c.NoSpawns) deny |= GameplayActions.Spawn;
        if (deny == GameplayActions.None)
        {
            Log.Warn($"claims: claim '{c.Name}' denies nothing; skipped");
            return;
        }
        var owners = c.Owners.Select(s => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Where(v => v != 0).ToArray();
        var shape = c.Shape?.ToLowerInvariant() == "sphere" ? ZoneShape.Sphere : ZoneShape.Cylinder;
        try
        {
            var h = Gameplay.Protect(new ProtectionZone
            {
                Name = c.Name, Deny = deny, Shape = shape, Center = center, Radius = c.Radius,
                MinY = c.MinY ?? center.Y - 64, MaxY = c.MaxY ?? center.Y + 256, AllowedSteamIds = owners,
                AllowAdmins = c.AllowAdmins,
            });
            _zones.Add(h);
            _byZone[h.Id] = c;
        }
        catch (ArgumentException e)
        {
            Log.Warn($"claims: claim '{c.Name}' refused: {e.Message}");
        }
    }

    // ----------------------------------------------------------------------------------------- messages ----

    private void Tell(EntityId player, uint zone, string action)
    {
        if (!player.IsPlayer || !_byZone.TryGetValue(zone, out var claim) || !Chat.Available) return;
        var now = DateTime.UtcNow;
        if (_lastTold.TryGetValue((player.Value, zone), out var last) && (now - last).TotalSeconds < _config.CooldownSeconds) return;
        _lastTold[(player.Value, zone)] = now;
        var text = _config.Message.Replace("{name}", claim.Name).Replace("{owner}", claim.Owner ?? "someone else")
            .Replace("{action}", action);
        try { Chat.Tell(player, text); }
        catch (InvalidOperationException) { /* left the server */ }
        Log.Info($"claims: {player} refused '{action}' in '{claim.Name}'");
    }

    [Subscribe(EventPriority.Monitor, ReceiveCanceled = true)]
    private void OnBuild(BuildEvent e)
    {
        if (e.Outcome != BuildOutcome.DeniedByZone) return;
        Tell(e.Player, e.DeniedByZone, e.Kind switch
        {
            BuildKind.PlaceBlock or BuildKind.PlaceProp => "build",
            BuildKind.Terraform => "dig or reshape the ground",
            _ => "take buildings apart",
        });
    }

    [Subscribe(EventPriority.Monitor, ReceiveCanceled = true)]
    private void OnInteract(InteractEvent e)
    {
        if (e.Canceled && e.DeniedByZone != 0) Tell(e.Guest, e.DeniedByZone, "use that");
    }

    [Subscribe(EventPriority.Monitor, ReceiveCanceled = true)]
    private void OnLoot(LootEvent e)
    {
        if (e.Canceled && e.DeniedByZone != 0) Tell(e.Guest, e.DeniedByZone, "take that");
    }

    [Subscribe(EventPriority.Monitor, ReceiveCanceled = true)]
    private void OnCraft(CraftEvent e)
    {
        if (e.Canceled && e.DeniedByZone != 0) Tell(e.Operator, e.DeniedByZone, "craft at that station");
    }

    // ----------------------------------------------------------------------------------------- commands ----

    private void OnCommand(CommandContext ctx)
    {
        var sub = ctx.ArgOrDefault(0, "list")!.ToLowerInvariant();
        if (sub == "list")
        {
            if (_config.Claims.Count == 0) ctx.Reply("No claims.");
            foreach (var c in _config.Claims)
                ctx.Reply($"{c.Name}: {(c.Center is { Length: 3 } p ? $"({p[0]:F0}, {p[1]:F0}, {p[2]:F0})" : "?")} r {c.Radius:F0}, " +
                          $"{c.Owners.Count} owner(s), denies {string.Join("/", c.Deny)}{(c.NoSpawns ? ", no spawns" : "")}");
            return;
        }
        if (!ctx.Source.IsAdmin) throw new CommandException("only admins can change claims");
        switch (sub)
        {
            case "here":
            {
                if (!ctx.Source.IsPlayer) throw new CommandException("run it in game, where the claim should be");
                var name = ctx.Arg(1);
                var radius = ctx.Args.Count > 2 ? ctx.Double(2) : 32;
                var pos = Players.PositionOf(ctx.Source.Player) ?? throw new CommandException("your position is unknown");
                if (_config.Claims.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new CommandException($"a claim named '{name}' exists");
                var steam = ctx.Source.SteamId;
                var claim = new ClaimConfig
                {
                    Name = name, Owner = ctx.Source.Name, Center = [pos.X, pos.Y, pos.Z], Radius = Math.Clamp(radius, 2, 500),
                    Owners = steam != 0 ? [steam.ToString(CultureInfo.InvariantCulture)] : [],
                };
                _config.Claims.Add(claim);
                Save();
                Apply(claim);
                ctx.Reply($"Claim '{name}' created around {pos} (radius {claim.Radius:F0} m){(steam == 0 ? "; your Steam id is unknown, trust yourself with /claim trust" : "")}.");
                return;
            }
            case "trust":
            {
                var claim = Find(ctx.Arg(1));
                var who = ctx.Player(2);
                var steam = Players.Find(who.Entity)?.SteamId ?? 0;
                if (steam == 0) throw new CommandException($"{who.Name}'s Steam id is unknown");
                var s = steam.ToString(CultureInfo.InvariantCulture);
                if (!claim.Owners.Contains(s)) claim.Owners.Add(s);
                Save();
                Load();
                ctx.Reply($"{who.Name} may now use '{claim.Name}'.");
                return;
            }
            case "remove":
            {
                var claim = Find(ctx.Arg(1));
                _config.Claims.Remove(claim);
                Save();
                Load();
                ctx.Reply($"Claim '{claim.Name}' removed.");
                return;
            }
            case "reload":
                Load();
                ctx.Reply($"{_zones.Count} claim(s) loaded.");
                return;
            default:
                throw new CommandException("usage: /claim list | here <name> [radius] | trust <name> <player> | remove <name> | reload");
        }
    }

    private ClaimConfig Find(string name) =>
        _config.Claims.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new CommandException($"no claim named '{name}'");

    // ------------------------------------------------------------------------------------------ config ----

    private sealed class Config
    {
        public string Message { get; set; } = "{name} belongs to {owner}: you cannot {action} here.";
        public double CooldownSeconds { get; set; } = 3;
        public List<ClaimConfig> Claims { get; set; } = new();
    }

    private sealed class ClaimConfig
    {
        public string Name { get; set; } = "claim";
        /// <summary>Shown in the refusal message.</summary>
        public string? Owner { get; set; }
        /// <summary>"cylinder" (default: radius around x/z, minY..maxY) or "sphere".</summary>
        public string? Shape { get; set; }
        public double[]? Center { get; set; }
        public double Radius { get; set; } = 32;
        public double? MinY { get; set; }
        public double? MaxY { get; set; }
        /// <summary>SteamID64s of the players allowed inside (strings: JSON numbers lose precision).</summary>
        public List<string> Owners { get; set; } = new();
        public List<string> Deny { get; set; } = ["build", "break", "terraform", "interact", "loot"];
        /// <summary>Also stop natural enemy/wildlife spawns inside.</summary>
        public bool NoSpawns { get; set; }
        public bool AllowAdmins { get; set; } = true;
    }
}
