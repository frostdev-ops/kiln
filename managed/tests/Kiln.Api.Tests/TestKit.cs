// SPDX-License-Identifier: MIT
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kiln.Api.Tests;

/// <summary>A fake table is published only for its allocation's lifetime; a replacement belongs to its own scope.</summary>
internal sealed class FakeServiceRegistration : IDisposable
{
    private readonly Dictionary<string, nint> _tables;
    private readonly string _name;
    private readonly nint _table;

    internal FakeServiceRegistration(Dictionary<string, nint> tables, string name, nint table)
    {
        _tables = tables;
        _name = name;
        _table = table;
        tables[name] = table;
    }

    public void Dispose()
    {
        if (_tables.TryGetValue(_name, out var current) && current == _table) _tables.Remove(_name);
    }
}

/// <summary>A backend that records log lines and lets a test say whether it is on the tick thread.</summary>
internal sealed class FakeBackend : IKilnBackend
{
    public readonly List<string> Lines = [];
    public bool TickThread = true;
    public string Version => "test";
    public bool IsTickThread => TickThread;
    public bool HasFeature(string name) => false;
    /// <summary>Native service tables a test provides (the fake kiln.types and kiln.assets).</summary>
    public readonly Dictionary<string, nint> Tables = [];
    public nint Service(string name, uint minVersion) => Tables.GetValueOrDefault(name);
    public ulong OwnerCreate(string id) => 777;

    public void Log(ulong owner, LogLevel level, string text)
    {
        lock (Lines) Lines.Add($"{level}: {text}");
    }

    public bool Logged(string part)
    {
        lock (Lines) return Lines.Any(l => l.Contains(part, StringComparison.Ordinal));
    }
}

/// <summary>Fresh Kiln state per test: a temp kiln folder, empty bus, configs, commands and permissions.</summary>
public abstract class KilnTest : IDisposable
{
    private static ulong _owners = 100;
    private readonly IKilnBackend _previousBackend = Kiln.Backend.Current;
    internal readonly FakeBackend Backend;
    protected readonly string Root;
    private readonly List<ModContext> _mods = [];
    private ulong _step = 1;

    protected KilnTest()
    {
        Backend = new FakeBackend();
        Kiln.Backend.Current = Backend;
        var dir = Path.Combine(Path.GetTempPath(), "kiln-api-tests-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(dir, "kiln");
        Directory.CreateDirectory(Path.Combine(Root, "config"));
        Directory.CreateDirectory(Path.Combine(Root, "control"));
        KilnPaths.Root = Root;
        KilnPaths.Config = null!;  // back to <root>/config (a test may have pointed it at a profile's configDir)
        ServiceTick.Reset();
        ServiceTick.AutoStartEnabled = false;
        Events.Reset();
        Config.Reset();
        Commands.ResetForTests();
        ConsoleControl.ResetForTests();
        PermissionStore.Reset();
        Permissions.ResetForTests();
        Permissions.FindPlayer = _ => null;
        Permissions.FindPlayerBySteamId = _ => null;
        Arg.OnlinePlayers = () => [];
        Arg.PositionOf = _ => null;
        Arg.FindItem = _ => null;
        Arg.SearchItems = (_, _) => [];
        HostAccess.Current = null;
    }

    public virtual void Dispose()
    {
        try
        {
            // Derived fixtures must keep their native fakes alive until every owner's cleanup has run.
            foreach (var m in _mods) m.Retire();
            HostAccess.Current = null;
            Config.Reset();
            try
            {
                Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        finally
        {
            if (ReferenceEquals(Kiln.Backend.Current, Backend)) Kiln.Backend.Current = _previousBackend;
        }
    }

    internal ModContext NewMod(string id)
    {
        var ctx = HostAccess.CreateContext(new ModInfo(id, id, "1.0.0", Path.Combine(Root, "mods", id)), ++_owners,
            Path.Combine(Root, "data", id));
        _mods.Add(ctx);
        return ctx;
    }

    /// <summary>Runs <paramref name="body"/> as code of <paramref name="mod"/>.</summary>
    internal static T As<T>(ModContext? mod, Func<T> body)
    {
        var previous = HostAccess.Current;
        HostAccess.Current = mod;
        try
        {
            return body();
        }
        finally
        {
            HostAccess.Current = previous;
        }
    }

    internal static void As(ModContext? mod, Action body) => As<int>(mod, () =>
    {
        body();
        return 0;
    });

    /// <summary>One service tick (as the host would drive it).</summary>
    internal void Step(int n = 1)
    {
        for (var i = 0; i < n; i++) ServiceTick.Run(HostAccess.Tick(++_step, (uint)_step * 2, 2, 0, 40.8));
    }

    internal ulong CurrentStep => _step;
}
