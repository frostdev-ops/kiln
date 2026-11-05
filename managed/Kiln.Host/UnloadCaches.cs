// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text.Json;

namespace Kiln.Host;

/// <summary>
/// Process-wide caches that can hold an unloaded mod's types: System.Text.Json's per-options type metadata (a mod
/// that serializes its own types with <see cref="JsonSerializerOptions.Default"/> or another shared options instance)
/// and the framework's reflection-emit accessor caches. The runtime's hot-reload handlers
/// (<see cref="MetadataUpdateHandlerAttribute"/> types, <c>ClearCache(Type[]?)</c>) flush them; Kiln calls them after
/// every unload, as hot reload would. CoreLib's own handler (per-type reflection caches) is skipped: those live with
/// the collectible types.
/// </summary>
internal static class UnloadCaches
{
    private static List<MethodInfo>? _handlers;
    private static int _assemblies;
    private static readonly MethodInfo? ClearOptions =
        typeof(JsonSerializerOptions).GetMethod("ClearCaches", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, []);

    public static void Clear(Action<LogLevel, string> log)
    {
        try
        {
            foreach (var h in Handlers()) h.Invoke(null, [null]);
            if (ClearOptions is not null)
            {
                ClearOptions.Invoke(JsonSerializerOptions.Default, null);
                ClearOptions.Invoke(JsonSerializerOptions.Web, null);
            }
        }
        catch (Exception e)
        {
            log(LogLevel.Debug, $"unload: clearing framework caches failed: {e.GetBaseException().Message}");
        }
    }

    private static List<MethodInfo> Handlers()
    {
        var all = AppDomain.CurrentDomain.GetAssemblies();
        if (_handlers is not null && all.Length == _assemblies) return _handlers;
        var found = new List<MethodInfo>();
        foreach (var asm in all)
        {
            if (asm.IsDynamic || asm == typeof(object).Assembly) continue;
            if (AssemblyLoadContext.GetLoadContext(asm) is { IsCollectible: true }) continue;  // never hold a mod's types
            IEnumerable<MetadataUpdateHandlerAttribute> attrs;
            try
            {
                attrs = asm.GetCustomAttributes<MetadataUpdateHandlerAttribute>();
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var a in attrs)
                if (a.HandlerType.GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Type[])]) is { } m)
                    found.Add(m);
        }
        _assemblies = all.Length;
        return _handlers = found;
    }
}
