// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>Loads the real api-test mod into a collectible context like Kiln.Host does, runs its synchronous checks,
/// unloads it and checks that nothing of Kiln's (bus, commands, config, permissions, service tick) keeps it alive.</summary>
public sealed class ModLeakTests : KilnTest
{
    private sealed class ModAlc(string dir) : AssemblyLoadContext("test-mod", isCollectible: true)
    {
        public Assembly LoadFile(string path)
        {
            using var s = new MemoryStream(File.ReadAllBytes(path));
            return LoadFromStream(s);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is { } n && n.StartsWith("Kiln.", StringComparison.Ordinal)) return null;  // shared, from the default context
            var p = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(p) ? LoadFile(p) : null;
        }
    }

    private static string? FindTestMod(string project, string assembly)
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(d.FullName, assembly), Path.Combine(d.FullName, project), Path.Combine(d.FullName, "mods", "tests", project, "bin") })
                if (Directory.Exists(candidate))
                {
                    var f = Directory.EnumerateFiles(candidate, assembly + ".dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (f is not null) return f;
                }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference LoadRunUnload(string dll, int round)
    {
        var alc = new ModAlc(Path.GetDirectoryName(dll)!);
        var asm = alc.LoadFile(dll);
        var mod = (Mod)Activator.CreateInstance(asm.GetType("ApiTest.ApiTestMod", throwOnError: true)!)!;
        var ctx = NewMod("api-test");
        HostAccess.Bind(mod, ctx);
        As(ctx, () =>
        {
            Events.Register(mod);
            mod.OnLoad();
        });
        for (var i = 0; i < 30; i++)
        {
            var t = HostAccess.Tick(CurrentStep + 1, 0, 2, 0, 40);
            As(ctx, () => ctx.Tick.RaisePre(in t));
            Step();
        }
        Assert.NotNull(Commands.Find("apitest"));
        Assert.NotNull(Config.Find("api-test"));
        As(ctx, mod.OnUnload);
        ctx.Retire();
        Assert.Null(Commands.Find("apitest"));
        Assert.Null(Config.Find("api-test"));
        Assert.Null(Permissions.Find("api-test.vip"));
        Assert.DoesNotContain(Events.Subscriptions, s => s.Owner == "api-test");
        alc.Unload();
        return new WeakReference(alc);
    }

    [Fact]
    public void Api_test_mod_unloads_without_a_trace()
    {
        var dll = FindTestMod("api-test", "ApiTest");
        Assert.True(dll is not null, "ApiTest.dll not built");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Root)!, "enshrouded_server.json"), """
            { "userGroups": [
              { "name": "Admin", "canKickBan": true, "canAccessInventories": true, "canEditBase": true, "canEditWorld": true, "canExtendBase": true },
              { "name": "Friend", "canAccessInventories": true, "canEditBase": true, "canEditWorld": true },
              { "name": "Guest", "canEditWorld": true }
            ] }
            """);
        var refs = new List<WeakReference>();
        for (var round = 0; round < 3; round++) refs.Add(LoadRunUnload(dll!, round));
        for (var i = 0; i < 10 && refs.Any(r => r.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        lock (Backend.Lines)
        {
            var fails = Backend.Lines.Where(l => l.Contains("FAIL ")).ToList();
            Assert.True(fails.Count == 0, string.Join("\n", fails));
            Assert.True(Backend.Lines.Count(l => l.Contains("PASS ")) >= 3 * 30, $"{Backend.Lines.Count(l => l.Contains("PASS "))} PASS lines");
            Assert.Contains(Backend.Lines, l => l.Contains("api-test: load #3 in this process"));
        }
        Assert.Equal(0, refs.Count(r => r.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SerializeRestartState(string dll)
    {
        var alc = new ModAlc(Path.GetDirectoryName(dll)!);
        var asm = alc.LoadFile(dll);
        var type = asm.GetType("WorldStatusTest.WorldStatusTestMod", throwOnError: true)!;
        var json = (string)type.GetMethod("FlameRestartState", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [3, 7, "marked"])!;
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(3, parsed.RootElement.GetProperty("original").GetInt32());
        Assert.Equal(7, parsed.RootElement.GetProperty("expected").GetInt32());
        Assert.Equal("marked", parsed.RootElement.GetProperty("mode").GetString());
        alc.Unload();
        return new WeakReference(alc);
    }

    [Fact]
    public void World_status_restart_serialization_does_not_retain_its_mod_context()
    {
        var dll = FindTestMod("worldstatus-test", "WorldStatusTest");
        Assert.NotNull(dll);
        var weak = SerializeRestartState(dll);
        for (var i = 0; i < 20 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RoundTripItemsState(string dll)
    {
        var alc = new ModAlc(Path.GetDirectoryName(dll)!);
        var type = alc.LoadFile(dll).GetType("ItemsTest.ItemsTestMod", throwOnError: true)!;
        const string json = """{"Chest":42,"Original":"before","Wood":13,"WoodBefore":2,"WoodCount":3,"Gear":17,"GearBefore":1,"GearEntity":99,"GearLevel":3,"GearRarity":4,"NamedText":"Lab Ores"}""";
        var record = type.GetMethod("ParsePersistJson", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [json]);
        var saved = (string)type.GetMethod("PersistJson", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [record])!;
        using var parsed = System.Text.Json.JsonDocument.Parse(saved);
        Assert.Equal(42u, parsed.RootElement.GetProperty("Chest").GetUInt32());
        Assert.Equal("before", parsed.RootElement.GetProperty("Original").GetString());
        Assert.Equal(3, parsed.RootElement.GetProperty("WoodCount").GetInt32());
        Assert.Equal("Lab Ores", parsed.RootElement.GetProperty("NamedText").GetString());
        Assert.Equal(0u, parsed.RootElement.GetProperty("ChestB").GetUInt32());
        const string expected = """{"Chest":42,"Original":"before","Wood":13,"WoodBefore":2,"WoodCount":3,"Gear":17,"GearBefore":1,"GearEntity":99,"GearLevel":3,"GearRarity":4,"NamedText":"Lab Ores","ChestB":0,"BTotals":"","Named":0}""";
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(saved)));
        alc.Unload();
        return new WeakReference(alc);
    }

    [Fact]
    public void Items_restart_serialization_does_not_retain_its_mod_context()
    {
        var dll = FindTestMod("items-test", "ItemsTest");
        Assert.NotNull(dll);
        var weak = RoundTripItemsState(dll);
        for (var i = 0; i < 20 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(weak.IsAlive);
    }
}
