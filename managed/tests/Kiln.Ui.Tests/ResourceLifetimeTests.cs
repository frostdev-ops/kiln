// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using Xunit;

namespace Kiln.Ui.Tests;

[Collection("bus")]
public class ResourceLifetimeTests
{
    private static ModContext NewMod() => HostAccess.CreateContext(
        new ModInfo("render-lifetime-" + Guid.NewGuid().ToString("N"), "Lifetime test", "1.0.0", Path.GetTempPath()),
        0, Path.GetTempPath());

    private static void As(ModContext mod, Action action)
    {
        var previous = HostAccess.Current;
        HostAccess.Current = mod;
        try { action(); }
        finally { HostAccess.Current = previous; }
    }

    [Fact]
    public void LateImagesAreDisposedWhenTheModHasAlreadyRetired()
    {
        var mod = NewMod();
        mod.Retire();
        var image = new UiImage { Handle = 1, Owner = mod };
        Assert.Throws<InvalidOperationException>(() => UiEngine.TrackImage(image));
        Assert.Equal(0ul, image.Handle);
    }

    [Fact]
    public void LateRegistrationsCannotRetainARetiredMod()
    {
        var mod = NewMod();
        mod.Retire();
        As(mod, () =>
        {
            Assert.Throws<InvalidOperationException>(() => UiEngine.AddHud("late", HudAnchor.TopLeft));
            Assert.Throws<InvalidOperationException>(() => UiEngine.AddConfigPage("late", _ => { }));
            Assert.Throws<InvalidOperationException>(() => UiEngine.FrameHandlers.Add(_ => { }));
            Assert.Throws<InvalidOperationException>(() => UiEngine.AddWindow(new UiWindow("late", _ => { }, new(), mod)));
            Assert.Throws<InvalidOperationException>(() => UiEngine.AddScreen(new UiScreen("late", _ => { }, new(), mod)));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference TrackAndDispose(ModContext mod)
    {
        var image = new UiImage { Handle = 1, Owner = mod };
        UiEngine.TrackImage(image);
        image.Dispose();
        return new WeakReference(image);
    }

    [Fact]
    public void RepeatedLoadsDropDisposedImageRecords()
    {
        var mod = NewMod();
        try
        {
            var weak = TrackAndDispose(mod);
            UiEngine.TrackImage(new UiImage { Handle = 2, Owner = mod });
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(weak.IsAlive);
        }
        finally { mod.Retire(); }
    }

    [Fact]
    public void ImagesAndFrameHandlersRacingUnloadAreCleanedUp()
    {
        for (var i = 0; i < 100; i++)
        {
            var mod = NewMod();
            var image = new UiImage { Handle = 1, Owner = mod };
            Parallel.Invoke(
                () => As(mod, () =>
                {
                    try { UiEngine.TrackImage(image); }
                    catch (InvalidOperationException) { }
                    try { UiEngine.FrameHandlers.Add(_ => { }); }
                    catch (InvalidOperationException) { }
                }),
                mod.Retire);
            Assert.Equal(0ul, image.Handle);
            Assert.DoesNotContain(UiEngine.FrameHandlers.Snapshot(), s => ReferenceEquals(s.Owner, mod));
        }
    }
}
