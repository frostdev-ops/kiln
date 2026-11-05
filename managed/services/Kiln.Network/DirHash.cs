// SPDX-License-Identifier: MIT
// kdh1, Kiln's directory hash (docs/kiln/launcher/contracts.md 5.11; reference schema/fixtures/dirhash/dirhash.py, the
// Rust port kiln_manage::hash::dirhash). The server advertises the kdh1 of each mod players need; the launcher checks a
// modpack's folders against it.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Kiln;

/// <summary>A folder cannot be hashed (symlink inside, bad name, two names equal after NFC, not a folder).</summary>
public sealed class DirHashException(string message) : Exception(message);

/// <summary>kdh1 of a folder: sha256 over <c>path "\0" size "\0" sha256hex "\n"</c> for each regular file, paths
/// relative with <c>/</c>, NFC, sorted by their UTF-8 bytes; entries whose name starts with <c>.</c> are skipped with
/// everything below them; symlinks and reparse points below the root are refused.</summary>
public static class DirHash
{
    public const string Prefix = "kdh1:";
    public const string Empty = "kdh1:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false,
        MatchType = MatchType.Win32,
    };

    /// <summary>kdh1 of <paramref name="root"/>.</summary>
    public static string Compute(string root)
    {
        var files = Files(root);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (key, path) in files)
        {
            long size;
            byte[] sha;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                size = fs.Length;
                sha = SHA256.HashData(fs);
            }
            h.AppendData(key);
            h.AppendData("\0"u8);
            h.AppendData(Encoding.ASCII.GetBytes(size.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            h.AppendData("\0"u8);
            h.AppendData(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(sha)));
            h.AppendData("\n"u8);
        }
        return Prefix + Convert.ToHexStringLower(h.GetHashAndReset());
    }

    /// <summary>A cheap fingerprint of the folder's tree (paths, sizes, write times) to know when to hash again.</summary>
    public static string Signature(string root)
    {
        var sb = new StringBuilder();
        foreach (var (key, path) in Files(root))
        {
            var fi = new FileInfo(path);
            sb.Append(Encoding.UTF8.GetString(key)).Append('\0').Append(fi.Length).Append('\0').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The regular files of the tree, sorted by their kdh1 key (relative NFC path, UTF-8).</summary>
    public static List<(byte[] Key, string Path)> Files(string root)
    {
        if (!Directory.Exists(root)) throw new DirHashException($"{root}: not a folder");
        var list = new List<(byte[] Key, string Path)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(root, "", list, seen);
        list.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        return list;
    }

    /// <summary>NFC of <paramref name="s"/>, null when it is not valid Unicode. Kiln's host runs with invariant
    /// globalization, where string.Normalize leaves non-ASCII text unchanged: on Windows (and Wine) this calls the
    /// OS's NormalizeString instead.</summary>
    internal static string? Nfc(string s)
    {
        var ascii = true;
        foreach (var c in s)
            if (c >= 0x80)
            {
                ascii = false;
                break;
            }
        if (ascii) return s;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var len = NormalizeString(1 /* NormalizationC */, s, s.Length, null, 0);
                for (var tries = 0; len > 0 && tries < 4; tries++)
                {
                    var buf = new char[len];
                    var n = NormalizeString(1, s, s.Length, buf, buf.Length);
                    if (n > 0) return new string(buf, 0, n);
                    var err = Marshal.GetLastPInvokeError();
                    if (err != 122 /* ERROR_INSUFFICIENT_BUFFER */) return err == 1113 /* ERROR_NO_UNICODE_TRANSLATION */ ? null : Managed(s);
                    len = -n > len ? -n : len * 2;
                }
                if (len <= 0 && Marshal.GetLastPInvokeError() == 1113) return null;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
        return Managed(s);

        static string? Managed(string s)
        {
            try
            {
                return s.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int NormalizeString(int normForm, string source, int sourceLength, char[]? destination, int destinationLength);

    private static void Walk(string dir, string rel, List<(byte[] Key, string Path)> list, HashSet<string> seen)
    {
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options))
        {
            var name = e.Name;
            if (name.StartsWith('.')) continue;
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0 || e.LinkTarget != null)
                throw new DirHashException($"{e.FullName}: symlinks are not allowed");
            var nfc = Nfc(name) ?? throw new DirHashException($"{e.FullName}: name is not valid Unicode");
            var path = rel.Length == 0 ? nfc : rel + "/" + nfc;
            if (e is DirectoryInfo d)
            {
                Walk(d.FullName, path, list, seen);
            }
            else if (e is FileInfo f && (f.Attributes & FileAttributes.Device) == 0)
            {
                if (!seen.Add(path)) throw new DirHashException($"{e.FullName}: two names are equal after NFC normalization");
                list.Add((Encoding.UTF8.GetBytes(path), f.FullName));
            }
            else
            {
                throw new DirHashException($"{e.FullName}: not a regular file or folder");
            }
        }
    }
}
