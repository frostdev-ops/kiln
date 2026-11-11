// SPDX-License-Identifier: MIT
namespace Kiln;

// Paths in blocks are portable mod-relative paths. Resolve links before accepting an existing file, including links in
// parent directories: a lexical prefix check alone allows icons/link/outside.png to escape the mod.
internal static class BlockFiles
{
    internal static string InMod(BlockContext ctx, string? name, string field, string? directory = null)
    {
        var relative = name?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\0') || Path.IsPathRooted(relative)
            || relative.Split('/').Contains(".."))
            throw new BlockError(ctx.Block, field, $"\"{name}\": a file in the mod's folder, with a relative path");
        var dir = directory ?? BlocksBridge.ModDirectory(ctx.Mod.Id);
        if (dir.Length == 0) return relative; // no loader (unit tests): the lexical check still applies
        try
        {
            var root = Resolve(Path.GetFullPath(dir));
            var file = Resolve(Path.GetFullPath(Path.Combine(root, relative)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!file.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
                throw new BlockError(ctx.Block, field, $"\"{name}\": a file in the mod's folder, with a relative path (links cannot leave it)");
            if (!File.Exists(file))
                throw new BlockError(ctx.Block, field, $"\"{name}\" is not in the mod's folder ({file}). The build copies the mod's folders (KilnModFolders)");
            return file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new BlockError(ctx.Block, field, $"\"{name}\": cannot read a file in the mod's folder ({e.Message})");
        }
    }

    private static string Resolve(string path, int links = 64)
    {
        if (links == 0) throw new IOException("too many file links");
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null) current = Resolve(info.ResolveLinkTarget(true)!.FullName, links - 1);
        }
        return current;
    }
}
