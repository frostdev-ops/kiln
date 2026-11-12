// SPDX-License-Identifier: MIT
// A mod built against one layout, stamped by the SDK task, run against regenerated Kiln.Game assemblies. The fixture
// mod (Fixtures/stamp/StampMod) is compiled against variant stamp-a (dump a.json) and loaded, in a load context of its
// own, next to:
//   stamp-b (b.json): an unrelated field moved, so every field the mod uses moved too, with name and type kept;
//   stamp-c (c.json): a field the mod uses (Lamp.glow) changed from float32 to float64.
// Against B the check passes and the mod reads the right values out of a buffer laid out as B says: its field
// accesses were bound by name when its code was compiled at run time. Against C the check refuses, naming the type
// and the field; and without the check the mod's code fails to bind, which is what the check is there to prevent.
using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace Kiln.Game.Tests;

public class StampCrossLayoutTests
{
    /// <summary>Resolves Kiln.Game to one variant; everything else (Kiln.Api, Kiln.World, the framework) from the
    /// default context, as Kiln.Host shares them.</summary>
    sealed class VariantContext(string gameDll) : AssemblyLoadContext("stamp-" + Path.GetFileName(Path.GetDirectoryName(gameDll)), isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == "Kiln.Game" ? LoadFromAssemblyPath(gameDll) : null;
    }

    /// <summary>Runs <paramref name="body"/> with the variant's Kiln.Game and the fixture mod loaded in one context.</summary>
    static T With<T>(string variant, Func<Assembly, Assembly, T> body)
    {
        var gameDll = Stamps.Variant(variant);
        var ctx = new VariantContext(gameDll);
        try
        {
            var game = ctx.LoadFromAssemblyName(new AssemblyName("Kiln.Game"));
            Assert.Equal(Path.GetFullPath(gameDll), Path.GetFullPath(game.Location));
            var mod = ctx.LoadFromAssemblyPath(Stamps.ModDll);
            return body(game, mod);
        }
        finally { ctx.Unload(); }
    }

    /// <summary><c>GameLayoutStamps.Check</c> of the loaded Kiln.Game, by reflection, as Kiln.Host calls it.</summary>
    static (bool Ok, string? Reason) Check(Assembly game, string stamp)
    {
        var check = game.GetType("Kiln.GameLayoutStamps", throwOnError: true)!
            .GetMethod("Check", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string).MakeByRefType()])!;
        var args = new object?[] { stamp, null };
        return ((bool)check.Invoke(null, args)!, (string?)args[1]);
    }

    /// <summary>The mod's <c>Probe.Read</c>; throws what binding or running it threw.</summary>
    static string Read(Assembly mod, byte[] buffer)
    {
        var read = mod.GetType("StampMod.Probe", throwOnError: true)!.GetMethod("Read")!;
        try { return (string)read.Invoke(null, [buffer])!; }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    /// <summary>A field's offset from a table line (<c>name@offset:type</c>).</summary>
    static int Offset(string[] line, string field) =>
        int.Parse(line.Skip(6).Single(m => m.StartsWith(field + "@", StringComparison.Ordinal)).Split('@', ':')[1], CultureInfo.InvariantCulture);

    /// <summary>A Lamp as the variant's table lays it out: glow 1.5, wick 7, mode Bright, flags 5, spare 0xBEEF, core
    /// {heat 2.25, ticks 9}.</summary>
    static byte[] LampBuffer(string variant)
    {
        var table = Stamps.Table(Stamps.Variant(variant));
        var lamp = Stamps.Line(table, "Kiln.Ecs+Lamp");
        var core = Stamps.Line(table, "Kiln.Ecs+LampCore");
        var b = new byte[64];
        var glowType = lamp.Skip(6).Single(m => m.StartsWith("glow@", StringComparison.Ordinal)).Split(':')[1];
        if (glowType == "double") BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(Offset(lamp, "glow")), 1.5);
        else BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(Offset(lamp, "glow")), 1.5f);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Offset(lamp, "wick")), 7);
        b[Offset(lamp, "mode")] = 2;
        b[Offset(lamp, "flags")] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(Offset(lamp, "spare")), 0xBEEF);
        var c = Offset(lamp, "core");
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(c + Offset(core, "heat")), 2.25f);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(c + Offset(core, "ticks")), 9);
        return b;
    }

    const string Expected = "glow=1.5 wick=7 mode=Bright flags=5 heat=2.25 ticks=9 bright=True";

    [Fact]
    public void The_fixtures_differ_as_the_test_needs()
    {
        var a = Stamps.Table(Stamps.Variant("a"));
        var b = Stamps.Table(Stamps.Variant("b"));
        var c = Stamps.Table(Stamps.Variant("c"));
        var (la, lb, lc) = (Stamps.Line(a, "Kiln.Ecs+Lamp"), Stamps.Line(b, "Kiln.Ecs+Lamp"), Stamps.Line(c, "Kiln.Ecs+Lamp"));
        // B: other layout, every field the mod uses at another offset with the same type
        Assert.NotEqual(la[2], lb[2]);
        Assert.NotEqual(GameLayoutStampsTests.Header(a)["set"], GameLayoutStampsTests.Header(b)["set"]);
        foreach (var f in new[] { "glow", "wick", "mode", "flags", "core" })
        {
            Assert.NotEqual(Offset(la, f), Offset(lb, f));
            Assert.Equal(la.Single(m => m.StartsWith(f + "@")).Split(':')[1], lb.Single(m => m.StartsWith(f + "@")).Split(':')[1]);
        }
        // C: glow retyped
        Assert.Contains("glow@0:float", la);
        Assert.Contains("glow@0:double", lc);
    }

    [Fact]
    public void Against_its_own_layout_the_mod_runs()
    {
        var stamp = Stamps.ModStamp();
        var log = With("a", (game, mod) =>
        {
            var (ok, reason) = Check(game, stamp);
            Assert.True(ok, reason);
            return Read(mod, LampBuffer("a"));
        });
        Assert.Equal(Expected, log);
    }

    [Fact]
    public void Against_a_layout_where_an_unrelated_field_moved_the_check_passes_and_the_mod_reads_the_moved_fields()
    {
        var stamp = Stamps.ModStamp();
        var (ok, reason, log, asIfA) = With("b", (game, mod) =>
        {
            var (ok, reason) = Check(game, stamp);
            return (ok, reason, Read(mod, LampBuffer("b")), Read(mod, LampBuffer("a")));
        });
        Assert.True(ok, reason);
        Assert.Null(reason);
        Assert.Equal(Expected, log);
        // the same code over a buffer laid out as A reads other bytes: it follows B's offsets, not A's
        Assert.NotEqual(Expected, asIfA);
    }

    [Fact]
    public void Against_a_layout_where_a_used_field_changed_type_the_check_refuses_naming_type_and_field()
    {
        var stamp = Stamps.ModStamp();
        var (ok, reason) = With("c", (game, _) => Check(game, stamp));
        Assert.False(ok);
        Assert.Equal("StampMod.dll: Kiln.Ecs+Lamp.glow changed from float to double in this Kiln.Game (server 00000000)", reason);
    }

    [Fact]
    public void Against_that_layout_without_the_check_the_mod_fails_to_bind()
    {
        // what the check prevents: the mod's code dies when it is first compiled, in the middle of whatever called it
        var e = Assert.ThrowsAny<Exception>(() => With("c", (_, mod) => Read(mod, LampBuffer("c"))));
        Assert.IsType<MissingFieldException>(e);
        Assert.Contains("glow", e.Message, StringComparison.Ordinal);
    }
}
