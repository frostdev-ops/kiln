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

    private static string? FindApiTest()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(d.FullName, "ApiTest"), Path.Combine(d.FullName, "mods", "tests", "api-test", "bin") })
                if (Directory.Exists(candidate))
                {
                    var f = Directory.EnumerateFiles(candidate, "ApiTest.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
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
        var dll = FindApiTest();
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
}
