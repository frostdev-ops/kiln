// SPDX-License-Identifier: MIT
// The one way Kiln's managed side reads a file Kiln.Sdk's bake wrote into a mod (docs/kiln/modding/baked.md). A mod's folder is
// input from the internet, so the read is bounded before anything is allocated: nothing outside the mod's folder, no link
// or reparse point on the way down from it (each folder and the file), at most 4 MiB (the limit kiln.vfx's boot reader
// has for these files). Null for anything else. Kiln.Sdk's build task and bake compile this file too (internal there).
namespace Kiln;

internal static class BakedFiles
{
    public const int MaxBytes = 4 << 20;

    /// <summary>The text of <paramref name="relative"/> under <paramref name="modDirectory"/>, or null: missing, outside the
    /// folder, a link or reparse point on the way, more than <see cref="MaxBytes"/>, or changed while it was read.</summary>
    public static string? Read(string modDirectory, string relative)
    {
        try
        {
            var root = Path.GetFullPath(modDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, cmp)) return null;
            var at = root;
            foreach (var part in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                at = Path.Combine(at, part);
                FileSystemInfo info = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);
                if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            }
            using var f = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            if (f.Length > MaxBytes) return null;
            var buf = new byte[f.Length];
            var n = 0;
            while (n < buf.Length)
            {
                var r = f.Read(buf, n, buf.Length - n);
                if (r <= 0) break;
                n += r;
            }
            if (n != buf.Length || f.ReadByte() != -1) return null;
            return System.Text.Encoding.UTF8.GetString(buf);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
