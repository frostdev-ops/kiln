// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Kiln.Rules.Tests;

// PlayerRules.GlideSteer: the rule, how it combines, the server's limits, availability, the native part, the wire with
// older and newer peers, the client's confirmation and what the server applies and sends per connection, and the
// creative preset on disk.
public sealed unsafe class GlideSteerTests
{
    private static readonly EntityId P = new(3);

    private static EffectiveRules Combine(RulesLimits limits, params PlayerRules[] sets) =>
        RulesMath.Combine(P, sets.Select(r => ((RuleSet?)null, r)).ToList(), limits);

    private static EffectiveRules Combine(params PlayerRules[] sets) => Combine(RulesLimits.Default, sets);

    [Fact]
    public void TheRuleReadsAndWritesAsJson()
    {
        Assert.Equal(85, PlayerRules.FromJson("{ \"glideSteer\": {} }").GlideSteer!.MaxClimbAngle);
        var r = PlayerRules.FromJson("{ \"glideSteer\": { \"maxClimbAngle\": 40 } }");
        Assert.Equal(40, r.GlideSteer!.MaxClimbAngle);
        Assert.Equal(r, PlayerRules.FromJson(r.ToJson()));
        Assert.Throws<JsonException>(() => PlayerRules.FromJson("{ \"glideSteer\": { \"limit\": 40 } }"));
        Assert.Null(PlayerRules.FromJson("{}").GlideSteer);
        Assert.Empty(new PlayerRules { GlideSteer = new GlideSteerRule() }.Problems());
        foreach (var bad in new[] { -1, 86, double.NaN, double.PositiveInfinity })
            Assert.Contains(new PlayerRules { GlideSteer = new GlideSteerRule { MaxClimbAngle = bad } }.Problems(), p => p.StartsWith("glideSteer.maxClimbAngle"));
        Assert.Equal(30, new PlayerRules { GlideSteer = new() }.With(new PlayerRules { GlideSteer = new() { MaxClimbAngle = 30 } }).GlideSteer!.MaxClimbAngle);
        Assert.Equal(85, new PlayerRules { GlideSteer = new() }.With(new PlayerRules()).GlideSteer!.MaxClimbAngle);
    }

    [Fact]
    public void TheMostGenerousLimitWinsAndAModWithoutItChangesNothing()
    {
        Assert.Null(Combine(new PlayerRules { GlideBoost = GlideBoostRule.Default }).GlideSteer);
        Assert.Equal(0u, Combine(new PlayerRules { GlideBoost = GlideBoostRule.Default }).Native.Flags & RulesNative.GlideSteer);
        var e = Combine(new PlayerRules { GlideSteer = new() { MaxClimbAngle = 30 } }, new PlayerRules { GlideSteer = new() { MaxClimbAngle = 60 } },
            new PlayerRules { UnlimitedGliding = true });
        Assert.Equal(60, e.GlideSteer!.MaxClimbAngle);
        Assert.False(e.IsDefault);
        Assert.Contains("glider steering", e.ToString());
        Assert.False(Combine(new PlayerRules { GlideSteer = new() }).IsDefault);
    }

    [Fact]
    public void TheServersLimitsHoldIt()
    {
        var off = Combine(RulesLimits.Default with { AllowGlideSteer = false }, PlayerRules.Creative);
        Assert.Null(off.GlideSteer);
        Assert.Contains("glideSteer", off.Denied);
        Assert.NotNull(off.GlideBoost);  // the boost flies as before
        var low = Combine(RulesLimits.Default with { MaxGlideSteerAngle = 30 }, PlayerRules.Creative);
        Assert.Equal(30, low.GlideSteer!.MaxClimbAngle);
        Assert.Equal(0, Combine(RulesLimits.Default with { MaxGlideSteerAngle = double.NaN }, PlayerRules.Creative).GlideSteer!.MaxClimbAngle);
        var policy = new RulesPolicy();
        Assert.True(policy.AllowGlideSteer);
        Assert.Equal(85, policy.MaxGlideSteerAngle);
        Assert.Equal(RulesLimits.Default, policy.Limits);
        policy.AllowGlideSteer = false;
        policy.MaxGlideSteerAngle = 20;
        Assert.False(policy.Limits.AllowGlideSteer);
        Assert.Equal(20, policy.Limits.MaxGlideSteerAngle);
    }

