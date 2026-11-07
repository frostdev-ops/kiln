// SPDX-License-Identifier: MIT
// Kiln's own byte buffer with the behaviour EML documents for its `buffer` module (a read cursor "head", a write cursor
// "tail", a byte order, fixed-size reads and writes incl. f16), so EML Lua code ports to C# line by line. Written from
// EML's documentation (docs/src/eml/api/buffer.md, the Buffer definitions' descriptions), not from its code.
using System.Buffers.Binary;
using System.Text;

namespace Kiln;

/// <summary>Byte order of a <see cref="DataBuffer"/>. Game data is little-endian.</summary>
public enum ByteOrder
{
    Little,
    Big,
}

/// <summary>
/// A growable byte buffer with a read position (<see cref="Head"/>) and a write position (<see cref="Tail"/>): writes
/// append at the tail, reads consume from the head, so bytes come back in the order they were written. The port of
/// EML's <c>Buffer</c> (<c>buffer.create</c> = <c>new DataBuffer(capacity)</c>, <c>buffer.wrap</c> =
/// <see cref="Wrap(byte[])"/>; <c>read_resource</c>/<c>write_resource</c> have no port: typed game data in Kiln goes
/// through Kiln.Assets and Kiln.Game's generated types).
/// </summary>
public sealed class DataBuffer
{
    private byte[] _data;
    private int _tail;

    public DataBuffer(int initialCapacity = 64) => _data = new byte[Math.Max(0, initialCapacity)];

    private DataBuffer(byte[] data, int tail)
    {
        _data = data;
        _tail = tail;
    }

    /// <summary>A buffer over a copy of <paramref name="bytes"/>: head 0, tail at the end.</summary>
    public static DataBuffer Wrap(byte[] bytes) => new((byte[])bytes.Clone(), bytes.Length);

    /// <summary>A buffer over the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static DataBuffer Wrap(string text) => Wrap(Encoding.UTF8.GetBytes(text));

    /// <summary>Read position. Setting it past the tail throws.</summary>
    public int Head
    {
        get;
        set => field = value >= 0 && value <= Tail ? value : throw new ArgumentOutOfRangeException(nameof(Head));
    }

