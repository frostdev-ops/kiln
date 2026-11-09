// SPDX-License-Identifier: MIT
// Minimal collaborators for the production EntityTable. No production identity logic is duplicated here.
using System.Text.Json;

namespace Kiln;

internal readonly record struct EntityId(uint Value);
internal readonly record struct WorldPosition(double X, double Y, double Z)
{
    public double DistanceTo(WorldPosition p) => Math.Sqrt((X-p.X)*(X-p.X) + (Y-p.Y)*(Y-p.Y) + (Z-p.Z)*(Z-p.Z));
}
internal sealed class ModContext;
internal enum LogLevel { Debug, Info, Error }
internal static class SavesRuntime
{
    public static void Log(LogLevel level, string text) { }
}
internal static class WorldStore
{
    internal sealed class Slot { public object? Value = null; public Type? Type = null; public string? Json; }
    public static void Freeze(Slot slot, JsonSerializerOptions? options, ModContext context, string scope, string key) { }
}
internal static class SpawnLink
{
    internal sealed record Ent(EntityId Entity, Guid? Template, string? Name, WorldPosition Position, float Yaw);
    public static readonly Dictionary<uint, Ent> World = [];
    public static readonly Dictionary<Guid, (uint kind, bool neverSaved, string name, bool canPersist)> Templates = [];
    public static bool Available => true;
    public static bool WorldRunning => true;
    public static Ent? Inspect(EntityId e) => World.GetValueOrDefault(e.Value);
    public static (uint kind, bool neverSaved, string name, bool canPersist) Template(Guid g) => Templates[g];
    public static bool IsMobile(Guid g) => Template(g).kind is 1 or 4;
    public static IReadOnlyList<EntityId> Find(Guid g, WorldPosition p, double radius, int max) => World.Values
        .Where(e => e.Template == g && e.Position.DistanceTo(p) <= radius)
        .OrderBy(e => e.Position.DistanceTo(p)).Take(max).Select(e => e.Entity).ToArray();
    public static void Reset() { World.Clear(); Templates.Clear(); }
}
