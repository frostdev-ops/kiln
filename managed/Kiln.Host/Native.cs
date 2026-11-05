// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/kiln_api.h (ABI 1). Blittable only; field order is the ABI.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln.Host;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnStr
{
    public byte* Ptr;
    public nuint Len;

    public override string ToString() => Ptr == null ? "" : Encoding.UTF8.GetString(Ptr, checked((int)Len));
}

[StructLayout(LayoutKind.Sequential)]
internal struct KilnTickInfo
{
    public uint Size;
    public uint WorldKind;
    public ulong Step;
    public uint Clock;
    public uint ClockDelta;
    public long ClockPeriod;
    public double WallDeltaMs;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnBuildInfo
{
    public uint Size;
    public uint Side;
    public fixed byte ExeSha256[65];
    public byte Verified;
    public byte HooksEnabled;
    public fixed byte Reserved[5];
    public KilnStr KilnVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnApi
{
    public uint Size;
    public uint AbiVersion;
    public delegate* unmanaged[Cdecl]<KilnStr> Version;
    public delegate* unmanaged[Cdecl]<ulong, int, KilnStr, void> Log;
    public delegate* unmanaged[Cdecl]<KilnBuildInfo*, int> BuildInfo;
    public delegate* unmanaged[Cdecl]<KilnStr, byte> Feature;
    public delegate* unmanaged[Cdecl]<int, byte*, nuint, nuint*, int> Path;
    public delegate* unmanaged[Cdecl]<KilnStr, ulong> OwnerCreate;
    public delegate* unmanaged[Cdecl]<ulong, void> OwnerRevoke;
    public delegate* unmanaged[Cdecl]<ulong, int, delegate* unmanaged[Cdecl]<KilnTickInfo*, void*, void>, void*, ulong*, int> SubscribeTick;
    public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
    public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<void*, void>, void*, int> Post;
    public delegate* unmanaged[Cdecl]<byte> OnTickThread;
    public delegate* unmanaged[Cdecl]<KilnTickInfo*, int> TickInfo;
    public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<int, KilnStr, void*, void>, void*, ulong*, int> SubscribeLog;
    public delegate* unmanaged[Cdecl]<KilnStr, nuint*, int> Symbol;
    public delegate* unmanaged[Cdecl]<KilnStr, long*, int> Constant;
    public delegate* unmanaged[Cdecl]<KilnStr, uint, void*> Service;
    public delegate* unmanaged[Cdecl]<ulong, KilnStr, void*, int> RegisterService;
    public delegate* unmanaged[Cdecl]<nuint> ServerWorld;
    public delegate* unmanaged[Cdecl]<nuint, KilnStr, nuint> Singleton;

    // ---- sides and worlds (appended for client support; the core's size covers them since this mirror requires
    // the matching core) ----
    public delegate* unmanaged[Cdecl]<uint> Side;
    public delegate* unmanaged[Cdecl]<uint, nuint> World;
    public delegate* unmanaged[Cdecl]<uint, byte> OnWorldThread;
    public delegate* unmanaged[Cdecl]<ulong, uint, int, delegate* unmanaged[Cdecl]<KilnTickInfo*, void*, void>, void*, ulong*, int> SubscribeWorldTick;
    public delegate* unmanaged[Cdecl]<ulong, uint, delegate* unmanaged[Cdecl]<void*, void>, void*, int> PostWorld;
    public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, ulong*, int> SubscribeWorld;
    public delegate* unmanaged[Cdecl]<void*, int> Session;
}

internal static class KilnAbi
{
    public const uint Version = 1;
    public const int Ok = 0;
    public const int TickPre = 0;
    public const int TickPost = 1;
    public const int PathGame = 0, PathKiln = 1, PathMods = 2, PathLogs = 3, PathData = 4, PathControl = 5;
    public const int PathConfig = 6, PathState = 7;  // an older core answers KILN_ERR_INVALID_ARGUMENT
    public const uint SideServer = 0, SideClient = 1;                       // KILN_SIDE_*
    public const uint WorldServer = 0, WorldPrediction = 1, WorldClient = 2; // KILN_WORLD_*

    /// <summary>The side this process runs as (KilnBuildInfo.side / KilnApi.side): "server" or "client". The mod
    /// loader filters manifests' <c>side</c> with it.</summary>
    public static unsafe string SideName(KilnApi* api) =>
        api->Size >= (uint)sizeof(KilnApi) && api->Side != null && api->Side() == SideClient ? "client" : "server";
}

/// <summary>Calls into the native core with UTF-8 strings.</summary>
internal static unsafe class NativeCalls
{
    public static T WithUtf8<T>(string s, Func<KilnStr, T> f)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        fixed (byte* p = bytes) return f(new KilnStr { Ptr = p, Len = (nuint)bytes.Length });
    }

    /// <summary>A path kind an older core may not know (KILN_PATH_CONFIG/STATE): null when it answers an error.</summary>
    public static string? TryPath(KilnApi* api, int kind)
    {
        nuint len = 0;
        api->Path(kind, null, 0, &len);
        if (len == 0) return null;
        var buf = new byte[(int)len];
        fixed (byte* p = buf)
            if (api->Path(kind, p, len, &len) != KilnAbi.Ok) return null;
        return Encoding.UTF8.GetString(buf, 0, (int)len);
    }

    public static string Path(KilnApi* api, int kind)
    {
        nuint len = 0;
        api->Path(kind, null, 0, &len);
        var buf = new byte[(int)len];
        fixed (byte* p = buf) api->Path(kind, p, len, &len);
        return Encoding.UTF8.GetString(buf, 0, (int)len);
    }
}

/// <summary>KilnWorldEvent (kiln_api.h): a world started or is about to be destroyed. Delivered by subscribe_world.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KilnWorldEvent
{
    public uint Size;
    public uint Event;    // WorldStarted / WorldStopping
    public uint Kind;     // KilnAbi.World*
    public uint Session;  // worlds of this kind since boot, from 1
    public nuint World;

    public const uint Started = 1, Stopping = 2;  // KILN_WORLD_STARTED / KILN_WORLD_STOPPING
}

/// <summary>KilnSessionInfo (kiln_api.h): what runs in this process. Filled by KilnApi.session.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnSessionInfo
{
    public uint Size;
    public uint State;       // None / Dedicated / Host / Remote
    public uint Side;
    public uint Generation;
    public fixed ulong Worlds[3];   // uintptr_t per KILN_WORLD_* (x64)
    public fixed uint Sessions[3];
    public uint Lifecycle;   // 1 = world stops are seen (WorldDestroy hook)
    public fixed ulong Steps[3];

    public const uint None = 0, Dedicated = 1, Host = 2, Remote = 3;  // KILN_SESSION_*
}

/// <summary>KilnProfileMod (abi/services/kiln_profile.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KilnProfileMod
{
    public uint Size;
    public uint Kind;
    public KilnStr Id;
    public KilnStr Version;
    public KilnStr Path;
    public uint Enabled;
    public uint Reserved;
}

/// <summary>kiln.profile v1 (abi/services/kiln_profile.h): the mod profile the process booted with.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnProfileApi
{
    public uint Size;
    public uint Version;
    public delegate* unmanaged[Cdecl]<uint> Mode;
    public delegate* unmanaged[Cdecl]<KilnStr> Id;
    public delegate* unmanaged[Cdecl]<KilnStr> Name;
    public delegate* unmanaged[Cdecl]<ulong> Generation;
    public delegate* unmanaged[Cdecl]<uint, uint> Count;
    public delegate* unmanaged[Cdecl]<uint, uint, KilnProfileMod*, int> Get;
    public delegate* unmanaged[Cdecl]<uint> Dropins;
    public delegate* unmanaged[Cdecl]<KilnStr> ConfigDir;
    public delegate* unmanaged[Cdecl]<KilnStr> DataDir;
    public delegate* unmanaged[Cdecl]<KilnStr> Json;

    public const string Name_ = "kiln.profile";
    public const uint Version1 = 1;
    public const uint ModeLegacy = 0, ModeActive = 1, ModeOnce = 2, ModeNoMods = 3, ModeInvalid = 4;  // KILN_PROFILE_*
    public const uint KindKiln = 0, KindNative = 1, KindPlugin = 2, KindEmf = 3;                    // KILN_PROFILE_KIND_*
}
