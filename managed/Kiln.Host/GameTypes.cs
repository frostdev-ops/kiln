// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;

namespace Kiln.Host;

/// <summary>
/// Kiln.Game, the generated game types every mod shares (docs/kiln/typegen.md, "Shipping and staying loadable"):
/// which file this process loads, and the check of a mod's layout stamp against it.
/// <list type="bullet">
/// <item>kiln/host/game/&lt;side&gt;-&lt;sha12&gt;/Kiln.Game.dll, generated from the dump of exactly the running exe
/// (the first twelve hex digits of its SHA-256, <c>KilnBuildInfo.exe_sha256</c>), when it exists; else
/// kiln/host/Kiln.Game.dll (built from the newest dump). One copy per process, in the host's context.</item>
/// <item>A mod whose folder has <c>kiln.gametypes.json</c> for its entry assembly (same MVID) is checked with
/// <c>Kiln.GameLayoutStamps.Check</c> of the loaded Kiln.Game before its assembly loads, and refused with the reason
/// when a type or field it uses is gone or retyped. No stamp, a stale one, no Kiln.Game or an older Kiln.Game without
/// the check: it loads as before.</item>
/// </list>
/// </summary>
internal static class GameTypes
{
    public const string AssemblyName = "Kiln.Game";
    public const string StampFile = "kiln.gametypes.json";

    private static readonly object Lock = new();
    private static string? _variant;
    private static Action<LogLevel, string>? _log;

    /// <summary>kiln/host: where Kiln.Host itself is.</summary>
    public static readonly string HostDirectory = Path.GetDirectoryName(typeof(GameTypes).Assembly.Location) ?? "";

    /// <summary>Set once at start from the core: the side (<c>server</c> | <c>client</c>), the running exe's
    /// SHA-256 (null when the core does not know it) and where to log which Kiln.Game was loaded.</summary>
    public static void Configure(string side, string? exeSha256, Action<LogLevel, string>? log)
    {
        _variant = Variant(side, exeSha256);
        _log = log;
    }

    /// <summary><c>&lt;side&gt;-&lt;sha12&gt;</c>, or null when the hash is not 64 hex digits.</summary>
    public static string? Variant(string side, string? exeSha256)
    {
        if (side is not ("server" or "client") || exeSha256 is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigit)) return null;
        return side + "-" + sha[..12].ToLowerInvariant();
    }

    /// <summary>The Kiln.Game.dll to load from <paramref name="hostDir"/>: <c>game/&lt;variant&gt;/Kiln.Game.dll</c>
    /// when it exists, else <c>Kiln.Game.dll</c>; null when neither exists.</summary>
    public static string? PathIn(string hostDir, string? variant)
    {
        if (variant is not null)
        {
            var own = Path.Combine(hostDir, "game", variant, AssemblyName + ".dll");
            if (File.Exists(own)) return own;
        }
        var flat = Path.Combine(hostDir, AssemblyName + ".dll");
        return File.Exists(flat) ? flat : null;
    }

    /// <summary>The host's context: the one Kiln.Host lives in.</summary>
    public static AssemblyLoadContext HostContext => AssemblyLoadContext.GetLoadContext(typeof(GameTypes).Assembly) ?? AssemblyLoadContext.Default;

    /// <summary>Kiln.Game in the host's context: the copy already there, else the file <see cref="PathIn"/> picks
    /// (logged once). Null when kiln/host has none.</summary>
    public static Assembly? Load() => Load(HostContext, HostDirectory, _variant, _log);

    public static Assembly? Load(AssemblyLoadContext context, string hostDir, string? variant, Action<LogLevel, string>? log)
    {
        lock (Lock)
        {
            foreach (var a in context.Assemblies)
                if (a.GetName().Name == AssemblyName) return a;
            var path = PathIn(hostDir, variant);
            if (path is null) return null;
            var game = context.LoadFromAssemblyPath(path);
            var shown = Path.GetRelativePath(hostDir, path).Replace('\\', '/');
            log?.Invoke(LogLevel.Info, variant is null
                ? $"Kiln.Game: {shown} (this game build's SHA-256 is unknown: the newest layouts)"
                : shown.StartsWith("game/", StringComparison.Ordinal)
                    ? $"Kiln.Game: {shown}, generated for this game build"
                    : $"Kiln.Game: {shown} (no game/{variant}/ for this game build: the newest layouts; mods are checked against them)");
            return game;
        }
    }

    /// <summary>Checks mod <paramref name="modId"/> in <paramref name="directory"/> against the loaded Kiln.Game. False
    /// with the reason when its layout stamp says it cannot run on this game build. A stamp that cannot be checked (a
    /// stale one, an unreadable one, a check that throws) is one warning line naming the mod, and the mod loads.</summary>
    public static bool CheckMod(string modId, string directory, string entryAssembly, Action<LogLevel, string> log, out string? reason) =>
        CheckMod(modId, directory, entryAssembly, Load, log, out reason);

    public static bool CheckMod(string modId, string directory, string entryAssembly, Func<Assembly?> game, Action<LogLevel, string> log, out string? reason)
    {
        reason = null;
        var stampPath = Path.Combine(directory, StampFile);
        var entryPath = Path.Combine(directory, entryAssembly);
        if (!File.Exists(stampPath) || !File.Exists(entryPath)) return true;
        string stamp;
        Guid? stamped = null, built;
        try
        {
            stamp = File.ReadAllText(stampPath);
            using (var doc = JsonDocument.Parse(stamp))
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("mvid", out var m) &&
                    m.ValueKind == JsonValueKind.String && Guid.TryParse(m.GetString(), out var g))
                    stamped = g;
            built = Mvid(entryPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or BadImageFormatException or InvalidOperationException)
        {
            log(LogLevel.Warn, $"mod {modId}: {StampFile} not checked, loading it unchecked ({e.GetType().Name}: {e.Message})");
            return true;
        }
        if (stamped is null || stamped != built)
        {
            log(LogLevel.Warn, $"mod {modId}: {StampFile} belongs to another build of {entryAssembly} (its MVID differs); loading it unchecked");
            return true;
        }
        var check = game()?.GetType("Kiln.GameLayoutStamps")?.GetMethod("Check", BindingFlags.Public | BindingFlags.Static,
            [typeof(string), typeof(string).MakeByRefType()]);
        if (check is null) return true;  // no Kiln.Game, or one older than the stamps
        var args = new object?[] { stamp, null };
        try
        {
            if ((bool)check.Invoke(null, args)!) return true;
        }
        catch (TargetInvocationException e)
        {
            var inner = e.InnerException ?? e;
            log(LogLevel.Warn, $"mod {modId}: {StampFile} not checked, loading it unchecked (Kiln.Game's check threw {inner.GetType().Name}: {inner.Message})");
            return true;
        }
        reason = $"it was built against game types this game build changed ({args[1] as string ?? "no reason given"}); update the mod, or ask its author for a build for this game version";
        return false;
    }

    private static Guid Mvid(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }
}
