// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.Hooks.Tests;

/// <summary>A stand-in for the core's kiln.hooks table with the link-slot rule of native/core/src/services/
/// hooks_chain.cpp install(): a revoked link whose kind, function, user pointer and owner equal a new install's is
/// revived (same slot) instead of taking a new one. (The label is the target's symbol or owner name and address, so
/// equal owner and address give an equal label.) Links are never freed, as there.</summary>
internal static unsafe class FakeRegistry
{
    public sealed class Link
    {
        public byte Kind;  // HookCookies.Detour / Mid
        public nint Fn, User;
        public ulong Owner;
        public nuint Address;
        public bool Live;
        public ulong Handle;
    }

    public const nuint Original = 0xDEAD;  // what CallOriginal returns: "the game's function ran"
    static readonly object Gate = new();
    static readonly List<Link> Slots = [];
    static ulong _handles;
    static HooksNative.Table* _table;

    /// <summary>Puts a fresh fake in place of the core's table.</summary>
    public static void Install()
    {
        lock (Gate) Slots.Clear();
        if (_table == null)
        {
            _table = (HooksNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(HooksNative.Table));
            _table->Detour = &Detour;
            _table->Mid = &Mid;
            _table->Revoke = &Revoke;
            _table->RevokeOwner = &RevokeOwner;
            _table->CallOriginal = &CallOriginal;
        }
        HooksNative.UseForTests(_table);
    }

    public static void Uninstall()
    {
        DuringInstall = null;
        HooksNative.UseForTests(null);
    }

    public static int SlotsUsed { get { lock (Gate) return Slots.Count; } }
    public static List<Link> Live { get { lock (Gate) return Slots.Where(l => l.Live).ToList(); } }
    public static List<Link> All { get { lock (Gate) return Slots.ToList(); } }

    /// <summary>Calls a link's handler the way the core's structured dispatch does: fn(call, user).</summary>
    public static nuint CallDetour(Link link, nuint arg0 = 0)
    {
        HooksNative.HookCallRaw call = default;
        call.Size = (uint)sizeof(HooksNative.HookCallRaw);
        call.ArgCount = 4;
        call.Arg0 = arg0;
        return ((delegate* unmanaged[Cdecl]<HooksNative.HookCallRaw*, void*, nuint>)link.Fn)(&call, (void*)link.User);
    }

    public static void CallMid(Link link)
    {
        HooksNative.RegsRaw regs = default;
        ((delegate* unmanaged[Cdecl]<HooksNative.RegsRaw*, void*, void>)link.Fn)(&regs, (void*)link.User);
    }

    /// <summary>Runs inside the native install, before the link is made (tests use it to race a retire).</summary>
    public static Action? DuringInstall;

    static int Add(byte kind, ulong owner, nuint address, nint fn, nint user, ulong* handle)
    {
        DuringInstall?.Invoke();
        lock (Gate)
        {
            var link = Slots.FirstOrDefault(l => !l.Live && l.Kind == kind && l.Fn == fn && l.User == user &&
                                                 l.Owner == owner && l.Address == address);
            if (link is null)
                Slots.Add(link = new Link { Kind = kind, Fn = fn, User = user, Owner = owner, Address = address });
            link.Live = true;
            link.Handle = ++_handles;  // a revived link gets a new handle; the old one no longer revokes it
            *handle = link.Handle;
            return HooksNative.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Detour(ulong owner, nuint address, int priority, delegate* unmanaged[Cdecl]<HooksNative.HookCallRaw*, void*, nuint> fn,
                      void* user, ulong* handle) => Add(HookCookies.Detour, owner, address, (nint)fn, (nint)user, handle);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Mid(ulong owner, nuint address, int priority, delegate* unmanaged[Cdecl]<HooksNative.RegsRaw*, void*, void> fn,
                   void* user, ulong* handle) => Add(HookCookies.Mid, owner, address, (nint)fn, (nint)user, handle);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Revoke(ulong handle)
    {
        lock (Gate)
        {
            var link = Slots.FirstOrDefault(l => l.Live && l.Handle == handle);
            if (link is null) return HooksNative.NotFound;
            link.Live = false;
            return HooksNative.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int RevokeOwner(ulong owner)
    {
        lock (Gate)
            foreach (var l in Slots)
                if (l.Owner == owner) l.Live = false;
        return HooksNative.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static nuint CallOriginal(HooksNative.HookCallRaw* call) => Original;
}
