// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Reads and writes the game process's memory, and applies byte patches (access transformers) that are
/// restored automatically when the mod unloads. Advanced mods only.</summary>
public static unsafe class Memory
{
    /// <summary>Reads <typeparamref name="T"/> from <paramref name="address"/>. Throws when the memory is
    /// unreadable.</summary>
    public static T Read<T>(nuint address) where T : unmanaged
        => TryRead<T>(address, out var v) ? v
            : throw new InvalidOperationException($"Memory.Read<{typeof(T).Name}> at 0x{address:X} faulted");

    public static bool TryRead<T>(nuint address, out T value) where T : unmanaged
    {
        HooksGate.RequireAdvanced("Memory.Read");
        var t = HooksNative.Require("Memory.Read");
        T v = default;
        var rc = t->Read(address, &v, (nuint)sizeof(T));
        value = v;
        return rc == HooksNative.Ok;
    }

    /// <summary>Copies <paramref name="count"/> bytes from <paramref name="address"/>.</summary>
    public static byte[] ReadBytes(nuint address, int count)
    {
        HooksGate.RequireAdvanced("Memory.ReadBytes");
        var t = HooksNative.Require("Memory.ReadBytes");
        var buf = new byte[count];
        fixed (byte* p = buf)
            if (t->Read(address, p, (nuint)count) != HooksNative.Ok)
                throw new InvalidOperationException($"Memory.ReadBytes at 0x{address:X} faulted");
        return buf;
    }

    /// <summary>Writes <typeparamref name="T"/> to <paramref name="address"/> (not restored on unload; use
    /// <see cref="Patch"/> for reversible code edits).</summary>
    public static void Write<T>(nuint address, T value) where T : unmanaged
    {
        HooksGate.RequireAdvanced("Memory.Write");
        var t = HooksNative.Require("Memory.Write");
        if (t->Write(address, &value, (nuint)sizeof(T)) != HooksNative.Ok)
            throw new InvalidOperationException($"Memory.Write<{typeof(T).Name}> at 0x{address:X} faulted");
    }

    /// <summary>Overwrites bytes at <paramref name="address"/>, remembering the originals; restored when the handle
    /// is removed or the mod unloads. The canonical access transformer.</summary>
    public static HookHandle Patch(nuint address, params byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new ArgumentException("no bytes", nameof(bytes));
        var mod = HooksGate.RequireAdvanced("Memory.Patch");
        var t = HooksNative.Require("Memory.Patch");
        ulong native = 0;
        int rc;
        fixed (byte* p = bytes) rc = t->Patch(mod.Owner, address, p, (nuint)bytes.Length, &native);
        if (rc != HooksNative.Ok)
            throw new InvalidOperationException($"Memory.Patch at 0x{address:X}: {HooksNative.Describe(rc)}");
        HooksGate.Track(mod);
        mod.Log.Info($"hooks: patched {bytes.Length} byte(s) at 0x{address:X}");
        return new HookHandle(0, native);
    }
}
