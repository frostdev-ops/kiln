// SPDX-License-Identifier: MIT
// World.SetTransformScale (0.2.0-dev.28) end to end on the kit's fake world: the place of the scale comes from the kit's
// type table (the layouts of the Kiln.Game beside the tests), the write is one tracked World.WriteAt of 12 bytes. The
// sequence of chapter 13 §13b.6 offline: every CurrentTransform byte recorded, Scale3(1.25, 0.75, 1.5) written, the 12
// target bytes equal, every other byte unchanged, one event, an unchanged write with none, the original restored, and the
// refusals (no writes declaration, an entity that went) changing nothing.
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class TransformScaleKitTests
{
    private sealed class ScaleMod : Mod;

    private sealed class NoWritesMod : Mod;

    private static byte[] Bytes(Scale3 s) => MemoryMarshal.AsBytes(new ReadOnlySpan<Scale3>(in s)).ToArray();

    [Fact]
    public void The_scale_vector_is_written_alone_restored_and_refused_without_its_declaration()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<ScaleMod>(new ModManifest("scale-kit").Writes("CurrentTransform"));
        var plain = kiln.Load<NoWritesMod>(new ModManifest("no-writes-kit"));
        kiln.Step(2);
        // the row is reported at Kiln.World's first use, as world.tracked_writes is
        Assert.True(kiln.Run(() => World.SupportsTrackedWrites));
        Assert.True(kiln.Run(() => Capabilities.Is(World.TransformScaleCapability)), kiln.Run(() => Capabilities.Why(World.TransformScaleCapability)));
        Assert.Contains("transform.scale at +40", kiln.Run(() => Capabilities.Get(World.TransformScaleCapability).ReasonText));

        var e = kiln.Entities.Create();
        var original = Enumerable.Range(0, 56).Select(i => (byte)(i * 3 + 1)).ToArray();
        Bytes(Scale3.One).CopyTo(original, 40);
        kiln.Entities.SetBytes(e, "CurrentTransform", original);
        var seen = new List<EntityScaleChangedEvent>();
        using var sub = kiln.Run(() => Events.On<EntityScaleChangedEvent>(ev => seen.Add(ev)));

        var asked = new Scale3(1.25f, 0.75f, 1.5f);
        var t = kiln.As(mod, () => World.SetTransformScale(e, asked));
        kiln.StepUntil(() => t.IsCompleted && seen.Count > 0, maxSteps: 20);
        var r = t.Result;
        Assert.True(r.Ok, r.Reason);
        Assert.Equal((WorldWriteStatus.Written, true), (r.WorldWriteStatus, r.Changed));
        Assert.Equal((Scale3.One, asked, asked), (r.Before!.Value, r.Now!.Value, r.Requested));
        var after = kiln.Entities.GetBytes(e, "CurrentTransform")!;
        Assert.Equal(Bytes(asked), after[40..52]);
        Assert.Equal(original[..40], after[..40]);   // position and orientation: every non-scale byte unchanged
        Assert.Equal(original[52..], after[52..]);
        var ev = Assert.Single(seen);
        Assert.Equal((e, Scale3.One, asked, "scale-kit"), (ev.Entity, ev.Before, ev.Now, ev.ModId));

        // the same scale again: unchanged, no event
        var same = kiln.As(mod, () => World.SetTransformScale(e, asked));
        kiln.StepUntil(() => same.IsCompleted, maxSteps: 20);
        kiln.Step(2);
        Assert.Equal((true, WorldWriteStatus.Unchanged, false), (same.Result.Ok, same.Result.WorldWriteStatus, same.Result.Changed));
        Assert.Single(seen);

        // restored through the same method
        var back = kiln.As(mod, () => World.SetTransformScale(e, Scale3.One));
        kiln.StepUntil(() => back.IsCompleted && seen.Count > 1, maxSteps: 20);
        Assert.True(back.Result.Ok);
        Assert.Equal(original, kiln.Entities.GetBytes(e, "CurrentTransform"));
        Assert.Equal(2, seen.Count);

        // refusals change nothing: no "writes" declaration, an entity that went
        var undeclared = kiln.As(plain, () => World.SetTransformScale(e, asked));
        kiln.StepUntil(() => undeclared.IsCompleted, maxSteps: 20);
        Assert.Equal((false, WorldWriteStatus.NotDeclared), (undeclared.Result.Ok, undeclared.Result.WorldWriteStatus));
        Assert.Contains("CurrentTransform", undeclared.Result.Reason);
        Assert.Null(undeclared.Result.Now);
        Assert.Equal(original, kiln.Entities.GetBytes(e, "CurrentTransform"));
        var gone = kiln.Entities.Create();
        kiln.Entities.SetBytes(gone, "CurrentTransform", original);
        kiln.Entities.Destroy(gone);
        var late = kiln.As(mod, () => World.SetTransformScale(gone, asked));
        kiln.StepUntil(() => late.IsCompleted, maxSteps: 20);
        Assert.False(late.Result.Ok);
        kiln.Step(2);
        Assert.Equal(2, seen.Count);
    }
}
