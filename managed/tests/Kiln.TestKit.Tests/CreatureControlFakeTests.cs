// SPDX-License-Identifier: MIT
// The kit's fake kiln.creature_control (Fakes/CreatureControl/) on its raw table, without Kiln.CreatureControl: what it
// answers must stay what the native service of this build answers (native/services/creature_control/tests).
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed unsafe class CreatureControlFakeTests
{
    [Fact]
    public void InvalidBodyKeysMatchTheNativeArgumentRefusals()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        var model = CreatureControlFake.ModelOf(kiln);
        var reserved = new CreatureControlAbi.BodyKey { WorldEpoch = 1, BodyGeneration = 1, Entity = 1024, Reserved = 1 };
        Assert.Multiple(
            () => Assert.Equal(FakeTables.InvalidArgument, model.Quarantine(default, 1)),
            () => Assert.Equal(FakeTables.InvalidArgument, model.ReleaseBody(77, reserved)),
            () => Assert.Equal(FakeTables.InvalidArgument, model.BodyRetired(77, reserved)));
    }

    [Fact]
    public void BindKeepsItsBaseline()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Step(2);
        var entity = kiln.Entities.Create(new WorldPosition(1, 0, 1));
        var model = CreatureControlFake.ModelOf(kiln);
        CreatureControlAbi.BodyKey key = default, other = default;
        Assert.Equal(FakeTables.Ok, model.Bind(77, entity.Value, 0, 1, &key));
        Assert.Equal(FakeTables.InvalidArgument, model.Bind(78, entity.Value, 1, 1, &other));
    }

    [Fact]
    public void RequestOverflowAndNoticeGapsMatchTheBroker()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Step(2);
        var entity = kiln.Entities.Create(new WorldPosition(1, 0, 1));
        var model = CreatureControlFake.ModelOf(kiln);
        CreatureControlAbi.BodyKey key = default;
        Assert.Equal(FakeTables.Ok, model.Bind(77, entity.Value, 0, 1, &key));
        var q = new CreatureControlAbi.Request { Size = (uint)sizeof(CreatureControlAbi.Request), Body = key };
        CreatureControlAbi.State s = default;
        s.Size = (uint)sizeof(CreatureControlAbi.State);
        for (ulong i = 1; i <= 1024; ++i)
        {
            q.RequestId = i;
            q.Role = i % 2 == 0 ? CreatureControlAbi.PetFollow : CreatureControlAbi.PetStay;
            Assert.Equal(FakeTables.Ok, model.Acquire(77, &q, &s));
            Assert.NotEqual(0ul, s.Lease);
        }
        q.RequestId++;
        q.Role = CreatureControlAbi.PetStay;
        Assert.Equal(FakeTables.Ok, model.Acquire(77, &q, &s));
        uint written = 0, resync = 0;
        Assert.Equal(FakeTables.Ok, model.Notices(77, null, 0, &written, &resync));
        var admitted = s.Lease;
        var gotResync = resync;
        Assert.Multiple(
            () => Assert.Equal(0ul, admitted),
            () => Assert.Equal(1u, gotResync));
        Assert.StartsWith("QueueFull", Reason(&s));
    }

    [Fact]
    public void RequestIdsRefuseExhaustion()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Step(2);
        var entity = kiln.Entities.Create(new WorldPosition(1, 0, 1));
        var model = CreatureControlFake.ModelOf(kiln);
        CreatureControlAbi.BodyKey key = default;
        Assert.Equal(FakeTables.Ok, model.Bind(77, entity.Value, 0, 1, &key));
        var q = new CreatureControlAbi.Request { Size = (uint)sizeof(CreatureControlAbi.Request), Body = key,
            Role = CreatureControlAbi.PetFollow, RequestId = ulong.MaxValue };
        CreatureControlAbi.State s = default;
        s.Size = (uint)sizeof(CreatureControlAbi.State);
        Assert.Equal(FakeTables.Ok, model.Acquire(77, &q, &s));
        ulong id = 5;
        Assert.Equal(FakeTables.BufferTooSmall, model.NextRequest(77, &id));
        Assert.Equal(5ul, id);
    }

    private static string Reason(CreatureControlAbi.State* state)
    {
        var n = 0;
        while (n < 160 && state->Reason[n] != 0) ++n;
        return System.Text.Encoding.UTF8.GetString(state->Reason, n);
    }

    private static CreatureControlAbi.Table* T => (CreatureControlAbi.Table*)KitBackend.Instance.Service(CreatureControlAbi.Service, 1);

    [Fact]
    public void TheTableIsTheHeadersV1()
    {
        Assert.Equal(24, sizeof(CreatureControlAbi.BodyKey));
        Assert.Equal(48, sizeof(CreatureControlAbi.Request));
        Assert.Equal(248, sizeof(CreatureControlAbi.State));
        Assert.True(T != null);
        Assert.Equal((uint)sizeof(CreatureControlAbi.Table), ((uint*)T)[0]);  // header: size, version
        Assert.Equal(1u, ((uint*)T)[1]);
        Assert.Contains(Coverage.All, r => r.Capabilities.Contains("creature_control") && r.Kind == CoverageKind.RecordingFake);
    }

    [Fact]
    public void ALeaseIsPendingThenActiveAndReleasedAfterTheRestoreSteps()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Step(2);
        var e = kiln.Entities.Create(new WorldPosition(1, 0, 1));
        const ulong owner = 77;
        // out records in native memory: a lambda run on the tick thread may not take a local's address
        var mem = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(512);
        var keyp = (CreatureControlAbi.BodyKey*)mem;
        var idp = (ulong*)(mem + 32);
        var qp = (CreatureControlAbi.Request*)(mem + 64);
        var sp = (CreatureControlAbi.State*)(mem + 128);
        var drive = (uint*)(mem + 400);
        try
        {
            Assert.Equal(FakeTables.Ok, kiln.Run(() => T->BindBody(owner, e.Value, 0, 1, keyp)));
            Assert.Equal(FakeTables.InvalidArgument, kiln.Run(() => T->BindBody(owner, e.Value, 0, 0, keyp)));  // no consent to retire
            Assert.Equal(FakeTables.WrongThread, T->BindBody(owner, e.Value, 0, 1, keyp));                     // not the tick thread
            Assert.Equal(FakeTables.Ok, kiln.Run(() => T->NextRequest(owner, idp)));
            *qp = new CreatureControlAbi.Request { Size = (uint)sizeof(CreatureControlAbi.Request), Role = CreatureControlAbi.Ridden, Body = *keyp, RequestId = *idp };
            sp->Size = (uint)sizeof(CreatureControlAbi.State);
            Assert.Equal(FakeTables.Ok, kiln.Run(() => T->AcquireControl(owner, qp, sp)));
            Assert.Equal(CreatureControlAbi.Pending, sp->LeaseState);
            var lease = sp->Lease;
            var key = *keyp;
            *drive = 9;
            Assert.Equal(FakeTables.Ok, T->CanDrive(owner, lease, key, 1, drive));  // any thread
            Assert.Equal(0u, *drive);
            kiln.Step(2);
            Assert.Equal(FakeTables.Ok, T->QueryControl(owner, lease, sp));
            Assert.Equal((CreatureControlAbi.Active, 1ul), (sp->LeaseState, sp->TransitionSequence));
            T->CanDrive(owner, lease, key, 1, drive);
            Assert.Equal(1u, *drive);
            T->CanDrive(owner + 1, lease, key, 1, drive);
            Assert.Equal(0u, *drive);
            Assert.Equal(FakeTables.Ok, kiln.Run(() => T->ReleaseControl(owner, lease)));
            T->CanDrive(owner, lease, key, 1, drive);
            Assert.Equal(0u, *drive);
            kiln.Step(3);
            T->QueryControl(owner, lease, sp);
            Assert.NotEqual(CreatureControlAbi.Released, sp->LeaseState);
            kiln.Step();
            T->QueryControl(owner, lease, sp);
            Assert.Equal((CreatureControlAbi.Released, CreatureControlAbi.ResultReleased), (sp->LeaseState, sp->Result));
            Assert.Equal(FakeTables.Ok, kiln.Run(() => T->ReleaseBody(owner, key)));
            Assert.Equal(FakeTables.NotFound, T->Quarantine(key, 1));  // the body is unbound now
            Assert.Equal(["bind_body", "bind_body", "next_request", "acquire_control", "release_control", "release_body"],
                kiln.CreatureControl.Calls.Select(c => c.Function));
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(mem);
        }
    }
}
