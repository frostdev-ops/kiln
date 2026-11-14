// SPDX-License-Identifier: MIT
// The native table kiln.ui.values (native/core/abi/services/kiln_ui_values.h): records and slots, as laid out in C.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class UiValuesNative
{
    public const string Service = "kiln.ui.values";
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5,
        Revoked = 6, Internal = 7;

    // KilnUiEditFlags
    public const uint EditFraction = 1, EditCurrent = 2, EditMaximum = 4, EditLabel = 8, EditGameLabel = 16;

    // KilnUiValueReason
    public const uint ReasonNone = 0, ReasonRejected = 1, ReasonTextBudget = 2, ReasonInvalidEdit = 3,
        ReasonCallbackFault = 4, ReasonNoFrame = 5, ReasonStaleFrame = 6, ReasonBufferSmall = 7,
        ReasonRegistrationBudget = 8, ReasonFrameBudget = 9, ReasonNoTooltip = 10;

    [StructLayout(LayoutKind.Sequential)]
    public struct ItemView
    {
        public uint Size, Flags, LocalPlayer, ClientEntity, ServerEntity, Item;
        public uint InventoryPage, SlotIndex, Count, Durability, DurabilityMax;
        public uint Fuel, FuelMax, UnitsPerCharge, Level, Rarity;
        public UiNative.Screen Screen;
        public ulong Session, Frame;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BarValue
    {
        public uint Size, Kind;
        public float Fraction;
        public uint Current, Maximum, LabelIsOverride;
        public ulong LabelId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BarEdit
    {
        public uint Size, Flags;
        public float Fraction;
        public uint Current, Maximum, Reason;
        public ulong LabelId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextOps
    {
        public uint Size, Version;
        public delegate* unmanaged[Cdecl]<ProviderContext*, NativeStr, ulong*, int> Intern;
        public delegate* unmanaged[Cdecl]<ProviderContext*, ulong, NativeStr*, int> Resolve;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProviderContext
    {
        public uint Size, Kind;
        public ulong Session, Frame, ClientStep, Owner;
        public TextOps* Text;
        public void* Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Registration
    {
        public uint Size, Ok, Reason, Reserved;
        public ulong Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Identity
    {
        public uint Size, Kind, LocalPlayer, UiState, HudScreen, Crafting, Station;
        public uint InventoryPage, SlotIndex, ClientEntity, Item, SyntheticGroup;
        public ulong Session;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ValueChange
    {
        public uint Size, ChangeKind, HasBefore, HasAfter, Reason, Reserved;
        public ulong Frame, OldSession, NewSession;
        public Identity Identity;
        public BarValue Before, After;
        public NativeStr BeforeLabel, AfterLabel;
    }

    public const uint ChangeAdded = 0, ChangeUpdated = 1, ChangeRemoved = 2, ChangeEvicted = 3, ChangeReset = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint,
            delegate* unmanaged[Cdecl]<ProviderContext*, ItemView*, BarValue*, BarEdit*, void*, int>, void*, Registration*, int> Subscribe;
        public void* TooltipLines;  // typed where it is used (UiTooltipDetails.cs)
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public void* CopySlots;     // typed where it is used (UiSlots.cs)
        public void* SubscribeSlots;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<ValueChange*, void*, void>, void*, Registration*, int> SubscribeChanges;
        public void* OpenTooltipDetails;
        public void* CloseTooltipDetails;
    }

    private static Table* _table;
    private static bool _looked;
    private static readonly object Lock = new();

    /// <summary>The table, or null (a server, a runtime without the service). Looked up once per registry binding.</summary>
    public static Table* T
    {
        get
        {
            if (_table != null) return _table;
            lock (Lock)
            {
                if (_table == null && !_looked)
                {
                    _table = Services.Get<Table>(Service, 1);
                    _looked = _table == null;
                }
                return _table;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Lock)
        {
            _table = null;
            _looked = false;
        }
    }

    /// <summary>Text through a provider's context: the native store copies it and returns an id (0 = empty).</summary>
    public static int Intern(ProviderContext* c, string text, out ulong id)
    {
        id = 0;
        if (c == null || c->Text == null) return Internal;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        ulong got = 0;
        fixed (byte* p = bytes)
        {
            var rc = c->Text->Intern(c, new NativeStr { Ptr = p, Len = (nuint)bytes.Length }, &got);
            id = got;
            return rc;
        }
    }

    /// <summary>The bytes of a text id through a provider's context; null when it does not resolve.</summary>
    public static string? Resolve(ProviderContext* c, ulong id)
    {
        if (id == 0) return "";
        if (c == null || c->Text == null) return null;
        NativeStr s = default;
        return c->Text->Resolve(c, id, &s) == Ok ? s.ToString() : null;
    }
}
