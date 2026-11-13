// SPDX-License-Identifier: MIT
// What a start is doing, for <game>/kiln/state/progress.json: Kiln.Host and Kiln's own managed services report the
// steps after the boot (mods, content, game) to kiln-core, the file's one writer, through kiln.progress v2's `step` and
// `mark` (native/core/abi/services/kiln_progress.h). Internal: not mod API (a mod's own work is part of "mods").
// A core without the v2 entries (older than 0.2.0-dev.27), no core at all (tests) or a file whose life is over: every
// call does nothing.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

internal static unsafe class StartProgress
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Str
    {
        public byte* Ptr;
        public nuint Len;
    }

    /// <summary>kiln.progress v2 (KilnProgressApi): v1's window calls, then step and mark.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Table
    {
        public uint Size;
        public uint Version;
        public void* Begin, Update, End;
        public delegate* unmanaged[Cdecl]<Str, Str, Str, ulong, ulong, int> Step;
        public delegate* unmanaged[Cdecl]<uint, int> Mark;
    }

    public const string Service = "kiln.progress";
    public const uint MarkMenu = 1, MarkServerStarted = 2;  // KILN_PROGRESS_MARK_*
    public const int Ok = 0, Refused = 1, Unavailable = 3;   // KilnResult

    /// <summary>The label of step "game": the game itself goes on loading after Kiln is done.</summary>
    public static string GameLabel(bool client) => client ? "The game is loading its main menu" : "The server is starting its world";

    private static nint _table;
    private static bool _looked;

    /// <summary>Uses this table (Kiln.Host passes the core's; tests a fake). 0: none.</summary>
    internal static void Use(nint table)
    {
        _table = table;
        _looked = true;
    }

    private static Table* Get()
    {
        if (!_looked)
        {
            _looked = true;
            try { _table = Backend.Current.Service(Service, 1); }
            catch { _table = 0; }
        }
        var t = (Table*)_table;
        // a v1 table (32 bytes) has no step and mark
        return t != null && t->Size >= (uint)sizeof(Table) ? t : null;
    }

    /// <summary>The start is in step <paramref name="id"/> (the running one, or the next of the plan). Label empty: keep it;
    /// detail empty: clear it; done and total both 0: keep the count. Returns the core's KilnResult, or Unavailable.</summary>
    public static int Step(string id, string label = "", string detail = "", ulong done = 0, ulong total = 0)
    {
        var t = Get();
        if (t == null || t->Step == null) return Unavailable;
        try
        {
            var a = Encoding.UTF8.GetBytes(id);
            var b = Encoding.UTF8.GetBytes(label);
            var c = Encoding.UTF8.GetBytes(detail);
            fixed (byte* pa = a)
            fixed (byte* pb = b)
            fixed (byte* pc = c)
                return t->Step(new Str { Ptr = pa, Len = (nuint)a.Length }, new Str { Ptr = pb, Len = (nuint)b.Length },
                    new Str { Ptr = pc, Len = (nuint)c.Length }, done, total);
        }
        catch
        {
            return Unavailable;  // reporting must never cost a start anything
        }
    }

    public static int Mark(uint what)
    {
        var t = Get();
        if (t == null || t->Mark == null) return Unavailable;
        try { return t->Mark(what); }
        catch { return Unavailable; }
    }
}
