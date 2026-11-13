// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Template service wrapper: managed API over the native table in native/core/abi/services/kiln_example.h.
/// It shows the API contract (docs/kiln/modding/contract.md) in its smallest form: the service's capability name, a
/// member marked with it that throws <see cref="KilnUnavailableException"/> when it is off, and the thread it may be
/// called on.</summary>
public static unsafe class Example
{
    /// <summary>The capability this class answers for: the core's row for the kiln.example table.</summary>
    internal const string Capability = "kiln.example";

    // The service's rows in the capability registry. kiln.example has only the core's own row, so they are reported at
    // the first use; asking for the table makes Services.Get record why it is missing. A service with rows of its own
    // reports them in this method (ServiceCaps.Static for a fixed row, ServiceCaps.Session for one that follows the
    // world) and starts at the first service tick: a startup type in ServiceTick.AutoStart whose static constructor
    // calls Rows.Ensure() (Kiln.Gameplay's GameplayStartup), so a mod sees the rows before it calls the service.
    internal static readonly ServiceCapRows Rows = new(Capability, ReportCapabilities);

    static Example() => Rows.Ensure();

    private static void ReportCapabilities() => _ = Available;

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong> Steps;
    }

    private static Table* _table;

    private static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.example", 1));

    /// <summary>True when the native service is loaded and usable on this build (capability <c>kiln.example</c>;
    /// <see cref="Capabilities.Why"/> says why not).</summary>
    [AnyThread]
    public static bool Available => T != null;

    /// <summary>Server-world steps since boot. Any thread.</summary>
    /// <exception cref="KilnUnavailableException"><c>kiln.example</c> is off (a <see cref="NotSupportedException"/>).</exception>
    [AnyThread]
    [RequiresCapability(Capability)]
    public static ulong Steps => T != null ? T->Steps() : throw Unavailable();

    private static KilnUnavailableException Unavailable()
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(Capability, "kiln.example is unavailable");
    }
}
