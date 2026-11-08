// SPDX-License-Identifier: MIT
// Kiln's own RGBA8 image with pixel access and a PNG codec, with the behaviour EML documents for its `image` module
// (`image.create`, `get_pixel`/`set_pixel`, the packed 0xRRGGBBAA forms, PNG `decode`/`encode`). Written from the PNG
// specification (ISO/IEC 15948) over System.IO.Compression: no code from EML, ShroudForge or kfc-parser.
using System.Buffers.Binary;
using System.IO.Compression;

namespace Kiln;

/// <summary>One pixel: 8 bits per channel, not premultiplied.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    /// <summary>0xRRGGBBAA.</summary>
    public uint Packed => ((uint)R << 24) | ((uint)G << 16) | ((uint)B << 8) | A;

    public static Rgba FromPacked(uint rrggbbaa) => new((byte)(rrggbbaa >> 24), (byte)(rrggbbaa >> 16), (byte)(rrggbbaa >> 8), (byte)rrggbbaa);

    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}{A:x2}";
}

/// <summary>
/// An image of 8-bit RGBA pixels, row by row from the top, 4 bytes per pixel (R, G, B, A). The port of EML's <c>Image</c>:
/// <c>image.create(w, h)</c> is <c>new DataImage(w, h)</c> (transparent black), <c>image.decode</c> and <c>encode</c> are
/// <see cref="DecodePng(ReadOnlySpan{byte})"/> and <see cref="EncodePng"/>, <c>get_pixel</c> and <c>set_pixel</c> (and the
/// packed forms) are <see cref="GetPixel"/>, <see cref="SetPixel(int,int,byte,byte,byte,byte)"/>,
/// <see cref="GetPixelPacked"/> and <see cref="SetPixelPacked"/>. Other file formats (jpg, webp...) and the block-compressed
/// texture formats are not here: .NET libraries do the first, <c>Assets.OverrideTexture</c> encodes the second from a PNG.
/// </summary>
/// <example>
/// <code>
/// var img = new DataImage(256, 256);
/// for (var y = 0; y &lt; 256; y++) for (var x = 0; x &lt; 256; x++) img.SetPixel(x, y, (byte)x, (byte)y, 0x80, 0xFF);
/// Assets.RegisterIcon(itemGuid, img.EncodePng());           // an item icon made in code (client)
/// </code>
/// </example>
public sealed class DataImage
{
    /// <summary>The most pixels an image may have (64 Mpixel: 256 MiB as RGBA8).</summary>
    public const long MaxPixels = 1L << 26;

    private readonly byte[] _rgba;

    /// <summary>A transparent black image (<c>image.create</c>).</summary>
    public DataImage(int width, int height)
    {
        CheckSize(width, height);
        Width = width;
        Height = height;
        _rgba = new byte[checked(width * height * 4)];
    }

    private DataImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The pixel bytes, shared with the image: row-major from the top row, R G B A per pixel.</summary>
    public Span<byte> Pixels => _rgba;

