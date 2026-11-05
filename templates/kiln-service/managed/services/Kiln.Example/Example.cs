// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Template service wrapper: managed API over the native table in native/core/abi/services/kiln_example.h.</summary>
public static unsafe class Example
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong> Steps;
    }

    private static Table* _table;

    private static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.example", 1));

    /// <summary>True when the native service is loaded and usable on this build.</summary>
    public static bool Available => T != null;

    /// <summary>Server-world steps since boot.</summary>
    public static ulong Steps => T != null ? T->Steps() : throw new NotSupportedException("kiln.example is unavailable");
}
// Next steps for a new service: add your service's assembly name to the InternalsVisibleTo list in
// managed/Kiln.Api/Kiln.Api.csproj (ask the Kiln maintainers; Services.Get is internal), describe its symbols in
// sigdb/symbols/server.d/<name>.json, and document it in docs/kiln/services/.
