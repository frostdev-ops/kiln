// SPDX-License-Identifier: MIT
using System.Diagnostics;

namespace Kiln.Host;

/// <summary>
/// Reads, on a background thread started at boot, the files the first mod load and the first service tick read on the
/// game's thread: each admitted mod's layout stamp, assemblies and symbols, this build's Kiln.Game, and Kiln's shared
/// assemblies (the early services, the ones the mods reference, the ones the first service tick starts). The bytes are
/// thrown away; what stays is the system's file cache, so those reads, and the CLR's mapping of the shared assemblies,
/// come from memory when the UI frame (a client) or the first server step (a server) loads the mods.
/// </summary>
/// <remarks>
/// <para>Why: on a hard disk the game is loading from, kiln.log of a player (0.2.0-dev.29.14) showed 1.56 s between the
/// start of the first mod's load and its OnLoad (Kiln.Game, 3.5 MB, loaded at +0.54 s; the stamp check, the mod's
/// assemblies and the shared assemblies its code touched until +1.56 s), while the game's own reads waited 175 ms each
/// (median). The same steps for that mod (Kiln Auto Loot 0.1.5) take about 20 ms with the files in memory, and read
/// 3.6 MB from disk (most of it page faults on mapped assemblies) with a cold cache; after this read, none. Boot came
/// half a minute before that frame.</para>
/// <para>Nothing changes but timing: no assembly is loaded here, no type initialised, nothing written; the load order,
/// the checks and the log lines of the load stay where they were. Files are opened shared for reading, writing and
/// deleting, so an update can still replace them. Budgets: 32 MiB per file (larger ones are skipped), 256 MiB in all.
/// KILN_NO_PREFETCH=1 turns it off. Kiln's assemblies are IL-only: the CLR maps them as data, from the same cache.</para>
/// </remarks>
internal static class ModPrefetch
{
    internal const long MaxFile = 32L << 20, MaxTotal = 256L << 20;

    internal readonly record struct Result(int Files, long Bytes, double Milliseconds);

    /// <summary>Starts the thread. <paramref name="files"/> runs on it (it may check files); null when switched off.</summary>
    public static Thread? Start(Func<IEnumerable<string>> files, Action<LogLevel, string> log)
    {
        if (Environment.GetEnvironmentVariable("KILN_NO_PREFETCH") == "1")
        {
            log(LogLevel.Info, "prefetch: off (KILN_NO_PREFETCH=1)");
            return null;
        }
        var thread = new Thread(() =>
        {
            try
            {
                var r = Read(files());
                log(LogLevel.Info, $"prefetch: {r.Files} file(s), {r.Bytes / 1048576.0:F1} MB of the mods' and Kiln's assemblies read " +
                                   $"in {r.Milliseconds:F0} ms on a background thread (the first mod load reads them from memory)");
            }
            catch (Exception e)
            {
                log(LogLevel.Warn, $"prefetch: stopped ({e.GetType().Name}: {e.Message}); the mods load as before");
            }
        })
        {
            IsBackground = true,
            Name = "kiln-host prefetch",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return thread;
    }

    /// <summary>Reads each file whole, once, in order: missing, unreadable and oversized files are skipped.</summary>
    internal static Result Read(IEnumerable<string> paths)
    {
        var timer = Stopwatch.StartNew();
        var buffer = new byte[1 << 20];
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int files = 0;
        long total = 0;
        foreach (var path in paths)
        {
            if (!seen.Add(Path.GetFullPath(path))) continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
                    FileOptions.SequentialScan);
                var length = stream.Length;
                if (length > MaxFile || total + length > MaxTotal) continue;
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) total += n;
                files++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // not there (a mod without symbols, a service this build does not ship) or not readable: its load reads it as before
            }
        }
        return new Result(files, total, timer.Elapsed.TotalMilliseconds);
    }
}

internal sealed partial class ModManager
{
    // The mods the boot pass admitted (PrepareGameplayHooks), and the assembly names their metadata references.
    private (Manifest Manifest, string Directory)[] _bootAdmitted = [];
    private string[] _bootReferences = [];

    /// <summary>The files <see cref="ModPrefetch"/> reads, in the order the game's thread will want them: the early
    /// services' assemblies, then per admitted mod with code its stamp, entry assembly, symbols and other assemblies,
    /// this build's Kiln.Game, the shared assemblies the mods reference, the ones the first service tick starts.
    /// Builds paths only; existence is the reader's business.</summary>
    internal IEnumerable<string> PrefetchFiles()
    {
        var host = GameTypes.HostDirectory;
        string Shared(string name) => Path.Combine(host, name + ".dll");
        foreach (var (assembly, _) in ServiceTick.EarlyStart) yield return Shared(assembly);
        foreach (var (manifest, directory) in _bootAdmitted)
        {
            if (!manifest.HasCode) continue;
            yield return Path.Combine(directory, GameTypes.StampFile);
            var entry = Path.Combine(directory, manifest.EntryAssembly);
            yield return entry;
            yield return Path.ChangeExtension(entry, ".pdb");
            IEnumerable<string> own;
            try { own = Directory.EnumerateFiles(directory, "*.dll").Concat(Directory.EnumerateFiles(directory, "*.pdb")).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { own = []; }
            foreach (var f in own)
                if (!Path.GetFileName(f).StartsWith("Kiln.", StringComparison.Ordinal)) yield return f;  // a stray copy of a shared one is not loaded
        }
        if (GameTypes.PathIn(host, GameTypes.ConfiguredVariant) is { } game) yield return game;
        var running = new[] { typeof(Kiln.Mod).Assembly.GetName().Name, typeof(ModManager).Assembly.GetName().Name };  // loaded at boot
        foreach (var name in _bootReferences)
            if (name.StartsWith("Kiln.", StringComparison.Ordinal) && !running.Contains(name)) yield return Shared(name);
        foreach (var (assembly, _) in ServiceTick.AutoStart) yield return Shared(assembly);
    }
}
