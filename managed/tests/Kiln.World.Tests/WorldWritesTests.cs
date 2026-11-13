// SPDX-License-Identifier: MIT
// Kiln.World's tracked writes without the native service: the mirrors of kiln_world.h's structs keep the sizes the
// native host test pins (native/services/world/tests/writes_test.cpp), a native answer becomes the right result, and
// the capability row follows the write program.
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Kiln.WorldTests;

public unsafe class WorldWritesTests
{
    [Fact]
    public void The_native_mirrors_have_the_header_s_sizes()
    {
        Assert.Equal(72, sizeof(WriteRequestNative));
        Assert.Equal(80, sizeof(WriteResultNative));
        Assert.Equal(256, sizeof(WriteInfoNative));
        Assert.Equal(72, sizeof(WriteStatusNative));
        Assert.Equal(192, sizeof(WorldNative.Table));
        Assert.Equal(280, sizeof(WorldWritesTable));
        Assert.Equal(240, (int)Marshal.OffsetOf<WorldWritesTable>(nameof(WorldWritesTable.WriteSubmit)));
        Assert.Equal(216, (int)Marshal.OffsetOf<WorldWritesTable>(nameof(WorldWritesTable.Capabilities)));
        Assert.Equal(56, (int)Marshal.OffsetOf<WriteRequestNative>(nameof(WriteRequestNative.ModId)));
        Assert.Equal(48, (int)Marshal.OffsetOf<WriteResultNative>(nameof(WriteResultNative.Before)));
        Assert.Equal(32, (int)Marshal.OffsetOf<WriteStatusNative>(nameof(WriteStatusNative.Submitted)));
    }

    private static WorldWriteResult Result(uint status, uint flags, string reason = "", uint writers = 0, Exception? error = null, bool size = false)
    {
        var text = Encoding.UTF8.GetBytes(reason);
        byte* before = stackalloc byte[4] { 1, 2, 3, 4 }, after = stackalloc byte[4] { 9, 2, 3, 4 };
        fixed (byte* p = text)
        {
            var r = new WriteResultNative
            {
                Size = (uint)sizeof(WriteResultNative), Status = status, Ticket = 7, Entity = 1042, Component = 5, Offset = 8, Length = 4,
                ComponentSize = 16, Flags = flags, Steps = 2, Writers = writers, Before = before, After = after,
                Reason = new NativeStr { Ptr = p, Len = (nuint)text.Length },
            };
            return WorldWrites.FromNative("Fuel", in r, error, size);
        }
    }

    [Fact]
    public void A_written_answer_carries_what_the_game_said()
    {
        var r = Result(0, WorldWritesAbi.GameWrites | WorldWritesAbi.BitsKnown | WorldWritesAbi.Saved | WorldWritesAbi.Attribute |
                          WorldWritesAbi.Checked | WorldWritesAbi.Kept, writers: 9);
        Assert.True(r.Ok);
        Assert.True(r.Changed);
        Assert.Null(r.Reason);
        Assert.Equal(WorldWriteStatus.Written, r.Status);
        Assert.Equal(new EntityId(1042), r.Entity);
        Assert.Equal("Fuel", r.Component);
        Assert.Equal(9, r.GameWriterCount);
        Assert.True(r.Kept);
        Assert.True(r.SaveTracked);
        Assert.False(r.ReplicationTracked);
        Assert.True(r.Attribute);
        Assert.Equal((8, 16, 2), (r.Offset, r.ComponentSize, r.Steps));
        Assert.Equal([1, 2, 3, 4], r.Before.ToArray());
        Assert.Equal([9, 2, 3, 4], r.After.ToArray());
        Assert.Equal("Written: Fuel of 1042 (9 game writers, kept, marked for the save)", r.ToString());
    }

    [Fact]
    public void What_the_build_does_not_tell_is_null_not_false()
    {
        var r = Result(0, 0);
        Assert.Null(r.SaveTracked);
        Assert.Null(r.ReplicationTracked);
        Assert.Null(r.Kept);
        Assert.Equal("Written: Fuel of 1042", r.ToString());
        var written = Result(0, WorldWritesAbi.GameWrites | WorldWritesAbi.Checked, writers: 1);
        Assert.False(written.Kept);
        Assert.True(written.Ok);   // the write happened; the game wrote over it afterwards
        Assert.Contains("written over by the game one step later", written.ToString());
    }

