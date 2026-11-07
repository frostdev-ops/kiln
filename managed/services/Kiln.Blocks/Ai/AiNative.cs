// SPDX-License-Identifier: MIT
// The native kiln.npcai table (native/core/abi/services/kiln_npcai.h). Field order and types must match the C header.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class AiNative
{
    public const string Name = "kiln.npcai";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Resolved, Installed, Relations, Rows, Pairs, Mapped, Reserved;
        public ulong Calls, Answered, ByPair, ByRow, ByTable;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Str
    {
        public byte* Ptr;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public uint HeaderSize, HeaderVersion;
        public delegate* unmanaged<Str, uint> OwnerOpen;
        public delegate* unmanaged<uint, int> OwnerRelease;
        public delegate* unmanaged<uint, byte, byte, uint, int> RelationSet;
        public delegate* unmanaged<byte, byte, uint> RelationGet;
        public delegate* unmanaged<uint, uint, byte*, int> EntityRow;
        public delegate* unmanaged<uint, uint, uint, uint, float, int> PairSet;
        public delegate* unmanaged<uint, uint, int> PairClear;
        public delegate* unmanaged<uint, uint, uint*, uint*, int> Query;
        public delegate* unmanaged<byte, byte, uint*, uint*, int> QueryFactions;
        public delegate* unmanaged<Status*, int> GetStatus;
        public delegate* unmanaged<uint, ulong> EntityLifetime;
        public delegate* unmanaged<ulong> TickStep;
    }

    private static Table* _t;
    private static bool _looked;

    /// <summary>The table, or null when kiln.npcai is not loaded (an unknown build, or a client without it).</summary>
    public static Table* T
    {
        get
        {
            if (_t != null || _looked) return _t;
            try { _t = (Table*)BlocksBridge.ServiceTable(Name, Version); }
            catch (Exception) { _t = null; }
            _looked = _t != null || Lifecycle.IsServerRunning;   // looked for once a world runs; earlier, ask again later
            return _t;
        }
    }

    /// <summary>kiln.npcai is loaded.</summary>
    public static bool Loaded => T != null;

    private static readonly Dictionary<string, uint> Owners = new(StringComparer.Ordinal);

    public static uint Owner(string mod)
    {
        var t = T;
        if (t == null) return 0;
        lock (Owners)
        {
            if (Owners.TryGetValue(mod, out var o)) return o;
            var bytes = System.Text.Encoding.UTF8.GetBytes("mod:" + mod);
            fixed (byte* p = bytes) o = t->OwnerOpen(new Str { Ptr = p, Len = (nuint)bytes.Length });
            Owners[mod] = o;
            return o;
        }
    }

    public static IReadOnlyList<(string Mod, uint Owner)> AllOwners()
    {
        lock (Owners) return Owners.Select(x => (x.Key, x.Value)).ToList();
    }

    public static void Forget(string mod)
    {
        lock (Owners) Owners.Remove(mod);
    }

    public static ulong Lifetime(EntityId e)
    {
        var t = T;
        return t != null && t->HeaderSize >= 8 + 11 * sizeof(nint) && t->EntityLifetime != null ? t->EntityLifetime(e.Value) : 0;
    }

    public static ulong Step
    {
        get
        {
            var t = T;
            return t != null && t->HeaderSize >= sizeof(Table) && t->TickStep != null ? t->TickStep() : 0;
        }
    }

    public static string Describe(int rc) => rc switch
    {
        InvalidArgument => "invalid argument",
        NotFound => "not found",
        Unavailable => "the faction hooks are not available on this build",
        WrongThread => "call it on the tick thread",
        _ => $"error {rc}",
    };

    public static void Check(int rc, string what)
    {
        if (rc == Ok) return;
        if (rc == WrongThread) throw new InvalidOperationException($"{what}: {Describe(rc)}");
        if (rc == Unavailable) throw new NotSupportedException($"{what}: {Describe(rc)}");
        if (rc == NotFound) throw new KeyNotFoundException($"{what}: {Describe(rc)}");
        throw new ArgumentException($"{what}: {Describe(rc)}");
    }
}
