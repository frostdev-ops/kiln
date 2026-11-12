// SPDX-License-Identifier: MIT
// Kiln.Game in the host: which file loads (host/game/<side>-<sha12>/Kiln.Game.dll for the running exe, else
// host/Kiln.Game.dll), and the layout-stamp check that refuses a mod before it loads when a game type it uses changed.
// The Kiln.Game here is the real one, built from the newest server dump and copied next to the tests.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class GameTypesTests : ModsFixture
{
    const string Sha = "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2";
    static string BuiltGame => Path.Combine(AppContext.BaseDirectory, "Kiln.Game.dll");

    static Guid Mvid(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }

    static string Stamp(Guid mvid, string types) =>
        $$"""{ "schema": 1, "assembly": "Hello.dll", "mvid": "{{mvid:D}}", "layoutSet": "0000000000000000", "types": { {{types}} } }""";

    [Fact]
    public void The_variant_is_side_and_the_first_twelve_hex_digits()
    {
        Assert.Equal("server-f0d824be7fcd", GameTypes.Variant("server", Sha));
        Assert.Equal("client-f0d824be7fcd", GameTypes.Variant("client", Sha.ToUpperInvariant()));
        Assert.Null(GameTypes.Variant("server", ""));
        Assert.Null(GameTypes.Variant("server", null));
        Assert.Null(GameTypes.Variant("server", Sha[..63] + "z"));
        Assert.Null(GameTypes.Variant("both", Sha));
    }

    [Fact]
    public void The_build_s_own_Kiln_Game_wins_else_the_flat_one()
    {
        var host = Directory.CreateDirectory(Path.Combine(Root, "host")).FullName;
        Assert.Null(GameTypes.PathIn(host, "server-f0d824be7fcd"));
        File.WriteAllText(Path.Combine(host, "Kiln.Game.dll"), "");
        Assert.Equal(Path.Combine(host, "Kiln.Game.dll"), GameTypes.PathIn(host, "server-f0d824be7fcd"));
        Assert.Equal(Path.Combine(host, "Kiln.Game.dll"), GameTypes.PathIn(host, null));
        var own = Directory.CreateDirectory(Path.Combine(host, "game", "server-f0d824be7fcd")).FullName;
        File.WriteAllText(Path.Combine(own, "Kiln.Game.dll"), "");
        Assert.Equal(Path.Combine(own, "Kiln.Game.dll"), GameTypes.PathIn(host, "server-f0d824be7fcd"));
        // another build's folder is never taken
        Assert.Equal(Path.Combine(host, "Kiln.Game.dll"), GameTypes.PathIn(host, "server-0123456789ab"));
        Assert.Equal(Path.Combine(host, "Kiln.Game.dll"), GameTypes.PathIn(host, "client-f0d824be7fcd"));
    }

    [Fact]
    public void Loading_takes_the_build_s_own_copy_once_and_says_which()
    {
        var host = Directory.CreateDirectory(Path.Combine(Root, "host")).FullName;
        File.Copy(BuiltGame, Path.Combine(host, "Kiln.Game.dll"));
        var own = Directory.CreateDirectory(Path.Combine(host, "game", "server-f0d824be7fcd")).FullName;
        File.Copy(BuiltGame, Path.Combine(own, "Kiln.Game.dll"));
        var lines = new List<string>();
        var ctx = new AssemblyLoadContext("game-types-test", isCollectible: true);
        try
        {
            var a = GameTypes.Load(ctx, host, "server-f0d824be7fcd", (l, t) => lines.Add($"{l}: {t}"));
            Assert.Equal(Path.Combine(own, "Kiln.Game.dll"), a!.Location);
            Assert.Same(a, GameTypes.Load(ctx, host, "server-f0d824be7fcd", (l, t) => lines.Add($"{l}: {t}")));
            Assert.Equal(["Info: Kiln.Game: game/server-f0d824be7fcd/Kiln.Game.dll, generated for this game build"], lines);
        }
        finally { ctx.Unload(); }
        ctx = new AssemblyLoadContext("game-types-test-flat", isCollectible: true);
        lines.Clear();
        try
        {
            var a = GameTypes.Load(ctx, host, "client-0123456789ab", (l, t) => lines.Add($"{l}: {t}"));
            Assert.Equal(Path.Combine(host, "Kiln.Game.dll"), a!.Location);
            Assert.Equal(["Info: Kiln.Game: Kiln.Game.dll (no game/client-0123456789ab/ for this game build: the newest layouts; mods are checked against them)"], lines);
        }
        finally { ctx.Unload(); }
    }

    [Fact]
    public void A_mod_whose_stamp_names_a_changed_game_type_is_refused_before_it_loads()
    {
        var dir = InstallHello();
        File.WriteAllText(Path.Combine(dir, GameTypes.StampFile), Stamp(Mvid(Path.Combine(dir, "Hello.dll")), """
            "Kiln.Ecs+NetworkHealth": { "layout": "0000000000000000", "fields": { "health": "int" } }
            """));
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.Empty(mods.Mods);
        var hello = Kiln.Mods.Get("hello")!;
        Assert.Equal(ModState.Failed, hello.State);
        const string reason = "mod hello: it was built against game types this game build changed (Hello.dll: Kiln.Ecs+NetworkHealth.health " +
                              "changed from int to uint in this Kiln.Game (server f0d824be)); update the mod, or ask its author for a build for this game version";
        Assert.Equal(reason, hello.Error);
        Assert.True(host.Has("Error: " + reason), host.Dump());
        Assert.True(host.Has("mods: 0 loaded, 1 failed, 0 skipped"), host.Dump());
        Assert.Contains(reason, File.ReadAllText(Path.Combine(LogsDir, "mods.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mod_whose_stamp_still_holds_loads()
    {
        var dir = InstallHello();
        File.WriteAllText(Path.Combine(dir, GameTypes.StampFile), Stamp(Mvid(Path.Combine(dir, "Hello.dll")), """
            "Kiln.Ecs+NetworkHealth": { "layout": "0000000000000000", "fields": { "health": "uint", "healthMax": "uint" } }
            """));
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.Equal(["hello"], mods.Mods.Select(m => m.Id));
    }

    [Fact]
    public void A_stale_stamp_a_missing_one_or_one_for_another_build_is_not_checked()
    {
        var dir = InstallHello();
        // another build of the mod (other MVID): loads as without a stamp
        File.WriteAllText(Path.Combine(dir, GameTypes.StampFile), Stamp(Guid.NewGuid(), """
            "Kiln.Ecs+NoSuchType": { "layout": "0000000000000000", "fields": {} }
            """));
        var host = new FakeHost();
        using (var mods = NewManager(host))
        {
            mods.LoadAll();
            Assert.Equal(["hello"], mods.Mods.Select(m => m.Id));
            Assert.True(host.Has("Warn: mod hello: kiln.gametypes.json belongs to another build of Hello.dll (its MVID differs); loading it unchecked"), host.Dump());
        }
        // no stamp at all
        File.Delete(Path.Combine(dir, GameTypes.StampFile));
        Assert.True(GameTypes.CheckMod("hello", dir, "Hello.dll", static () => throw new InvalidOperationException("Kiln.Game must not be needed"), static (_, _) => { }, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void An_unreadable_stamp_is_one_warning_naming_the_mod_and_the_mod_loads()
    {
        var dir = InstallHello();
        File.WriteAllText(Path.Combine(dir, GameTypes.StampFile), "{ not json");
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.Equal(["hello"], mods.Mods.Select(m => m.Id));
        Assert.Single(host.Lines, l => l.StartsWith("Warn: mod hello: kiln.gametypes.json not checked, loading it unchecked (", StringComparison.Ordinal));
        Assert.Contains(host.Lines, l => l.StartsWith("Warn: mod hello: kiln.gametypes.json not checked, loading it unchecked (JsonReaderException: ", StringComparison.Ordinal)
                                         || l.StartsWith("Warn: mod hello: kiln.gametypes.json not checked, loading it unchecked (JsonException: ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_check_that_throws_is_one_warning_naming_the_mod_and_the_exception_and_the_mod_loads()
    {
        var dir = InstallHello();
        File.WriteAllText(Path.Combine(dir, GameTypes.StampFile), Stamp(Mvid(Path.Combine(dir, "Hello.dll")), """
            "Kiln.Ecs+NoSuchType": { "layout": "0000000000000000", "fields": {} }
            """));
        var lines = new List<string>();
        Assert.True(GameTypes.CheckMod("hello", dir, "Hello.dll", static () => ThrowingGame.Value, (l, t) => lines.Add($"{l}: {t}"), out var reason));
        Assert.Null(reason);
        Assert.Equal(["Warn: mod hello: kiln.gametypes.json not checked, loading it unchecked (Kiln.Game's check threw InvalidOperationException: the check broke)"], lines);
    }

    /// <summary>An assembly with a <c>Kiln.GameLayoutStamps.Check(string, out string?)</c> that throws.</summary>
    static readonly Lazy<Assembly> ThrowingGame = new(() =>
    {
        var asm = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ThrowingGame"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var type = asm.DefineDynamicModule("ThrowingGame").DefineType("Kiln.GameLayoutStamps",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var check = type.DefineMethod("Check", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), [typeof(string), typeof(string).MakeByRefType()]);
        var il = check.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "the check broke");
        il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
        il.Emit(System.Reflection.Emit.OpCodes.Throw);
        type.CreateType();
        return asm;
    });

    [Fact]
    public void Without_a_Kiln_Game_or_with_one_older_than_the_stamps_the_mod_is_not_checked()
    {
        var dir = InstallHello();
        File.WriteAllText(Path.Combine(dir, GameTypes.StampFile), Stamp(Mvid(Path.Combine(dir, "Hello.dll")), """
            "Kiln.Ecs+NoSuchType": { "layout": "0000000000000000", "fields": {} }
            """));
        Assert.True(GameTypes.CheckMod("hello", dir, "Hello.dll", static () => null, static (_, _) => { }, out var reason));
        Assert.Null(reason);
        // an assembly without Kiln.GameLayoutStamps (Kiln.Api stands in for a dev.24 Kiln.Game)
        Assert.True(GameTypes.CheckMod("hello", dir, "Hello.dll", static () => typeof(Kiln.Mod).Assembly, static (_, _) => { }, out reason));
        // the real one refuses it
        Assert.False(GameTypes.CheckMod("hello", dir, "Hello.dll", static () => Assembly.LoadFrom(BuiltGame), static (_, _) => { }, out reason));
        Assert.Contains("Kiln.Ecs+NoSuchType is not in this Kiln.Game (server f0d824be)", reason, StringComparison.Ordinal);
    }
}
