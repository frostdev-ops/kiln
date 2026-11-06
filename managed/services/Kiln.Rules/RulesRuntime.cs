// SPDX-License-Identifier: MIT
// The server side of Kiln.Rules: active rule sets, each player's combined rules, and what applies them each step.
namespace Kiln;

internal static unsafe class RulesRuntime
{
    private const int ResolveEverySteps = 6;  // ~0.25 s: areas and expiry
    private const int VitalsEverySteps = 2;
    private const double LandingGraceSeconds = 60;   // a fall from high up takes a while: invulnerable until grounded

    private sealed class PlayerState(EntityId entity)
    {
        public readonly EntityId Entity = entity;
        public EffectiveRules Effective = EffectiveRules.None(entity);
        public RulesNativePart Native = RulesNativePart.Default;
        public RulesNativePart Client = RulesNativePart.Default;
        public bool ClientSent;
        public bool KilnInvulnerable;
        public List<FuelKeep> Water = new();
        // flight
        public uint Spark;
        public WorldPosition? SparkAt;
        public bool Carried;   // flying with FlightCarriesBody: the character goes to where the flight ends
        public Task? Teleporting;
        public DateTime? GraceUntil;
        public int GroundedChecks;
    }

    private static readonly object Gate = new();
    private static readonly List<RuleSet> SetsList = new();
    private static readonly Dictionary<EntityId, PlayerState> PlayersByEntity = new();
    private static readonly HashSet<EntityId> Characters = new();  // player characters with no client (lab tests)
    private static bool _dirty, _started;
    private static ulong _step;
    private static ModConfig<RulesPolicy>? _config;
    private static RulesLimits _limits = RulesLimits.Default;
    private static IReadOnlyList<PlayerBase> _bases = Array.Empty<PlayerBase>();
    private static ulong _basesStep;

    public static RulesPolicy Policy => _config?.Value ?? new RulesPolicy();

    // ---------------------------------------------------------------------------------------- start --------

    public static void Start()
    {
        if (_started) return;
        _started = true;
        // Kiln's own, whichever mod's call starts it: the config is <configDir>/rules.json and outlives every mod
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            StartAsKiln();
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    private static void StartAsKiln()
    {
        try
        {
            _config = Config.Register<RulesPolicy>(name: "rules");
            _limits = _config.Value.Limits;
            _config.Changed += _ =>
            {
                _limits = _config.Value.Limits;
                Dirty();
            };
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"rules.json unavailable ({e.Message}); the defaults apply");
        }
        RulesPresets.WriteDefaults();
        RulesNet.Start();
        ServiceTick.Add("kiln rules", Tick);
        Events.On<BuildEvent>(OnBuild, EventPriority.Monitor);
        Events.On<NetPeerReadyEvent>(e =>
        {
            lock (Gate)
                if (PlayersByEntity.TryGetValue(e.Peer.Player, out var s)) s.ClientSent = false;
        });
        Log(LogLevel.Info, $"Kiln.Rules up: {Describe(Rules.Status())}");
    }

    private static string Describe(RulesStatus? s) => s is null
        ? "no kiln.rules (the game's rules only; vitals and water still apply)"
        : $"hooks flight {(s.Flight ? "on" : "OFF")}, placement {(s.Placement ? "on" : "OFF")}, costs {(s.Costs ? "on" : "OFF")}, mining {(s.Mining ? "on" : "OFF")}" +
          (s.Detail.Length > 0 ? $" ({s.Detail})" : "");

