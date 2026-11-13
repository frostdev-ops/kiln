// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>A row says "available" exactly when the call it stands for works: checked on fake native tables whose
/// hooks are on or off, against the call itself.</summary>
public sealed unsafe class GameplayRowTruthTests : FakeTableTest
{
    private static byte _spawnHook, _entityHooks;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeStatus(GameplayNative.Status* s)
    {
        s->SpawnHook = _spawnHook;
        s->EntityHooks = _entityHooks;
        return 0;
    }

    // native hooks_spawn_replace: refused while the random spawners' hook is off
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSpawnReplace(ulong owner, byte* from, byte* to, float chance) => _spawnHook != 0 ? 0 : 3;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeEntityFilter(uint categories) => 0;

    private void UseTable(bool spawnHook, bool entityHooks)
    {
        _spawnHook = spawnHook ? (byte)1 : (byte)0;
        _entityHooks = entityHooks ? (byte)1 : (byte)0;
        // the table, then entity_filter, spawn_replace, spawn_replace_clear, component_change appended after it
        var size = sizeof(GameplayNative.Table) + 4 * sizeof(nint);
        var t = (GameplayNative.Table*)NativeMemory.AllocZeroed((nuint)size);
        t->Header = new ServiceHeader { Size = (uint)size, Version = 1 };
        t->Status = &FakeStatus;
        var tail = (nint*)((byte*)t + sizeof(GameplayNative.Table));
        tail[0] = (nint)(delegate* unmanaged[Cdecl]<uint, int>)&FakeEntityFilter;
        tail[1] = (nint)(delegate* unmanaged[Cdecl]<ulong, byte*, byte*, float, int>)&FakeSpawnReplace;
        Swap(typeof(Gameplay), "_listening", 1);  // no native subscription on a fake table
        Serve("kiln.gameplay", t, typeof(GameplayNative), "_table");
        StartRows(typeof(Gameplay).Assembly);
    }

