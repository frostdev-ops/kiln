// SPDX-License-Identifier: MIT
// The native kiln.hooks table (native/core/abi/services/kiln_hooks.h). Field order and types must match the C header.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

internal static unsafe class HooksNative
{
    public const string Name = "kiln.hooks";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;

    /// <summary>A detour call in progress; mirrors KilnHookCall. args are rcx,rdx,r8,r9.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HookCallRaw
    {
        public uint Size;
        public uint ArgCount;
        public ulong Hook;
        public nuint Arg0, Arg1, Arg2, Arg3;
        public void* Reserved;
    }

    /// <summary>Full register context of a mid hook; mirrors KilnRegisters / safetyhook Context64 exactly, so writing
    /// a field changes the register on resume.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RegsRaw
    {
        public fixed byte Xmm[256];  // xmm0..xmm15, 16 bytes each
        public nuint Rflags, R15, R14, R13, R12, R11, R10, R9, R8, Rdi, Rsi, Rdx, Rcx, Rbx, Rax, Rbp, Rsp,
            TrampolineRsp, Rip;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SigSummary
    {
        public uint Size;
        public uint Symbols, SymbolsOk, Constants, ConstantsOk, Features, FeaturesOk;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public uint Size;
        public uint DetourTargets, MidTargets, Handlers, Patches;
        public ulong DetourCalls, MidCalls;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<nuint> ImageBase;
        public delegate* unmanaged[Cdecl]<nuint> ImageSize;
        public delegate* unmanaged[Cdecl]<NativeStr, nuint*, int> Symbol;
        public delegate* unmanaged[Cdecl]<NativeStr, long*, int> Constant;
        public delegate* unmanaged[Cdecl]<NativeStr, nuint*, uint, uint*, int> Scan;
        public delegate* unmanaged[Cdecl]<NativeStr, nuint, NativeStr, long*, int> ScanHole;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, NativeStr, SigSummary*, int> LoadSymbols;
        public delegate* unmanaged[Cdecl]<NativeStr, int> UnloadSymbols;
        public delegate* unmanaged[Cdecl]<nuint, void*, nuint, int> Read;
        public delegate* unmanaged[Cdecl]<nuint, void*, nuint, int> Write;
        public delegate* unmanaged[Cdecl]<ulong, nuint, void*, nuint, ulong*, int> Patch;
        public delegate* unmanaged[Cdecl]<ulong, int> Unpatch;
        public delegate* unmanaged[Cdecl]<ulong, nuint, int, delegate* unmanaged[Cdecl]<HookCallRaw*, void*, nuint>, void*, ulong*, int> Detour;
        public delegate* unmanaged[Cdecl]<ulong, nuint, int, delegate* unmanaged[Cdecl]<RegsRaw*, void*, void>, void*, ulong*, int> Mid;
        public delegate* unmanaged[Cdecl]<HookCallRaw*, nuint> CallOriginal;
        public delegate* unmanaged[Cdecl]<ulong, int> Revoke;
        public delegate* unmanaged[Cdecl]<ulong, int> RevokeOwner;
        public delegate* unmanaged[Cdecl]<Stats*, int> GetStats;
    }

    private static Table* _table;
    private static bool _looked;

    public static Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _table = Services.Get<Table>(Name, Version);
            _looked = true;
            return _table;
        }
    }

    /// <summary>Puts <paramref name="table"/> in place of the core's (null: look the core's up again). For unit tests
    /// that drive the managed side against a fake registry; nothing in Kiln calls it.</summary>
    internal static void UseForTests(Table* table)
    {
        _table = table;
        _looked = table != null;
    }

    public static Table* Require(string api)
    {
        var t = T;
        if (t == null)
        {
            HooksCaps.Rows.Ensure();
            throw ServiceCaps.Unavailable(HooksCaps.Service, $"{api}: kiln.hooks is unavailable on this build");
        }
        return t;
    }

    public static string Describe(int rc) => rc switch
    {
        Ok => "ok",
        InvalidArgument => "invalid argument",
        NotFound => "not found",
        Unavailable => "unavailable",
        WrongThread => "wrong thread",
        BufferTooSmall => "buffer too small",
        _ => $"error {rc}",
    };

    /// <summary>Runs <paramref name="f"/> with <paramref name="s"/> as a UTF-8 (ptr,len) the native side can read.</summary>
    public static T WithUtf8<T>(string s, Func<NativeStr, T> f)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        fixed (byte* p = bytes) return f(new NativeStr { Ptr = p, Len = (nuint)bytes.Length });
    }
}
