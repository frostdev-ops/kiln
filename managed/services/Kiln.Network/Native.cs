// SPDX-License-Identifier: MIT
// The native frame transport kiln.net (native/core/abi/services/kiln_net.h).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

internal static unsafe class NetNative
{
    public const uint PeerServer = 0xFFFFFFFF;
    public const uint PeerLab = 0x80000000;
    public const int Ok = 0, NotFound = 2, Unavailable = 3, WrongThread = 4, TooBig = 5;
    public const uint TransportGame = 1, TransportLab = 2;
    public const uint RoleServer = 1, RoleClient = 2;
    public const uint PeerHasPlayer = 1, PeerProved = 4, PeerHeld = 8;
    public const uint GateOff = 0, GateHold = 1, GateCanHold = 1, GateCanWithhold = 2, GateCanGuard = 4;
    public const uint SelftestCreate = 1, SelftestSlot = 2, SelftestMask = 3, SelftestQueue = 4, SelftestBind = 5, SelftestHeap = 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct PeerInfo
    {
        public uint Size, Peer, Transport, Flags, Entity, Slot, Connection, RecordState;
        public ulong Account, UpMs, RxFrames, RxBytes, TxFrames, TxBytes, Dropped;
        public fixed byte Name[64];

        public string NameString()
        {
            fixed (byte* p = Name)
            {
                var n = 0;
                while (n < 64 && p[n] != 0) n++;
                return Encoding.UTF8.GetString(p, n);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Roles;
        public byte ServerReady, ClientReady, Lab, Reserved;
        public uint Channel, MaxFrame, Peers, LabPort, Subscribers;
        public ulong RxFrames, RxBytes, TxFrames, TxBytes, Dropped, SendFailures, Drains, GameFramesSeen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GateInfo
    {
        public uint Size, Caps, Mode, Held, KilnTemplates, WithheldSlots;
        public ulong WithheldEntities;
        public uint MissingTemplates, NewerSlots;
        public ulong DroppedCreates;

        public readonly bool CanHold => (Caps & GateCanHold) != 0;
        public readonly bool CanWithhold => (Caps & GateCanWithhold) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> Status;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<uint, byte*, uint, void*, void>, delegate* unmanaged[Cdecl]<uint, uint, void*, void>, void*, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<uint, byte*, uint, int> Send;
        public delegate* unmanaged[Cdecl]<PeerInfo*, uint, uint*, int> Peers;
        public delegate* unmanaged[Cdecl]<uint, PeerInfo*, int> PeerInfo;
        public delegate* unmanaged[Cdecl]<uint, uint, int> Kick;
        public delegate* unmanaged[Cdecl]<uint, uint*, int> PeerOfEntity;
        public delegate* unmanaged[Cdecl]<uint, int> GateSet;
        public delegate* unmanaged[Cdecl]<GateInfo*, int> GateInfo;
        public delegate* unmanaged[Cdecl]<uint, int> GateRelease;
        public delegate* unmanaged[Cdecl]<uint, int> EntityWithheld;
        public delegate* unmanaged[Cdecl]<byte*, int> TemplateKilnOnly;
        public delegate* unmanaged[Cdecl]<uint, uint, int> WithholdSet;
        public delegate* unmanaged[Cdecl]<uint, ulong, ulong, ulong*, int> LabSelftest;
    }

    private static Table* _table;
    private static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.net", 1));
    private static ulong _handle;
    private static readonly object Lock = new();

    /// <summary>Replaces the native transport (unit tests): frames sent go to it instead of kiln.net.</summary>
    internal static INetTransport? TestTransport { get; set; }

    public static bool Available => TestTransport != null || T != null;

    public static bool Subscribe()
    {
        if (TestTransport != null) return true;
        lock (Lock)
        {
            if (_handle != 0) return true;
            if (T == null) return false;
            ulong h = 0;
            var rc = T->Subscribe(0, &OnFrame, &OnPeer, null, &h);
            if (rc != Ok) return false;
            _handle = h;
            return true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFrame(uint peer, byte* data, uint len, void* user)
    {
        try
        {
            NetEngine.OnFrame(peer, new ReadOnlySpan<byte>(data, checked((int)len)));
        }
        catch (Exception e)
        {
            NetEngine.LogError($"frame from 0x{peer:x}: {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPeer(uint peer, uint ev, void* user)
    {
        try
        {
            NetEngine.OnPeer(peer, ev == 1);
        }
        catch (Exception e)
        {
            NetEngine.LogError($"peer event 0x{peer:x}/{ev}: {e}");
        }
    }

    public static int Send(uint peer, ReadOnlySpan<byte> frame)
    {
        if (TestTransport is { } t) return t.Send(peer, frame.ToArray());
        if (T == null) return Unavailable;
        fixed (byte* p = frame) return T->Send(peer, p, (uint)frame.Length);
    }

    public static Status? ReadStatus()
    {
        if (TestTransport is { } t) return t.Status();
        if (T == null) return null;
        Status s = default;
        s.Size = (uint)sizeof(Status);
        return T->Status(&s) == Ok ? s : null;
    }

    public static PeerInfo? Info(uint peer)
    {
        if (TestTransport is { } t) return t.Info(peer);
        if (T == null) return null;
        PeerInfo i = default;
        i.Size = (uint)sizeof(PeerInfo);
        return T->PeerInfo(peer, &i) == Ok ? i : null;
    }

    public static int Kick(uint peer, uint reason)
    {
        if (TestTransport is { } t) return t.Kick(peer, reason);
        return T == null ? Unavailable : T->Kick(peer, reason);
    }

    public static int GateSet(uint mode) => TestTransport != null || T == null ? Unavailable : T->GateSet(mode);

    public static GateInfo ReadGate()
    {
        GateInfo g = default;
        g.Size = (uint)sizeof(GateInfo);
        return TestTransport == null && T != null && T->GateInfo(&g) == Ok ? g : default;
    }

    public static int GateRelease(uint peer) => TestTransport != null || T == null ? Unavailable : T->GateRelease(peer);

    /// <summary>1 when this server-world entity is kept from clients without Kiln, 0 when not, -1 when it can't be read.</summary>
    public static int EntityWithheld(uint entity) => TestTransport != null || T == null ? -1 : T->EntityWithheld(entity);

    /// <summary>Whether Kiln-only entities are kept from this peer. Clearing sends what was kept back.</summary>
    public static int WithholdSet(uint peer, bool on) => TestTransport != null || T == null ? Unavailable : T->WithholdSet(peer, on ? 1u : 0u);

    /// <summary>Lab transport only: drives the game's own code (kiln_net.h, tools/nettest).</summary>
    public static int LabSelftest(uint op, ulong a, ulong b, out ulong result)
    {
        result = 0;
        if (TestTransport != null || T == null) return Unavailable;
        fixed (ulong* o = &result) return T->LabSelftest(op, a, b, o);
    }

    /// <summary>The same with a template GUID as <paramref name="b"/>'s companion (SelftestCreate).</summary>
    public static int LabSelftestCreate(Guid template, uint entity, uint parent, out ulong result)
    {
        var g = template.ToByteArray();
        fixed (byte* p = g) return LabSelftest(SelftestCreate, (ulong)p, entity | (ulong)parent << 32, out result);
    }

    /// <summary>1 when only Kiln has this template (a vanilla client can't know it), 0 when not, -1 when unknown.</summary>
    public static int TemplateKilnOnly(Guid template)
    {
        if (TestTransport != null || T == null) return -1;
        var g = template.ToByteArray();
        fixed (byte* p = g) return T->TemplateKilnOnly(p);
    }

    public static uint MaxFrame => ReadStatus() is { MaxFrame: > 64 } s ? s.MaxFrame : 1200;
}

/// <summary>Seam for tests: a fake kiln.net.</summary>
internal interface INetTransport
{
    int Send(uint peer, byte[] frame);
    NetNative.Status Status();
    NetNative.PeerInfo? Info(uint peer);
    int Kick(uint peer, uint reason);
}