    [Fact]
    public void ABuildWithoutTheHookDeniesIt()
    {
        var e = Combine(PlayerRules.Creative);
        var without = RulesCrafting.Supported(e, RulesNative.HookGlide);
        Assert.Null(without.GlideSteer);
        Assert.Contains("glideSteerUnavailable", without.Denied);
        Assert.Equal(e.Native.WithoutSteer, without.Native);  // today's creative glide, nothing else changed
        var with = RulesCrafting.Supported(e, RulesNative.HookGlide | RulesNative.HookGlideSteer);
        Assert.Equal(85, with.GlideSteer!.MaxClimbAngle);
        Assert.DoesNotContain("glideSteerUnavailable", with.Denied);
    }

    [Fact]
    public void TheNativePartCarriesTheFlagAndTheLimitAndIsAPredictedGlideRule()
    {
        var part = Combine(new PlayerRules { GlideSteer = new() { MaxClimbAngle = 70 } }).Native;
        Assert.Equal(RulesNative.GlideSteer, part.Flags);
        Assert.Equal(0x2000u, RulesNative.GlideSteer);
        Assert.Equal(0x2000u, RulesNative.HookGlideSteer);
        Assert.Equal(70f, part.GlideSteerLimit);
        Assert.True(part.HasGlide);
        Assert.True(part.HasPredicted);  // a game without Kiln's rules channel never gets it
        Assert.Equal(RulesNativePart.Default, part.WithoutGlide);
        Assert.Equal(RulesNativePart.Default, part.WithoutPredicted);
        Assert.Equal(RulesNativePart.Default, part.WithoutSteer);
        Assert.Contains("steer limit 70", part.ToString());
        var native = RulesNative.Of(part);
        Assert.Equal(70f, native.GlideSteerLimit);
        Assert.Equal(56, Marshal.SizeOf<RulesNative.Player>());
        Assert.Equal(256, Marshal.SizeOf<RulesNative.Status>());
        Assert.Equal(52, (int)Marshal.OffsetOf<RulesNative.Player>(nameof(RulesNative.Player.GlideSteerLimit)));
        Assert.Equal(248, (int)Marshal.OffsetOf<RulesNative.Status>(nameof(RulesNative.Status.GlideSteered)));
    }

    // ---------------------------------------------------------------------------------------- the wire -----

    // dev.15 to dev.23's RulesMsg codec (channel v4), as released: 11 fields.
    private static void OldWrite(NetWriter w, RulesNativePart m) => w.UInt32(m.Flags).Single(m.BuildCost).Single(m.BuildRefund)
        .Single(m.DigDamage).Single(m.ShroudFloor).Single(m.GlideSink).Single(m.GlideThrust).Single(m.GlideMaxSpeed).Single(m.GlideLift)
        .Single(m.GlideCoast).Single(m.GlideClimb);

    private static RulesNativePart OldRead(ReadOnlySpan<byte> body)
    {
        var r = new NetReader(body);
        return new RulesNativePart(r.UInt32(), r.Single(), r.Single(), r.Single(), r.Single(), r.Single(), r.Single(), r.Single(),
            r.Single(), r.Single(), r.Single());
    }

    private static byte[] Body(Action<NetWriter> write)
    {
        var w = new NetWriter();
        write(w);
        return w.ToArray();
    }

    private static readonly RulesNativePart Creative = new(RulesNative.GlideBoost | RulesNative.GlideUnlimited | RulesNative.GlideSteer, 0, 0,
        1000, 0, 0, 15, 35, 0, 8, 15, 85);

    [Fact]
    public void AnOlderClientDecodesTheNewRulesMessage()
    {
        var body = Body(w => RulesNet.WriteRules(RulesNet.MessageOf(Creative), w));
        Assert.Equal(48, body.Length);
        var old = OldRead(body);  // the trailing limit is left unread, as NetChannel's decoder does with any body
        Assert.Equal(Creative with { GlideSteerLimit = 0 }, old);
    }

