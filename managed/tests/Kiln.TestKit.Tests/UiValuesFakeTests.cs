// SPDX-License-Identifier: MIT
// The kit's fake kiln.ui.values (Fakes/UiValues/) on its raw table: registration, the providers run in order over the
// accumulated value with the kinds' masks, tooltip lines with the 16-entry limit, the rows a test switches off.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed unsafe class UiValuesFakeTests
{
    private static UiValuesAbi.Table* T => (UiValuesAbi.Table*)KitBackend.Instance.Service(UiValuesAbi.Service, 1);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Quarter(UiValuesAbi.ProviderContext* c, UiValuesAbi.ItemView* v, UiValuesAbi.BarValue* a, UiValuesAbi.BarEdit* e, void* u)
    {
        if (v->FuelMax == 0) return 0;  // pass
        e->Flags = 1 | 2;               // fraction, current
        e->Fraction = 0.25f;
        e->Current = a->Current + 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TooBig(UiValuesAbi.ProviderContext* c, UiValuesAbi.ItemView* v, UiValuesAbi.BarValue* a, UiValuesAbi.BarEdit* e, void* u)
    {
        e->Flags = 2;
        e->Current = 0x8000_0000;  // above Int32.MaxValue: invalid for a detail number
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Lines(UiValuesAbi.ProviderContext* c, UiValuesAbi.ItemView* v, UiValuesAbi.LineWriter* w, void* u)
    {
        var n = (int)(nint)u;
        for (var i = 0; i < n; i++)
        {
            var id = UiValuesFake.TextId(UiValuesFake.State!, $"line {i + 1}");  // what intern stores
            if (w->Append(w, id) != 0) return 0;  // the writer has latched the overflow: the fake drops these lines
        }
        return 0;
    }

    private static ulong Add(uint kind, uint item, delegate* unmanaged[Cdecl]<UiValuesAbi.ProviderContext*, UiValuesAbi.ItemView*, UiValuesAbi.BarValue*, UiValuesAbi.BarEdit*, void*, int> fn)
    {
        var r = new UiValuesAbi.Registration { Size = (uint)sizeof(UiValuesAbi.Registration) };
        var rc = T->Subscribe(77, kind, item, fn, null, &r);
        Assert.Equal(0, rc);
        return r.Handle;
    }

    [Fact]
    public void TheTableIsTheHeadersV1()
    {
        Assert.Equal(120, sizeof(UiValuesAbi.ItemView));
        Assert.Equal(32, sizeof(UiValuesAbi.BarValue));
        Assert.Equal(56, sizeof(UiValuesAbi.ProviderContext));
        Assert.Equal(288, sizeof(UiValuesAbi.LineWriter));
        Assert.Equal(88, sizeof(UiValuesAbi.Table));
        Assert.True(T != null);
        Assert.Contains(Coverage.All, r => r.Capabilities.Contains("kiln.ui.values") && r.Kind == CoverageKind.RecordingFake);
    }

    [Fact]
    public void ProvidersRunInOrderOverTheAccumulatedValue()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Run(() =>
        {
            Add(0, 0, &Quarter);
            Add(0, 0, &Quarter);
            Add(0, 0, &TooBig);
            return 0;
        });
        var bar = kiln.UiValues.Draw(0, new FakeUiItem(0xAA, Fuel: 3200, FuelMax: 6400), new FakeUiBar(1, 10, 100));
        Assert.Equal(new FakeUiBar(0.25f, 12, 100), bar);  // two increments, the invalid edit dropped
        Assert.Equal([false, false, true], kiln.UiValues.Decisions.Select(d => d.Invalid));
        var pass = kiln.UiValues.Draw(0, new FakeUiItem(0xAA), new FakeUiBar(1, 10, 100));
        Assert.Equal(new FakeUiBar(1, 10, 100), pass);  // no fuel: both pass, the invalid one still refused
        Assert.Equal(new FakeUiBar(1, 0, 0), kiln.UiValues.Draw(1, new FakeUiItem(0xAA, FuelMax: 1), new FakeUiBar(1, 0, 0)));
        Assert.Equal(3, kiln.UiValues.Registrations);
    }

    [Fact]
    public void TooltipLinesStopAtSixteenAndAProviderThatDoesNotFitAddsNone()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Run(() =>
        {
            var r = new UiValuesAbi.Registration { Size = (uint)sizeof(UiValuesAbi.Registration) };
            Assert.Equal(0, T->TooltipLines(77, 0, (delegate* unmanaged[Cdecl]<UiValuesAbi.ProviderContext*, UiValuesAbi.ItemView*, UiValuesAbi.LineWriter*, void*, int>)&Lines, (void*)10, &r));
            Assert.Equal(0, T->TooltipLines(77, 0, (delegate* unmanaged[Cdecl]<UiValuesAbi.ProviderContext*, UiValuesAbi.ItemView*, UiValuesAbi.LineWriter*, void*, int>)&Lines, (void*)7, &r));
            Assert.Equal(0, T->TooltipLines(77, 0, (delegate* unmanaged[Cdecl]<UiValuesAbi.ProviderContext*, UiValuesAbi.ItemView*, UiValuesAbi.LineWriter*, void*, int>)&Lines, (void*)6, &r));
            return 0;
        });
        var lines = kiln.UiValues.DetailLines(new FakeUiItem(0xAA));
        Assert.Equal(16, lines.Count);  // 10, then 7 does not fit (dropped), then 6
        Assert.Equal("line 10", lines[9]);
        Assert.Equal("line 1", lines[10]);
    }

    [Fact]
    public void ARowSwitchedOffRefusesRegistration()
    {
        using var kiln = new KilnHarness();
        kiln.Capabilities.Override("ui.values.held", false, "switched off by the test");
        kiln.Boot();
        var rc = kiln.Run(() =>
        {
            var r = new UiValuesAbi.Registration { Size = (uint)sizeof(UiValuesAbi.Registration) };
            return T->Subscribe(77, 2, 0, (void*)(delegate* unmanaged[Cdecl]<UiValuesAbi.ProviderContext*, UiValuesAbi.ItemView*, UiValuesAbi.BarValue*, UiValuesAbi.BarEdit*, void*, int>)&Quarter, null, &r);
        });
        Assert.Equal(FakeTables.Unavailable, rc);
        var none = kiln.Run(() =>
        {
            uint required = 0;
            return T->CopySlots(77, 0, 0, null, null, 0, &required);
        });
        Assert.Equal(FakeTables.Unavailable, none);  // not modelled: unavailable, never "ok"
    }
}
