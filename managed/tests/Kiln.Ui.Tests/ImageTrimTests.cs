// SPDX-License-Identifier: MIT
// Trimmed and part-of-image draws (0.2.0-dev.29.16): UiDraw.ItemIconTrimmed, ImageTrimmed / ImageButtonTrimmed, ImagePart
// (UVs), RenderDraw's trimmed quad (ImageTrim), and kiln.ui's image_ex tail layout. A fake kiln.ui table records what reaches
// the native side; no game.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln;

[Collection(FakeUi.UiSerial.Name)]
public sealed unsafe class ImageTrimTests : IDisposable
{
    private static readonly Guid Logs = Guid.Parse("3f4093ae-75ed-4e5d-bd0b-784ba83b88f5");

    private record struct ExCall(string Id, ulong Image, uint Flags, float Width, float Height, float U0, float V0, float U1, float V1);

    private static readonly List<ExCall> ExCalls = [];
    private static readonly List<(ulong Image, float Width, float Height)> Plain = [];
    private static bool _clicks;
    private static UiNative.TableImageEx* _tail;
    private readonly List<int> _buckets = [];
    private ulong _next = 100;

    public ImageTrimTests()
    {
        FakeUi.FakeUiTable.Install();
        Backend.Current = new FakeUi.FakeUiTable.Backend();
        UiNative.ResetForTests();
        FakeUi.FakeUiTable.Table->Base.Base.Base.Image = &PlainImage;
        ExCalls.Clear();
        Plain.Clear();
        _clicks = false;
        ItemIcons.ResetForTests();
        ItemIcons.Synchronous = true;
        ItemIcons.Session = () => 1;
        ItemIcons.GameItem = id => id.Value == NewContent.IdOf(Logs) ? Logs : null;
        ItemIcons.Defined = _ => null;
        ItemIcons.Decode = (g, bucket) =>
        {
            _buckets.Add(bucket);
            return (new byte[bucket * bucket * 4], bucket, bucket);
        };
        ItemIcons.Upload = (_, _, _) => ++_next;
        ItemIcons.Free = _ => { };
        ItemIcons.ImageState = _ => UiNative.ImageReady;
        _tail = (UiNative.TableImageEx*)NativeMemory.AllocZeroed((nuint)sizeof(UiNative.TableImageEx));
        _tail->ImageEx = &ImageEx;
        _tail->ImageOpaqueRect = &OpaqueRect;
        UiNative.ImageExOverride = _tail;
    }

    public void Dispose()
    {
        ItemIcons.ResetForTests();
        UiNative.ResetForTests();
        NativeMemory.Free(_tail);
        _tail = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ImageEx(NativeStr id, ulong image, UiNative.ImageDraw* d)
    {
        // no assertion here (an exception must not cross back into the caller): a wrong size shows as its own id
        ExCalls.Add(new ExCall(d->Size == (uint)sizeof(UiNative.ImageDraw) ? id.ToString() : "bad size", image, d->Flags, d->Width, d->Height, d->U0, d->V0, d->U1, d->V1));
        return _clicks && id.Len > 0 ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OpaqueRect(ulong image, uint* x, uint* y, uint* w, uint* h)
    {
        if (image != 7) return 0;
        (*x, *y, *w, *h) = (16, 8, 32, 48);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PlainImage(ulong image, float w, float h) => Plain.Add((image, w, h));

    private static string Header([CallerFilePath] string source = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(source) ?? "."); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "native", "core", "abi", "services", "kiln_ui.h");
            if (File.Exists(f)) return f;
        }
        throw new InvalidOperationException("kiln_ui.h not found above the test's source");
    }

    private static UiDraw NewUi() => new() { Frame = new UiNative.Frame { DpiScale = 1 } };

    private static ItemId Item => new(NewContent.IdOf(Logs));

    /// <summary>The icon loaded (the first draw shows the placeholder while it reads).</summary>
    private static void Warm(UiDraw ui, bool trim)
    {
        if (trim) ui.ItemIconTrimmed(Item, 40);
        else ui.ItemIcon(Item, 40);
        ExCalls.Clear();
        Plain.Clear();
    }

