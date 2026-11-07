// SPDX-License-Identifier: MIT
// Materials that replace a game material ("replaces") belong to their mod. kiln.vfx takes the override off when the owner it was given with the
// definition is revoked; a mod whose blocks compile under one of Kiln's owners (data files only, or code that never defines a block) keeps
// that owner across an unload, so Kiln.Blocks tells kiln.vfx itself when such a mod is no longer loaded. docs/kiln/modding/materials.md.
using System.Text.Json;

namespace Kiln;

internal static class Reskins
{
    private static readonly HashSet<string> Tied = new();
    private static readonly object Gate = new();

    /// <summary>A replacement of this mod's is registered with kiln.vfx.</summary>
    public static void Registered(string mod)
    {
        lock (Gate) Tied.Add(mod);
    }

    /// <summary>The mod is not loaded (Kiln.Blocks' data pass, on the tick thread): its replacements go, once.</summary>
    public static void Unloaded(string mod)
    {
        lock (Gate)
            if (!Tied.Remove(mod)) return;
        VfxNative.DefineJson($"{{\"format\":1,\"mod\":{JsonSerializer.Serialize(mod)},\"release\":true}}", 0);
    }
}