    [Fact]
    public void Spawn_replacement_is_unavailable_while_the_spawn_hook_is_off_and_the_call_says_the_same()
    {
        UseTable(spawnHook: false, entityHooks: true);
        var row = Capabilities.Get("gameplay.spawn_replace");
        Assert.Equal(CapabilityState.Unavailable, row.State);
        Assert.Equal(CapabilityReason.Faulted, row.Reason);
        Assert.Contains("spawners' hook is off", row.ReasonText);
        AssertUnavailable("gameplay.spawn_replace", () => Gameplay.ReplaceNaturalSpawns(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Spawn_replacement_is_available_with_the_hook_and_then_the_call_works()
    {
        UseTable(spawnHook: true, entityHooks: true);
        Assert.True(Capabilities.Is("gameplay.spawn_replace"));
        Assert.True(Capabilities.Is("gameplay.entity_events"));
        using var replacement = Gameplay.ReplaceNaturalSpawns(Guid.NewGuid(), Guid.NewGuid(), 0.5);
    }

    [Fact]
    public void Entity_events_follow_the_entity_hooks()
    {
        UseTable(spawnHook: true, entityHooks: false);
        Assert.False(Gameplay.EntityEventsAvailable);
        Assert.Equal(CapabilityReason.Faulted, Capabilities.Get("gameplay.entity_events").Reason);
    }
}

/// <summary>assets.textures and the parts of kiln.assets whose hooks can be off.</summary>
public sealed unsafe class AssetsRowTruthTests : FakeTableTest
{
    private sealed class OnSide(ModSide side) : IModsBackend
    {
        public IReadOnlyList<ModEntry> All => [];
        public ModSide Side => side;
        public ModEntry? Get(string id) => null;
        public ModStats Stats(string id) => default;
        public bool Send(ModContext? sender, string targetId, string method, object? payload) => false;
        public Task<ModOperationResult> Request(ModOperation op, string arg) => throw new NotSupportedException();
        public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) => throw new NotSupportedException();
    }

    private static bool _resources, _content, _client;
    private static byte[] _status = [];

    // what native/services/assets prints: the store's counters, then "hooks" (the reasons unescaped)
    private static void Status(bool resources, bool content, bool icons, string why) =>
        _status = Encoding.UTF8.GetBytes(
            "{\"resources\":3,\"blobs\":0,\"hooks\":{\"resources\":" + (resources ? "true" : "false") + ",\"resourcesWhy\":\"" +
            (resources ? "" : why) + "\",\"content\":" + (content ? "true" : "false") + ",\"contentWhy\":\"" + (content ? "" : why) +
            "\",\"container\":-1,\"containerOpen\":false,\"port\":false,\"reads\":0,\"readBytes\":0,\"readMisses\":0,\"uiIcons\":" +
            (icons ? "true" : "false") + ",\"uiWhy\":\"\",\"uiCache\":false,\"populates\":0,\"service\":\"0x0\"}}");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeStatus(byte* json, nuint cap, nuint* len)
    {
        *len = (nuint)_status.Length;
        if (json == null || cap <= (nuint)_status.Length) return 5;
        for (var i = 0; i < _status.Length; i++) json[i] = _status[i];
        json[_status.Length] = 0;
        return 0;
    }

    // native texture_override_img: client only, and both the resource and the content hooks
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeTexture(ulong owner, AssetsNative.Key* key, void* png, nuint len) =>
        _client && _resources && _content ? 0 : 3;

    private void UseTable(ModSide side, bool resources, bool content, string why = "")
    {
        _client = side == ModSide.Client;
        _resources = resources;
        _content = content;
        Status(resources, content, icons: _client, why);
        var t = Alloc<AssetsNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(AssetsNative.Table), Version = 1 };
        t->Status = &FakeStatus;
        t->TextureOverridePng = &FakeTexture;
        Serve("kiln.assets", t, typeof(Assets), "_table");
        Swap(typeof(ModsBackend), "<Current>k__BackingField", new OnSide(side));
        StartRows(typeof(Assets).Assembly);
    }

    private static void OverrideTexture() => Assets.OverrideTexture(new AssetKey(Guid.NewGuid(), AssetKey.UiTextureType), [1, 2, 3]);

    [Fact]
    public void Textures_are_unavailable_on_a_client_whose_content_hooks_are_off_and_the_call_says_the_same()
    {
        UseTable(ModSide.Client, resources: true, content: false, why: "symbol assets/ReadContent: 0 matches");
        var row = Capabilities.Get("assets.textures");
        Assert.Equal(CapabilityState.Unavailable, row.State);
        Assert.Equal(CapabilityReason.Faulted, row.Reason);
        Assert.Contains("content hooks", row.ReasonText);
        Assert.Contains("ReadContent", row.ReasonText);  // kiln.assets' own reason
        Assert.False(Capabilities.Is("assets.content"));
        AssertUnavailable("assets.textures", OverrideTexture);
    }

    [Fact]
    public void Textures_are_unavailable_when_the_resource_hooks_are_off()
    {
        UseTable(ModSide.Client, resources: false, content: false, why: "the \"resource\" service was not found");  // a reason that breaks the JSON
        Assert.False(Capabilities.Is("assets.textures"));
        Assert.False(Capabilities.Is("assets.resources"));
        AssertUnavailable("assets.textures", OverrideTexture);
    }

    [Fact]
    public void Textures_are_available_on_a_client_with_both_hooks_and_then_the_call_works()
    {
        UseTable(ModSide.Client, resources: true, content: true);
        Assert.True(Capabilities.Is("assets.textures"));
        OverrideTexture();
    }

    [Fact]
    public void Textures_are_the_client_s_on_a_server_whatever_the_hooks()
    {
        UseTable(ModSide.Server, resources: true, content: true);
        Assert.Equal(CapabilityReason.WrongSide, Capabilities.Get("assets.textures").Reason);
        AssertUnavailable("assets.textures", OverrideTexture);
    }
}

/// <summary>Which rows a service's start lowers (ServiceCaps.Lower): only the core's own claim, a feature sigdb resolved.</summary>
public sealed class FeatureLoweringTests
{
    private static Capability Row(CapabilityState state, string source, CapabilityReason reason = CapabilityReason.None) =>
        new("gameplay.craft", state, reason, "", source);

    [Fact]
    public void A_feature_sigdb_resolved_is_lowered_while_its_service_is_missing()
    {
        Assert.True(ServiceCaps.LowersRow(Row(CapabilityState.Available, "sigdb")));
    }

    [Fact]
    public void A_row_someone_else_claims_or_no_row_is_left_alone()
    {
        // a test kit's backend switching the feature on for a test, a feature with no row (no Kiln core), and a feature
        // that is already off with its own reason
        Assert.False(ServiceCaps.LowersRow(Row(CapabilityState.Available, "backend")));
        Assert.False(ServiceCaps.LowersRow(Row(CapabilityState.Available, "testkit")));
        Assert.False(ServiceCaps.LowersRow(Row(CapabilityState.Unavailable, "", CapabilityReason.Unknown)));
        Assert.False(ServiceCaps.LowersRow(Row(CapabilityState.Unavailable, "sigdb", CapabilityReason.SymbolMissing)));
    }
}