    private static void CheckSize(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "an image is at least 1x1");
        if ((long)width * height > MaxPixels) throw new ArgumentOutOfRangeException(nameof(width), $"an image has at most {MaxPixels} pixels");
    }

    /// <summary>An image over a copy of raw RGBA8 data (<paramref name="width"/> x <paramref name="height"/> x 4 bytes).</summary>
    public static DataImage FromRgba8(ReadOnlySpan<byte> rgba, int width, int height)
    {
        CheckSize(width, height);
        if (rgba.Length != (long)width * height * 4) throw new ArgumentException($"{width}x{height} RGBA8 is {(long)width * height * 4} bytes, not {rgba.Length}", nameof(rgba));
        return new DataImage(width, height, rgba.ToArray());
    }

    /// <summary>The raw RGBA8 bytes (a copy).</summary>
    public byte[] ToRgba8() => (byte[])_rgba.Clone();

    public DataImage Clone() => new(Width, Height, (byte[])_rgba.Clone());

    private int At(int x, int y) => (y * Width + x) * 4;

    private bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>The pixel at (x, y), or null outside the image (EML <c>get_pixel</c> gives nil).</summary>
    public Rgba? GetPixel(int x, int y)
    {
        if (!Inside(x, y)) return null;
        var i = At(x, y);
        return new Rgba(_rgba[i], _rgba[i + 1], _rgba[i + 2], _rgba[i + 3]);
    }

    /// <summary>Sets a pixel. Throws <see cref="ArgumentOutOfRangeException"/> outside the image (EML errors too).</summary>
    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        if (!Inside(x, y)) throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the {Width}x{Height} image");
        var i = At(x, y);
        _rgba[i] = r;
        _rgba[i + 1] = g;
        _rgba[i + 2] = b;
        _rgba[i + 3] = a;
    }

    public void SetPixel(int x, int y, Rgba color) => SetPixel(x, y, color.R, color.G, color.B, color.A);

    /// <summary>The pixel as 0xRRGGBBAA, or null outside the image (EML <c>get_pixel_packed</c>).</summary>
    public uint? GetPixelPacked(int x, int y) => GetPixel(x, y)?.Packed;

    /// <summary>Sets a pixel from 0xRRGGBBAA (EML <c>set_pixel_packed</c>). Throws outside the image.</summary>
    public void SetPixelPacked(int x, int y, uint rrggbbaa) => SetPixel(x, y, Rgba.FromPacked(rrggbbaa));

    /// <summary>Sets every pixel.</summary>
    public void Fill(Rgba color)
    {
        for (var i = 0; i < _rgba.Length; i += 4)
        {
            _rgba[i] = color.R;
            _rgba[i + 1] = color.G;
            _rgba[i + 2] = color.B;
            _rgba[i + 3] = color.A;
        }
    }

    // ---- PNG ----------------------------------------------------------------------------------------------------

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>True when the bytes start with the PNG signature.</summary>
    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(PngSignature);

    /// <summary>Decodes an image file. Only PNG is supported (<see cref="NotSupportedException"/> otherwise).</summary>
    public static DataImage Decode(ReadOnlySpan<byte> data) =>
        IsPng(data) ? DecodePng(data) : throw new NotSupportedException("only PNG images are supported");

    /// <summary>
    /// Decodes a PNG: every colour type and bit depth (grey, RGB, palette, grey+alpha, RGBA; 1, 2, 4, 8 and 16 bits),
    /// the transparency chunk, and Adam7 interlacing. 16-bit samples keep their high byte. Chunk checksums are checked.
    /// Throws <see cref="InvalidDataException"/> for a file that is not a valid PNG.
    /// </summary>
    public static DataImage DecodePng(ReadOnlySpan<byte> png)
    {
        if (!IsPng(png)) throw new InvalidDataException("not a PNG file (bad signature)");
        var pos = 8;
        Header? header = null;
        byte[]? palette = null;
        byte[]? trns = null;
        using var idat = new MemoryStream();
        var sawIdat = false;
        var ended = false;
        while (pos < png.Length && !ended)
        {
            if (png.Length - pos < 12) throw new InvalidDataException("truncated PNG chunk");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[pos..]);
            if (length > int.MaxValue || length > (uint)(png.Length - pos - 12)) throw new InvalidDataException("a PNG chunk is longer than the file");
            var typeAndData = png.Slice(pos + 4, 4 + (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png[(pos + 8 + (int)length)..]);
            if (DataHash.Crc32(typeAndData) != crc) throw new InvalidDataException($"PNG chunk {System.Text.Encoding.ASCII.GetString(typeAndData[..4])} has a bad checksum");
            var type = System.Text.Encoding.ASCII.GetString(typeAndData[..4]);
            var data = typeAndData[4..];
            pos += 12 + (int)length;
            if (header == null && type != "IHDR") throw new InvalidDataException("a PNG starts with an IHDR chunk");
            switch (type)
            {
                case "IHDR":
                    if (header != null) throw new InvalidDataException("two IHDR chunks");
                    header = Header.Parse(data);
                    break;
                case "PLTE":
                    if (sawIdat) throw new InvalidDataException("PLTE after the image data");
                    if (data.Length == 0 || data.Length % 3 != 0 || data.Length > 3 * 256) throw new InvalidDataException("bad PNG palette");
                    palette = data.ToArray();
                    break;
                case "tRNS":
                    if (sawIdat) throw new InvalidDataException("tRNS after the image data");
                    trns = data.ToArray();
                    break;
                case "IDAT":
                    sawIdat = true;
                    idat.Write(data);
                    break;
                case "IEND":
                    ended = true;
                    break;
                default:
                    // a chunk whose first letter is capital is critical: a decoder that does not know it must refuse the file
                    if (type[0] is >= 'A' and <= 'Z') throw new NotSupportedException($"unsupported critical PNG chunk {type}");
                    break;
            }
        }
        if (header is not { } h) throw new InvalidDataException("empty PNG");
        if (!ended) throw new InvalidDataException("PNG has no IEND chunk");
        if (!sawIdat) throw new InvalidDataException("PNG has no image data");
        if (h.ColorType == 3 && palette == null) throw new InvalidDataException("a palette PNG needs a PLTE chunk");
        if (h.ColorType == 3 && palette!.Length / 3 > (1 << h.BitDepth)) throw new InvalidDataException("PNG palette is larger than its bit depth allows");
        return h.Decode(idat.ToArray(), palette, trns);
    }

    private readonly record struct Header(int Width, int Height, int BitDepth, int ColorType, bool Interlaced)
    {
        public int Channels => ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        public int BitsPerPixel => Channels * BitDepth;
        public int BytesPerPixel => Math.Max(1, BitsPerPixel / 8);

        public static Header Parse(ReadOnlySpan<byte> d)
        {
            if (d.Length != 13) throw new InvalidDataException("bad PNG header");
            var w = BinaryPrimitives.ReadUInt32BigEndian(d);
            var ht = BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
            int depth = d[8], color = d[9];
            if (w == 0 || ht == 0 || (long)w * ht > MaxPixels || w > int.MaxValue || ht > int.MaxValue)
                throw new InvalidDataException($"PNG size {w}x{ht} is not supported (at most {MaxPixels} pixels)");
            var ok = color switch
            {
                0 => depth is 1 or 2 or 4 or 8 or 16,
                2 or 4 or 6 => depth is 8 or 16,
                3 => depth is 1 or 2 or 4 or 8,
                _ => false,
            };
            if (!ok) throw new InvalidDataException($"PNG colour type {color} with {depth} bits is not valid");
            if (d[10] != 0 || d[11] != 0) throw new InvalidDataException("unknown PNG compression or filter method");
            if (d[12] > 1) throw new InvalidDataException("unknown PNG interlace method");
            return new Header((int)w, (int)ht, depth, color, d[12] == 1);
        }

        private static readonly (int X0, int Y0, int Dx, int Dy)[] Adam7 =
            [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

        public DataImage Decode(byte[] zlib, byte[]? palette, byte[]? trns)
        {
            var passes = Interlaced ? Adam7 : [(0, 0, 1, 1)];
            // the unfiltered size of every pass
            long total = 0;
            foreach (var (x0, y0, dx, dy) in passes)
            {
                var pw = (Width - x0 + dx - 1) / dx;
                var ph = (Height - y0 + dy - 1) / dy;
                if (pw <= 0 || ph <= 0) continue;
                total += (long)ph * (1 + ((long)pw * BitsPerPixel + 7) / 8);
            }
            if (total > int.MaxValue - 64) throw new InvalidDataException("PNG image data is too large");
            var raw = new byte[total];
            try
            {
                using var z = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
                z.ReadExactly(raw);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("PNG image data ends early");
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
                throw new InvalidDataException("PNG image data is damaged: " + e.Message);
            }

            var image = new DataImage(Width, Height);
            var px = image._rgba;
            var offset = 0;
            foreach (var (x0, y0, dx, dy) in passes)
            {
                var pw = (Width - x0 + dx - 1) / dx;
                var ph = (Height - y0 + dy - 1) / dy;
                if (pw <= 0 || ph <= 0) continue;
                var stride = (int)(((long)pw * BitsPerPixel + 7) / 8);
                var previous = new byte[stride];
                for (var row = 0; row < ph; row++)
                {
                    var filter = raw[offset++];
                    var line = raw.AsSpan(offset, stride);
                    Unfilter(filter, line, previous, BytesPerPixel);
                    offset += stride;
                    var y = y0 + row * dy;
                    for (var i = 0; i < pw; i++) Store(px, (y * Width + x0 + i * dx) * 4, line, i, palette, trns);
                    previous = line.ToArray();
                }
            }
            return image;
        }

        private static void Unfilter(byte filter, Span<byte> line, ReadOnlySpan<byte> up, int bpp)
        {
            switch (filter)
            {
                case 0:
                    break;
                case 1:
                    for (var i = bpp; i < line.Length; i++) line[i] += line[i - bpp];
                    break;
                case 2:
                    for (var i = 0; i < line.Length; i++) line[i] += up[i];
                    break;
                case 3:
                    for (var i = 0; i < line.Length; i++)
                    {
                        var left = i >= bpp ? line[i - bpp] : 0;
                        line[i] += (byte)((left + up[i]) / 2);
                    }
                    break;
                case 4:
                    for (var i = 0; i < line.Length; i++)
                    {
                        int a = i >= bpp ? line[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
                        var p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        line[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }
                    break;
                default:
                    throw new InvalidDataException($"unknown PNG filter type {filter}");
            }
        }

        // The sample c of pixel i in an unfiltered line, as its raw value (up to 16 bits).
        private int Sample(ReadOnlySpan<byte> line, int i, int c)
        {
            switch (BitDepth)
            {
                case 8: return line[i * Channels + c];
                case 16:
                {
                    var at = (i * Channels + c) * 2;
                    return (line[at] << 8) | line[at + 1];
                }
                default:
                {
                    var bit = i * BitDepth;
                    var shift = 8 - BitDepth - (bit & 7);
                    return (line[bit >> 3] >> shift) & ((1 << BitDepth) - 1);
                }
            }
        }

        private byte To8(int sample) => BitDepth switch
        {
            8 => (byte)sample,
            16 => (byte)(sample >> 8),
            _ => (byte)(sample * 255 / ((1 << BitDepth) - 1)),
        };

        private void Store(byte[] px, int at, ReadOnlySpan<byte> line, int i, byte[]? palette, byte[]? trns)
        {
            byte r, g, b, a = 255;
            switch (ColorType)
            {
                case 0:
                {
                    var v = Sample(line, i, 0);
                    r = g = b = To8(v);
                    if (trns is { Length: >= 2 } && v == ((trns[0] << 8) | trns[1])) a = 0;
                    break;
                }
                case 2:
                {
                    int sr = Sample(line, i, 0), sg = Sample(line, i, 1), sb = Sample(line, i, 2);
                    r = To8(sr);
                    g = To8(sg);
                    b = To8(sb);
                    if (trns is { Length: >= 6 } && sr == ((trns[0] << 8) | trns[1]) && sg == ((trns[2] << 8) | trns[3]) && sb == ((trns[4] << 8) | trns[5])) a = 0;
                    break;
                }
                case 3:
                {
                    var index = Sample(line, i, 0);
                    if (index >= palette!.Length / 3) throw new InvalidDataException("PNG pixel uses a palette entry that is not there");
                    r = palette[index * 3];
                    g = palette[index * 3 + 1];
                    b = palette[index * 3 + 2];
                    if (trns != null && index < trns.Length) a = trns[index];
                    break;
                }
                case 4:
                    r = g = b = To8(Sample(line, i, 0));
                    a = To8(Sample(line, i, 1));
                    break;
                default:
                    r = To8(Sample(line, i, 0));
                    g = To8(Sample(line, i, 1));
                    b = To8(Sample(line, i, 2));
                    a = To8(Sample(line, i, 3));
                    break;
            }
            px[at] = r;
            px[at + 1] = g;
            px[at + 2] = b;
            px[at + 3] = a;
        }
    }

    /// <summary>The image as a PNG file: 8-bit RGBA, not interlaced, each row filtered with the best of the five PNG filters.</summary>
    public byte[] EncodePng()
    {
        var stride = Width * 4;
        var filtered = new byte[(stride + 1) * Height];
        var candidates = new byte[5][];
        for (var f = 0; f < 5; f++) candidates[f] = new byte[stride];
        var zero = new byte[stride];
        for (var y = 0; y < Height; y++)
        {
            var line = _rgba.AsSpan(y * stride, stride);
            var up = y == 0 ? zero : _rgba.AsSpan((y - 1) * stride, stride).ToArray();
            var best = 0;
            long bestCost = long.MaxValue;
            for (var f = 0; f < 5; f++)
            {
                Filter(f, line, up, candidates[f]);
                long cost = 0;
                foreach (var v in candidates[f]) cost += Math.Abs((int)(sbyte)v);  // the spec's suggestion: the smallest sum of absolute values
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = f;
                }
            }
            filtered[y * (stride + 1)] = (byte)best;
            candidates[best].CopyTo(filtered, y * (stride + 1) + 1);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(filtered);

        using var output = new MemoryStream();
        output.Write(PngSignature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)Height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // RGBA
        ihdr[10] = ihdr[11] = ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Filter(int type, ReadOnlySpan<byte> line, ReadOnlySpan<byte> up, Span<byte> result)
    {
        const int bpp = 4;
        for (var i = 0; i < line.Length; i++)
        {
            int x = line[i], a = i >= bpp ? line[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
            int predictor;
            switch (type)
            {
                case 0: predictor = 0; break;
                case 1: predictor = a; break;
                case 2: predictor = b; break;
                case 3: predictor = (a + b) / 2; break;
                default:
                {
                    var p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    break;
                }
            }
            result[i] = (byte)(x - predictor);
        }
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body.AsSpan(4));
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        output.Write(word);
        output.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(word, DataHash.Crc32(body));
        output.Write(word);
    }
}
