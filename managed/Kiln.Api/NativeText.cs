// SPDX-License-Identifier: MIT
using System.Text;

namespace Kiln;

/// <summary>Text from a native call that reports its full length and fills what fits.</summary>
internal static unsafe class NativeText
{
    internal delegate int Call(byte* buf, nuint cap, nuint* len);

    /// <summary>Asks for the length, then the text. The text can grow between two calls (a counter passes a power of ten, a new entry), so the length from the call that filled
    /// the buffer decides, and a text that no longer fits is asked for again. <paramref name="rc"/> is the result of the call that filled the buffer.</summary>
    internal static string Read(Call call, out int rc)
    {
        nuint len = 0;
        call(null, 0, &len);
        while (true)
        {
            var buf = new byte[(int)len + 1024];
            fixed (byte* b = buf) rc = call(b, (nuint)buf.Length, &len);
            if ((int)len < buf.Length) return Encoding.UTF8.GetString(buf, 0, (int)len);
        }
    }
}
