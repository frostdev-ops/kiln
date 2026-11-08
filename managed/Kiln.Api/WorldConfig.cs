// SPDX-License-Identifier: MIT
// Per-world mod settings (Enshwick's settings: one file per world beside its save; the loader hands the mod the values as
// the world loads). A mod declares the same kind of config class as for Config.Register, and Kiln keeps one file of it per
// world, in the world's sidecar folder next to WorldData: kiln/data/worlds/<world id>/config/<mod id>[-<name>]-world.json.
// Values follow the world that is open: a game client hosts one world after another in a process, and each world has its
// own values; a world without a file gets the defaults and a file of its own. Usage: docs/kiln/modding/lifecycle.md.
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>
/// A mod's typed settings for one world (see <see cref="Config.RegisterWorld{T}"/>). <see cref="Value"/> is updated in
/// place, both when someone edits the world's file and when another world becomes the open one, so references to it (and
/// to its sections) stay current.
/// </summary>
/// <remarks>
/// <para>The class takes the same members and attributes as <see cref="Config.Register{T}"/>. The file is JSONC with
/// comments, like every Kiln config; it is a server-side file (<see cref="ConfigKind.Server"/>: <see cref="SyncedAttribute"/>
/// entries reach the clients, and a client joined to a server sees the server's values read-only).</para>
/// <para>Entries marked <see cref="RequiresRestartAttribute"/> keep their value while the world runs: an edit of the file
/// applies the next time that world opens (Enshwick: "a change applies the next time the world loads").</para>
/// </remarks>
public sealed class WorldConfig<T> : ConfigFile where T : class, new()
{
    internal WorldConfig(ModContext? owner, string id) : base(owner, id, ConfigKind.Server, typeof(T), new T(), perWorld: true)
    {
    }

    /// <summary>The running values: the open world's, or the defaults while no world is open. Read them where you need them;
    /// after changing them in code call <see cref="ConfigFile.Save"/>.</summary>
    public T Value => (T)ValueObject;
}

public static partial class Config
{
    /// <summary>
    /// Registers the calling mod's per-world config of type <typeparamref name="T"/>: one file for each world, in the
    /// world's sidecar folder (<c>kiln/data/worlds/&lt;world id&gt;/config/&lt;modid&gt;[-&lt;name&gt;]-world.json</c>,
    /// beside <c>WorldData</c>), created from the schema when the world has none. The values are those of the world
    /// that is open: <see cref="ConfigFile.Changed"/> fires with <see cref="ConfigChangeSource.World"/> when another world
    /// replaces them (a game client hosting one world after another), before <c>WorldLoadedEvent</c>. A world that has
    /// no id yet (a new world before its first save) runs on the defaults, and its file appears with its first save.
    /// Tick thread (e.g. <c>OnLoad</c>). The config goes away when the mod unloads; the files stay.
    /// </summary>
    /// <param name="name">For a second per-world config of the mod: becomes part of the file name.</param>
    [TickThreadOnly]
    public static WorldConfig<T> RegisterWorld<T>(string? name = null) where T : class, new()
    {
        Game.AssertTickThread();
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        if (mod is null && string.IsNullOrEmpty(name)) throw new InvalidOperationException("Config.RegisterWorld outside a mod needs a name");
        if (name is not null && !ValidName(name)) throw new ArgumentException($"config name '{name}' must be [a-z0-9_.-]", nameof(name));
        var id = (mod?.Info.Id ?? "") is { Length: > 0 } modId ? modId + (name is null ? "" : "-" + name) : name!;
        id += "-world";
        var file = new WorldConfig<T>(mod, id);
        lock (Lock)
        {
            if (Files.Any(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"config '{id}' is already registered");
            Files.Add(file);
        }
        if (mod is not null) mod.Retiring += () => Unregister(file);
        try
        {
            file.BindWorld(WorldConfigs.Current());
            WorldConfigs.Watch(file.WorldDirectory);
            if (ServerValuesFor?.Invoke(file) is { } server) file.ApplyServerValues(server, initial: true);
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"config {id}: could not be bound to the open world: {e.Message}");
        }
        StartWatching();
        RegistryChanged?.Invoke();
        return file;
    }

    /// <summary>Wakes the file poll (a watcher saw a change in a world's config folder).</summary>
    internal static void Signal() => _signal = true;
}

/// <summary>The seam between per-world configs and Kiln.Saves, which knows which world is open.</summary>
internal static class WorldConfigs
{
    private static readonly object Lock = new();
    private static string? _directory, _watching;
    private static bool _tried;
    private static FileSystemWatcher? _watcher;

    /// <summary>Kiln.Saves: the open world's sidecar folder (null: none yet). Opening it is what makes
    /// <c>kiln/data/worlds/&lt;id&gt;</c> exist, so it is asked for when a config registers, not before.</summary>
    internal static Func<string?>? DirectoryProvider;

    /// <summary>The open world's sidecar folder now: from Kiln.Saves when it is there, else what was announced last.</summary>
    internal static string? Current()
    {
        if (DirectoryProvider is null) StartSaves();
        if (DirectoryProvider is { } p)
        {
            try
            {
                return _directory = p();
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Warn, $"per-world config: the open world could not be asked for: {e.Message}");
            }
        }
        return _directory;
    }

    /// <summary>Kiln.Saves: the open world's sidecar folder changed (a world opened, got its id, or closed). Tick thread.</summary>
    internal static void Announce(string? directory)
    {
        _directory = directory;
        var any = false;
        foreach (var f in Config.All)
        {
            if (!f.PerWorld) continue;
            any = true;
            try
            {
                f.BindWorld(directory);
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Error, $"config {f.Id}: could not follow the world change: {e}");
            }
        }
        if (any) Watch(directory);
    }

    /// <summary>A mod that only uses per-world configs never touches Kiln.Saves, so nothing would start it: this does
    /// (once). Without the assembly (unit tests, an install without it) configs stay in memory.</summary>
    private static void StartSaves()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            if (Type.GetType("Kiln.SavesRuntime, Kiln.Saves") is { } t) RuntimeHelpers.RunModuleConstructor(t.Module.ModuleHandle);
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"per-world config: Kiln.Saves could not be started: {e.Message}");
        }
    }

    // The config folder of the open world: an edit of a file there is picked up like one in kiln/config.
    internal static void Watch(string? directory)
    {
        lock (Lock)
        {
            if (string.Equals(directory, _watching, StringComparison.Ordinal) && (_watcher is not null || directory is null)) return;
            _watcher?.Dispose();
            _watcher = null;
            _watching = directory;
            if (directory is null) return;
            try
            {
                var dir = System.IO.Path.Combine(directory, "config");
                Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                w.Changed += (_, _) => Config.Signal();
                w.Created += (_, _) => Config.Signal();
                w.Renamed += (_, _) => Config.Signal();
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception)
            {
                // the five-second poll still finds edits
            }
        }
    }

    /// <summary>Test hook.</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            _watcher?.Dispose();
            _watcher = null;
            _watching = null;
        }
        DirectoryProvider = null;
        _directory = null;
        _tried = true;  // tests never start Kiln.Saves
    }
}
