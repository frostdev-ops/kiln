// SPDX-License-Identifier: MIT
// Kiln.Client's building input (BuildingInput.cs) against a fake kiln.client v1 table of 80 bytes: the binding's layout
// is kiln_client.h's byte for byte, each submit sends what the header asks for (the local player, its client session, the
// caller's owner), kiln.client's answers become the documented results, and the checks that throw run before any call.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.Api.Tests;

public sealed unsafe class BuildingInputTests : IDisposable
{
    private readonly IKilnBackend _previous = Backend.Current;
    private readonly FakeBackend _backend = new();
    private readonly ModContext _mod;
    private readonly ModContext? _outerMod = ModContext.CurrentMod;

    public BuildingInputTests()
    {
        Backend.Current = _backend;
        FakeBuildingTable.Reset();
        _backend.Tables["kiln.client"] = FakeBuildingTable.Create(80);
        _backend.Features.Add(BuildingInput.Capability);
        _mod = HostAccess.CreateContext(new ModInfo("bi-test", "bi-test", "1.0.0", Path.GetTempPath()), 4242, Path.GetTempPath());
        FakeBuildingTable.InGame(player: 17, session: 3);
    }

    // The mod whose code calls (ModContext.CurrentMod is per thread: set on the test's own thread).
    private void Enter() => ModContext.CurrentMod = _mod;

    public void Dispose()
    {
        ModContext.CurrentMod = _outerMod;
        Backend.Current = _previous;
    }

    private static int Offset<T>(T* start, void* field) where T : unmanaged => (int)((byte*)field - (byte*)start);

