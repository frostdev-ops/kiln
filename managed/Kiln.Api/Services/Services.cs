// SPDX-License-Identifier: MIT
// Access to native service tables. Each service has a public C header in native/core/abi/services/kiln_<name>.h and a
// C# wrapper in this folder (one file per service, e.g. Teleport.cs). Wrappers keep the public surface managed:
// mods never see pointers.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class Services
{
    /// <summary>The service's function table, or null when it is unavailable on this build.</summary>
    public static T* Get<T>(string name, uint minVersion) where T : unmanaged
    {
        var p = (T*)Backend.Current.Service(name, minVersion);
        return p != null && ((ServiceHeader*)p)->Size >= (uint)sizeof(T) ? p : null;
    }

    /// <summary>Throws if the calling code is not on the tick thread (game state is single-threaded).</summary>
    public static void RequireTickThread(string api)
    {
        if (!Backend.Current.IsTickThread) throw new InvalidOperationException($"{api} must be called on the tick thread (use Scheduler.Post)");
    }

    /// <summary>Owner id of the mod calling right now (0 outside mod code).</summary>
    public static ulong CallerOwner => ModContext.CurrentMod?.Owner ?? 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceHeader
{
    public uint Size;
    public uint Version;
}

/// <summary>UTF-8 (ptr, len) as passed to and from the native core.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeStr
{
    public byte* Ptr;
    public nuint Len;
    public override string ToString() => Ptr == null ? "" : System.Text.Encoding.UTF8.GetString(Ptr, checked((int)Len));
}
