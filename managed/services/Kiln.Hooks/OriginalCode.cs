// SPDX-License-Identifier: MIT
// The game's code as it is in its executable file: unique pattern finds and reads that are not fooled by hooks other
// mods (or Kiln itself) have written over the first bytes of a function. Modelled on Greeneye Loader's host->find /
// host->read (MIT, Greeneye0), which see code "as it was before the loader's hooks"; reading the file gives the same
// answer for every hook in the process, whoever wrote it.
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Finds and reads the running executable's code as it is in the exe file, before any hook. A pattern that
/// another mod's detour hid at runtime still matches here, and <see cref="Read"/> returns the bytes a hook replaced.
/// The file is read once (its .text section, with the image's base relocations applied). <see cref="FindUnique"/>,
/// <see cref="TryFindUnique"/> and <see cref="Read"/> are for advanced mods only, like the rest of kiln.hooks.</summary>
public static class OriginalCode
{
    static readonly Lazy<(PeCode? Code, ulong Base, string Error)> Image = new(Load);

    static (PeCode?, ulong, string) Load()
    {
        try
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var module = self.MainModule ?? throw new InvalidOperationException("no main module");
            var baseAddr = (ulong)module.BaseAddress;
            if (!OperatingSystem.IsWindows() || baseAddr == 0) throw new InvalidOperationException("not a Windows PE process");
            var file = File.ReadAllBytes(path);
            var code = PeCode.Parse(file, baseAddr, ReadImageHeaders(module.BaseAddress, module.ModuleMemorySize));
            return (code, baseAddr, "");
        }
        catch (Exception e)
        {
            return (null, 0, e.Message);
        }
    }

    // Read sizes from the mapped image, rather than trusting the file's e_lfanew or SizeOfHeaders to choose a
    // native read. Comparing all headers also refuses a different build copied over the executable on disk.
    static byte[] ReadImageHeaders(nint image, int imageSize)
    {
        byte[] Copy(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > imageSize || count > imageSize - offset)
                throw new FormatException("mapped PE header outside image");
            var bytes = new byte[count];
            Marshal.Copy(image + offset, bytes, 0, count);
            return bytes;
        }
        var dos = Copy(0, 64);
        if (dos[0] != 'M' || dos[1] != 'Z') throw new FormatException("mapped image is not PE");
        int pe = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
        if (pe < 64 || pe > 1024 * 1024 - 88) throw new FormatException("invalid mapped PE header offset");
        var nt = Copy(pe, 88);
        if (BinaryPrimitives.ReadUInt32LittleEndian(nt) != 0x4550) throw new FormatException("invalid mapped PE signature");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(24 + 60));
        if (size < (uint)pe + 88 || size > 1024 * 1024) throw new FormatException("invalid mapped PE header size");
        return Copy(0, (int)size);
    }

    static (PeCode Code, ulong Base) Require()
    {
        var (code, b, err) = Image.Value;
        return code is null ? throw new InvalidOperationException($"OriginalCode: the exe could not be read ({err})") : (code, b);
    }

    /// <summary>True when the exe file could be read and parsed.</summary>
    public static bool Available => Image.Value.Code is not null;

    internal const string Why = "This raw entry point is powerful and build-specific";

    /// <summary>The single address whose original bytes match <paramref name="pattern"/> ("48 8B ?? 05": hex bytes,
    /// <c>?</c> or <c>??</c> for any byte). Throws when there is not exactly one match in .text. Advanced mods only
    /// (<c>"advanced": true</c> in kiln.mod.json): an ordinary mod gets <see cref="InvalidOperationException"/>.</summary>
    /// <remarks>Call from OnLoad or a Kiln callback that sets the calling mod context, not an unattributed background
    /// thread or a raw native job handler. Even an advanced mod is refused when that thread has no current mod.
    /// Cache the result during OnLoad for those threads. An UnmanagedCallersOnly callback must catch its own exceptions;
    /// never let this gate's InvalidOperationException escape into native code.</remarks>
    [AdvancedApi]
    public static nuint FindUnique(string pattern)
    {
        HooksGate.RequireAdvanced("OriginalCode.FindUnique", Why);
        return TryFindUniqueCore(pattern, out var a, out var why) ? a : throw new KeyNotFoundException($"pattern '{pattern}': {why}");
    }

    /// <summary>Like <see cref="FindUnique"/>, without throwing; <paramref name="why"/> says "no match",
    /// "N+ matches", or why the pattern or executable could not be read. Advanced mods only (the gate throws).</summary>
    /// <remarks>Requires the calling mod context on this thread, as <see cref="FindUnique"/> does. An advanced mod on an
    /// unattributed background thread or in a raw native job handler is also refused: cache the result during OnLoad.
    /// The gate throws even though a failed search returns false. An UnmanagedCallersOnly callback must catch its own
    /// exceptions; never let the gate's InvalidOperationException escape into native code.</remarks>
    [AdvancedApi]
    public static bool TryFindUnique(string pattern, out nuint address, out string why)
    {
        HooksGate.RequireAdvanced("OriginalCode.TryFindUnique", Why);
        return TryFindUniqueCore(pattern, out address, out why);
    }

    static bool TryFindUniqueCore(string pattern, out nuint address, out string why)
    {
        address = 0;
        var (code, b, error) = Image.Value;
        if (code is null) { why = $"the exe could not be read ({error})"; return false; }
        short[] parsed;
        try { parsed = BytePattern.Parse(pattern); }
        catch (Exception e) when (e is ArgumentException or FormatException) { why = e.Message; return false; }
        var rva = code.FindUnique(parsed, out int count);
        why = count == 0 ? "no match" : count > 1 ? $"{count}+ matches; narrow it" : "";
        if (rva < 0) return false;
        address = (nuint)(b + (ulong)rva);
        return true;
    }

    /// <summary>The bytes at <paramref name="address"/> as they are in the exe file. Throws outside .text. Advanced mods
    /// only (<c>"advanced": true</c> in kiln.mod.json): an ordinary mod gets <see cref="InvalidOperationException"/>.</summary>
    /// <remarks>Call from OnLoad or a Kiln callback that sets the calling mod context, not an unattributed background
    /// thread or a raw native job handler. Even an advanced mod is refused when that thread has no current mod.
    /// Cache the bytes during OnLoad for those threads. An UnmanagedCallersOnly callback must catch its own exceptions;
    /// never let this gate's InvalidOperationException escape into native code.</remarks>
    [AdvancedApi]
    public static byte[] Read(nuint address, int count)
    {
        HooksGate.RequireAdvanced("OriginalCode.Read", Why);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var (code, b) = Require();
        if (count > code.Text.Length) throw new ArgumentOutOfRangeException(nameof(count), "larger than the executable's .text section");
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
        ArgumentNullException.ThrowIfNull(pattern);
        var list = new List<short>();
        for (int i = 0; i < pattern.Length;)
        {
            char c = pattern[i];
            if (char.IsWhiteSpace(c)) { ++i; continue; }
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

    readonly record struct Section(string Name, uint Va, uint Size, uint Raw, uint RawSize);

    static ReadOnlySpan<byte> Range(ReadOnlySpan<byte> file, ulong offset, ulong count)
    {
        if (offset > (ulong)file.Length || count > (ulong)file.Length - offset)
            throw new FormatException("PE range outside file");
        return file.Slice((int)offset, (int)count);
    }
    static ushort U16(ReadOnlySpan<byte> file, ulong offset) => BinaryPrimitives.ReadUInt16LittleEndian(Range(file, offset, 2));
    static uint U32(ReadOnlySpan<byte> file, ulong offset) => BinaryPrimitives.ReadUInt32LittleEndian(Range(file, offset, 4));

    /// <summary>Parses an x64 PE32+ image. Rejects malformed ranges and code sections larger than 512 MiB before
    /// allocating a snapshot. Optional mapped headers must match the file, preventing addresses from another build.</summary>
    public static PeCode Parse(ReadOnlySpan<byte> file, ulong loadedBase) => ParseCore(file, loadedBase, default, false);

    /// <summary>Like <see cref="Parse(ReadOnlySpan{byte}, ulong)"/>, requiring all mapped PE headers to match the
    /// file before using its bytes. This detects a different executable build at the same file path.</summary>
    public static PeCode Parse(ReadOnlySpan<byte> file, ulong loadedBase, ReadOnlySpan<byte> mappedHeaders)
        => ParseCore(file, loadedBase, mappedHeaders, true);

    static PeCode ParseCore(ReadOnlySpan<byte> file, ulong loadedBase, ReadOnlySpan<byte> mappedHeaders, bool verifyHeaders)
    {
        if (file.Length < 64 || file[0] != 'M' || file[1] != 'Z') throw new FormatException("not a PE file");
        ulong pe = U32(file, 0x3C);
        if (pe < 64 || U32(file, pe) != 0x4550) throw new FormatException("no PE signature");
        Range(file, pe, 24);
        if (U16(file, pe + 4) != 0x8664) throw new FormatException("not an x64 PE image");
        int sections = U16(file, pe + 6), optSize = U16(file, pe + 20);
        ulong opt = pe + 24;
        var optional = Range(file, opt, (ulong)optSize);
        if (optSize < 112 || U16(optional, 0) != 0x20B) throw new FormatException("not a complete PE32+ header");
        ulong preferred = BinaryPrimitives.ReadUInt64LittleEndian(optional[24..]);
        uint imageSize = U32(optional, 56), headerSize = U32(optional, 60), directories = U32(optional, 108);
        if (directories > (optSize - 112) / 8) throw new FormatException("data directories outside optional header");
        ulong sec0 = opt + (ulong)optSize, secEnd = sec0 + (ulong)sections * 40;
        if (sections == 0 || headerSize < secEnd || imageSize < headerSize || loadedBase > ulong.MaxValue - imageSize)
            throw new FormatException("invalid image or header size");
        Range(file, 0, headerSize);
        if (verifyHeaders && (mappedHeaders.Length != headerSize || !file[..(int)headerSize].SequenceEqual(mappedHeaders)))
            throw new FormatException("the executable file headers differ from the running image");
        var secs = new List<Section>(sections);
        for (int i = 0; i < sections; ++i)
        {
            var raw = Range(file, sec0 + (ulong)i * 40, 40);
            string name = System.Text.Encoding.ASCII.GetString(raw[..8]).TrimEnd('\0');
            uint vsize = U32(raw, 8), va = U32(raw, 12), rsize = U32(raw, 16), rptr = U32(raw, 20);
            uint size = vsize == 0 ? rsize : vsize;
            ulong extent = Math.Max(size, rsize);
            if (extent != 0 && (va < headerSize || (ulong)va + extent > imageSize))
                throw new FormatException("section outside image");
            if (rsize != 0)
            {
                if (rptr < headerSize) throw new FormatException("section overlaps headers");
                Range(file, rptr, rsize);
            }
            secs.Add(new Section(name, va, size, rptr, rsize));
        }
        ulong previousEnd = headerSize;
        foreach (var section in secs.OrderBy(x => x.Va))
        {
            uint size = Math.Max(section.Size, section.RawSize);
            if (size == 0) continue;
            if (section.Va < previousEnd) throw new FormatException("overlapping image sections");
            previousEnd = (ulong)section.Va + size;
        }
        var texts = secs.Where(x => x.Name == ".text").ToArray();
        if (texts.Length != 1 || texts[0].Size == 0 || texts[0].Size > 512 * 1024 * 1024)
            throw new FormatException("missing, duplicate or oversized .text section");
        var text = texts[0];

        // Validate the complete relocation directory even when no relocation is needed for this base. In
        // particular, a directory cannot use a section's zero-filled virtual tail as bytes from the next file data.
        uint relocRva = directories > 5 ? U32(optional, 112 + 5 * 8) : 0;
        uint relocSize = directories > 5 ? U32(optional, 112 + 5 * 8 + 4) : 0;
        if ((relocRva == 0) != (relocSize == 0)) throw new FormatException("incomplete relocation directory");
        ReadOnlySpan<byte> reloc = default;
        if (relocRva != 0)
        {
            bool found = false;
            foreach (var section in secs)
                if (relocRva >= section.Va && (ulong)relocRva - section.Va <= section.RawSize &&
                    relocSize <= (ulong)section.RawSize - (relocRva - section.Va))
                {
                    reloc = Range(file, (ulong)section.Raw + relocRva - section.Va, relocSize);
                    found = true;
                    break;
                }
            if (!found) throw new FormatException("relocation directory is not backed by file bytes");
        }
        for (int at = 0; at < reloc.Length;)
        {
            if (reloc.Length - at < 8) throw new FormatException("truncated relocation block");
            uint page = U32(reloc, (ulong)at), block = U32(reloc, (ulong)at + 4);
            if (block < 8 || (block & 1) != 0 || block > reloc.Length - at)
                throw new FormatException("invalid relocation block size");
            int end = at + (int)block;
            for (int e = at + 8; e < end; e += 2)
            {
                ushort entry = U16(reloc, (ulong)e);
                int kind = entry >> 12;
                if (kind == 0) continue; // IMAGE_REL_BASED_ABSOLUTE (padding)
                if (kind != 10) throw new FormatException("unsupported x64 base relocation");
                ulong target = (ulong)page + (uint)(entry & 0xFFF);
                if (target + 8 > imageSize) throw new FormatException("relocation target outside image");
                if (target < (ulong)text.Va + text.Size && target + 8 > text.Va)
                {
                    if (target < text.Va || target + 8 > (ulong)text.Va + text.Size)
                        throw new FormatException("relocation straddles .text boundary");
                }
            }
            at = end;
        }
        var bytes = new byte[text.Size];
        uint copy = Math.Min(text.Size, text.RawSize);
        if (copy != 0) Range(file, text.Raw, copy).CopyTo(bytes);
        ulong delta = unchecked(loadedBase - preferred);
        // A second pass applies the already-validated entries without retaining an unbounded list of fixups.
        if (delta != 0)
            for (int at = 0; at < reloc.Length;)
            {
                uint page = U32(reloc, (ulong)at);
                int end = at + (int)U32(reloc, (ulong)at + 4);
                for (int e = at + 8; e < end; e += 2)
                {
                    ushort entry = U16(reloc, (ulong)e);
                    if (entry >> 12 != 10) continue;
                    ulong target = (ulong)page + (uint)(entry & 0xFFF);
                    if (target < text.Va || target >= (ulong)text.Va + text.Size) continue;
                    var slot = bytes.AsSpan((int)(target - text.Va), 8);
                    BinaryPrimitives.WriteUInt64LittleEndian(slot, unchecked(BinaryPrimitives.ReadUInt64LittleEndian(slot) + delta));
                }
                at = end;
            }
        return new PeCode(text.Va, bytes);
    }

    /// <summary>The bytes at <paramref name="rva"/> (relative to the image base); false outside .text.</summary>
    public bool TryRead(ulong rva, Span<byte> dst)
    {
        if (rva < TextRva) return false;
        ulong offset = rva - TextRva;
        if (offset > (ulong)Text.Length || (ulong)dst.Length > (ulong)Text.Length - offset) return false;
        Text.AsSpan((int)offset, dst.Length).CopyTo(dst);
        return true;
    }

    /// <summary>The RVA of the single match of <paramref name="pattern"/> (see <see cref="BytePattern"/>), or -1.
    /// <paramref name="count"/> is 0, 1, or 2 for "more than one" (the scan stops at the second match).</summary>
    public long FindUnique(ReadOnlySpan<short> pattern, out int count)
    {
        count = 0;
        long found = -1;
        foreach (short value in pattern)
            if (value < -1 || value > 255) throw new ArgumentOutOfRangeException(nameof(pattern), "expected byte values or -1 wildcards");
        if (pattern.Length == 0 || pattern[0] < 0 || pattern.Length > Text.Length) return -1;
        var t = Text.AsSpan();
        byte first = (byte)pattern[0];
        for (int i = 0; i <= t.Length - pattern.Length;)
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
/// Loader's <c>host->keep</c> gives its mods. Raw native memory: advanced mods only.</summary>
public static unsafe class Keep
{
    static readonly object Gate = new();
    static readonly Dictionary<string, (nint Ptr, int Size)> Blocks = new();

    static (nint Ptr, int Size) GetBlock(string key, int size)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        lock (Gate)
        {
            if (Blocks.TryGetValue(key, out var block)) return block;
            // GetOrAdd may run its allocator many times and discard all but one result. These blocks intentionally
            // live forever, so allocate only after winning the key, while holding the same lock as publication.
            nint ptr = (nint)NativeMemory.AllocZeroed((nuint)Math.Max(size, 1));
            if (ptr == 0) throw new OutOfMemoryException();
            try { Blocks.Add(key, block = (ptr, size)); }
            catch { NativeMemory.Free((void*)ptr); throw; }
            return block;
        }
    }

    /// <summary>The block for <paramref name="key"/>; <paramref name="size"/> bytes, zeroed, on first use. Advanced mods
    /// only (<c>"advanced": true</c> in kiln.mod.json): an ordinary mod gets <see cref="InvalidOperationException"/>.</summary>
    /// <remarks>Call from OnLoad or a Kiln callback that sets the calling mod context, not an unattributed background
    /// thread or a raw native job handler. Even an advanced mod is refused when that thread has no current mod.
    /// Cache the pointer during OnLoad for those threads. An UnmanagedCallersOnly callback must catch its own exceptions;
    /// never let this gate's InvalidOperationException escape into native code.</remarks>
    [AdvancedApi]
    public static nint Block(string key, int size)
    {
        HooksGate.RequireAdvanced("Keep.Block", OriginalCode.Why);
        return GetBlock(key, size).Ptr;
    }

    /// <summary>The block as a span of <typeparamref name="T"/> (its first-use size, in whole T). Advanced mods only.</summary>
    /// <remarks>Requires the calling mod context on this thread, as <see cref="Block"/> does. An advanced mod on an
    /// unattributed background thread or in a raw native job handler is also refused. Cache the pointer from
    /// <see cref="Block"/> during OnLoad and construct a span over it on those threads instead. An UnmanagedCallersOnly
    /// callback must catch its own exceptions; never let the gate's InvalidOperationException escape into native code.</remarks>
    [AdvancedApi]
    public static Span<T> Span<T>(string key, int count) where T : unmanaged
    {
        HooksGate.RequireAdvanced("Keep.Span", OriginalCode.Why);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var block = GetBlock(key, checked(count * sizeof(T)));
        return new Span<T>((void*)block.Ptr, block.Size / sizeof(T));
    }
}