    [Fact]
    public void A_trimmed_item_icon_asks_kiln_ui_for_its_art_fitted_in_the_box()
    {
        var ui = NewUi();
        Warm(ui, trim: true);
        ui.ItemIconTrimmed(Item, 40);
        var c = Assert.Single(ExCalls);
        Assert.Equal(UiNative.ImageDrawTrim | UiNative.ImageDrawFit, c.Flags);
        Assert.Equal((40f, 40f), (c.Width, c.Height));
        Assert.Equal("", c.Id);  // an image, not a button
        Assert.NotEqual(ItemIcons.Placeholder(), c.Image);
        Assert.Empty(Plain);
        // read sharper than its box: the art is magnified out of its margin (40 px -> the 128 level)
        Assert.Equal([128], _buckets);
    }

    [Fact]
    public void An_untrimmed_item_icon_draws_as_before()
    {
        var ui = NewUi();
        Warm(ui, trim: false);
        ui.ItemIcon(Item, 40);
        ui.ItemIcon(Item, 40);  // drawn again: the same
        Assert.Empty(ExCalls);
        Assert.Equal(2, Plain.Count);
        Assert.All(Plain, p => Assert.Equal((40f, 40f), (p.Width, p.Height)));
        Assert.Equal([64], _buckets);
    }

    [Fact]
    public void The_placeholder_is_drawn_trimmed_too_and_fills_the_box()
    {
        var ui = NewUi();
        ui.ItemIconTrimmed(Item, 32);  // still loading on this draw
        var c = Assert.Single(ExCalls);
        Assert.Equal(ItemIcons.Placeholder(), c.Image);
        Assert.Equal((32f, 32f), (c.Width, c.Height));
    }

    [Fact]
    public void Without_the_image_ex_tail_a_trimmed_icon_falls_back_to_the_whole_picture()
    {
        UiNative.ImageExOverride = null;  // the fake kiln.ui is a v4 table: no image_ex
        var ui = NewUi();
        Warm(ui, trim: true);
        ui.ItemIconTrimmed(Item, 40);
        Assert.Empty(ExCalls);
        Assert.Equal((40f, 40f), (Plain.Single().Width, Plain.Single().Height));
    }

    [Fact]
    public void Images_and_buttons_take_trim_and_uvs()
    {
        var ui = NewUi();
        var mine = new UiImage { Handle = 9, Width = 64, Height = 32 };
        ui.ImageTrimmed(mine, 40, 20);
        ui.Image(mine, 40, 20);
        ui.ImagePart(mine, 0, 0, 0.5f, 0, 1, 0.5f);       // the top right quarter at its own size
        ui.ImagePart(mine, 10, 10, 0, 0, 0, 0);       // all 0: the whole image
        _clicks = true;
        Assert.True(ui.ImageButtonTrimmed("pick", mine, 24, 24));
        Assert.Equal(4, ExCalls.Count);
        Assert.Equal(new ExCall("", 9, UiNative.ImageDrawTrim | UiNative.ImageDrawFit, 40, 20, 0, 0, 0, 0), ExCalls[0]);
        Assert.Equal((9ul, 40f, 20f), Plain.Single());  // untrimmed: image as it always was
        Assert.Equal(new ExCall("", 9, 0, 32, 16, 0.5f, 0, 1, 0.5f), ExCalls[1]);
        Assert.Equal(new ExCall("", 9, 0, 10, 10, 0, 0, 1, 1), ExCalls[2]);
        Assert.Equal(new ExCall("pick", 9, UiNative.ImageDrawTrim | UiNative.ImageDrawFit, 24, 24, 0, 0, 0, 0), ExCalls[3]);
    }

