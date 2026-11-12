// SPDX-License-Identifier: MIT
// The nine dev.24 types through Kiln.GameTypes.Components. The namespace Kiln.GameTypes can only be named through the
// extern alias on the Kiln.Game reference (it clashes with the class Kiln.GameTypes in Kiln.Api); the types are
// [Obsolete] since dev.26, which is what this file checks they still do.
#pragma warning disable CS0618
extern alias game;
using System.Runtime.InteropServices;
using Kiln;
using G = game::Kiln.GameTypes;

namespace TypegenProbe;

public sealed record LegacyResult(string Name, int Size, int Entities, int Matched, List<string> Mismatches);

public static class LegacyCheck
{
    public static List<LegacyResult> Run(int maxEntities) =>
    [
        One<G.NetworkHealth>("NetworkHealth", maxEntities),
        One<G.Health>("Health", maxEntities),
        One<G.Mana>("Mana", maxEntities),
        One<G.Stamina>("Stamina", maxEntities),
        One<G.CurrentTransform>("CurrentTransform", maxEntities),
        One<G.LastTransform>("LastTransform", maxEntities),
        One<G.Velocity>("Velocity", maxEntities),
        One<G.TeleportCount>("TeleportCount", maxEntities),
        One<G.NpcCommon>("NpcCommon", maxEntities),
    ];

    static unsafe LegacyResult One<T>(string name, int maxEntities) where T : unmanaged
    {
        var bad = new List<string>();
        int n = 0, ok = 0;
        if (G.Components.NameOf<T>() != name) bad.Add($"{name}: Components.NameOf gives {G.Components.NameOf<T>()}");
        foreach (var e in G.Components.All<T>().Take(maxEntities))
        {
            var raw = World.ReadBytes(e, name);
            if (raw is null) continue;
            n++;
            if (!G.Components.TryGet<T>(e, out var v)) { bad.Add($"entity {e.Value}: Kiln.GameTypes.{name}: Components.TryGet returned false"); continue; }
            var typed = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref v, 1)).ToArray();
            if (typed.Length != raw.Length) { bad.Add($"entity {e.Value}: Kiln.GameTypes.{name}: {typed.Length} typed bytes, the live component has {raw.Length}"); continue; }
            var i = typed.AsSpan().CommonPrefixLength(raw);
            if (i < typed.Length) bad.Add($"entity {e.Value}: Kiln.GameTypes.{name}: byte +{i} is 0x{typed[i]:X2} typed, 0x{raw[i]:X2} raw");
            else ok++;
        }
        return new LegacyResult(name, sizeof(T), n, ok, bad);
    }
}
