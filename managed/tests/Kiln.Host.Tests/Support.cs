// SPDX-License-Identifier: MIT
using Kiln.Host;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]  // Events, Lifecycle and the backends are static

namespace Kiln.Host.Tests;

internal sealed class FakeHost : IHostServices, IKilnBackend
{
    public FakeHost() => Backend.Current = this;
    public string Version => "0.1.0-dev";
    public bool IsTickThread => true;
    public bool HasFeature(string name) => name != "no.such.feature";
    public void Log(ulong owner, LogLevel level, string text) => Log(level, text);
    public nint Service(string name, uint minVersion) => 0;
    public readonly List<string> Lines = [];
    public readonly HashSet<ulong> Revoked = [];
    private ulong _next = 1;
    public ulong OwnerCreate(string id) => _next++;
    public void OwnerRevoke(ulong owner) => Revoked.Add(owner);
    public void Log(LogLevel level, string text) { lock (Lines) Lines.Add($"{level}: {text}"); }
    public bool Has(string fragment) { lock (Lines) return Lines.Exists(l => l.Contains(fragment, StringComparison.Ordinal)); }
    public string Dump() { lock (Lines) return string.Join("\n", Lines); }
}

/// <summary>A temporary kiln/{mods,data,logs} with the sample mods available to install.</summary>
public abstract class ModsFixture : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "kiln-tests-" + Guid.NewGuid().ToString("N"));
    protected readonly string ModsDir, DataDir, LogsDir;

    protected ModsFixture()
    {
        ModsDir = Directory.CreateDirectory(Path.Combine(Root, "mods")).FullName;
        DataDir = Directory.CreateDirectory(Path.Combine(Root, "data")).FullName;
        LogsDir = Directory.CreateDirectory(Path.Combine(Root, "logs")).FullName;
        Events.Reset();
        Lifecycle.Phase = ServerPhase.Booting;
        Lifecycle.ModsLoaded = false;
    }

    public virtual void Dispose()
    {
        ModsBackend.Current = null;
        Events.Reset();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    private static readonly Lazy<Dictionary<string, string>> Samples = new(() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "samples.txt"))
            .Where(l => l.Length > 0)
            .ToDictionary(l => Path.GetFileNameWithoutExtension(l), l => Path.GetDirectoryName(l)!));

    /// <summary>Copies a built sample's mod folder (its output minus shared Kiln assemblies) into kiln/mods/<paramref name="folder"/>.</summary>
    protected string Install(string project, string folder)
    {
        var src = Samples.Value[project];
        var dst = Directory.CreateDirectory(Path.Combine(ModsDir, folder)).FullName;
        foreach (var f in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("Kiln.", StringComparison.Ordinal) || name.EndsWith(".deps.json") || name.EndsWith(".runtimeconfig.json")) continue;
            File.Copy(f, Path.Combine(dst, name), overwrite: true);
        }
        return dst;
    }

    protected string InstallHello() => Install("Hello", "hello");

    protected void InstallLibSet()
    {
        Install("SampleLib", "sample-lib");
        Install("SampleConsumer", "sample-consumer");
        Install("SampleTests", "sample-tests");
    }

    protected string WriteManifest(string folder, string json)
    {
        var dir = Directory.CreateDirectory(Path.Combine(ModsDir, folder)).FullName;
        File.WriteAllText(Path.Combine(dir, Manifest.FileName), json);
        return dir;
    }

    internal ModManager NewManager(FakeHost host, ModManagerOptions? options = null)
    {
        var m = new ModManager(host, ModsDir, DataDir, options ?? new ModManagerOptions
        {
            KilnVersion = host.Version, HasFeature = host.HasFeature, StateFile = Path.Combine(LogsDir, "mods.txt"),
            TestResultsDirectory = Path.Combine(LogsDir, "tests"),
        });
        ModsBackend.Current = m;
        return m;
    }

    internal static TickInfo Tick(ulong step) => HostAccess.Tick(step, (uint)step * 2, 2, 0, 40.8);

    /// <summary>Steps the manager until <paramref name="done"/> or <paramref name="max"/> steps.</summary>
    internal static ulong Run(ModManager mods, Func<bool> done, ulong from = 1, int max = 2000, int sleepMs = 2)
    {
        var s = from;
        for (var i = 0; i < max && !done(); i++, s++)
        {
            mods.ProcessRequests();
            mods.Pre(Tick(s), []);
            if (sleepMs > 0) Thread.Sleep(sleepMs);
        }
        return s;
    }
}