    [Fact]
    public void An_item_image_drawn_trimmed_goes_through_the_icon_cache()
    {
        var ui = NewUi();
        var img = ItemIcons.View(Item)!;  // what Ui.ItemImage hands out
        ui.ImageTrimmed(img, 40, 40);
        ui.ImageTrimmed(img, 40, 40);
        Assert.Equal(2, ExCalls.Count);
        Assert.All(ExCalls, c => Assert.Equal(UiNative.ImageDrawTrim | UiNative.ImageDrawFit, c.Flags));
        Assert.NotEqual(ExCalls[0].Image, ExCalls[1].Image);  // the placeholder, then the icon
        Assert.Equal([128], _buckets);
    }

    [Fact]
    public void Opaque_rect_reads_kiln_ui()
    {
        Assert.True(UiNative.ImageOpaqueRect(7, out var x, out var y, out var w, out var h));
        Assert.Equal((16u, 8u, 32u, 48u), (x, y, w, h));
        Assert.False(UiNative.ImageOpaqueRect(8, out _, out _, out _, out _));
        UiNative.ImageExOverride = null;
        Assert.False(UiNative.ImageOpaqueRect(7, out _, out _, out _, out _));
    }

    [Fact]
    public void A_trimmed_quad_samples_the_art_and_keeps_its_shape()
    {
        // 32 x 48 of art at (16, 8) in a 64 x 64 icon, into a 40 x 40 rectangle: 26.67 x 40, centred
        var q = ImageTrim.Fit(16, 8, 32, 48, 64, 64, 100, 200, 140, 240);
        Assert.Equal(0.25f, q.U0, 4);
        Assert.Equal(0.125f, q.V0, 4);
        Assert.Equal(0.75f, q.U1, 4);
        Assert.Equal(0.875f, q.V1, 4);
        Assert.Equal(100 + 20 / 3f, q.X0, 3);
        Assert.Equal(200f, q.Y0, 3);
        Assert.Equal(140 - 20 / 3f, q.X1, 3);
        Assert.Equal(240f, q.Y1, 3);
        // a rectangle given right to left is the same box
        var r = ImageTrim.Fit(16, 8, 32, 48, 64, 64, 140, 240, 100, 200);
        Assert.Equal(q, r);
        // nothing sensible to trim to: the whole image over the rectangle
        Assert.Equal(ImageTrim.Whole(0, 0, 10, 10), ImageTrim.Fit(0, 0, 0, 0, 64, 64, 0, 0, 10, 10));
        Assert.Equal(ImageTrim.Whole(0, 0, 10, 10), ImageTrim.Fit(60, 0, 10, 10, 64, 64, 0, 0, 10, 10));
        Assert.Equal(ImageTrim.Whole(0, 0, 10, 10), ImageTrim.Fit(0, 0, 4, 4, 0, 0, 0, 0, 10, 10));
        // the art is the whole image: the whole rectangle
        var all = ImageTrim.Fit(0, 0, 64, 64, 64, 64, 0, 0, 32, 32);
        Assert.Equal(new ImageTrim.Quad(0, 0, 32, 32, 0, 0, 1, 1), all);
    }

    [Fact]
    public void The_image_ex_tail_follows_the_looks()
    {
        Assert.Equal(992, sizeof(UiNative.TableLooks));
        Assert.Equal(992, (int)Marshal.OffsetOf<UiNative.TableImageEx>(nameof(UiNative.TableImageEx.ImageEx)));
        Assert.Equal(1000, (int)Marshal.OffsetOf<UiNative.TableImageEx>(nameof(UiNative.TableImageEx.ImageOpaqueRect)));
        Assert.Equal(1008, sizeof(UiNative.TableImageEx));
        Assert.Equal(32, sizeof(UiNative.ImageDraw));
        Assert.Equal(8, (int)Marshal.OffsetOf<UiNative.ImageDraw>(nameof(UiNative.ImageDraw.Width)));
        Assert.Equal(16, (int)Marshal.OffsetOf<UiNative.ImageDraw>(nameof(UiNative.ImageDraw.U0)));
        // kiln_ui.h says the same
        var h = File.ReadAllText(Header());
        Assert.Contains("KILN_UI_IMAGE_DRAW_TRIM = 1u", h);
        Assert.Contains("KILN_UI_IMAGE_DRAW_FIT = 2u", h);
    }
}
