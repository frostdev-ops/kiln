// SPDX-License-Identifier: MIT
// The game's code as it is in its executable file: unique pattern finds and reads that are not fooled by hooks other
// mods (or Kiln itself) have written over the first bytes of a function. Modelled on Greeneye Loader's host->find /
// host->read (MIT, Greeneye0), which see code "as it was before the loader's hooks"; reading the file gives the same
// answer for every hook in the process, whoever wrote it.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Finds and reads the running executable's code as it is in the exe file, before any hook. A pattern that
/// another mod's detour hid at runtime still matches here, and <see cref="Read"/> returns the bytes a hook replaced.
/// The file is read once (its .text section, with the image's base relocations applied).</summary>
public static class OriginalCode
{
    static readonly Lazy<(PeCode? Code, ulong Base, string Error)> Image = new(Load);

    static (PeCode?, ulong, string) Load()
    {
        try
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var baseAddr = (ulong)(self.MainModule?.BaseAddress ?? 0);
            var code = PeCode.Parse(File.ReadAllBytes(path), baseAddr);
            return (code, baseAddr, "");
        }
        catch (Exception e)
        {
            return (null, 0, e.Message);
        }
    }

    static (PeCode Code, ulong Base) Require()
    {
        var (code, b, err) = Image.Value;
        return code is null ? throw new InvalidOperationException($"OriginalCode: the exe could not be read ({err})") : (code, b);
    }

    /// <summary>True when the exe file could be read and parsed.</summary>
    public static bool Available => Image.Value.Code is not null;

    /// <summary>The single address whose original bytes match <paramref name="pattern"/> ("48 8B ?? 05": hex bytes,
    /// <c>?</c> or <c>??</c> for any byte). Throws when there is not exactly one match in .text.</summary>
    public static nuint FindUnique(string pattern)
        => TryFindUnique(pattern, out var a, out var why) ? a : throw new KeyNotFoundException($"pattern '{pattern}': {why}");

    /// <summary>Like <see cref="FindUnique"/>, without throwing; <paramref name="why"/> says "no match" or
    /// "N+ matches".</summary>
    public static bool TryFindUnique(string pattern, out nuint address, out string why)
    {
        address = 0;
        var (code, b) = Require();
        var rva = code.FindUnique(BytePattern.Parse(pattern), out int count);
        why = count == 0 ? "no match" : count > 1 ? $"{count}+ matches; narrow it" : "";
        if (rva < 0) return false;
        address = (nuint)(b + (ulong)rva);
        return true;
    }

    /// <summary>The bytes at <paramref name="address"/> as they are in the exe file. Throws outside .text.</summary>
    public static byte[] Read(nuint address, int count)
    {
        var (code, b) = Require();
        var buf = new byte[count];
        if ((ulong)address < b || !code.TryRead((ulong)address - b, buf))
            throw new ArgumentOutOfRangeException(nameof(address), "outside the executable's .text section");
        return buf;
    }
}

/// <summary>Byte patterns in the common "48 8B ?? 05" form: two hex digits per byte, <c>?</c> or <c>??</c> for any
/// byte, spaces optional between bytes. Entries are 0-255, or -1 for a wildcard.</summary>
public static class BytePattern
{
    public static short[] Parse(string pattern)
    {
        var list = new List<short>();
        for (int i = 0; i < pattern.Length;)
        {
            char c = pattern[i];
            if (c == ' ') { ++i; continue; }
            if (c == '?') { list.Add(-1); i += i + 1 < pattern.Length && pattern[i + 1] == '?' ? 2 : 1; continue; }
            if (i + 1 >= pattern.Length || !Uri.IsHexDigit(c) || !Uri.IsHexDigit(pattern[i + 1]))
                throw new FormatException($"bad byte at {i} in pattern '{pattern}'");
            list.Add((short)Convert.ToByte(pattern.Substring(i, 2), 16));
            i += 2;
        }
        if (list.Count == 0 || list[0] < 0) throw new FormatException($"pattern '{pattern}' must start with a byte");
        return [.. list];
    }
}

/// <summary>The .text section of a PE32+ file as it is mapped at <c>loadedBase</c>: its bytes, with the image's
/// DIR64 base relocations applied for the difference between the preferred and the actual base.</summary>
public sealed class PeCode
{
    public uint TextRva { get; }
    public byte[] Text { get; }

    PeCode(uint rva, byte[] text) { TextRva = rva; Text = text; }

