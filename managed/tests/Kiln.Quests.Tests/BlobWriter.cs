// SPDX-License-Identifier: MIT
// Builds game-style blobs for the reader tests: a root struct at 0, arrays and strings appended after it, each referenced
// by {i32 offset from the field, u32 count}, and variants {u32 hash, i32 offset from +4, u32 size}.
using System.Buffers.Binary;
using System.Text;

namespace Kiln.QuestTests;

internal sealed class BlobWriter
{
    private byte[] _b;
    private int _len;

    public BlobWriter(int rootSize)
    {
        _b = new byte[Math.Max(256, rootSize * 2)];
        _len = rootSize;
    }

    public byte[] ToArray() => _b.AsSpan(0, _len).ToArray();

    private void Ensure(int size)
    {
        if (size <= _b.Length) return;
        System.Array.Resize(ref _b, Math.Max(size, _b.Length * 2));
    }

    /// <summary>Appends zeroed space (4-aligned) and returns its offset.</summary>
    public int Alloc(int size)
    {
        var at = (_len + 3) & ~3;
        Ensure(at + size);
        _len = at + size;
        return at;
    }

    public void U8(int at, byte v) => _b[at] = v;
    public void U16(int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(_b.AsSpan(at), v);
    public void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_b.AsSpan(at), v);
    public void I32(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(_b.AsSpan(at), v);
    public void Guid(int at, Guid g) => g.TryWriteBytes(_b.AsSpan(at, 16));

    /// <summary>An array of <paramref name="count"/> elements of <paramref name="stride"/> bytes referenced by the field at
    /// <paramref name="field"/>; returns the first element's offset.</summary>
    public int Array(int field, int count, int stride)
    {
        var at = Alloc(Math.Max(1, count * stride));
        I32(field, at - field);
        U32(field + 4, (uint)count);
        return at;
    }

    public void String(int field, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var at = Alloc(bytes.Length + 1);
        bytes.CopyTo(_b.AsSpan(at));
        I32(field, at - field);
        U32(field + 4, (uint)bytes.Length);
    }

    /// <summary>A variant at <paramref name="field"/> holding <paramref name="size"/> bytes; returns the data's offset.</summary>
    public int Variant(int field, uint hash, int size)
    {
        var at = Alloc(size);
        U32(field, hash);
        I32(field + 4, at - (field + 4));
        U32(field + 8, (uint)size);
        return at;
    }
}