    [Fact]
    public void ANewClientDecodesAnOlderServersRulesMessage()
    {
        var oldBody = Body(w => OldWrite(w, Creative with { Flags = Creative.Flags & ~RulesNative.GlideSteer, GlideSteerLimit = 0 }));
        Assert.Equal(44, oldBody.Length);
        foreach (var body in new[] { oldBody, oldBody.Concat(new byte[3]).ToArray() })  // kiln.net may pad a frame by up to 3 zero bytes
        {
            var r = new NetReader(body);
            var m = RulesNet.ReadRules(ref r);
            Assert.Equal(0f, m.GlideSteerLimit);
            Assert.Equal(0u, m.Flags & RulesNative.GlideSteer);
            Assert.Equal(15f, m.GlideClimb);
        }
        var newBody = Body(w => RulesNet.WriteRules(RulesNet.MessageOf(Creative), w));
        var nr = new NetReader(newBody);
        var got = RulesNet.ReadRules(ref nr);
        Assert.Equal(85f, got.GlideSteerLimit);
        Assert.Equal(Creative, RulesNet.PartOf(got));
    }

    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private static NetPeer Peer(uint id)
    {
        var p = (NetPeer)Activator.CreateInstance(typeof(NetPeer), InstancePrivate, null, [id], null)!;
        typeof(NetPeer).GetProperty(nameof(NetPeer.State))!.SetValue(p, NetPeerState.Ready);
        return p;
    }

    [Fact]
    public void TheClientsHookMessageCarriesItsHookBits()
    {
        // message 2 on kiln:rules: a new id, which dev.23's server (ids 0 and 1 only) drops unread
        Assert.Equal((ushort)2, RulesNet.MsgClientHooks);
        var body = Body(w => RulesNet.WriteClientHooks(new RulesNet.ClientHooks(RulesNative.HookGlideSteer), w));
        Assert.Equal(4, body.Length);
        var r = new NetReader(body);
        Assert.Equal(RulesNative.HookGlideSteer, RulesNet.ReadClientHooks(ref r).Hooks);
    }

    [Fact]
    public void SteeringNeedsTheClientsConfirmationOnALiveConnection()
    {
        var a = Peer(200);
        var reused = Peer(200);
        Assert.False(RulesNet.SteerReady(a));  // an older Kiln never sends it: GlideBoost flies as before
        RulesNet.ClientHooksOf(a, RulesNative.HookGlideSteer);
        try
        {
            Assert.True(RulesNet.SteerReady(a));
            Assert.False(RulesNet.SteerReady(reused));
            Assert.False(RulesNet.FlightReady(a));  // flight keeps its own confirmation
            RulesNet.ClientHooksOf(a, 0);           // a client build without the hook
            Assert.False(RulesNet.SteerReady(a));
            RulesNet.ClientHooksOf(a, RulesNative.HookGlideSteer);
            typeof(NetPeer).GetProperty(nameof(NetPeer.State))!.SetValue(a, NetPeerState.Closed);
            Assert.False(RulesNet.SteerReady(a));
            Assert.False(RulesNet.SteerReady(null));
        }
        finally { RulesNet.ClientHooksOf(a, 0); }
        // what such a client gets: the creative glide exactly as before steering existed
        var part = Combine(PlayerRules.Creative).Native.WithoutSteer;
        Assert.Equal(Combine(PlayerRules.Creative with { GlideSteer = null }).Native, part);
        Assert.Equal(0x1dfu, part.Flags);
    }

    // ---------------------------------------------------------------------------------------- the strip ------

    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type NetEngineType = typeof(Network).Assembly.GetType("Kiln.NetEngine", throwOnError: true)!;
    private static readonly Type NetNativeType = typeof(Network).Assembly.GetType("Kiln.NetNative", throwOnError: true)!;
    private static readonly Type PlayerStateType = typeof(RulesRuntime).GetNestedType("PlayerState", BindingFlags.NonPublic)!;
    private static readonly string ChannelName = (string)typeof(RulesNet).Assembly.GetType("Kiln.Rules")!
        .GetField("ChannelName")!.GetRawConstantValue()!;