    internal static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, "kiln.rules: " + text);

    // ---------------------------------------------------------------------------------------- sets ---------

    public static void Add(RuleSet set, ModContext? mod)
    {
        lock (Gate) SetsList.Add(set);
        if (mod != null)
        {
            Action retire = set.Dispose;
            mod.Retiring += retire;
            set.Detach = () => mod.Retiring -= retire;
        }
        Log(LogLevel.Info, $"applied {set}: {set.Rules.ToJson().ReplaceLineEndings(" ")}");
        Dirty();
        Resolve();  // at once: the caller can read Rules.Of right after
    }

    public static void Remove(RuleSet set)
    {
        lock (Gate) SetsList.Remove(set);
        Log(LogLevel.Info, $"ended {set}");
        Dirty();
        if (Game.IsTickThread) Resolve();
    }

    public static void Dirty() => _dirty = true;

    /// <summary>Treats a player character that is not a joined player (a lab's spawned <c>1_Player</c>) as a player.</summary>
    internal static void TrackCharacter(EntityId e, bool on)
    {
        lock (Gate)
        {
            if (on) Characters.Add(e);
            else Characters.Remove(e);
        }
        Start();
        Dirty();
        if (!on)
        {
            PlayerState? st;
            lock (Gate) PlayersByEntity.TryGetValue(e, out st);
            if (st != null) Leave(st);
        }
    }

    public static IReadOnlyList<RuleSet> Sets()
    {
        lock (Gate) return SetsList.ToArray();
    }

    public static EffectiveRules Effective(EntityId player)
    {
        lock (Gate)
            if (PlayersByEntity.TryGetValue(player, out var s)) return s.Effective;
        return Combine(player, Players.Available ? Players.Find(player)?.Position : null);
    }

    private static EffectiveRules Combine(EntityId player, WorldPosition? at)
    {
        List<(RuleSet?, PlayerRules)> matching;
        lock (Gate)
            matching = SetsList.Where(s => s.Active && s.Scope.Covers(player, at)).Select(s => ((RuleSet?)s, s.Rules)).ToList();
        return RulesMath.Combine(player, matching, _limits);
    }

    public static RulesClient ClientOf(EntityId player)
    {
        try
        {
            if (Client.IsHosting && Client.LocalPlayer == player) return RulesClient.Host;
        }
        catch (Exception)
        {
            // no kiln.client: a dedicated server
        }
        if (!Network.Available) return RulesClient.Unknown;
        var peer = Network.PeerOf(player);
        if (peer is null) return RulesClient.Unknown;
        return peer.State switch
        {
            NetPeerState.Ready => peer.HasChannel(Rules.ChannelName) ? RulesClient.Kiln : RulesClient.KilnWithoutRules,
            NetPeerState.Vanilla => RulesClient.Vanilla,
            NetPeerState.Connecting => RulesClient.Pending,
            _ => RulesClient.Unknown,
        };
    }

    // ---------------------------------------------------------------------------------------- the step -----

    private static void Tick(in TickInfo t)
    {
        _step = t.Step;
        var now = DateTime.UtcNow;
        List<RuleSet>? expired = null;
        lock (Gate)
            foreach (var s in SetsList)
                if (s.Expires is { } e && e <= now) (expired ??= new()).Add(s);
        if (expired != null)
            foreach (var s in expired) s.Dispose();
        bool anyArea, anySets;
        lock (Gate)
        {
            anySets = SetsList.Count > 0 || PlayersByEntity.Count > 0;
            anyArea = SetsList.Any(s => s.Scope.IsArea);
        }
        if (!anySets) return;
        if (_dirty || (anyArea && _step % ResolveEverySteps == 0) || _step % (ResolveEverySteps * 4) == 0) Resolve();
        List<PlayerState> live;
        lock (Gate) live = PlayersByEntity.Values.ToList();
        ModContext.CurrentMod = null;
        foreach (var s in live)
        {
            try
            {
                if (_step % VitalsEverySteps == 0) Vitals(s);
                Flight(s);
                SyncClient(s);
            }
            catch (Exception e)
            {
                Log(LogLevel.Warn, $"player {s.Entity}: {e.Message}");
            }
        }
    }

    // Works out every player's rules and applies what changed. Kiln.Rules holds what it sets up in other services
    // (invulnerability, refill policies) as Kiln, whichever mod's call led here.
    private static void Resolve()
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            ResolveAll();
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    private static void ResolveAll()
    {
        _dirty = false;
        var list = new List<(EntityId Entity, string Name, WorldPosition? At)>();
        if (Players.Available)
            foreach (var p in Players.All) list.Add((p.Entity, p.Name, p.Position));
        lock (Gate)
            foreach (var c in Characters)
                if (World.Position(c) is { } at) list.Add((c, $"character {c}", at));
        var present = new HashSet<EntityId>();
        foreach (var p in list)
        {
            present.Add(p.Entity);
            var eff = Combine(p.Entity, p.At);
            PlayerState s;
            lock (Gate)
            {
                if (!PlayersByEntity.TryGetValue(p.Entity, out s!))
                {
                    if (eff.IsDefault) continue;  // nothing to track
                    PlayersByEntity[p.Entity] = s = new PlayerState(p.Entity);
                }
            }
            if (s.Effective.SameAs(eff))
            {
                s.Effective = eff;
                continue;
            }
            var before = s.Effective;
            s.Effective = eff;
            Apply(s, before);
            if (Events.HasSubscribers<PlayerRulesChangedEvent>()) Events.Post(new PlayerRulesChangedEvent(before, eff));
            Log(LogLevel.Info, $"player {p.Name} ({p.Entity}): {eff}" + (eff.Denied.Count > 0 ? $" (rules.json denies {string.Join(", ", eff.Denied)})" : ""));
        }
        List<PlayerState> gone;
        lock (Gate) gone = PlayersByEntity.Values.Where(s => !present.Contains(s.Entity)).ToList();
        foreach (var s in gone) Leave(s);
    }

    private static void Apply(PlayerState s, EffectiveRules before)
    {
        var eff = s.Effective;
        // the native hooks
        var part = eff.Native;
        if (part != s.Native && RulesNative.T is var t && t != null)
        {
            var p = RulesNative.Of(part);
            t->Set(s.Entity.Value, part.IsDefault ? null : &p);
            s.Native = part;
        }
        // invulnerability (Kiln.Combat holds it for Kiln; a landing grace keeps it on)
        SetInvulnerable(s, eff.Invulnerable || s.GraceUntil is not null);
        // water
        if (!Equals(before.Water, eff.Water)) Water(s);
    }

    private static void Leave(PlayerState s)
    {
        lock (Gate) PlayersByEntity.Remove(s.Entity);
        List<RuleSet> theirs;
        lock (Gate) theirs = SetsList.Where(r => r.Scope.Player == s.Entity).ToList();
        foreach (var r in theirs) r.Dispose();
        if (RulesNative.T is var t && t != null) t->Set(s.Entity.Value, null);
        foreach (var k in s.Water) k.Dispose();
        s.Water.Clear();
        SetInvulnerable(s, false);
        Log(LogLevel.Info, $"player {s.Entity} left: their rules ended");
    }

    private static void SetInvulnerable(PlayerState s, bool on)
    {
        if (on == s.KilnInvulnerable || !Combat.Available) return;
        try
        {
            Combat.SetInvulnerable(s.Entity, on);
            s.KilnInvulnerable = on;
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"invulnerability of {s.Entity}: {e.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------- client -------

    private static void SyncClient(PlayerState s)
    {
        var part = s.Effective.Native;
        if (s.ClientSent && part == s.Client) return;
        bool host;
        try
        {
            host = Client.IsHosting && Client.LocalPlayer == s.Entity;
        }
        catch (Exception)
        {
            host = false;
        }
        if (host)
        {
            RulesNet.SetLocal(part);
        }
        else if (!RulesNet.Send(s.Entity, part))
        {
            return;  // no Kiln client with the channel (yet): try again next step
        }
        s.Client = part;
        s.ClientSent = true;
    }

    // ---------------------------------------------------------------------------------------- vitals -------

    private static void Vitals(PlayerState s)
    {
        var e = s.Effective;
        if (e.MinHealth <= 0 && e.MinStamina <= 0 && e.MinMana <= 0) return;
        if (!Abilities.Available) return;
        if (e.MinHealth > 0 && !(Combat.Available && Combat.GetHealth(s.Entity) is { Dead: true }))
            TopUp(s.Entity, AbilityResource.Health, e.MinHealth);
        if (e.MinStamina > 0) TopUp(s.Entity, AbilityResource.Stamina, e.MinStamina);
        if (e.MinMana > 0) TopUp(s.Entity, AbilityResource.Mana, e.MinMana);
    }

    private static void TopUp(EntityId entity, AbilityResource r, double floor)
    {
        if (Abilities.Attributes.Get(entity, r) is not { } cur || Abilities.Attributes.Max(entity, r) is not { } max || max <= 0) return;
        var want = (int)Math.Ceiling(max * Math.Clamp(floor, 0, 1));
        if (cur < want) Abilities.Attributes.Add(entity, r, want - cur);
    }

    // ---------------------------------------------------------------------------------------- water --------

    private static void Water(PlayerState s)
    {
        foreach (var k in s.Water) k.Dispose();
        s.Water.Clear();
        if (s.Effective.Water is not { } w || !Items.Fuel.Available) return;
        var policy = w.KeepsFull ? FuelRefill.Full : FuelRefill.ChargesPerSecond(w.ChargesPerSecond);
        var items = new List<ItemId>();
        foreach (var name in w.Items ?? Array.Empty<string>())
        {
            if (Items.Lookup(name) is { } info) items.Add(info.Id);
            else Log(LogLevel.Warn, $"water rule: no item '{name}'");
        }
        try
        {
            if (w.Items is not { Count: > 0 }) s.Water.Add(Items.Fuel.Refill(s.Entity, policy));
            else foreach (var id in items.Distinct()) s.Water.Add(Items.Fuel.Refill(s.Entity, policy, id));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, $"water rule for {s.Entity}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------- flight -------

    // The game's building fly mode puts the player's input into a spark entity (PlayerState.buildingSparkEntityId) and
    // leaves the character sitting where it was (docs/re/rules.md 1). With FlightCarriesBody the character is brought to
    // where the flight ends: on the ground below the spark's last position, invulnerable until it stands there. It is not
    // moved during the flight: a Teleport component on the player makes the game end its fly mode (the program reads it).
    private const int PlayerStateSpark = 0x50;

    private static uint SparkOf(EntityId player) =>
        World.ReadBytes(player, "PlayerState") is { Length: >= PlayerStateSpark + 4 } b ? BitConverter.ToUInt32(b, PlayerStateSpark) : 0;

    private static void Flight(PlayerState s)
    {
        var carry = s.Effective.FlyAnywhere && s.Effective.FlightCarriesBody;
        if (!carry && !s.Carried && s.GraceUntil is null) return;
        var spark = SparkOf(s.Entity);
        if (spark != 0)
        {
            s.Spark = spark;
            if (World.Position(new EntityId(spark)) is { } at) s.SparkAt = at;
            if (carry) s.Carried = true;
        }
        else if (s.Spark != 0)
        {
            // the flight ended (the player left it, a hit, or the rules no longer allow it outside a base)
            s.Spark = 0;
            if (s.Carried && s.SparkAt is { } last && World.Position(s.Entity) is { } body && Dist(body, last) > 2.5)
            {
                // the game's ground search looks near the target only: start it on the ground the voxels show below
                var target = GroundBelow(last) is { } g ? g with { Y = g.Y + 1.0 } : last;
                s.Teleporting = Teleport.To(s.Entity, target, new TeleportOptions { FindGround = true });
                s.GraceUntil = DateTime.UtcNow.AddSeconds(LandingGraceSeconds);
                s.GroundedChecks = 0;
                Log(LogLevel.Info, $"player {s.Entity}: the flight ended at {last}; brought there from {body}");
            }
            s.Carried = false;
            s.SparkAt = null;
        }
        if (s.GraceUntil is { })
        {
            var grounded = s.Teleporting is not { IsCompleted: false } && PlayerStates.Is(s.Entity, PlayerStateId.Grounded);
            s.GroundedChecks = grounded ? s.GroundedChecks + 1 : 0;
            if (s.GroundedChecks >= 12 || DateTime.UtcNow >= s.GraceUntil)
            {
                s.GraceUntil = null;
                SetInvulnerable(s, s.Effective.Invulnerable);
            }
            else
            {
                SetInvulnerable(s, true);
            }
        }
    }

    /// <summary>The top of the highest solid cell below <paramref name="p"/>, within 256 m (the voxels the server has
    /// loaded there; the flight's spark keeps them loaded), or null.</summary>
    internal static WorldPosition? GroundBelow(WorldPosition p, double depth = 256)
    {
        if (!Build.Available) return null;
        try
        {
            var box = Build.BoxAround(p with { Y = p.Y - depth / 2 }, 0.1, depth / 2, 0.1);
            if (!Build.TryRead(box, out var cells) || cells.Length == 0) return null;
            var layer = Math.Max(1, box.SizeX * box.SizeZ);
            var top = Build.CellOf(p).Y;
            for (var i = cells.Length - 1; i >= 0; i--)
            {
                var y = box.Y + i / layer;
                if (y > top || cells[i].IsAir) continue;
                return p with { Y = Build.CornerOf(new CellPos(box.X, y, box.Z)).Y + Build.Grid.CellSize };
            }
        }
        catch (Exception)
        {
            // not loaded: the game's ground search alone
        }
        return null;
    }

    private static double Dist(WorldPosition a, WorldPosition b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    // ---------------------------------------------------------------------------------------- builds -------

    // The game saves terrain and blocks only inside bases: what a player with KeepBuildsOutsideBases places outside
    // them joins Kiln.Build's kept regions.
    private static void OnBuild(BuildEvent e)
    {
        if (!e.IsPlacement || e.Outcome != BuildOutcome.Done) return;
        PlayerState? s;
        lock (Gate) PlayersByEntity.TryGetValue(e.Player, out s);
        if (s is null || !s.Effective.KeepBuildsOutsideBases || !Build.Available) return;
        if (_step - _basesStep > 250 || _basesStep == 0)
        {
            try
            {
                _bases = Build.Bases;
                _basesStep = _step;
            }
            catch (Exception)
            {
                _bases = Array.Empty<PlayerBase>();
            }
        }
        var p = e.Position;
        foreach (var b in _bases)
            if (Math.Abs(p.X - b.Center.X) <= b.Half.X && Math.Abs(p.Y - b.Center.Y) <= b.Half.Y && Math.Abs(p.Z - b.Center.Z) <= b.Half.Z)
                return;
        try
        {
            Build.Keep(Build.BoxAround(p, 4, 4, 4));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, $"keeping a build at {p}: {ex.Message}");
        }
    }
}
