// SPDX-License-Identifier: MIT
// Ui.Values against a fake kiln.ui.values table: the error rule (ArgumentException, KilnUnavailableException, a refused
// registration as a result), the bridge from a native provider call to a managed provider and back (edits, labels,
// refusals, faults), and the events made from the native change records. The native publication rules themselves
// (Added/Updated/Removed after two absent frames, one Reset per session change, eviction order) are the host test's:
// native/core/tests/ui_values_test.cpp.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln;
using Kiln.FakeUi;
using Xunit;

namespace UiValuesTestSuite;

internal static unsafe class FakeUiValues
{
    public static UiValuesNative.Table* Table;
    public static UiValuesNative.TextOps* Ops;
    public static int SubscribeRc;
    public static uint SubscribeOk = 1, SubscribeReason;
    public static readonly List<(ulong Owner, uint Kind, uint Item, nint Fn, nint User)> Subscribed = [];
    public static readonly List<ulong> Unsubscribed = [];
    public static nint ChangeFn;
    public static ulong ChangeOwner;
    public static ulong NextHandle = 100;
    public static readonly Dictionary<ulong, string> Texts = [];
    public static int InternRc;
    public static UiValuesNative.SlotsFrame SlotsMeta;
    public static UiValuesNative.VisibleSlot[] SlotList = [];
    public static readonly List<(ulong Session, ulong Frame, uint Capacity)> Copies = [];
    public static nint SlotsFn;
    public static nint LinesFn;
    public static nint LinesUser;
    public static uint OpenOk;
    public static int Opens, Closes;
    public static nint SlotsUser;

    public static void Install()
    {
        Free();
        Table = (UiValuesNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(UiValuesNative.Table));
        Table->Header = new ServiceHeader { Size = (uint)sizeof(UiValuesNative.Table), Version = 1 };
        Table->Subscribe = &Subscribe;
        Table->Unsubscribe = &Unsubscribe;
        Table->SubscribeChanges = &SubscribeChanges;
        Table->CopySlots = &CopySlots;
        Table->TooltipLines = &TooltipLines;
        Table->OpenTooltipDetails = &OpenDetails;
        Table->CloseTooltipDetails = &CloseDetails;
        Table->GetTooltipDetails = &GetDetails;
        Table->RefreshTooltipDetails = &OpenDetails;
        Table->SubscribeSlots = &SubscribeSlots;
        Ops = (UiValuesNative.TextOps*)NativeMemory.AllocZeroed((nuint)sizeof(UiValuesNative.TextOps));
        Ops->Size = (uint)sizeof(UiValuesNative.TextOps);
        Ops->Version = 1;
        Ops->Intern = &Intern;
        Ops->Resolve = &Resolve;
        SubscribeRc = 0;
        SubscribeOk = 1;
        SubscribeReason = 0;
        InternRc = 0;
        ChangeFn = 0;
        ChangeOwner = 0;
        lock (Subscribed) Subscribed.Clear();
        lock (Unsubscribed) Unsubscribed.Clear();
        lock (Texts) Texts.Clear();
    }

