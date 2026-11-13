// SPDX-License-Identifier: MIT
using System.Numerics;
using Kiln;

namespace MaterialSample;

/// <summary>
/// Sample: materials written from nothing (docs/kiln/modding/materials.md).
/// <list type="bullet">
/// <item>blocks/materials.json: frost_steel and frost_grip, on the game's shaders with no game material named, and a spark effect whose particles take their look from a material.</item>
/// <item>assets/cook.json: Frost Steel Sword's glTF materials are mapped to those blocks by name. Ember Sword's blocks are made from its glTF's own materials.</item>
/// <item>The same block as frost_steel in C#, below. A definition from code is there from the next start, as the game lists every material's textures once at boot, so a data file is what a
/// model should name.</item>
/// </list>
/// The code reports what the game ended up with, once data files are compiled: one line per material. Ctrl+9 plays the sparks 3 m ahead.
/// </summary>
public sealed class MaterialSampleMod : Mod
{
    public override void OnLoad()
    {
        var code = Blocks.Define("frost_steel-code", new MaterialBlock
        {
            Kind = MaterialKind.Metal,
            Albedo = "textures/steel.png",
            Normal = "textures/steel_n.png",
            MetallicRoughness = "textures/steel_mr.png",
            Tint = "#dff2ff",
            Emissive = new EmissiveBlock { Map = "textures/runes.png", Color = "#7fd4ff", Strength = 8 },
        });
        Log.Info($"MaterialSample: frost_steel-code is {code.Guid}, {code.Status}");
        if (Content.Available)
            Content.OnReady(_ =>
            {
                foreach (var name in new[] { "frost_steel", "frost_grip", "ember_sword.blade", "ember_sword.grip" })
                    Log.Info(Blocks.Find(name) is MaterialBlockHandle h ? $"MaterialSample: {name} is {h.Guid}, {h.Status}" : $"MaterialSample: {name} is not defined: run build-assets.sh before deploying");
            });
        if (Client.Available && Client.IsClient)
            KeyBindings.Register("materialsample.sparks", "Ctrl+9", () => Client.Post(Sparks), "MaterialSample: sparks that take their look from a material");
    }

    // World.Transform belongs to the world's tick thread, and a play from a key to the main thread.
    private void Sparks() => Context.Scheduler.Post(() =>
    {
        if (World.Transform(Client.LocalPlayer) is not { } t || Blocks.Find("frost_sparks") is not VfxBlockHandle sparks) return;
        var ahead = Vector3.Transform(new Vector3(0, 0, 1), t.Rotation) * 3;
        var at = new WorldPosition(t.Position.X + ahead.X, t.Position.Y + 1, t.Position.Z + ahead.Z);
        Client.Post(() => Log.Info($"MaterialSample: sparks at {at}: {Vfx.PlayAt(sparks, at)}"));
    });
}
