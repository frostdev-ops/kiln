// SPDX-License-Identifier: MIT
// What a mod's world code looks like to the build: two typed writes of generated components, one of them twice, a
// write through the mod's own generic helper (the build cannot name its type), a write of the mod's own component
// struct, a transform-scale write (CurrentTransform), and the string-named calls the build leaves to the author.
using Kiln;

namespace WritesMod;

public struct Homemade : IGameComponent<Homemade>
{
    public int Value;
    public static uint Hash => 1;
    public static string Name => "Homemade";
    public static string QualifiedName => "keen::ecs::Homemade";
    public static int Size => 4;
    public static ulong Layout => 0;
}

public static class Calls
{
    public static void Typed(EntityId e)
    {
        _ = World.Write(e, new Ecs.ScaleComponent { uniformScale = 2f });
        _ = World.Update<Ecs.Fuel>(e, (ref Ecs.Fuel fuel) => fuel.Values[0] = 0);
        _ = World.Update<Ecs.ScaleComponent>(e, (ref Ecs.ScaleComponent s) => s.uniformScale *= 2f);
    }

    public static Task<WorldWriteResult> Generic<T>(EntityId e, T value) where T : unmanaged, IGameComponent<T> => World.Write(e, value);

    public static void Own(EntityId e) => _ = World.Write(e, new Homemade { Value = 1 });

    // 0.2.0-dev.28: a non-generic writer of a fixed component (CurrentTransform)
    public static void Scale(EntityId e) => _ = World.SetTransformScale(e, Scale3.Uniform(2f));

    public static void ByName(EntityId e)
    {
        _ = World.WriteField(e, "Health", "dataStorage[0]", 5);
        _ = World.WriteAt(e, "Stamina", 8, 5);
        World.WriteRaw(e, "PlayerInput", 0, (byte)1);
        _ = World.WriteInfo<Ecs.Mana>();          // asking is not writing
        _ = World.Query<Ecs.NpcCommon>();
    }
}