    public static void Free()
    {
        if (Table != null) NativeMemory.Free(Table);
        if (Ops != null) NativeMemory.Free(Ops);
        Table = null;
        Ops = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Subscribe(ulong owner, uint kind, uint item,
        delegate* unmanaged[Cdecl]<UiValuesNative.ProviderContext*, UiValuesNative.ItemView*, UiValuesNative.BarValue*, UiValuesNative.BarEdit*, void*, int> fn,
        void* user, UiValuesNative.Registration* r)
    {
        lock (Subscribed) Subscribed.Add((owner, kind, item, (nint)fn, (nint)user));
        r->Ok = SubscribeRc == 0 ? SubscribeOk : 0;
        r->Reason = SubscribeReason;
        r->Handle = SubscribeRc == 0 && SubscribeOk != 0 ? NextHandle++ : 0;
        return SubscribeRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unsubscribe(ulong h)
    {
        lock (Unsubscribed) Unsubscribed.Add(h);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SubscribeChanges(ulong owner, delegate* unmanaged[Cdecl]<UiValuesNative.ValueChange*, void*, void> fn,
        void* user, UiValuesNative.Registration* r)
    {
        ChangeFn = (nint)fn;
        ChangeOwner = owner;
        r->Ok = 1;
        r->Handle = NextHandle++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CopySlots(ulong owner, ulong session, ulong frame, UiValuesNative.SlotsFrame* f, UiValuesNative.VisibleSlot* s,
        uint capacity, uint* required)
    {
        lock (Copies) Copies.Add((session, frame, capacity));
        var size = f->Size;
        *f = SlotsMeta;
        f->Size = size;
        *required = SlotsMeta.Count;
        if (capacity < SlotsMeta.Count) return UiValuesNative.BufferTooSmall;
        for (var i = 0; i < SlotsMeta.Count; i++) s[i] = SlotList[i];
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TooltipLines(ulong owner, uint item,
        delegate* unmanaged[Cdecl]<UiValuesNative.ProviderContext*, UiValuesNative.ItemView*, UiValuesNative.LineWriter*, void*, int> fn,
        void* user, UiValuesNative.Registration* r)
    {
        LinesFn = (nint)fn;
        LinesUser = (nint)user;
        r->Ok = 1;
        r->Handle = NextHandle++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OpenDetails(ulong owner, UiValuesNative.TooltipOpen* o)
    {
        Opens++;
        o->Ok = OpenOk;
        o->Reason = OpenOk != 0 ? 0 : UiValuesNative.ReasonNoTooltip;
        o->Session = 7;
        o->SourceFrame = 1201;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CloseDetails(ulong owner)
    {
        Closes++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetDetails(ulong owner, uint which, UiValuesNative.TooltipDetails* d)
    {
        d->Ok = OpenOk;
        d->Item = 0xAA;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SubscribeSlots(ulong owner, delegate* unmanaged[Cdecl]<UiValuesNative.SlotsFrame*, UiValuesNative.VisibleSlot*, uint, void*, void> fn,
        void* user, UiValuesNative.Registration* r)
    {
        SlotsFn = (nint)fn;
        SlotsUser = (nint)user;
        r->Ok = 1;
        r->Handle = NextHandle++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Intern(UiValuesNative.ProviderContext* c, NativeStr s, ulong* id)
    {
        if (InternRc != 0) return InternRc;
        lock (Texts)
        {
            var text = s.ToString();
            var n = (ulong)Texts.Count + 0x5554_0000_0000_0001;
            Texts[n] = text;
            *id = n;
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Resolve(UiValuesNative.ProviderContext* c, ulong id, NativeStr* out_)
    {
        lock (Texts)
            if (!Texts.TryGetValue(id, out _)) return 2;
        return 2;  // tests resolve through FakeUiValues.Texts; the bridge only shows the label
    }

    public sealed class Backend(HashSet<string> features) : IKilnBackend
    {
        public readonly List<string> Logs = [];
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => features.Contains(name);
        public void Log(ulong owner, LogLevel level, string text) { lock (Logs) Logs.Add($"{level}: {text}"); }
        public bool TableHidden;
        public nint Service(string name, uint minVersion) => name == UiValuesNative.Service && !TableHidden ? (nint)Table : 0;
        public ulong OwnerCreate(string id) => 99;
    }
}

[Collection(UiSerial.Name)]
public sealed unsafe class UiValuesTests : IDisposable
{
    private readonly IKilnBackend _previous;
    private readonly FakeUiValues.Backend _backend;
    private static readonly string[] AllRows =
        ["ui.values.detail", "ui.values.slot", "ui.values.held", "ui.values.hud", "ui.values.tooltip_lines", "ui.slots.inspect"];

    public UiValuesTests()
    {
        _previous = Backend.Current;
        FakeUiValues.Install();
        _backend = new FakeUiValues.Backend(new HashSet<string>(AllRows));
        Backend.Current = _backend;
        UiValuesNative.ResetForTests();
        UiValueBridge.ResetForTests();
        UiValueEvents.ResetForTests();
        UiSlotsBridge.ResetForTests();
        UiSlotsBridge.Now = () => 10_000;
        UiTooltipBridge.ResetForTests();
        UiTooltipBridge.Viewport = () => (1920, 1080, 1);
        FakeUiValues.OpenOk = 1;
        FakeUiValues.Opens = FakeUiValues.Closes = 0;
        Events.Reset();
    }

    public void Dispose()
    {
        UiTooltipBridge.ResetForTests();
        UiSlotsBridge.Now = () => Environment.TickCount64;
        Events.Reset();
        UiValueBridge.ResetForTests();
        UiValueEvents.ResetForTests();
        Backend.Current = _previous;
        UiValuesNative.ResetForTests();
        FakeUiValues.Free();
    }

    [Fact]
    public void The_native_records_have_the_C_layout()
    {
        Assert.Equal(120, sizeof(UiValuesNative.ItemView));
        Assert.Equal(32, sizeof(UiValuesNative.BarValue));
        Assert.Equal(32, sizeof(UiValuesNative.BarEdit));
        Assert.Equal(56, sizeof(UiValuesNative.ProviderContext));
        Assert.Equal(24, sizeof(UiValuesNative.Registration));
        Assert.Equal(56, sizeof(UiValuesNative.Identity));
        Assert.Equal(200, sizeof(UiValuesNative.ValueChange));
        Assert.Equal(88, sizeof(UiValuesNative.Table));
        Assert.Equal(320, sizeof(UiValuesNative.TooltipDetails));
        Assert.Equal(288, sizeof(UiValuesNative.LineWriter));
        Assert.Equal(112, sizeof(UiValuesNative.VisibleSlot));
        Assert.Equal(120, sizeof(UiValuesNative.SlotsFrame));
    }

    [Fact]
    public void Bad_arguments_throw_ArgumentException()
    {
        Assert.Throws<ArgumentNullException>(() => Ui.Values.ItemBar(new ItemId(1), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ui.Values.Hud((HudValue)7, Pass));
        FakeUiValues.SubscribeRc = UiValuesNative.InvalidArgument;
        Assert.ThrowsAny<ArgumentException>(() => Ui.Values.SlotBar(new ItemId(1), Pass));
    }

    [Fact]
    public void A_capability_that_is_off_throws_KilnUnavailableException_naming_it()
    {
        _backend.Logs.Clear();
        Backend.Current = new FakeUiValues.Backend(["ui.values.detail"]);
        var e = Assert.Throws<KilnUnavailableException>(() => Ui.Values.HeldBar(Pass));
        Assert.Equal("ui.values.held", e.Capability);
        Assert.Empty(FakeUiValues.Subscribed);
        Backend.Current = _backend;
        FakeUiValues.SubscribeRc = UiValuesNative.Unavailable;  // the hooks could not go in
        Assert.Equal("ui.values.hud", Assert.Throws<KilnUnavailableException>(() => Ui.Values.Hud(HudValue.Mana, Pass)).Capability);
    }

    [Fact]
    public void A_table_published_after_a_miss_is_found_at_the_next_use()
    {
        var b = new FakeUiValues.Backend(new HashSet<string>(AllRows)) { TableHidden = true };
        Backend.Current = b;
        UiValuesNative.ResetForTests();
        Assert.Equal("ui.values.detail", Assert.Throws<KilnUnavailableException>(() => Ui.Values.ItemBar(default, Pass)).Capability);
        b.TableHidden = false;  // published later, under the same registry
        Assert.True(Ui.Values.ItemBar(default, Pass).Ok);
    }

    [Fact]
    public void A_refused_registration_is_a_result_and_a_good_one_disposes_once()
    {
        FakeUiValues.SubscribeOk = 0;
        FakeUiValues.SubscribeReason = UiValuesNative.ReasonRegistrationBudget;
        var refused = Ui.Values.ItemBar(new ItemId(7), Pass);
        Assert.False(refused.Ok);
        Assert.Contains("128", refused.Reason);
        Assert.Equal(0ul, refused.Handle);
        Assert.Equal(0, UiValueBridge.Count);
        FakeUiValues.SubscribeOk = 1;
        var ok = Ui.Values.ItemBar(new ItemId(7), Pass);
        Assert.True(ok.Ok);
        Assert.Null(ok.Reason);
        var sub = FakeUiValues.Subscribed[^1];
        Assert.Equal((99ul, 0u, 7u), (sub.Owner, sub.Kind, sub.Item));  // Kiln.Ui's own owner without a mod
        ok.Dispose();
        ok.Dispose();
        Assert.Equal([ok.Handle], FakeUiValues.Unsubscribed);
        Assert.Equal(0, UiValueBridge.Count);
    }

    // ---- the bridge ----
    private static UiProviderDecision Pass(in UiValueContext c, in UiItemView v, UiBarValue a) => UiProviderDecision.Pass;

    private sealed class Call
    {
        public UiValuesNative.BarEdit Edit;
        public int Rc;
    }

    private static Call Invoke(UiValueKind kind, UiValuesNative.BarValue input = default, Action<UiValuesNative.ItemView>? _ = null,
        UiValuesNative.ItemView view = default)
    {
        var sub = FakeUiValues.Subscribed[^1];
        var fn = (delegate* unmanaged[Cdecl]<UiValuesNative.ProviderContext*, UiValuesNative.ItemView*, UiValuesNative.BarValue*, UiValuesNative.BarEdit*, void*, int>)sub.Fn;
        UiValuesNative.ProviderContext c = default;
        c.Size = (uint)sizeof(UiValuesNative.ProviderContext);
        c.Kind = (uint)kind;
        c.Session = 3;
        c.Frame = 501;
        c.ClientStep = 9000;
        c.Text = FakeUiValues.Ops;
        view.Size = (uint)sizeof(UiValuesNative.ItemView);
        input.Size = (uint)sizeof(UiValuesNative.BarValue);
        input.Kind = (uint)kind;
        var call = new Call();
        call.Edit.Size = (uint)sizeof(UiValuesNative.BarEdit);
        fixed (UiValuesNative.BarEdit* e = &call.Edit) call.Rc = fn(&c, &view, &input, e, (void*)sub.User);
        return call;
    }

    [Fact]
    public void A_decision_becomes_the_native_edit()
    {
        UiValueContext seenCtx = default;
        UiItemView seenView = default;
        UiBarValue? seenIn = null;
        Ui.Values.ItemBar(default, (in UiValueContext c, in UiItemView v, UiBarValue a) =>
        {
            seenCtx = c;
            seenView = v;
            seenIn = a;
            return UiProviderDecision.Replace(new UiBarEdit(Fraction: 0.25f, Current: 1_000_098, Max: 2_000_000));
        });
        var view = new UiValuesNative.ItemView
        {
            Flags = 13, LocalPlayer = 77, ClientEntity = 17500, Item = 0xAA, InventoryPage = 0x5000, SlotIndex = 3, Count = 1,
            Fuel = 3200, FuelMax = 6400, UnitsPerCharge = 64, Rarity = 2,
        };
        var call = Invoke(UiValueKind.Detail, new UiValuesNative.BarValue { Fraction = 0.5f, Current = 50, Maximum = 100 }, null, view);
        Assert.Equal(0, call.Rc);
        Assert.Equal(UiValuesNative.EditFraction | UiValuesNative.EditCurrent | UiValuesNative.EditMaximum, call.Edit.Flags);
        Assert.Equal((0.25f, 1_000_098u, 2_000_000u), (call.Edit.Fraction, call.Edit.Current, call.Edit.Maximum));
        Assert.Equal(new UiValueContext(UiValueKind.Detail, 3, 501, 9000), seenCtx);
        Assert.Equal((new EntityId(17500), new ItemId(0xAA), new EntityId(0x5000), (byte)3, 64u, 6400u, EntityId.None),
            (seenView.ClientEntity, seenView.Item, seenView.InventoryPage, seenView.SlotIndex, seenView.UnitsPerCharge,
                seenView.FuelMax, seenView.ServerEntity));
        Assert.Equal((0.5f, 50u, 100u), (seenIn!.Fraction, seenIn.Current, seenIn.Max));
        Assert.Null(seenIn.LabelOverride);
    }

    [Fact]
    public void Labels_set_clear_and_game()
    {
        var label = new UiLabelEdit(UiLabelMode.Set, "Charges");
        Ui.Values.ItemBar(default, (in UiValueContext c, in UiItemView v, UiBarValue a) =>
            UiProviderDecision.Replace(new UiBarEdit(Label: label)));
        var set = Invoke(UiValueKind.Detail);
        Assert.Equal((0, UiValuesNative.EditLabel), (set.Rc, set.Edit.Flags));
        Assert.Equal("Charges", FakeUiValues.Texts[set.Edit.LabelId]);
        label = new UiLabelEdit(UiLabelMode.Clear);
        var clear = Invoke(UiValueKind.Detail);
        Assert.Equal((UiValuesNative.EditLabel, 0ul), (clear.Edit.Flags, clear.Edit.LabelId));
        label = new UiLabelEdit(UiLabelMode.Game);
        Assert.Equal(UiValuesNative.EditGameLabel, Invoke(UiValueKind.Detail).Edit.Flags);
        label = new UiLabelEdit(UiLabelMode.Set);  // no text
        Assert.Equal(UiValuesNative.Internal, Invoke(UiValueKind.Detail).Rc);
        label = new UiLabelEdit(UiLabelMode.Clear, "x");  // a text with Clear
        Assert.Equal(UiValuesNative.Internal, Invoke(UiValueKind.Detail).Rc);
        label = new UiLabelEdit(UiLabelMode.Set, "Charges");
        FakeUiValues.InternRc = UiValuesNative.BufferTooSmall;  // the text budget is used up: a refusal
        var budget = Invoke(UiValueKind.Detail);
        Assert.Equal((UiValuesNative.Unavailable, 0u, UiValuesNative.ReasonTextBudget), (budget.Rc, budget.Edit.Flags, budget.Edit.Reason));
    }

    [Fact]
    public void Edits_outside_the_kind_rules_are_invalid()
    {
        UiBarEdit edit = UiBarEdit.None;
        Ui.Values.HeldBar((in UiValueContext c, in UiItemView v, UiBarValue a) => UiProviderDecision.Replace(edit));
        edit = new UiBarEdit(Fraction: 0.5f);
        Assert.Equal(UiValuesNative.Internal, Invoke(UiValueKind.Held).Rc);  // no fraction for a pair
        edit = new UiBarEdit(Max: 0);
        Assert.Equal(UiValuesNative.Internal, Invoke(UiValueKind.Held).Rc);  // a pair's maximum is above 0
        edit = new UiBarEdit(Current: 1, Max: 4);
        var ok = Invoke(UiValueKind.Held);
        Assert.Equal((0, UiValuesNative.EditCurrent | UiValuesNative.EditMaximum), (ok.Rc, ok.Edit.Flags));
        Ui.Values.ItemBar(default, (in UiValueContext c, in UiItemView v, UiBarValue a) => UiProviderDecision.Replace(edit));
        edit = new UiBarEdit(Current: (uint)int.MaxValue + 1);
        Assert.Equal(UiValuesNative.Internal, Invoke(UiValueKind.Detail).Rc);
        edit = new UiBarEdit(Fraction: float.NaN);
        Assert.Equal(UiValuesNative.Internal, Invoke(UiValueKind.Detail).Rc);
        edit = new UiBarEdit(Current: int.MaxValue);
        Assert.Equal(0, Invoke(UiValueKind.Detail).Rc);
    }

    [Fact]
    public void A_refusal_posts_one_event_per_reason_and_is_not_a_fault()
    {
        var refused = new List<string>();
        Events.On<UiValueProviderRefusedEvent>(e => refused.Add(e.Reason));
        var reason = "no synced data";
        Ui.Values.SlotBar(default, (in UiValueContext c, in UiItemView v, UiBarValue a) => UiProviderDecision.Refuse(reason));
        for (var i = 0; i < 7; i++)
        {
            var r = Invoke(UiValueKind.Slot);
            Assert.Equal((UiValuesNative.Unavailable, UiValuesNative.ReasonRejected), (r.Rc, r.Edit.Reason));
        }
        reason = "stale";
        Invoke(UiValueKind.Slot);
        Events.DrainQueued();
        Assert.Equal(["no synced data", "stale"], refused);
        Assert.Equal(1, UiValueBridge.Count);  // refusals never remove it
    }

    [Fact]
    public void Five_faults_in_a_row_remove_the_provider_and_say_so()
    {
        var changes = new List<(UiValueProviderChange, string)>();
        Events.On<UiValueProviderChangedEvent>(e => changes.Add((e.Change, e.Mod)));
        var fail = true;
        var reg = Ui.Values.Hud(HudValue.Health, (in UiValueContext c, in UiItemView v, UiBarValue a) =>
            fail ? throw new InvalidOperationException("bug") : UiProviderDecision.Pass);
        for (var i = 0; i < 4; i++) Assert.Equal(UiValuesNative.Internal, Invoke(UiValueKind.Health).Rc);
        fail = false;
        Assert.Equal(0, Invoke(UiValueKind.Health).Rc);  // a pass resets the count
        fail = true;
        for (var i = 0; i < 4; i++) Invoke(UiValueKind.Health);
        Assert.Equal(1, UiValueBridge.Count);
        Invoke(UiValueKind.Health);
        Assert.Equal(0, UiValueBridge.Count);
        Assert.Equal([reg.Handle], FakeUiValues.Unsubscribed);
        Events.DrainQueued();
        Assert.Equal([(UiValueProviderChange.Added, "kiln"), (UiValueProviderChange.Faulted, "kiln")], changes);
        Assert.Equal(0, Invoke(UiValueKind.Health).Rc);  // a late native call for a removed provider passes
    }

    // ---- change and reset events ----
    private static void Change(uint kind, uint session, uint page, float? before, float? after, uint reason = 0, uint oldSession = 0)
    {
        var c = new UiValuesNative.ValueChange
        {
            Size = (uint)sizeof(UiValuesNative.ValueChange), ChangeKind = kind, Frame = 42, Reason = reason, OldSession = oldSession,
            NewSession = session,
        };
        c.Identity = new UiValuesNative.Identity
        {
            Size = (uint)sizeof(UiValuesNative.Identity), Kind = 1, Session = session, LocalPlayer = 77, InventoryPage = page,
            SlotIndex = 2, Item = 0xBB, UiState = 7, HudScreen = 9,
        };
        if (before is { } b) (c.HasBefore, c.Before) = (1, new UiValuesNative.BarValue { Fraction = b, Current = 1, Maximum = 2 });
        if (after is { } a) (c.HasAfter, c.After) = (1, new UiValuesNative.BarValue { Fraction = a, Current = 1, Maximum = 2 });
        ((delegate* unmanaged[Cdecl]<UiValuesNative.ValueChange*, void*, void>)FakeUiValues.ChangeFn)(&c, null);
    }

    [Fact]
    public void Change_records_become_events_once_someone_listens()
    {
        var got = new List<UiValueChangedEvent>();
        var resets = new List<UiValuesResetEvent>();
        Events.On<UiValueChangedEvent>(got.Add);
        Events.On<UiValuesResetEvent>(resets.Add);
        UiValueEvents.EnsureFeed();  // the event types' static constructors call it at their first subscription
        Assert.NotEqual(0, FakeUiValues.ChangeFn);
        Assert.Equal(99ul, FakeUiValues.ChangeOwner);
        Change(UiValuesNative.ChangeAdded, 1, 0x5000, null, 0.5f);
        Change(UiValuesNative.ChangeUpdated, 1, 0x5000, 0.5f, 0.25f);
        Change(UiValuesNative.ChangeRemoved, 1, 0x5000, 0.25f, null);
        Change(UiValuesNative.ChangeEvicted, 1, 0x5001, 1f, null);
        Change(UiValuesNative.ChangeReset, 2, 0, null, null, reason: 0, oldSession: 1);
        Assert.Empty(got);  // delivered on the tick thread, not inside the frame
        Events.DrainQueued();
        Assert.Equal([UiValueChangeKind.Added, UiValueChangeKind.Updated, UiValueChangeKind.Removed, UiValueChangeKind.Evicted],
            got.Select(e => e.ChangeKind));
        var first = got[0];
        Assert.Null(first.Before);
        Assert.Equal(0.5f, first.After!.Fraction);
        Assert.Equal(new UiValueIdentity(UiValueKind.Slot, 1, new EntityId(77), new UiScreenKey(GameUiState.Play, (HudScreen)9, 0, 0),
            new EntityId(0x5000), 2, EntityId.None, new ItemId(0xBB), false), first.Identity);
        Assert.Null(got[2].After);
        Assert.Equal(42ul, got[1].Frame);
        var reset = Assert.Single(resets);
        Assert.Equal((UiValuesResetReason.World, 1ul, 2ul), (reset.Reason, reset.OldSession, reset.NewSession));
    }

    // ---- visible slots ----
    private static void Frame(uint count, ulong captureMs = 9_500, uint transform = UiValuesNative.Viewport)
    {
        FakeUiValues.SlotsMeta = new UiValuesNative.SlotsFrame
        {
            Ok = 1, Count = count, LocalPlayer = 77, Session = 5, Frame = 700, CaptureMs = captureMs, CanvasWidth = 1920,
            CanvasHeight = 1080, ViewportPx = new UiValuesNative.Rect { X = 100, Y = 50, Width = 2560, Height = 1440 },
            PixelsPerUnit = 2, TransformFlags = transform,
        };
        FakeUiValues.SlotList = new UiValuesNative.VisibleSlot[count];
        for (var i = 0; i < count; i++)
        {
            ref var s = ref FakeUiValues.SlotList[i];
            s.Size = (uint)sizeof(UiValuesNative.VisibleSlot);
            s.Occurrence = (uint)i;
            s.InventoryPage = 0x5000;
            s.SlotIndex = (uint)(i % 8);
            s.Item = 0xAA;
            s.Count = 1;
            s.Available = UiValuesNative.OuterRect | UiValuesNative.NativeState | (i == 0 ? UiValuesNative.InnerRect : 0) | transform;
            s.OuterUi = new UiValuesNative.Rect { X = 10, Y = 20, Width = 80, Height = 80 };
            s.InnerUi = new UiValuesNative.Rect { X = 12, Y = 22, Width = 76, Height = 76 };
            s.NativeState[0] = 1;
            s.Holder = 77;
            s.HolderFlags = 7;
            s.HolderSerial = 3;
            s.PageOrdinal = 1;
        }
    }

    [Fact]
    public void Slots_copies_the_latest_frame_without_pixels_before_the_mapping_is_verified()
    {
        Frame(3);
        var r = Ui.Values.Slots();
        Assert.True(r.Ok, r.Reason);
        var snap = r.Snapshot!;
        Assert.Equal((5ul, 700ul, new EntityId(77), 3), (snap.Session, snap.Frame, snap.LocalPlayer, snap.Slots.Count));
        var s = snap.Slots[0];
        Assert.Equal(new UiRect(10, 20, 80, 80), s.OuterUi);
        Assert.Equal(new UiRect(12, 22, 76, 76), s.InnerUi);
        Assert.Null(snap.Slots[1].InnerUi);
        Assert.Null(s.OuterPixels);
        Assert.Equal(1, s.NativeState[0]);
        Assert.Equal((new EntityId(77), 3ul, 1u, 7u), (s.Holder, s.HolderSerial, s.PageOrdinal, s.HolderFlags));
        Frame(1, transform: UiValuesNative.Viewport | UiValuesNative.PixelMappingVerified);
        var px = Ui.Values.Slots().Snapshot!.Slots[0].OuterPixels;
        Assert.Equal(new UiRect(100 + 10 * 2, 50 + 20 * 2, 160, 160), px);
    }

    [Fact]
    public void Slots_retries_a_small_buffer_with_the_same_frame_and_refuses_an_old_frame()
    {
        Frame(100);
        lock (FakeUiValues.Copies) FakeUiValues.Copies.Clear();
        var r = Ui.Values.Slots();
        Assert.True(r.Ok);
        Assert.Equal(100, r.Snapshot!.Slots.Count);
        Assert.Equal([(0ul, 0ul, 64u), (5ul, 700ul, 128u)], FakeUiValues.Copies);  // the retry names the frame it saw
        Frame(2, captureMs: 8_000);  // 2000 ms before "now"
        var old = Ui.Values.Slots();
        Assert.False(old.Ok);
        Assert.Contains("ms old", old.Reason);
        FakeUiValues.SlotsMeta = new UiValuesNative.SlotsFrame { Ok = 0, Reason = UiValuesNative.ReasonFrameBudget };
        Assert.Contains("4096", Ui.Values.Slots().Reason);
        Backend.Current = new FakeUiValues.Backend(["ui.values.detail"]);
        Assert.Equal("ui.slots.inspect", Assert.Throws<KilnUnavailableException>(() => Ui.Values.Slots()).Capability);
    }

    [Fact]
    public void SubscribeSlots_delivers_each_frame_as_a_snapshot()
    {
        var got = new List<VisibleSlotsResult>();
        var reg = Ui.Values.SubscribeSlots(got.Add);
        Assert.True(reg.Ok);
        Frame(2);
        var fn = (delegate* unmanaged[Cdecl]<UiValuesNative.SlotsFrame*, UiValuesNative.VisibleSlot*, uint, void*, void>)FakeUiValues.SlotsFn;
        var meta = FakeUiValues.SlotsMeta;
        fixed (UiValuesNative.VisibleSlot* p = FakeUiValues.SlotList) fn(&meta, p, 2, (void*)FakeUiValues.SlotsUser);
        var r = Assert.Single(got);
        Assert.Equal(2, r.Snapshot!.Slots.Count);
        reg.Dispose();
        fixed (UiValuesNative.VisibleSlot* p = FakeUiValues.SlotList) fn(&meta, p, 2, (void*)FakeUiValues.SlotsUser);
        Assert.Single(got);  // nothing after Dispose
        Assert.Contains(reg.Handle, FakeUiValues.Unsubscribed);
        Assert.Throws<ArgumentNullException>(() => Ui.Values.SubscribeSlots(null!));
    }

    // ---- tooltip lines and the details window ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriterAppend(UiValuesNative.LineWriter* w, ulong id)
    {
        if (w->Count >= w->Capacity)
        {
            w->Status = UiValuesNative.ReasonTextBudget;
            return UiValuesNative.BufferTooSmall;
        }
        var lines = (UiValuesNative.Line*)w->Lines;
        lines[w->Count] = new UiValuesNative.Line { Size = 16, Text = id };
        w->Count++;
        return 0;
    }

    private static (int Rc, List<string> Lines) CallLines(uint capacity = 16)
    {
        var fn = (delegate* unmanaged[Cdecl]<UiValuesNative.ProviderContext*, UiValuesNative.ItemView*, UiValuesNative.LineWriter*, void*, int>)FakeUiValues.LinesFn;
        UiValuesNative.ProviderContext c = default;
        c.Size = (uint)sizeof(UiValuesNative.ProviderContext);
        c.Text = FakeUiValues.Ops;
        UiValuesNative.ItemView v = default;
        v.Size = (uint)sizeof(UiValuesNative.ItemView);
        v.Item = 0xAA;
        UiValuesNative.LineWriter w = default;
        w.Size = (uint)sizeof(UiValuesNative.LineWriter);
        w.Capacity = capacity;
        w.Append = &WriterAppend;
        var rc = fn(&c, &v, &w, (void*)FakeUiValues.LinesUser);
        var lines = new List<string>();
        var l = (UiValuesNative.Line*)w.Lines;
        for (var i = 0; i < w.Count; i++) lines.Add(FakeUiValues.Texts[l[i].Text]);
        return (rc, lines);
    }

    [Fact]
    public void Tooltip_lines_go_through_the_writer_and_a_refusal_adds_none()
    {
        var answer = UiTooltipDecision.With("Water: 3 of 4 charges", UiText.Of(("en", "Fuel"), ("de", "Brennstoff")), "");
        Ui.Values.TooltipLines(new ItemId(0xAA), (in UiValueContext c, in UiItemView v) => answer);
        var (rc, lines) = CallLines();
        Assert.Equal(0, rc);
        Assert.Equal(["Water: 3 of 4 charges", "Fuel", ""], lines);
        answer = UiTooltipDecision.Refuse("no data yet");
        Assert.Equal(UiValuesNative.Unavailable, CallLines().Rc);
        answer = UiTooltipDecision.With(Enumerable.Range(1, 5).Select(i => (UiText)$"line {i}").ToArray());
        Assert.Equal(UiValuesNative.Unavailable, CallLines(capacity: 4).Rc);  // the 5th does not fit: all of them go
        Assert.Throws<ArgumentNullException>(() => Ui.Values.TooltipLines(default, null!));
    }

    [Fact]
    public void Open_needs_a_snapshot_and_room_and_close_is_idempotent()
    {
        var panels = new List<(bool Open, string? Reason)>();
        Events.On<UiTooltipPanelChangedEvent>(e => panels.Add((e.Open, e.Reason)));
        FakeUiValues.OpenOk = 0;
        var none = Ui.Values.OpenTooltipDetails();
        Assert.False(none.Ok);
        Assert.Equal("no tooltip", none.Reason);
        UiTooltipBridge.Viewport = () => (200, 1080, 1);  // 200 - 64 = 136 pixels wide: below 160
        var small = Ui.Values.OpenTooltipDetails();
        Assert.False(small.Ok);
        Assert.Equal("viewport too small", small.Reason);
        Assert.Equal(1, FakeUiValues.Opens);  // the small viewport refused before asking native
        UiTooltipBridge.Viewport = () => (1920, 1080, 1.5f);
        Assert.Equal((840f, 900f), UiTooltipBridge.Size((1920, 1080, 1.5f)));
        FakeUiValues.OpenOk = 1;
        var open = Ui.Values.OpenTooltipDetails();
        Assert.True(open.Ok);
        Assert.Equal((7ul, 1201ul), (open.Session, open.SourceFrame));
        Ui.Values.CloseTooltipDetails();
        Ui.Values.CloseTooltipDetails();
        Assert.Equal(2, FakeUiValues.Closes);
        Events.DrainQueued();
        Assert.Equal([(false, "viewport too small"), (true, null)], panels);
        UiTooltipBridge.Viewport = () => null;  // no overlay
        Assert.Equal("ui.values.tooltip_lines", Assert.Throws<KilnUnavailableException>(() => Ui.Values.OpenTooltipDetails()).Capability);
    }

    private static float Measure(string s) => new System.Globalization.StringInfo(s).LengthInTextElements * 7f;

    [Fact]
    public void Wrapping_keeps_every_line_inside_the_pane_and_never_splits_a_text_element()
    {
        var english = string.Join(" ", Enumerable.Range(0, 300).Select(i => $"word{i}")) + " LAYOUT-LONG-END";
        var german = "Größenänderung der Wasserkanne: Füllstandsanzeige für Brennstoffbehälter " +
                     "Donaudampfschifffahrtsgesellschaftskapitänswitwenrentenauszahlungsstelle ÄÖÜäöüß Ende";
        var marks = "e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301";
        marks = System.Text.RegularExpressions.Regex.Unescape(marks);
        var sixteen = Enumerable.Range(1, 16).Select(i => $"LAYOUT-MULTI-{i:00} {english[..120]}").ToList();
        foreach (var width in new[] { 70f, 250f, 560f })
        {
            var input = new List<string> { english, german, "", marks };
            input.AddRange(sixteen);
            var wrapped = UiTooltipWrap.Wrap(input, width, Measure);
            Assert.All(wrapped, line => Assert.True(Measure(line) <= width || new System.Globalization.StringInfo(line).LengthInTextElements == 1,
                $"'{line}' is {Measure(line)} wide for {width}"));
            // nothing lost and nothing reordered (spaces at the breaks aside)
            Assert.Equal(string.Concat(input.Select(l => l.Replace(" ", ""))), string.Concat(wrapped.Select(l => l.Replace(" ", ""))));
            Assert.Contains("", wrapped);  // the empty line keeps its place
            Assert.DoesNotContain(wrapped, l => l.StartsWith('\u0301'));  // a combining mark stays with its letter
            Assert.Contains(wrapped, l => l.Contains("LAYOUT-LONG-END") || l.EndsWith("END"));
            var first = wrapped.FindIndex(l => l.Contains("TI-01"));
            Assert.True(first >= 0 && first < wrapped.FindIndex(l => l.Contains("TI-16")));
        }
    }
}