    /// <summary>Write position (end of the data). Setting it moves the end; growing it zero-fills.</summary>
    public int Tail
    {
        get => _tail;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(Tail));
            int previous = _tail;
            if (value > previous) Reserve(value - previous);
            if (value > previous) _data.AsSpan(previous, value - previous).Clear();
            _tail = value;
            if (Head > _tail) Head = _tail;
        }
    }

    /// <summary>Bytes between head and tail.</summary>
    public int Remaining => Tail - Head;

    public int Capacity => _data.Length;

    public ByteOrder Order { get; set; } = ByteOrder.Little;

    /// <summary>Head and tail back to 0 (the bytes stay allocated).</summary>
    public void Reset()
    {
        Head = 0;
        Tail = 0;
    }

    /// <summary>Makes room for at least <paramref name="length"/> more bytes after the tail.</summary>
    public void Reserve(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        int required = checked(Tail + length);
        if (required <= _data.Length) return;
        Array.Resize(ref _data, (int)Math.Max(required, Math.Min((long)_data.Length * 2, Array.MaxLength)));
    }

    /// <summary>Moves the head forward by <paramref name="length"/> bytes.</summary>
    public void Skip(int length) => Head = checked(Head + length);

    /// <summary>Appends <paramref name="length"/> bytes of <paramref name="source"/> from its head + offset (all remaining by default).</summary>
    public void Copy(DataBuffer source, int offset = 0, int? length = null)
    {
        int n = length ?? source.Remaining - offset;
        if (offset < 0 || n < 0 || n > source.Remaining - offset) throw new ArgumentOutOfRangeException(nameof(length));
        WriteBytes(source._data.AsSpan(source.Head + offset, n));
    }

    /// <summary>The bytes between head and tail.</summary>
    public byte[] ToArray() => _data.AsSpan(Head, Remaining).ToArray();

    /// <summary>The bytes between head and tail as UTF-8 text (EML <c>to_string</c>).</summary>
    public override string ToString() => Encoding.UTF8.GetString(_data, Head, Remaining);

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n > Remaining) throw new InvalidOperationException($"read past the end ({n} bytes wanted, {Remaining} left)");
        var s = _data.AsSpan(Head, n);
        Head += n;
        return s;
    }

    private Span<byte> Put(int n)
    {
        Reserve(n);
        var s = _data.AsSpan(Tail, n);
        Tail += n;
        return s;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Put(bytes.Length));
    public byte[] ReadBytes(int length) => Take(length).ToArray();

    private bool Little => Order == ByteOrder.Little;

    public bool ReadBool() => Take(1)[0] != 0;
    public sbyte ReadI8() => (sbyte)Take(1)[0];
    public byte ReadU8() => Take(1)[0];
    public short ReadI16() => Little ? BinaryPrimitives.ReadInt16LittleEndian(Take(2)) : BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public ushort ReadU16() => Little ? BinaryPrimitives.ReadUInt16LittleEndian(Take(2)) : BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public int ReadI32() => Little ? BinaryPrimitives.ReadInt32LittleEndian(Take(4)) : BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public uint ReadU32() => Little ? BinaryPrimitives.ReadUInt32LittleEndian(Take(4)) : BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public long ReadI64() => Little ? BinaryPrimitives.ReadInt64LittleEndian(Take(8)) : BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public ulong ReadU64() => Little ? BinaryPrimitives.ReadUInt64LittleEndian(Take(8)) : BinaryPrimitives.ReadUInt64BigEndian(Take(8));
    public Half ReadF16() => Little ? BinaryPrimitives.ReadHalfLittleEndian(Take(2)) : BinaryPrimitives.ReadHalfBigEndian(Take(2));
    public float ReadF32() => Little ? BinaryPrimitives.ReadSingleLittleEndian(Take(4)) : BinaryPrimitives.ReadSingleBigEndian(Take(4));
    public double ReadF64() => Little ? BinaryPrimitives.ReadDoubleLittleEndian(Take(8)) : BinaryPrimitives.ReadDoubleBigEndian(Take(8));

    /// <summary><paramref name="length"/> bytes as UTF-8 text.</summary>
    public string ReadString(int length) => Encoding.UTF8.GetString(Take(length));

    public void WriteBool(bool v) => Put(1)[0] = v ? (byte)1 : (byte)0;
    public void WriteI8(sbyte v) => Put(1)[0] = (byte)v;
    public void WriteU8(byte v) => Put(1)[0] = v;
    public void WriteI16(short v) { if (Little) BinaryPrimitives.WriteInt16LittleEndian(Put(2), v); else BinaryPrimitives.WriteInt16BigEndian(Put(2), v); }
    public void WriteU16(ushort v) { if (Little) BinaryPrimitives.WriteUInt16LittleEndian(Put(2), v); else BinaryPrimitives.WriteUInt16BigEndian(Put(2), v); }
    public void WriteI32(int v) { if (Little) BinaryPrimitives.WriteInt32LittleEndian(Put(4), v); else BinaryPrimitives.WriteInt32BigEndian(Put(4), v); }
    public void WriteU32(uint v) { if (Little) BinaryPrimitives.WriteUInt32LittleEndian(Put(4), v); else BinaryPrimitives.WriteUInt32BigEndian(Put(4), v); }
    public void WriteI64(long v) { if (Little) BinaryPrimitives.WriteInt64LittleEndian(Put(8), v); else BinaryPrimitives.WriteInt64BigEndian(Put(8), v); }
    public void WriteU64(ulong v) { if (Little) BinaryPrimitives.WriteUInt64LittleEndian(Put(8), v); else BinaryPrimitives.WriteUInt64BigEndian(Put(8), v); }
    public void WriteF16(Half v) { if (Little) BinaryPrimitives.WriteHalfLittleEndian(Put(2), v); else BinaryPrimitives.WriteHalfBigEndian(Put(2), v); }
    public void WriteF32(float v) { if (Little) BinaryPrimitives.WriteSingleLittleEndian(Put(4), v); else BinaryPrimitives.WriteSingleBigEndian(Put(4), v); }
    public void WriteF64(double v) { if (Little) BinaryPrimitives.WriteDoubleLittleEndian(Put(8), v); else BinaryPrimitives.WriteDoubleBigEndian(Put(8), v); }

    /// <summary>The UTF-8 bytes of <paramref name="text"/> (no length prefix, no terminator).</summary>
    public void WriteString(string text) => WriteBytes(Encoding.UTF8.GetBytes(text));
}