    [Theory]
    [InlineData(1u, WorldWriteStatus.Unchanged, true)]
    [InlineData(2u, WorldWriteStatus.NoComponent, false)]
    [InlineData(3u, WorldWriteStatus.OutOfRange, false)]
    [InlineData(4u, WorldWriteStatus.NotDeclared, false)]
    [InlineData(5u, WorldWriteStatus.Refused, false)]
    [InlineData(6u, WorldWriteStatus.Faulted, false)]
    [InlineData(7u, WorldWriteStatus.Dropped, false)]
    [InlineData(99u, WorldWriteStatus.Faulted, false)]   // a status a newer kiln.world invents is not "ok"
    public void Every_status_maps_and_a_refusal_has_a_reason(uint native, WorldWriteStatus status, bool ok)
    {
        var r = Result(native, 0, ok ? "" : "because");
        Assert.Equal(status, r.Status);
        Assert.Equal(ok, r.Ok);
        Assert.False(r.Changed);
        Assert.Equal(ok ? null : "because", r.Reason);
        Assert.NotNull(Result(native, 0).Ok ? "x" : Result(native, 0).Reason);   // never Ok = false without words
    }

    [Fact]
    public void A_change_that_threw_or_saw_another_size_is_told_apart_from_a_refusal()
    {
        var threw = Result(5, 0, "the change callback returned false", error: new InvalidOperationException("boom"));
        Assert.Equal(WorldWriteStatus.Faulted, threw.Status);
        Assert.Contains("InvalidOperationException: boom", threw.Reason);
        var size = Result(5, 0, "the change callback returned false", size: true);
        Assert.Equal(WorldWriteStatus.LayoutMismatch, size.Status);
        Assert.Contains("16 bytes", size.Reason);
    }

    [Fact]
    public void A_type_of_another_layout_is_answered_before_anything_is_sent()
    {
        // Kiln.Game's own layout check is installed for the whole process as soon as any test names a generated type
        // (the next test does), and it says no in a test host, which is no game build. Which test runs first is not
        // fixed, so this one takes the check out while it looks at the size rule alone, and puts it back.
        var previous = WorldWrites.LayoutWhyNot;
        try
        {
            WorldWrites.LayoutWhyNot = null;
            Assert.Null(WorldWrites.LayoutProblem(typeof(int), "Fuel", "keen::ecs::Fuel", 16, 16, new EntityId(3)));
            var bad = WorldWrites.LayoutProblem(typeof(int), "Fuel", "keen::ecs::Fuel", 16, 12, new EntityId(3))!;
            Assert.Equal(WorldWriteStatus.LayoutMismatch, bad.Status);
            Assert.False(bad.Ok);
            Assert.Contains("12 bytes", bad.Reason);
            WorldWrites.LayoutWhyNot = (_, qn) => $"{qn}: size 16 generated, 24 live";
            var live = WorldWrites.LayoutProblem(typeof(int), "Fuel", "keen::ecs::Fuel", 16, 16, new EntityId(3))!;
            Assert.Contains("size 16 generated, 24 live", live.Reason);
        }
        finally { WorldWrites.LayoutWhyNot = previous; }
    }

    [Fact]
    public void Kiln_Game_hands_over_its_layout_check_before_a_generated_type_is_used()
    {
        // Naming a member of a generated type runs Kiln.Game's module initializer.
        Assert.Equal("Fuel", Name<Ecs.Fuel>());
        Assert.NotNull(WorldWrites.LayoutWhyNot);

        static string Name<T>() where T : unmanaged, IGameComponent<T> => T.Name;
    }

    [Fact]
    public void A_mod_built_against_the_older_Kiln_Game_still_finds_the_marker_interfaces()
    {
        // They moved to Kiln.Api; Kiln.Game forwards them, so a reference to "Kiln.IGameComponent`1, Kiln.Game" resolves.
        var forwarded = typeof(Ecs).Assembly.GetForwardedTypes();
        foreach (var type in (Type[])[typeof(IGameStruct<>), typeof(IGameComponent<>), typeof(IGameEvent<>), typeof(IGameAttribute<,>)])
        {
            Assert.Contains(type, forwarded);
            Assert.Equal("Kiln.Api", type.Assembly.GetName().Name);
            Assert.Same(type, Type.GetType($"{type.FullName}, Kiln.Game", throwOnError: true));
        }
        Assert.True(typeof(IGameComponent<Ecs.Fuel>).IsAssignableFrom(typeof(Ecs.Fuel)));
    }

