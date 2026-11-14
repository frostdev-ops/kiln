// SPDX-License-Identifier: MIT
// Access to native service tables. Each service has a public C header in native/core/abi/services/kiln_<name>.h and a
// C# wrapper in this folder (one file per service, e.g. Teleport.cs). Wrappers keep the public surface managed:
// mods never see pointers.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class Services
{
    /// <summary>The service's function table, or null when it is unavailable on this build. When it returns null it
    /// records why on the service's capability row (<see cref="Capabilities"/>), once per name and cause.</summary>
    /// <remarks>Probes stay harmless: a wrapper may ask for one service with several table types and expect null for
    /// the bigger ones. As soon as one <c>Get</c> for a name succeeds, the report made for that name is taken back and
    /// the name is never lowered again in this process. So a lowered <c>kiln.&lt;service&gt;</c> row means that nothing
    /// this Kiln.Api asked of the service has worked yet.</remarks>
    public static T* Get<T>(string name, uint minVersion) where T : unmanaged
    {
        var p = (T*)Backend.Current.Service(name, minVersion);
        var header = (ServiceHeader*)p;
        if (p != null && header->Size >= (uint)sizeof(T))
        {
            Found(name, header->Version);
            return p;
        }
        Missed(name, minVersion, header, (uint)sizeof(T));
        return null;
    }

    // Get<T>'s causes for a null (one record per name and cause).
    private const int TableSmaller = 1, VersionBelow = 2, NotThere = 3, NoCore = 4;

    private static void Found(string name, uint version)
    {
        try
        {
            var b = Capabilities.CurrentBinding;
            if (b.Found.ContainsKey(name) || !b.Found.TryAdd(name, 0)) return;
            Capabilities.ServiceFound(name, version);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // a name the registry refuses (not a capability name) still gets its table
        }
    }

    private static void Missed(string name, uint minVersion, ServiceHeader* header, uint need)
    {
        try
        {
            var b = Capabilities.CurrentBinding;
            if (b.Found.ContainsKey(name)) return;  // a table satisfied an earlier caller: a bigger optional one is not the row's business
            int cause;
            Capability row = default;
            if (header != null) cause = TableSmaller;
            else if (Capabilities.TryGetRow(name, out row))
            {
                if (row.State != CapabilityState.Available) return;  // the core's reason stands
                cause = VersionBelow;
            }
            else cause = b.Table != null ? NotThere : NoCore;
            if (b.Recorded.ContainsKey((name, cause)) || !b.Recorded.TryAdd((name, cause), 0)) return;
            var (reason, text) = cause switch
            {
                TableSmaller => (CapabilityReason.ServiceOlder,
                    $"{name} v{header->Version} has a {header->Size}-byte table; this Kiln.Api needs {need} bytes"),
                VersionBelow => (CapabilityReason.ServiceOlder, VersionOf(row.ReasonText) is { } v
                    ? $"{name} is {v}; this Kiln.Api needs v{minVersion}"
                    : $"{name} is older than the v{minVersion} this Kiln.Api needs"),
                NotThere => (CapabilityReason.ServiceMissing, $"{name} is not here (Kiln.Api needs v{minVersion})"),
                _ => (CapabilityReason.Offline, "no Kiln core in this process"),
            };
            Capabilities.ReportService(name, reason, text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // never fail a lookup over its bookkeeping
        }
    }

    // "v3" or "v3 (kiln-items.dll)": the version a service row's text starts with.
    private static string? VersionOf(string text)
    {
        if (text.Length < 2 || text[0] != 'v' || !char.IsAsciiDigit(text[1])) return null;
        var n = 2;
        while (n < text.Length && char.IsAsciiDigit(text[n])) n++;
        return text[..n];
    }

    /// <summary>Throws if the calling code is not on the tick thread (game state is single-threaded). In <see cref="Mod.OnBoot"/>
    /// (before the game's first tick) the message says to call <paramref name="api"/> in OnLoad.</summary>
    public static void RequireTickThread(string api)
    {
        if (Backend.Current.IsTickThread) return;
        throw new InvalidOperationException(Game.InBoot ? Game.NotInBoot(api) : $"{api} must be called on the tick thread (use Scheduler.Post)");
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