    public static PeCode Parse(ReadOnlySpan<byte> file, ulong loadedBase)
    {
        if (file.Length < 0x100 || file[0] != 'M' || file[1] != 'Z') throw new FormatException("not a PE file");
        int pe = BinaryPrimitives.ReadInt32LittleEndian(file[0x3C..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(file[pe..]) != 0x4550) throw new FormatException("no PE signature");
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(file[(pe + 6)..]);
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(file[(pe + 20)..]);
        int opt = pe + 24;
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[opt..]) != 0x20B) throw new FormatException("not PE32+");
        ulong preferred = BinaryPrimitives.ReadUInt64LittleEndian(file[(opt + 24)..]);
        uint relocRva = BinaryPrimitives.ReadUInt32LittleEndian(file[(opt + 112 + 5 * 8)..]);
        uint relocSize = BinaryPrimitives.ReadUInt32LittleEndian(file[(opt + 112 + 5 * 8 + 4)..]);
        var secs = new List<(string Name, uint Va, uint VSize, uint Raw, uint RawSize)>();
        for (int i = 0; i < sections; ++i)
        {
            int s = opt + optSize + i * 40;
            var name = System.Text.Encoding.ASCII.GetString(file.Slice(s, 8)).TrimEnd('\0');
            secs.Add((name, BinaryPrimitives.ReadUInt32LittleEndian(file[(s + 12)..]), BinaryPrimitives.ReadUInt32LittleEndian(file[(s + 8)..]),
                      BinaryPrimitives.ReadUInt32LittleEndian(file[(s + 20)..]), BinaryPrimitives.ReadUInt32LittleEndian(file[(s + 16)..])));
        }
        var text = secs.Find(x => x.Name == ".text");
        if (text.Name is null) throw new FormatException("no .text section");
        var bytes = new byte[text.VSize];
        int n = (int)Math.Min(text.VSize, text.RawSize);
        file.Slice((int)text.Raw, n).CopyTo(bytes);

        long delta = (long)(loadedBase - preferred);
        if (delta != 0 && relocRva != 0)
        {
            int Off(uint rva)
            {
                foreach (var x in secs)
                    if (rva >= x.Va && rva < x.Va + Math.Max(x.VSize, x.RawSize)) return (int)(x.Raw + (rva - x.Va));
                return -1;
            }
            int at = Off(relocRva), end = at + (int)relocSize;
            while (at >= 0 && at + 8 <= end && at + 8 <= file.Length)
            {
                uint page = BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);
                int block = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);
                if (block < 8) break;
                for (int e = at + 8; e + 2 <= at + block; e += 2)
                {
                    ushort entry = BinaryPrimitives.ReadUInt16LittleEndian(file[e..]);
                    if (entry >> 12 != 10) continue;  // IMAGE_REL_BASED_DIR64
                    long rva = page + (entry & 0xFFF) - (long)text.Va;
                    if (rva < 0 || rva + 8 > bytes.Length) continue;
                    var span = bytes.AsSpan((int)rva, 8);
                    BinaryPrimitives.WriteUInt64LittleEndian(span, BinaryPrimitives.ReadUInt64LittleEndian(span) + (ulong)delta);
                }
                at += block;
            }
        }
        return new PeCode(text.Va, bytes);
    }

    /// <summary>The bytes at <paramref name="rva"/> (relative to the image base); false outside .text.</summary>
    public bool TryRead(ulong rva, Span<byte> dst)
    {
        if (rva < TextRva || rva - TextRva + (ulong)dst.Length > (ulong)Text.Length) return false;
        Text.AsSpan((int)(rva - TextRva), dst.Length).CopyTo(dst);
        return true;
    }

    /// <summary>The RVA of the single match of <paramref name="pattern"/> (see <see cref="BytePattern"/>), or -1.
    /// <paramref name="count"/> is 0, 1, or 2 for "more than one" (the scan stops at the second match).</summary>
    public long FindUnique(ReadOnlySpan<short> pattern, out int count)
    {
        count = 0;
        long found = -1;
        if (pattern.Length == 0 || pattern[0] < 0) return -1;
        var t = Text.AsSpan();
        byte first = (byte)pattern[0];
        for (int i = 0; i + pattern.Length <= t.Length;)
        {
            int j = t[i..(t.Length - pattern.Length + 1)].IndexOf(first);
            if (j < 0) break;
            i += j;
            int k = 1;
            while (k < pattern.Length && (pattern[k] < 0 || t[i + k] == pattern[k])) ++k;
            if (k == pattern.Length)
            {
                if (++count > 1) return -1;
                found = TextRva + i;
            }
            ++i;
        }
        return found;
    }
}

/// <summary>Zeroed native memory that outlives a mod's reload: the same block for the same key for the life of the
/// process (the first call with a key decides the size). For state a hot-reloaded mod must find again, as Greeneye
/// Loader's <c>host->keep</c> gives its mods.</summary>
public static unsafe class Keep
{
    static readonly ConcurrentDictionary<string, (nint Ptr, int Size)> Blocks = new();

    /// <summary>The block for <paramref name="key"/>; <paramref name="size"/> bytes, zeroed, on first use.</summary>
    public static nint Block(string key, int size)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return Blocks.GetOrAdd(key, static (_, s) => ((nint)NativeMemory.AllocZeroed((nuint)Math.Max(s, 1)), Math.Max(s, 1)), size).Ptr;
    }

    /// <summary>The block as a span of <typeparamref name="T"/> (its first-use size, in whole T).</summary>
    public static Span<T> Span<T>(string key, int count) where T : unmanaged
    {
        var p = Block(key, count * sizeof(T));
        return new Span<T>((void*)p, Blocks[key].Size / sizeof(T));
    }
}