    [Fact]
    public void The_binding_is_kiln_client_h_byte_for_byte()
    {
        Assert.Equal(112, sizeof(BuildingInputNative.Input));
        Assert.Equal(48, sizeof(BuildingInputNative.State));
        Assert.Equal(80, sizeof(BuildingInputNative.Table));
        var i = default(BuildingInputNative.Input);
        Assert.Equal(4, Offset(&i, &i.Action));
        Assert.Equal(8, Offset(&i, &i.Player));
        Assert.Equal(12, Offset(&i, &i.ItemId));
        Assert.Equal(16, Offset(&i, &i.MaterialItemId));
        Assert.Equal(20, Offset(&i, &i.Slot));
        Assert.Equal(24, Offset(&i, &i.TargetEntityId));
        Assert.Equal(28, Offset(&i, &i.Reserved));
        Assert.Equal(32, Offset(&i, i.Position));
        Assert.Equal(56, Offset(&i, i.Rotation));
        Assert.Equal(88, Offset(&i, i.Scale));
        var s = default(BuildingInputNative.State);
        Assert.Equal(16, Offset(&s, &s.LocalPlayer));
        Assert.Equal(24, Offset(&s, &s.ClientSession));
        Assert.Equal(32, Offset(&s, &s.ClientSteps));
        var t = default(BuildingInputNative.Table);
        Assert.Equal(8, Offset(&t, &t.GetState));
        Assert.Equal(48, Offset(&t, &t.ClientWorld));
        Assert.Equal(56, Offset(&t, &t.Submit));
        Assert.Equal(64, Offset(&t, &t.Status));
        Assert.Equal(72, Offset(&t, &t.Cancel));
        // the header's enums
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, Enum.GetValues<BuildingAction>().Select(v => (uint)v));
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, Enum.GetValues<BuildingInputState>().Select(v => (uint)v));
    }

    [Fact]
    public void Place_sends_the_local_player_its_session_the_owner_and_the_transform()
    {
        Enter();
        var rotation = new Quaternion(0, 2, 0, 0);  // any length: kiln.client normalises it
        var r = BuildingInput.Place(new WorldPosition(10.5, -2, 300.25), rotation);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(BuildingInputRefusal.None, r.Refusal);
        Assert.NotNull(r.Request);
        Assert.Equal(7u, r.Request!.Id);
        Assert.Equal(BuildingAction.Place, r.Request.Action);
        Assert.Equal(3u, r.Request.ClientSession);
        Assert.Equal(1, FakeBuildingTable.Submits);
        Assert.Equal(4242ul, FakeBuildingTable.LastOwner);
        Assert.Equal(3ul, FakeBuildingTable.LastExpected);
        var input = FakeBuildingTable.LastInput;
        Assert.Equal(112u, input.Size);
        Assert.Equal(1u, input.Action);
        Assert.Equal(17u, input.Player);
        Assert.Equal(0u, input.ItemId);
        Assert.Equal(0u, input.TargetEntityId);
        Assert.Equal(0u, input.Reserved);
        Assert.Equal(new[] { 10.5, -2, 300.25 }, new[] { input.Position[0], input.Position[1], input.Position[2] });
        Assert.Equal(new[] { 0.0, 2, 0, 0 }, new[] { input.Rotation[0], input.Rotation[1], input.Rotation[2], input.Rotation[3] });
        Assert.Equal(new[] { 1.0, 1, 1 }, new[] { input.Scale[0], input.Scale[1], input.Scale[2] });
    }

    [Fact]
    public void Select_Undo_and_Dismantle_send_their_fields_and_nothing_else()
    {
        Enter();
        Assert.True(BuildingInput.Select(new ItemId(0xabcd), 255, new ItemId(0x1234)).Ok);
        var select = FakeBuildingTable.LastInput;
        Assert.Equal((0u, 0xabcdu, 0x1234u, 255u, 0u), (select.Action, select.ItemId, select.MaterialItemId, select.Slot, select.TargetEntityId));
        Assert.Equal(0.0, select.Rotation[3]);  // no transform

        Assert.True(BuildingInput.Undo().Ok);
        var undo = FakeBuildingTable.LastInput;
        Assert.Equal((3u, 0u, 0u, 0u), (undo.Action, undo.ItemId, undo.Slot, undo.TargetEntityId));

        Assert.True(BuildingInput.Dismantle(new EntityId(9001), new WorldPosition(1, 2, 3), Quaternion.Identity, new Vector3(2, 2, 2)).Ok);
        var dismantle = FakeBuildingTable.LastInput;
        Assert.Equal((4u, 9001u), (dismantle.Action, dismantle.TargetEntityId));
        Assert.Equal(2.0, dismantle.Scale[1]);
        Assert.Equal(1.0, dismantle.Rotation[3]);

        Assert.True(BuildingInput.Remove(new WorldPosition(0, 0, 0), Quaternion.Identity).Ok);
        Assert.Equal(2u, FakeBuildingTable.LastInput.Action);
        Assert.Equal(4, FakeBuildingTable.Submits);
    }

    [Fact]
    public void An_expected_session_is_passed_as_given()
    {
        Enter();
        FakeBuildingTable.SubmitRc = 201;
        var r = BuildingInput.Undo(expectedSession: 2);
        Assert.Equal(2ul, FakeBuildingTable.LastExpected);
        Assert.False(r.Ok);
        Assert.Equal(BuildingInputRefusal.Stale, r.Refusal);
        Assert.Contains("session 2 is not the live one (3)", r.Reason);
    }

    [Theory]
    [InlineData(200, BuildingInputRefusal.Busy)]
    [InlineData(201, BuildingInputRefusal.Stale)]
    [InlineData(6, BuildingInputRefusal.ModUnloaded)]
    [InlineData(1, BuildingInputRefusal.Rejected)]
    [InlineData(7, BuildingInputRefusal.Rejected)]
    public void Kiln_client_refusals_are_results(int rc, BuildingInputRefusal refusal)
    {
        Enter();
        FakeBuildingTable.SubmitRc = rc;
        var r = BuildingInput.Undo();
        Assert.False(r.Ok);
        Assert.Null(r.Request);
        Assert.Equal(refusal, r.Refusal);
        Assert.False(string.IsNullOrEmpty(r.Reason));
    }

    [Fact]
    public void Kiln_client_answering_unavailable_throws_naming_the_row()
    {
        Enter();
        FakeBuildingTable.SubmitRc = 3;
        var e = Assert.Throws<KilnUnavailableException>(() => BuildingInput.Undo());
        Assert.Equal(BuildingInput.Capability, e.Capability);
    }

    [Fact]
    public void No_world_or_no_local_player_is_refused_before_kiln_client_is_asked()
    {
        Enter();
        FakeBuildingTable.Menu();
        var menu = BuildingInput.Undo();
        Assert.Equal(BuildingInputRefusal.Stale, menu.Refusal);

        FakeBuildingTable.InGame(player: 0, session: 4);
        var loading = BuildingInput.Undo();
        Assert.Equal(BuildingInputRefusal.NoLocalPlayer, loading.Refusal);
        Assert.Equal(0, FakeBuildingTable.Submits);
    }

    [Fact]
    public void Bad_arguments_throw_before_kiln_client_is_asked()
    {
        Enter();
        var at = new WorldPosition(0, 0, 0);
        Assert.Throws<ArgumentException>(() => BuildingInput.Select(ItemId.None, 0));
        Assert.Throws<ArgumentException>(() => BuildingInput.Dismantle(EntityId.None, at, Quaternion.Identity));
        Assert.Throws<ArgumentException>(() => BuildingInput.Place(at, new Quaternion(0, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => BuildingInput.Place(at, new Quaternion(float.NaN, 0, 0, 1)));
        Assert.Throws<ArgumentException>(() => BuildingInput.Place(at, new Quaternion(1e-7f, 0, 0, 0)));  // squared norm below 1e-12
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingInput.Place(new WorldPosition(2147483648.0, 0, 0), Quaternion.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingInput.Place(new WorldPosition(0, double.NaN, 0), Quaternion.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingInput.Remove(at, Quaternion.Identity, new Vector3(1, 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingInput.Remove(at, Quaternion.Identity, new Vector3(1, 1, 1024.5f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingInput.Remove(at, Quaternion.Identity, new Vector3(float.PositiveInfinity, 1, 1)));
        Assert.Equal(0, FakeBuildingTable.Submits);
        // the edges kiln.client accepts
        Assert.True(BuildingInput.Place(new WorldPosition(2147483647.0, -2147483647.0, 0), Quaternion.Identity, new Vector3(1024, 1e-3f, 1)).Ok);
    }

    [Fact]
    public void A_submit_outside_mod_code_throws()
    {
        ModContext.CurrentMod = null;
        Assert.Throws<InvalidOperationException>(() => BuildingInput.Undo());
        Assert.Equal(0, FakeBuildingTable.Submits);
    }

    [Fact]
    public void The_request_reads_its_state_and_cancels_with_its_own_owner_from_any_thread()
    {
        Enter();
        var request = BuildingInput.Undo().Request!;
        ModContext.CurrentMod = null;  // a worker thread has no current mod: the request carries its owner
        FakeBuildingTable.StatusState = 2;
        Assert.Equal(BuildingInputState.Pressed, request.State);
        Assert.Equal((4242ul, 7u), (FakeBuildingTable.LastStatusOwner, FakeBuildingTable.LastStatusId));
        FakeBuildingTable.StatusState = 9;  // not a state this Kiln knows
        Assert.Equal(BuildingInputState.Unknown, request.State);
        FakeBuildingTable.StatusState = 3;
        FakeBuildingTable.StatusRc = 6;  // revoked
        Assert.Equal(BuildingInputState.Unknown, request.State);
        FakeBuildingTable.StatusRc = 0;
        Assert.Equal(BuildingInputState.Dispatched, request.State);

        Assert.True(request.Cancel());
        FakeBuildingTable.Cancelled = 0;  // an older id
        Assert.False(request.Cancel());
        FakeBuildingTable.Cancelled = 1;
        FakeBuildingTable.CancelRc = 6;
        Assert.False(request.Cancel());
        Assert.Equal(3, FakeBuildingTable.Cancels);
        Assert.Equal("building input 7 (Undo, session 3)", request.ToString());
    }

    [Fact]
    public void Off_row_throws_naming_it_and_Available_is_false()
    {
        Enter();
        _backend.Features.Remove(BuildingInput.Capability);
        Assert.False(BuildingInput.Available);
        var e = Assert.Throws<KilnUnavailableException>(() => BuildingInput.Undo());
        Assert.Equal(BuildingInput.Capability, e.Capability);
        Assert.Equal(0, FakeBuildingTable.Submits);
    }

    [Fact]
    public void An_older_kiln_client_has_no_building_input_and_its_row_says_why()
    {
        Enter();
        var older = new FakeBackend();
        older.Tables["kiln.client"] = FakeBuildingTable.Create(56);  // main's table before 0.2.0-dev.30.3
        Backend.Current = older;
        Assert.False(BuildingInput.Available);
        var e = Assert.Throws<KilnUnavailableException>(() => BuildingInput.Undo());
        Assert.Equal(BuildingInput.Capability, e.Capability);
        Assert.Contains("a Kiln before 0.2.0-dev.30.3", e.Message);
        var row = Capabilities.Get(BuildingInput.Capability);
        Assert.Equal(CapabilityState.Unavailable, row.State);
        Assert.Equal(CapabilityReason.ServiceOlder, row.Reason);
        Assert.Contains("56-byte table", row.ReasonText);
        Assert.Equal(BuildingInputState.Unknown, new BuildingInputRequest(1, BuildingAction.Undo, 1, 4242).State);
    }

    [Fact]
    public void Without_kiln_client_the_row_says_why()
    {
        Enter();
        Backend.Current = new FakeBackend();
        Assert.False(BuildingInput.Available);
        var e = Assert.Throws<KilnUnavailableException>(() => BuildingInput.Undo());
        Assert.Contains("kiln.client is not here", e.Message);
        Assert.Equal(CapabilityState.Unavailable, Capabilities.Get(BuildingInput.Capability).State);
        Assert.NotEqual(CapabilityReason.Unknown, Capabilities.Get(BuildingInput.Capability).Reason);
    }

    [Fact]
    public void Available_when_the_row_is_on_and_the_members_are_there()
    {
        Assert.True(BuildingInput.Available);
    }

    [Fact]
    public void The_surface_declares_its_side_and_thread()
    {
        Assert.True(Attribute.IsDefined(typeof(BuildingInput), typeof(ClientOnlyAttribute)));
        Assert.True(Attribute.IsDefined(typeof(BuildingInput), typeof(AnyThreadAttribute)));
        foreach (var name in new[] { "Select", "Place", "Remove", "Undo", "Dismantle" })
        {
            var m = typeof(BuildingInput).GetMethod(name)!;
            Assert.Equal(BuildingInput.Capability, m.GetCustomAttributes(typeof(RequiresCapabilityAttribute), false).Cast<RequiresCapabilityAttribute>().Single().Name);
        }
    }

    internal sealed class FakeBackend : IKilnBackend
    {
        public readonly Dictionary<string, nint> Tables = new(StringComparer.Ordinal);
        public readonly HashSet<string> Features = new(StringComparer.Ordinal);
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => Features.Contains(name);
        public void Log(ulong owner, LogLevel level, string text) { }
        public nint Service(string name, uint minVersion) => Tables.GetValueOrDefault(name);
        public ulong OwnerCreate(string id) => 1;
    }
}

/// <summary>A fake kiln.client v1 table (kiln_client.h): state, and the building input members when it is 80 bytes. Its
/// memory lives for the process (a table is never freed).</summary>
internal static unsafe class FakeBuildingTable
{
    public static int SubmitRc, StatusRc, CancelRc, Submits, Cancels;
    public static uint StatusState, Cancelled, LastStatusId;
    public static ulong LastOwner, LastExpected, LastStatusOwner;
    public static BuildingInputNative.Input LastInput;
    private static BuildingInputNative.State _state;

    public static void Reset()
    {
        SubmitRc = StatusRc = CancelRc = Submits = Cancels = 0;
        StatusState = 1;
        Cancelled = 1;
        LastStatusId = 0;
        LastOwner = LastExpected = LastStatusOwner = 0;
        LastInput = default;
    }

    public static void InGame(uint player, uint session) =>
        _state = new BuildingInputNative.State { Side = 1, Session = 3, LocalPlayer = player, ClientSession = session, Flags = 1 };

    public static void Menu() => _state = new BuildingInputNative.State { Side = 1, Session = 0, ClientSession = 2 };

    // A dedicated server (KILN_SIDE_SERVER 0) with its world running (GameInputTests).
    public static void Server() => _state = new BuildingInputNative.State { Side = 0, Session = 1 };

    public static nint Create(uint size)
    {
        var t = (BuildingInputNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(BuildingInputNative.Table));
        t->Header.Size = size;
        t->Header.Version = 1;
        t->GetState = &GetState;
        if (size >= 80)
        {
            t->Submit = &Submit;
            t->Status = &Status;
            t->Cancel = &Cancel;
        }
        return (nint)t;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetState(BuildingInputNative.State* s)
    {
        if (s->Size < (uint)sizeof(BuildingInputNative.State)) return 1;
        var size = s->Size;
        *s = _state;
        s->Size = size;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Submit(ulong owner, ulong expected, BuildingInputNative.Input* input, uint* id)
    {
        Submits++;
        LastOwner = owner;
        LastExpected = expected;
        LastInput = *input;
        *id = SubmitRc == 0 ? 7u : 0u;
        return SubmitRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Status(ulong owner, uint id, uint* state)
    {
        LastStatusOwner = owner;
        LastStatusId = id;
        *state = StatusRc == 0 ? StatusState : 0;
        return StatusRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Cancel(ulong owner, uint id, uint* cancelled)
    {
        Cancels++;
        *cancelled = CancelRc == 0 ? Cancelled : 0;
        return CancelRc;
    }
}
