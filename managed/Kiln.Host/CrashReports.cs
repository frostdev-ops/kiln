// SPDX-License-Identifier: MIT
using System.Text;

namespace Kiln.Host;

/// <summary>
/// Writes <c>kiln/crash-reports/crash-&lt;time&gt;-&lt;kind&gt;.txt</c> (Forge's crash-reports folder): Kiln and game
/// identity, the mod list with versions and states, the exception, and the last lines of <c>kiln.log</c>. Managed
/// kinds: <c>mod-disabled</c> (fault policy), <c>managed-unhandled</c> (an exception that kills the process),
/// <c>host-fault</c> (Kiln.Host itself threw on the tick thread). The native core writes <c>native-fault</c> reports
/// (SEH guards, native/core/src/crash.cpp) in the same format. Never throws.
/// </summary>
internal sealed class CrashReports(string directory, string? logFile, Func<string> system, Func<string> mods,
    Action<LogLevel, string>? log = null)
{
    public const int MaxPerRun = 20;
    public const int Keep = 100;
    private int _written;

    public string Directory => directory;

    public string? Write(string kind, string title, string details)
    {
        if (Interlocked.Increment(ref _written) > MaxPerRun) return null;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var now = DateTime.Now;
            var path = Path.Combine(directory, $"crash-{now:yyyy-MM-dd_HH.mm.ss.fff}-{kind}.txt");
            var sb = new StringBuilder();
            sb.Append("---- Kiln crash report ----\n");
            sb.Append($"Time: {now:yyyy-MM-dd HH:mm:ss.fff} local, {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\n");
            sb.Append($"Kind: {kind}\n");
            sb.Append($"Description: {title}\n\n");
            sb.Append("-- System --\n");
            sb.Append(Safe(system));
            sb.Append("\n-- Mods (load order) --\n");
            sb.Append(Safe(mods));
            sb.Append("\n-- Details --\n");
            sb.Append(Lines(details).TrimEnd()).Append('\n');
            sb.Append("\n-- Last kiln.log lines --\n");
            foreach (var line in logFile is null ? [] : Tail(logFile, 60)) sb.Append(line).Append('\n');
            File.WriteAllText(path + ".tmp", sb.ToString());
            File.Move(path + ".tmp", path, overwrite: true);
            Prune();
            return path;
        }
        catch (Exception e)
        {
            log?.Invoke(LogLevel.Error, $"crash report ({kind}) could not be written: {e.Message}");
            return null;
        }
    }

    /// <summary>"\n" line ends; the core's text-mode log turns a managed "\r\n" into "\r\r\n".</summary>
    private static string Lines(string s) => s.Replace("\r\r\n", "\n").ReplaceLineEndings("\n");

    private static string Safe(Func<string> f)
    {
        try
        {
            return f();
        }
        catch (Exception e)
        {
            return $"(unavailable: {e.Message})\n";
        }
    }

    /// <summary>The last <paramref name="count"/> lines of a file another process keeps open for writing.</summary>
    public static List<string> Tail(string file, int count)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var take = (int)Math.Min(fs.Length, 64 * 1024);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            fs.ReadExactly(buf);
            var lines = Lines(Encoding.UTF8.GetString(buf)).Split('\n').ToList();
            if (take < fs.Length && lines.Count > 0) lines.RemoveAt(0);  // partial first line
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(directory).GetFiles("crash-*.txt").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
        foreach (var f in files.Skip(Keep)) f.Delete();
    }
}
