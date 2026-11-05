// SPDX-License-Identifier: MIT
using System.Reflection;

namespace Kiln;

/// <summary>Runs a mod's delegate as that mod (its <c>CurrentMod</c> and SynchronizationContext), logging exceptions
/// to its log. Used by Kiln's own callbacks (config change handlers, command handlers).</summary>
internal static class ModInvoke
{
    /// <summary>False when the mod is gone or <paramref name="action"/> threw (already logged).</summary>
    public static bool Run(ModContext? mod, Action action, string what)
    {
        if (mod is { Alive: false }) return false;
        var previousSync = SynchronizationContext.Current;
        var previousMod = ModContext.CurrentMod;
        if (mod is not null)
        {
            SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
            ModContext.CurrentMod = mod;
        }
        try
        {
            action();
            return true;
        }
        catch (Exception e)
        {
            if (mod is not null) mod.Log.Error(e, $"{what} threw");
            else Backend.Current.Log(0, LogLevel.Error, $"kiln: {what} threw: {e}");
            return false;
        }
        finally
        {
            if (mod is not null)
            {
                ModContext.CurrentMod = previousMod;
                SynchronizationContext.SetSynchronizationContext(previousSync);
            }
        }
    }

    public static string Name(ModContext? mod) => mod?.Info.Id ?? "kiln";
}

/// <summary>Where Kiln's folders are on this server: <c>&lt;server&gt;/kiln/{config,control,data,state,...}</c>.</summary>
internal static class KilnPaths
{
    private static string? _root;
    private static string? _config;

    /// <summary>The kiln folder. Kiln.Api lives in <c>kiln/host/</c>; <c>KILN_ROOT</c> overrides; tests set it.</summary>
    public static string Root
    {
        get
        {
            if (_root is not null) return _root;
            var env = Environment.GetEnvironmentVariable("KILN_ROOT");
            if (!string.IsNullOrEmpty(env)) return _root = Path.GetFullPath(env);
            var dir = Path.GetDirectoryName(typeof(KilnPaths).Assembly.Location);
            if (!string.IsNullOrEmpty(dir) && string.Equals(Path.GetFileName(dir), "host", StringComparison.OrdinalIgnoreCase))
                return _root = Path.GetDirectoryName(dir)!;
            return _root = Path.Combine(string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir, "kiln");
        }
        set => _root = value;
    }

    /// <summary>The server folder (with enshrouded_server.json).</summary>
    public static string Server => Path.GetDirectoryName(Root) ?? Root;

    /// <summary>Mod config files (<c>&lt;id&gt;.json</c>, <c>.schema/</c>): <c>kiln/config</c> unless the host set
    /// another folder (the active profile's configDir, KILN_PATH_CONFIG). Set it before the first mod loads.</summary>
    public static string Config
    {
        get => _config ?? Path.Combine(Root, "config");
        internal set => _config = string.IsNullOrEmpty(value) ? null : Path.GetFullPath(value);
    }

    public static string Control => Path.Combine(Root, "control");

    /// <summary><c>kiln/state</c>: status files Kiln writes for the launcher (KILN_PATH_STATE; boot.json, mods.json, ...).</summary>
    public static string State => Path.Combine(Root, "state");
}

/// <summary>What Kiln registers from a mod's attributes besides <c>[Subscribe]</c>.</summary>
internal static class ModScan
{
    /// <summary>Registers the mod's <c>[Command]</c> methods through Kiln.Chat (found by name, as Kiln.Api cannot
    /// reference it). Errors go to the mod's log; the load goes on.</summary>
    public static void RegisterCommands(Mod mod)
    {
        Assembly? chat = null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        for (var t = mod.GetType(); t != null && t != typeof(Mod) && chat is null; t = t.BaseType)
            foreach (var m in t.GetMethods(flags | BindingFlags.DeclaredOnly))
            {
                foreach (var a in m.CustomAttributes)
                    if (a.AttributeType.FullName == "Kiln.CommandAttribute" && a.AttributeType.Assembly.GetName().Name == "Kiln.Chat")
                    {
                        chat = a.AttributeType.Assembly;
                        break;
                    }
                if (chat is not null) break;
            }
        if (chat is null) return;
        try
        {
            var register = chat.GetType("Kiln.Commands")?.GetMethod("RegisterAll", [typeof(object)]);
            register?.Invoke(null, [mod]);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            mod.Context.Log.Error($"[Command] methods were not registered: {e.InnerException.Message}");
        }
        catch (Exception e)
        {
            mod.Context.Log.Error($"[Command] methods were not registered: {e.Message}");
        }
    }
}
