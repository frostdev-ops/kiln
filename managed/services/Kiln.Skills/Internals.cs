// SPDX-License-Identifier: MIT
// Kiln.Skills' use of Kiln.Api internals (InternalsVisibleTo Kiln.Skills): the native service lookup, Kiln's log, the
// calling mod, a mod's Retiring event, and the data owner of talent-tree states.
namespace Kiln;

internal static class SkillsInternals
{
    /// <summary>The mod whose code runs on this thread now (null: Kiln's own code).</summary>
    public static ModContext? CurrentMod
    {
        get => ModContext.CurrentMod;
        set => ModContext.CurrentMod = value;
    }

    public static bool IsAlive(ModContext m) => m.Alive;

    public static ulong OwnerOf(ModContext m) => m.Owner;

    /// <summary>Runs <paramref name="action"/> as <paramref name="mod"/> (callbacks, event subscriptions).</summary>
    public static void RunAs(ModContext? mod, Action action)
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = mod;
        try
        {
            action();
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    public static T RunAs<T>(ModContext? mod, Func<T> f)
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = mod;
        try
        {
            return f();
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    public static void OnRetiring(ModContext mod, Action cleanup) => mod.Retiring += cleanup;

    private static ModContext? _data;

    /// <summary>Talent-tree states live in Kiln.Saves player data under this owner ("kiln-skills"), keyed
    /// "talents:&lt;tree id&gt;": the same for every tree whichever mod registered it, so a mod reload or a tree moved to
    /// another mod keeps every character's unlocks, and data-only trees (skills/*.json) have an owner.</summary>
    public static ModContext DataOwner => _data ??= HostAccess.CreateContext(
        new ModInfo("kiln-skills", "Kiln talent trees", "1.0.0", Path.Combine(KilnDir(), "host")),
        0, Path.Combine(KilnDir(), "data", "kiln-skills"));

    private static string KilnDir()
    {
        var host = Path.GetDirectoryName(typeof(SkillsInternals).Assembly.Location) ?? ".";
        return Path.GetDirectoryName(host) ?? host;
    }

    /// <summary>The native table of a service (0 when unavailable).</summary>
    public static nint Service(string name, uint minVersion) => Backend.Current.Service(name, minVersion);

    public static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, "skills: " + text);

    public static void Info(string s) => Log(LogLevel.Info, s);
    public static void Warn(string s) => Log(LogLevel.Warn, s);
    public static void Error(string s) => Log(LogLevel.Error, s);
}
