// SPDX-License-Identifier: MIT
using System.Numerics;
using Kiln;
using Kiln.Testing;

namespace ClientPass;

/// <summary>
/// The client pass: one guided check for each thing only a player in the real game can confirm (the status page's rows
/// awaiting a game test, and the open client checklists), ordered into sessions. A session is one start of the game
/// with one set of mods and one kind of world; <see cref="ClientCheckAttribute.Order"/> is the session number times 100
/// plus the place in it. docs/kiln/client-pass.md lists the sessions with what to load for each; rows.json maps the
/// status page to the check ids.
/// </summary>
/// <remarks>
/// The checks use the sample mods for content instead of copying them. A check whose sample is not loaded says which one
/// to add and asks for skip, so a missing sample never reads as a failure. What a check spawns it removes when it ends;
/// what it gives stays in the inventory.
/// </remarks>
public sealed partial class ClientPassMod : Mod
{
    // Since values: the release that added what a check covers.
    private const string Dev10 = "0.2.0-dev.10";
    private const string Dev16 = "0.2.0-dev.16";
    private const string Dev18 = "0.2.0-dev.18";
    private const string Dev21 = "0.2.0-dev.21";
    private const string Dev22 = "0.2.0-dev.22";
    private const string Dev24 = "0.2.0-dev.24";
    private const string Dev26 = "0.2.0-dev.26";

    // The sample and test mods the pass uses (their ids).
    private const string RenderSample = "rendersample";
    private const string VfxSample = "vfx-sample";
    private const string ShaderSample = "shader-sample";
    private const string MaterialSample = "materialsample";
    private const string BlocksMore = "blocks-more-test";
    private const string Frostbolt = "frostbolt-block";
    private const string ItemBlocks = "item-blocks";
    private const string AbilityBlocks = "ability-blocks";
    private const string CreatureAi = "creature-ai";
    private const string CreatureBlocks = "creature-blocks";
    private const string RetargetTest = "retarget-test";
    private const string CosmeticSample = "cosmetic-sample";
    private const string RulesSample = "rules-sample";
    private const string NpcSample = "npcsample";
    private const string WorldPrograms = "world-programs";

    // Game items and templates the scenes use.
    private const string Staff = "Weapon_T1_2H_Staff_01";
    private const string Vukah = "Enemy_Vukah_Melee";
    private const string Twigs = "Material_T1_Twigs";

    public override void OnLoad()
    {
        var n = ClientChecks.Register(this);
        Log.Info($"client-pass: {n} guided check(s) registered; open the list with Ctrl+Shift+F5");
    }

    // ------------------------------------------------------------------------------------------------ helpers ---

    /// <summary>True when every mod in <paramref name="ids"/> is loaded. Otherwise names the missing ones, asks the
    /// player for skip and returns false after the verdict: a missing sample is not a failure of what the check
    /// covers.</summary>
    private static async Task<bool> Need(CheckContext c, params string[] ids)
    {
        var missing = ids.Where(id => !Mods.IsLoaded(id)).ToArray();
        if (missing.Length == 0) return true;
        var list = string.Join(", ", missing);
        c.Log.Info($"client-pass: {c.Id} needs {list}, which is not loaded");
        c.Instruct($"This check needs the mod{(missing.Length > 1 ? "s" : "")} {list} (see the profile in " +
                   "docs/kiln/client-pass.md). Add it to the profile for the next start. Press skip now.");
        await c.Verdict();
        return false;
    }

    /// <summary>True when the check runs in a world with a player (hosted or joined). Otherwise asks for skip.</summary>
    private static async Task<bool> NeedPlayer(CheckContext c)
    {
        if (!c.Player.IsNone && World.Position(c.Player) is not null) return true;
        c.Instruct("This check needs your character in the world. Host a world or join one, then run it again. Press skip now.");
        await c.Verdict();
        return false;
    }

    /// <summary>A point <paramref name="forward"/> metres ahead of the player (and <paramref name="right"/> metres to the
    /// right, <paramref name="up"/> above the feet), or null when the player's place is not known.</summary>
    private static WorldPosition? Ahead(CheckContext c, double forward, double right = 0, double up = 0)
    {
        if (World.Transform(c.Player) is not { } t) return null;
        var offset = Vector3.Transform(new Vector3((float)-right, 0, (float)forward), t.Rotation);
        return new WorldPosition(t.Position.X + offset.X, t.Position.Y + up, t.Position.Z + offset.Z);
    }

    /// <summary>Spawns a game template or another mod's creature block at <paramref name="at"/> and removes it when the
    /// check ends. A creature block is named <c>mod:name</c>.</summary>
    private static async Task<EntityId> SpawnHere(CheckContext c, string what, WorldPosition at, float yaw = 0)
    {
        EntityId e;
        try
        {
            e = what.Contains(':') ? await Blocks.Spawn(what, at, yaw) : await Spawn.Entity(what, at, yaw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.Expect(false, $"spawned {what}", ex.Message);
            return EntityId.None;
        }
        c.Expect(!e.IsNone, $"spawned {what}", e.ToString());
        if (!e.IsNone) c.OnEnd(() => Spawn.Destroy(e));
        return e;
    }

    /// <summary>Gives the player <paramref name="count"/> of a game item by its name and records the result.</summary>
    private static async Task GiveGame(CheckContext c, string item, int count = 1)
    {
        var r = await Items.Give(c.Player, item, count);
        c.Expect(r.Succeeded, $"gave {count} x {item}", r.ToString());
    }

    /// <summary>Gives the player <paramref name="count"/> of another mod's item block and records the result.</summary>
    private static async Task GiveBlock(CheckContext c, string mod, string block, int count = 1)
    {
        if (Blocks.Find(block, mod) is not { } h)
        {
            c.Expect(false, $"gave {count} x {mod}:{block}", "the block is not defined");
            return;
        }
        var r = await Items.Give(c.Player, h.GameName, count);
        c.Expect(r.Succeeded, $"gave {count} x {mod}:{block}", r.ToString());
    }

    /// <summary>Plays another mod's VFX block at <paramref name="at"/>.</summary>
    private static void PlayVfx(CheckContext c, VfxId vfx, WorldPosition at, VfxParams? parameters = null)
    {
        try
        {
            var h = Vfx.PlayAt(vfx, at, parameters: parameters);
            c.Log.Info($"client-pass: {c.Id} played {vfx} -> {h}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.Expect(false, $"played {vfx}", ex.Message);
        }
    }

    /// <summary>Plays <paramref name="play"/> every <paramref name="every"/> until the player gives a verdict, so the
    /// player can look as long as they need.</summary>
    private static async Task RepeatUntilVerdict(CheckContext c, TimeSpan every, Action play)
    {
        var verdict = c.Verdict();
        while (!verdict.IsCompleted)
        {
            play();
            var until = DateTime.UtcNow + every;
            while (!verdict.IsCompleted && DateTime.UtcNow < until) await c.Ticks();
        }
        await verdict;
    }

    /// <summary>A check done (partly) outside the game: shows the steps and takes the answer.</summary>
    private static async Task Outside(CheckContext c, string text)
    {
        c.Instruct(text);
        await c.Verdict();
    }
}
