// SPDX-License-Identifier: MIT
// Kiln.Hooks in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md): the core's row for the
// kiln.hooks table is the only one. Asking for the table makes Services.Get record why it is missing.
namespace Kiln;

internal static unsafe class HooksCaps
{
    /// <summary>The native table is here (the core's row).</summary>
    public const string Service = HooksNative.Name;

    internal static readonly ServiceCapRows Rows = new(Service, ReportCapabilities);

    // HooksNative looks the table up once per process: ask again, so the row says why it is missing under the registry
    // binding of now (the test kit starts each harness with an empty registry).
    private static void ReportCapabilities()
    {
        if (HooksNative.T == null) ServiceCaps.Probe(Service, HooksNative.Version);
    }
}