    private static int _frames, _sets;
    private static uint _setFlags;
    private static float _setLimit;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NetOk() => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NetSend(uint peer, byte* frame, uint length)
    {
        _frames++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RulesSet(uint entity, RulesNative.Player* p)
    {
        _sets++;
        _setFlags = p == null ? 0 : p->Flags;
        _setLimit = p == null ? 0 : p->GlideSteerLimit;
        return 0;
    }

    // A dedicated server and one player, without a game: kiln.net's table is a stand-in (every function answers Ok, a
    // send counts a frame), the server's peers are the one connected here, kiln:rules is open with its rules message,
    // and kiln.rules' table records what the server world's hooks are given. RulesRuntime's own PushNative and
    // SyncClient run on the player's state, as its tick runs them.
    private sealed class DedicatedServer : IDisposable
    {
        private readonly FieldInfo _netTable = NetNativeType.GetField("_table", StaticPrivate)!;
        private readonly FieldInfo _snapshot = NetEngineType.GetField("_serverSnapshot", StaticPrivate)!;
        private readonly FieldInfo _rulesTable = typeof(RulesNative).GetField("_t", StaticPrivate)!;
        private readonly FieldInfo _rulesChannel = typeof(RulesNet).GetField("_channel", StaticPrivate)!;
        private readonly object? _netBefore, _snapshotBefore, _rulesBefore, _channelBefore;
        private readonly nint* _net;
        private readonly RulesNative.Table* _rules;
        private readonly NetChannel _channel;
        private readonly EntityId _player;
        private readonly object _state;
        private readonly List<NetPeer> _peers = new();

        public DedicatedServer(EntityId player, EffectiveRules rules)
        {
            _player = player;
            var table = NetNativeType.GetNestedType("Table", BindingFlags.NonPublic)!;
            var size = Marshal.SizeOf(table);
            _net = (nint*)NativeMemory.AllocZeroed((nuint)size);
            for (var i = 0; i < size / sizeof(nint); i++) _net[i] = (nint)(delegate* unmanaged[Cdecl]<int>)&NetOk;
            _net[Marshal.OffsetOf(table, "Send") / sizeof(nint)] = (nint)(delegate* unmanaged[Cdecl]<uint, byte*, uint, int>)&NetSend;
            _rules = (RulesNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(RulesNative.Table));
            _rules->Set = &RulesSet;
            _netBefore = _netTable.GetValue(null);
            _snapshotBefore = _snapshot.GetValue(null);
            _rulesBefore = _rulesTable.GetValue(null);
            _channelBefore = _rulesChannel.GetValue(null);
            _channel = Network.Channel(ChannelName, RulesNet.ChannelVersion, required: false)
                .Register<RulesNet.RulesMsg>(0, static (m, w) => RulesNet.WriteRules(m, w), static (ref NetReader r) => RulesNet.ReadRules(ref r));
            _netTable.SetValue(null, Pointer.Box(_net, _netTable.FieldType));
            _rulesTable.SetValue(null, Pointer.Box(_rules, _rulesTable.FieldType));
            _rulesChannel.SetValue(null, _channel);
            _snapshot.SetValue(null, Array.Empty<NetPeer>());
            _state = Activator.CreateInstance(PlayerStateType, player)!;
            PlayerStateType.GetField("Effective")!.SetValue(_state, rules);
        }

        /// <summary>A Kiln client with kiln:rules joins as the player (a new connection object, as kiln.net makes).</summary>
        public NetPeer Connect(uint id)
        {
            var p = Peer(id);
            typeof(NetPeer).GetProperty(nameof(NetPeer.Player))!.SetValue(p, _player);
            ((Dictionary<string, string>)typeof(NetPeer).GetField("Declared", InstancePrivate)!.GetValue(p)!)[ChannelName] = RulesNet.ChannelVersion;
            NetEngineType.GetMethod("RemapServerChannels", StaticPrivate)!.Invoke(null, [p]);
            Assert.True(p.HasChannel(ChannelName));
            _snapshot.SetValue(null, new[] { p });
            _peers.Add(p);
            return p;
        }

        /// <summary>The connection closes (kiln.net drops it from the server's peers).</summary>
        public void Drop(NetPeer p)
        {
            typeof(NetPeer).GetProperty(nameof(NetPeer.State))!.SetValue(p, NetPeerState.Closed);
            _snapshot.SetValue(null, Array.Empty<NetPeer>());
        }

        /// <summary>One tick of RulesRuntime for the player: the server world's hooks, then the player's game.</summary>
        public void Tick()
        {
            typeof(RulesRuntime).GetMethod("PushNative", StaticPrivate)!.Invoke(null, [_state]);
            typeof(RulesRuntime).GetMethod("SyncClient", StaticPrivate)!.Invoke(null, [_state]);
        }

        public RulesNativePart ServerWorld => (RulesNativePart)PlayerStateType.GetField("Native")!.GetValue(_state)!;
        public RulesNativePart TheirGame => (RulesNativePart)PlayerStateType.GetField("Client")!.GetValue(_state)!;

        public void Dispose()
        {
            foreach (var p in _peers) RulesNet.ClientHooksOf(p, 0);
            _rulesChannel.SetValue(null, _channelBefore);
            _rulesTable.SetValue(null, _rulesBefore);
            _snapshot.SetValue(null, _snapshotBefore);
            _netTable.SetValue(null, _netBefore);
            _channel.Dispose();
            NativeMemory.Free(_net);
            NativeMemory.Free(_rules);
        }
    }

    [Fact]
    public void TheServerStripsSteeringUntilThisConnectionConfirmsItsHook()
    {
        var creative = Combine(PlayerRules.Creative);
        var steered = creative.Native;
        var held = steered.WithoutSteer;
        Assert.Equal(0x21dfu, steered.Flags);
        Assert.Equal(0x1dfu, held.Flags);
        using var server = new DedicatedServer(P, creative);
        void Both(RulesNativePart expected, string when)
        {
            Assert.True(expected == server.ServerWorld, $"{when}: the server world's hooks got {server.ServerWorld}");
            Assert.True(expected.Flags == _setFlags && expected.GlideSteerLimit == _setLimit,
                $"{when}: kiln.rules was given flags 0x{_setFlags:x}, limit {_setLimit}");
            Assert.True(expected == server.TheirGame, $"{when}: their game was sent {server.TheirGame}");
        }

        // a Kiln client with the rules channel, its hook message not (yet) received: an older Kiln never sends one
        var a = server.Connect(300);
        var (sets, frames) = (_sets, _frames);
        server.Tick();
        Both(held, "before the capability");
        Assert.Equal((sets + 1, frames + 1), (_sets, _frames));
        server.Tick();
        Assert.Equal((sets + 1, frames + 1), (_sets, _frames));  // nothing new: nothing written or sent again

        RulesNet.ClientHooksOf(a, RulesNative.HookGlideSteer);
        server.Tick();
        Both(steered, "after the capability");
        Assert.Equal(85f, server.TheirGame.GlideSteerLimit);

        // a reconnect: kiln.net makes a new connection object, here with the same id, while the old one's
        // confirmation was never withdrawn; the new game has to confirm again
        server.Drop(a);
        var b = server.Connect(300);
        server.Tick();
        Both(held, "after a reconnect, before its capability");
        RulesNet.ClientHooksOf(b, RulesNative.HookGlideSteer);
        server.Tick();
        Both(steered, "after a reconnect and its capability");

        RulesNet.ClientHooksOf(b, 0);  // a client build without the hook says so
        server.Tick();
        Both(held, "a game reporting no steering hook");
    }

    // ---------------------------------------------------------------------------------------- presets -------

    private const string Header =
        "// Kiln's \"creative\" rules (Kiln.Rules: docs/kiln/services/rules.md). Edit to change what /creative and Rules.Preset(\"creative\") give;\n" +
        "// delete the file to get Kiln's defaults back. The server's limits (rules.json next to this folder) still apply.\n";

    // What dev.15 (with glideBoost.climb) to dev.23 wrote, as on the build machine (LF) ...
    private const string Dev23Json = """
        {
          "flyAnywhere": true,
          "flightCarriesBody": true,
          "buildOutsideBases": true,
          "ignoreNoBuildZones": true,
          "ignoreEnemiesNearby": true,
          "buildInShroud": true,
          "keepBuildsOutsideBases": true,
          "buildCost": 0,
          "buildRefund": 0,
          "digDamage": 1000,
          "invulnerable": true,
          "minHealth": 1,
          "minStamina": 1,
          "minMana": 1,
          "freezeShroudTimer": true,
          "unlimitedGliding": true,
          "glideSink": 0,
          "glideBoost": {
            "thrust": 15,
            "maxSpeed": 35,
            "lift": 0,
            "coast": 8,
            "climb": 15
          },
          "water": {
            "chargesPerSecond": "Infinity"
          }
        }
        """;
    private static readonly string Dev23 = Header + Dev23Json + "\n";
    // ... and as a Windows game or server wrote it (System.Text.Json's indented lines end in Environment.NewLine there)
    private static readonly string Dev23Windows = Header + Dev23Json.Replace("\n", "\r\n") + "\n";

    private static void InFolder(Action<string> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-rules-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var outer = RulesPresets.FolderOverride;
        RulesPresets.FolderOverride = dir;
        try { test(Path.Combine(dir, "creative.json")); }
        finally
        {
            RulesPresets.FolderOverride = outer;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CreativeSteersTheGlider()
    {
        Assert.Equal(GlideSteerRule.Default, PlayerRules.Creative.GlideSteer);
        Assert.Equal(85, PlayerRules.Creative.GlideSteer!.MaxClimbAngle);
        Assert.Equal(GlideBoostRule.Default, PlayerRules.Creative.GlideBoost);
        Assert.Equal(0, PlayerRules.Creative.GlideSink);
        Assert.True(PlayerRules.Creative.UnlimitedGliding);
        Assert.Equal(0x21dfu, Combine(PlayerRules.Creative).Native.Flags);
        Assert.Contains("\"glideSteer\"", PlayerRules.Creative.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUneditedDev23CreativeFileIsUpgraded(bool windows)
    {
        InFolder(file =>
        {
            var text = windows ? Dev23Windows : Dev23;
            Assert.Equal(PlayerRules.Creative with { GlideSteer = null }, PlayerRules.FromJson(text));
            File.WriteAllText(file, text);
            Assert.True(RulesPresets.IsEarlierDefault("creative", file));
            Assert.Equal(PlayerRules.Creative, RulesPresets.Get("creative"));
            Assert.Contains("\"glideSteer\"", File.ReadAllText(file));
            Assert.False(RulesPresets.IsEarlierDefault("creative", file));  // this version's: not rewritten again
            File.WriteAllText(file, text);
            RulesPresets.WriteDefaults();
            Assert.Equal(PlayerRules.Creative, PlayerRules.FromJson(File.ReadAllText(file)));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEditedDev23CreativeFileIsLeftAlone(bool windows)
    {
        InFolder(file =>
        {
            var nl = windows ? "\r\n" : "\n";
            var text = windows ? Dev23Windows : Dev23;
            foreach (var edited in new[]
                     {
                         text.Replace("\"glideSink\": 0", "\"glideSink\": 1"),
                         text.Replace("  \"water\":", "  \"glideSteer\": { \"maxClimbAngle\": 40 }," + nl + "  \"water\":"),
                         text + "\n",  // a blank line added at the end
                         text.Replace("\"climb\": 15", "\"climb\": 15 "),
                     })
            {
                File.WriteAllText(file, edited);
                Assert.False(RulesPresets.IsEarlierDefault("creative", file));
                RulesPresets.WriteDefaults();
                Assert.Equal(edited, File.ReadAllText(file));
                Assert.Equal(PlayerRules.FromJson(edited), RulesPresets.Get("creative"));
            }
        });
    }

    // As a Windows game or server writes a preset: the header's comment lines end in "\n", the JSON's lines in CRLF.
    private static string Windows(string text)
    {
        var json = text.IndexOf("\n{\n", StringComparison.Ordinal) + 1;
        return text[..json] + text[json..^1].Replace("\n", "\r\n") + "\n";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThisVersionsCreativeFileWithTheSteeringBlockDeletedIsLeftAlone(bool windows)
    {
        // the owner's way to switch the steered glider off; without its block the JSON is dev.15 to dev.23's exactly
        InFolder(file =>
        {
            RulesPresets.WriteDefaults();
            var text = File.ReadAllText(file);
            Assert.Equal(PlayerRules.Creative, PlayerRules.FromJson(text));
            if (windows) text = Windows(text);
            var nl = windows ? "\r\n" : "\n";
            var edited = text.Replace("  \"glideSteer\": {" + nl + "    \"maxClimbAngle\": 85" + nl + "  }," + nl, "");
            Assert.NotEqual(text, edited);
            Assert.Equal(PlayerRules.Creative with { GlideSteer = null }, PlayerRules.FromJson(edited));
            File.WriteAllText(file, edited);
            Assert.False(RulesPresets.IsEarlierDefault("creative", file));
            RulesPresets.WriteDefaults();
            Assert.Equal(edited, File.ReadAllText(file));
            Assert.Null(RulesPresets.Get("creative")!.GlideSteer);
            Assert.Equal(edited, File.ReadAllText(file));
        });
    }

    [Fact]
    public void NoEarlierCreativeDefaultCanBeReachedByEditingThisVersionsJson()
    {
        // every earlier snapshot (LF or CRLF) differs from this version's default in the header comment, so an edit of
        // the JSON alone (deleting glideSteer, climb, ...) is never taken for an earlier unedited file
        InFolder(file =>
        {
            RulesPresets.WriteDefaults();
            var text = File.ReadAllText(file);
            var header = text[..(text.IndexOf("\n{\n", StringComparison.Ordinal) + 1)];
            Assert.Contains("\"glideSteer\": null", header);
            Assert.Contains("allowGlideSteer", header);
            var earlier = (Dictionary<string, string[]>)typeof(RulesPresets)
                .GetField("Earlier", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.NotEmpty(earlier["creative"]);
            foreach (var snapshot in earlier["creative"])
            {
                Assert.False(snapshot.StartsWith(header, StringComparison.Ordinal));
                File.WriteAllText(file, header + snapshot[(snapshot.IndexOf("\n{\n", StringComparison.Ordinal) + 1)..]);
                Assert.False(RulesPresets.IsEarlierDefault("creative", file));
            }
        });
    }

    [Fact]
    public void TheEarlierDefaultsMatchWhatWindowsWroteToo()
    {
        // dev.14 and early dev.15 files written by a Windows server had CRLF JSON lines as well: they upgrade too
        InFolder(file =>
        {
            var dev14 = Header + """
                {
                  "flyAnywhere": true,
                  "flightCarriesBody": true,
                  "buildOutsideBases": true,
                  "ignoreNoBuildZones": true,
                  "ignoreEnemiesNearby": true,
                  "buildInShroud": true,
                  "keepBuildsOutsideBases": true,
                  "buildCost": 0,
                  "buildRefund": 0,
                  "digDamage": 1000,
                  "invulnerable": true,
                  "minHealth": 1,
                  "minStamina": 1,
                  "minMana": 1,
                  "water": {
                    "chargesPerSecond": "Infinity"
                  }
                }
                """.Replace("\n", "\r\n") + "\n";
            File.WriteAllText(file, dev14);
            Assert.True(RulesPresets.IsEarlierDefault("creative", file));
            File.WriteAllText(file, dev14.Replace("\"minHealth\": 1", "\"minHealth\": 0.5"));
            Assert.False(RulesPresets.IsEarlierDefault("creative", file));  // edited: left alone, as with LF
            Assert.True(Encoding.UTF8.GetBytes(Dev23Windows).Length > Encoding.UTF8.GetBytes(Dev23).Length);
        });
    }
}
