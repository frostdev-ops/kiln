// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Kiln;

/// <summary>
/// One tick for Kiln's own services (not a mod): event pumps (players joined, day started), the config and
/// permission file watchers, the console folder and <see cref="Events.Enqueue"/>'s queue. Handlers run on the tick
/// thread at the start of a step, as Kiln (no current mod), each under its own exception guard.
/// </summary>
/// <remarks>
/// Kiln.Host does not drive it yet, so it rides on the Pre tick of one loaded mod (the first mod that registered;
/// when that mod unloads it moves to another). Its time then shows in that mod's tick statistics. Once the host calls
/// <see cref="Run"/> itself (setting <see cref="HostDriven"/>), the ride ends. Without any loaded mod nothing runs;
/// nothing needs to then (no handler, no config).
/// </remarks>
internal static class ServiceTick
{
    private sealed class Entry(string name, TickHandler handler)
    {
        public readonly string Name = name;
        public readonly TickHandler Handler = handler;
        public int Faults;
        public bool Active = true;
    }

    private sealed class Registration(Entry entry) : IDisposable
    {
        public void Dispose() => Remove(entry);
    }

    /// <summary>Assemblies (in kiln/host) whose startup type is started at the first service tick, so their file
    /// watchers run and their capability rows are reported even when no mod touched them (the console folder lives in
    /// Kiln.Chat; a service's rows: docs/kiln/services/README.md, "Managed rules").</summary>
    internal static readonly (string Assembly, string Type)[] AutoStart =
        [("Kiln.Chat", "Kiln.ConsoleControl"), ("Kiln.Content", "Kiln.Content"),
         ("Kiln.Network", "Kiln.NetworkStartup"), ("Kiln.Ui", "Kiln.UiStartup"), ("Kiln.Skills", "Kiln.SkillsStartup"),
         ("Kiln.Audio", "Kiln.AudioStartup"), ("Kiln.Quests", "Kiln.QuestsStartup"), ("Kiln.Vfx", "Kiln.VfxStartup"),
         ("Kiln.Rules", "Kiln.RulesStartup"), ("Kiln.DevTools", "Kiln.DevToolsStartup"),
         ("Kiln.Spawn", "Kiln.SpawnStartup"), ("Kiln.Items", "Kiln.ItemsStartup"),
         ("Kiln.Players", "Kiln.PlayersStartup"),
         // dev.27: services with capability rows of their own, which must be there from the first tick, not from the
         // service's first use (a service that only lowers the sigdb features of its missing table is not here: the
         // core's kiln.<service> row says so from the start, and every assembly loaded here costs every start)
         ("Kiln.Assets", "Kiln.AssetsStartup"), ("Kiln.Settings", "Kiln.SettingsStartup"),
         ("Kiln.Gameplay", "Kiln.GameplayStartup"), ("Kiln.Combat", "Kiln.CombatStartup"),
         ("Kiln.Abilities", "Kiln.AbilitiesStartup"), ("Kiln.Time", "Kiln.TimeStartup")];

    private static readonly object Lock = new();
    private static Entry[] _entries = [];
    private static readonly List<ModContext> Candidates = new();
    private static ModContext? _rider;
    private static readonly TickHandler Driver = static (in TickInfo t) => RunFromMod(in t);
    private static ulong _lastStep;
    private static bool _started;
    private static bool _hostDriven;

    /// <summary>The step of the last run (0 before the first).</summary>
    public static ulong Step => _lastStep;

    /// <summary>Changes when <see cref="Reset"/> dropped every handler (tests): a handler added once and kept adds itself
    /// again when it sees another value.</summary>
    internal static int Generation => Volatile.Read(ref _generation);

    private static int _generation;

    /// <summary>Whether autostart runs (tests turn it off).</summary>
    internal static bool AutoStartEnabled { get; set; } = true;

    /// <summary>Set by Kiln.Host when it calls <see cref="Run"/> every step itself: Kiln stops riding a mod's tick.</summary>
    internal static bool HostDriven
    {
        get => _hostDriven;
        set
        {
            lock (Lock)
            {
                _hostDriven = value;
                if (value) Detach();
                else Rehome();
            }
        }
    }