    [Fact]
    public void Write_info_says_what_to_add()
    {
        var n = new WriteInfoNative { Size = (uint)sizeof(WriteInfoNative), State = 2 };
        var missing = WorldWrites.FromNative("Fuel", "my-mod", in n);
        Assert.Equal(WorldWriteState.NotDeclared, missing.State);
        Assert.False(missing.CanWrite);
        Assert.Contains("\"writes\": [\"Fuel\"]", missing.Reason);
        Assert.Contains("restart the server", missing.Reason);

        n.State = 3;
        Assert.Contains("mod 'my-mod' does not list Fuel", WorldWrites.FromNative("Fuel", "my-mod", in n).Reason);

        n.State = 0;
        n.Flags = WorldWritesAbi.GameWrites | WorldWritesAbi.BitsKnown | WorldWritesAbi.Replicated;
        n.Writers = 2;
        n.InitWriters = 1;
        n.ComponentSize = 16;
        "fuel_setup, actor_interaction"u8.CopyTo(new Span<byte>(n.WriterNames, 232));
        var ready = WorldWrites.FromNative("Fuel", "my-mod", in n);
        Assert.True(ready.CanWrite);
        Assert.Null(ready.Reason);
        Assert.Equal(["fuel_setup", "actor_interaction"], ready.GameWriters);
        Assert.Equal((2, 1, 16), (ready.GameWriterCount, ready.InitWriterCount, ready.ComponentSize));
        Assert.False(ready.SaveTracked);
        Assert.True(ready.ReplicationTracked);

        foreach (var state in Enum.GetValues<WorldWriteState>())
            Assert.Equal(state == WorldWriteState.Ready, WorldWrites.RefusalText(state, "Fuel", "m") is null);
        n.State = 77;
        Assert.Equal(WorldWriteState.Off, WorldWrites.FromNative("Fuel", null, in n).State);

        // a mod whose "writes" was not read at boot: the state says so, the mod loaded
        n.State = 7;
        var refused = WorldWrites.FromNative("Fuel", "my-mod", in n);
        Assert.Equal(WorldWriteState.ListRefused, refused.State);
        Assert.Contains("was not read when the server started", refused.Reason);
        Assert.Contains("the mod itself loaded", refused.Reason);

        // what the running world knows is not published yet: nothing of the previous world's is passed on
        n.State = 0;
        n.Flags = WorldWritesAbi.NotReady | WorldWritesAbi.GameWrites | WorldWritesAbi.BitsKnown | WorldWritesAbi.Saved;
        n.Writers = 3;
        var early = WorldWrites.FromNative("Fuel", "my-mod", in n);
        Assert.False(early.FactsKnown);
        Assert.True(early.CanWrite);
        Assert.Equal((0, 0), (early.GameWriterCount, early.ComponentSize));
        Assert.Null(early.SaveTracked);
        Assert.True(ready.FactsKnown);
    }

    [Fact]
    public void The_capability_row_follows_the_write_program()
    {
        WriteStatusNative s = default;
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceOlder), Head(WorldCapabilities.Decide(2, false, false, s, "")));
        Assert.Contains("kiln.world v2 has no tracked writes", WorldCapabilities.Decide(2, false, false, s, "").Text);
        var off = WorldCapabilities.Decide(3, true, false, s, "needs items.fuel: symbol items/WriteOther: 0 matches");
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.SymbolMissing), Head(off));
        Assert.Contains("items.fuel", off.Text);

        // nothing listed: the mechanism works, every write answers NotDeclared
        var idle = WorldCapabilities.Decide(3, true, true, s, "");
        Assert.Equal((CapabilityState.Available, CapabilityReason.None), Head(idle));
        Assert.Contains("\"writes\"", idle.Text);

        s.Declared = 2;
        s.Program = WorldWritesAbi.ProgramPending;
        Assert.Equal((CapabilityState.Pending, CapabilityReason.Session), Head(WorldCapabilities.Decide(3, true, true, s, "")));
        s.Program = WorldWritesAbi.ProgramActive;
        Assert.Equal((CapabilityState.Available, CapabilityReason.None, ""), WorldCapabilities.Decide(3, true, true, s, ""));
        s.Program = WorldWritesAbi.ProgramFaulted;
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Faulted), Head(WorldCapabilities.Decide(3, true, true, s, "")));
        foreach (var refused in (uint[])[WorldWritesAbi.ProgramRefused, WorldWritesAbi.ProgramSkipped])
        {
            s.Program = refused;
            Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Faulted), Head(WorldCapabilities.Decide(3, true, true, s, "")));
        }
        // only refused names (unknown components): still not "nothing listed"
        s = new WriteStatusNative { Refused = 1, Program = WorldWritesAbi.ProgramActive };
        Assert.Equal("", WorldCapabilities.Decide(3, true, true, s, "").Text);

        static (CapabilityState, CapabilityReason) Head((CapabilityState State, CapabilityReason Reason, string Text) row) => (row.State, row.Reason);
    }

    [Fact]
    public void The_event_is_about_its_entity()
    {
        var ev = new ComponentWrittenEvent(Result(0, 0), "my-mod");
        Assert.True(ev.Involves(new EntityId(1042)));
        Assert.False(ev.Involves(new EntityId(1043)));
        Assert.Equal(("Fuel", "my-mod"), (ev.Component, ev.ModId));
        Assert.True(EventFilter.ForEntity(new EntityId(1042)).Passes(ev));
        Assert.False(EventFilter.ForEntity(new EntityId(7)).Passes(ev));
    }
}
