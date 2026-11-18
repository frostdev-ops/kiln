// SPDX-License-Identifier: MIT
// ModPrefetch: the files the first mod load and the first service tick read on the game's thread, read at boot on a
// background thread (a hard disk the game loads from made them slow there); and the split of a mod's load time in its
// "loaded in" line (Kiln's checks, its assemblies, OnLoad).
using System.Text.RegularExpressions;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed partial class ModPrefetchTests : ModsFixture
{
    [GeneratedRegex(@"^Info: mod hello 0\.1\.0 loaded in (?<all>[0-9.]+) ms \(layout check (?<check>[0-9.]+) ms, assemblies (?<code>[0-9.]+) ms, OnLoad (?<onload>[0-9.]+) ms\)")]
    private static partial Regex LoadedLine();

    [Fact]
    public void The_list_has_each_admitted_mods_files_then_Kiln_Game_and_the_shared_assemblies_in_load_order()
    {
        InstallLibSet();
        InstallHello();
        var lib = Path.Combine(ModsDir, "sample-lib");
        File.WriteAllText(Path.Combine(lib, GameTypes.StampFile), "{}");
        WriteManifest("data-only", """{ "id": "data-only", "name": "Data", "version": "1.0.0", "apiVersion": "0.1.0" }""");
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.PrepareGameplayHooks();
        var files = mods.PrefetchFiles().ToList();
        string Shared(string n) => Path.Combine(GameTypes.HostDirectory, n + ".dll");
        int At(string path) => files.IndexOf(path) is var i and >= 0 ? i : throw new Xunit.Sdk.XunitException($"{path} is not listed:\n{string.Join("\n", files)}");
        var entry = Path.Combine(lib, "SampleLib.dll");
        // the early services first (they start right before the mods load), then each mod's stamp, entry and symbols
        Assert.True(At(Shared("Kiln.World")) < At(Path.Combine(lib, GameTypes.StampFile)));
        Assert.True(At(Path.Combine(lib, GameTypes.StampFile)) < At(entry));
        Assert.True(At(entry) < At(Path.ChangeExtension(entry, ".pdb")));
        At(Path.Combine(lib, "SampleLib.Api.dll"));                                   // a helper (the lib's export)
        At(Path.Combine(ModsDir, "sample-consumer", "SampleConsumer.dll"));
        Assert.DoesNotContain(files, f => f.StartsWith(Path.Combine(ModsDir, "data-only"), StringComparison.Ordinal));  // no code: nothing to read
        // Kiln.Game after the mods' files, then the shared assemblies the mods reference and those the first tick starts
        var game = GameTypes.PathIn(GameTypes.HostDirectory, GameTypes.ConfiguredVariant)!;
        Assert.True(At(Path.ChangeExtension(entry, ".pdb")) < At(game));
        Assert.True(At(game) < At(Shared("Kiln.Example")));                            // Hello references it
        Assert.DoesNotContain(Shared("Kiln.Api"), files);                              // loaded at boot
        Assert.True(At(Shared("Kiln.Example")) <= files.LastIndexOf(Shared("Kiln.Chat")));
        Assert.Equal(Shared(ServiceTick.AutoStart[^1].Assembly), files[^1]);
        Assert.DoesNotContain(files, f => Path.GetDirectoryName(f) != GameTypes.HostDirectory && Path.GetFileName(f).StartsWith("Kiln.", StringComparison.Ordinal));
    }

    [Fact]
    public void Reading_takes_each_file_once_and_skips_missing_and_oversized_ones()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Root, "prefetch")).FullName;
        var a = Path.Combine(dir, "a.dll");
        var b = Path.Combine(dir, "b.pdb");
        File.WriteAllBytes(a, new byte[3 << 20]);  // more than one buffer
        File.WriteAllBytes(b, new byte[1000]);
        var huge = Path.Combine(dir, "huge.dll");
        using (var f = File.Create(huge)) f.SetLength(ModPrefetch.MaxFile + 1);
        var r = ModPrefetch.Read([a, b, a, Path.Combine(dir, ".", "b.pdb"), Path.Combine(dir, "missing.dll"), huge]);
        Assert.Equal((2, (3L << 20) + 1000), (r.Files, r.Bytes));
        // nothing stays open: the files can be replaced (an update) and deleted
        File.WriteAllBytes(a, [1]);
        File.Delete(b);
    }

    [Fact]
    public void The_thread_logs_what_it_read_and_is_off_with_KILN_NO_PREFETCH()
    {
        var file = Path.Combine(Root, "x.dll");
        File.WriteAllBytes(file, new byte[2048]);
        var lines = new List<string>();
        void Log(LogLevel l, string t) { lock (lines) lines.Add($"{l}: {t}"); }
        // The thread's own state, read on it while it runs (the list is asked for there): reading IsBackground on the
        // Thread object afterwards races the thread's end and throws once it is dead.
        (bool Background, string? Name, ThreadPriority Priority) seen = default;
        var thread = ModPrefetch.Start(() =>
        {
            var self = Thread.CurrentThread;
            seen = (self.IsBackground, self.Name, self.Priority);
            return [file, Path.Combine(Root, "none.dll")];
        }, Log);
        Assert.NotNull(thread);
        thread.Join();
        Assert.Equal((true, "kiln-host prefetch", ThreadPriority.BelowNormal), seen);
        Assert.Matches(@"^Info: prefetch: 1 file\(s\), 0\.0 MB of the mods' and Kiln's assemblies read in [0-9]+ ms on a background thread", Assert.Single(lines));
        lines.Clear();
        Environment.SetEnvironmentVariable("KILN_NO_PREFETCH", "1");
        try
        {
            Assert.Null(ModPrefetch.Start(() => throw new InvalidOperationException("not called"), Log));
            Assert.Equal(["Info: prefetch: off (KILN_NO_PREFETCH=1)"], lines);
        }
        finally { Environment.SetEnvironmentVariable("KILN_NO_PREFETCH", null); }
        lines.Clear();
        ModPrefetch.Start(() => throw new IOException("the list could not be made"), Log)!.Join();
        Assert.Equal(["Warn: prefetch: stopped (IOException: the list could not be made); the mods load as before"], lines);
    }

    [Fact]
    public void The_loaded_line_splits_the_time_into_Kilns_checks_the_assemblies_and_OnLoad()
    {
        InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        var line = host.Dump().Split('\n').Single(l => l.Contains("mod hello 0.1.0 loaded in", StringComparison.Ordinal));
        var m = LoadedLine().Match(line);
        Assert.True(m.Success, line);
        double Ms(string g) => double.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Ms("check") + Ms("code") + Ms("onload") <= Ms("all") + 0.3, line);  // each part rounded to 0.1
        Xunit.TestContext.Current.SendDiagnosticMessage(line);
    }
}