    /// <summary>Adds a handler that runs every step (filter with <c>tick.Step % n</c> for less). Any thread.</summary>
    public static IDisposable Add(string name, TickHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var e = new Entry(name, handler);
        lock (Lock) _entries = [.. _entries, e];
        return new Registration(e);
    }

    private static void Remove(Entry e)
    {
        lock (Lock)
        {
            e.Active = false;
            _entries = _entries.Where(x => !ReferenceEquals(x, e)).ToArray();
        }
    }

    /// <summary>A mod registered (Events.Register): it may carry the service tick.</summary>
    internal static void Offer(ModContext context)
    {
        lock (Lock)
        {
            if (!context.Alive || Candidates.Contains(context)) return;
            Candidates.Add(context);
            context.Retiring += () => Retired(context);
            if (_rider is null && !_hostDriven) Attach(context);
        }
    }

    private static void Retired(ModContext context)
    {
        lock (Lock)
        {
            Candidates.Remove(context);
            if (!ReferenceEquals(_rider, context)) return;
            Detach();
            if (!_hostDriven) Rehome();
        }
    }

    private static void Attach(ModContext context)
    {
        _rider = context;
        context.Tick.Pre += Driver;
    }

    private static void Detach()
    {
        if (_rider is null) return;
        _rider.Tick.Pre -= Driver;
        _rider = null;
    }

    private static void Rehome()
    {
        if (_rider is not null) return;
        var next = Candidates.FirstOrDefault(c => c.Alive);
        if (next is not null) Attach(next);
    }

    private static void RunFromMod(in TickInfo tick)
    {
        if (_hostDriven) return;
        Run(in tick);
    }

    /// <summary>Runs every handler once for this step (a second call in the same step does nothing). Tick thread.</summary>
    internal static void Run(in TickInfo tick)
    {
        if (tick.Step == _lastStep && _lastStep != 0) return;
        _lastStep = tick.Step;
        var previousMod = ModContext.CurrentMod;
        var previousSync = SynchronizationContext.Current;
        ModContext.CurrentMod = null;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            if (!_started)
            {
                _started = true;
                if (AutoStartEnabled) StartServices();
            }
            Events.DrainQueued();
            foreach (var e in Volatile.Read(ref _entries))
            {
                if (!e.Active) continue;
                try
                {
                    e.Handler(in tick);
                }
                catch (Exception ex)
                {
                    e.Faults++;
                    if (e.Faults <= 5 || e.Faults % 1000 == 0)
                        Backend.Current.Log(0, LogLevel.Error, $"kiln service tick '{e.Name}' threw ({e.Faults}x): {ex}");
                }
            }
        }
        finally
        {
            ModContext.CurrentMod = previousMod;
            SynchronizationContext.SetSynchronizationContext(previousSync);
        }
    }

    /// <summary>Test hook.</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            Detach();
            Candidates.Clear();
            _entries = [];
            _lastStep = 0;
            _started = false;
            _hostDriven = false;
            _generation++;
        }
    }

    private static void StartServices()
    {
        var self = typeof(ServiceTick).Assembly;
        var alc = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
        var dir = Path.GetDirectoryName(self.Location) ?? "";
        foreach (var (name, typeName) in AutoStart)
        {
            try
            {
                var asm = alc.Assemblies.FirstOrDefault(a => a.GetName().Name == name);
                if (asm is null)
                {
                    var file = Path.Combine(dir, name + ".dll");
                    if (dir.Length == 0 || !File.Exists(file)) continue;
                    try
                    {
                        asm = alc.LoadFromAssemblyName(new AssemblyName(name));
                    }
                    catch (FileNotFoundException)
                    {
                        asm = alc.LoadFromAssemblyPath(file);
                    }
                }
                var type = asm.GetType(typeName, throwOnError: false);
                if (type is not null) RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Warn, $"kiln: could not start {name}: {e.InnerException?.Message ?? e.Message}");
            }
        }
    }
}
