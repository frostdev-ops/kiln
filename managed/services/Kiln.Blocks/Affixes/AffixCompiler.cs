// SPDX-License-Identifier: MIT
// Affix kinds and pools as blocks: what Compiler.Lower does with an AffixBlock or an AffixPoolBlock. Nothing reaches the
// game: the block is checked by field, and at commit it becomes the mod's affix or pool in AffixRuntime's registry under
// "mod:name", the id rolls keep. Blocks.Define, the "affixes" and "affixPools" sections of blocks/*.json and
// Affixes.Define/DefinePool/Load all come here, so a definition is one object and one id whichever route made it.
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    private static BlockHandle DefineAffix(BlockContext ctx, Lowered low, string name, AffixBlock a)
    {
        var what = $"affix {name}";
        AffixRuntime.CheckName(what, name);
        AffixRuntime.CheckAffix(what, a);
        var handle = new AffixHandle(ctx.Mod.Id, name, a);
        low.Json["affix"] = new JsonObject
        {
            ["id"] = handle.Id, ["name"] = a.Name, ["stat"] = a.Stat.Key, ["tiers"] = a.Tiers.Count, ["stacks"] = a.Stacks, ["group"] = a.Group,
        };
        handle.Lowered = low.Json;
        low.AfterCommit(() => AffixRuntime.Register(handle));
        return handle;
    }

    private static BlockHandle DefineAffixPool(BlockContext ctx, Lowered low, string name, AffixPoolBlock p)
    {
        var what = $"affix pool {name}";
        AffixRuntime.CheckName(what, name);
        var entries = AffixRuntime.CheckPool(what, ctx.Mod.Id, p, (affix, path) => AffixOf(ctx, what, affix, path));
        var handle = new AffixPoolHandle(ctx.Mod.Id, name, p) { Entries = entries };
        low.Json["affixPool"] = new JsonObject
        {
            ["id"] = handle.Id,
            ["items"] = p.Items is { } items ? new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i.Value)).ToArray()) : null,
            ["affixes"] = new JsonArray(entries.Select(e => (JsonNode?)new JsonObject { ["affix"] = e.Affix, ["weight"] = e.Weight }).ToArray()),
            ["min"] = p.Min,
            ["max"] = p.Max,
        };
        handle.Lowered = low.Json;
        low.AfterCommit(() => AffixRuntime.Register(handle, entries));
        return handle;
    }

    /// <summary>The data pass's rule for an affix or a pool of a data file (BlockJson.LoadMod): after <c>OnLoad</c> the file's
    /// definition is the mod's, as for every block whose name code also defines. One that code named during <c>OnLoad</c>
    /// was compiled then, and the data pass gets that same handle back; if code defined the name again after naming it,
    /// the file's goes back in here, so the outcome does not depend on whether something named the affix first.</summary>
    internal static void AffixDataPass(ModBlocks mod, string name, BlockHandle made, string file)
    {
        if (made is not (AffixHandle or AffixPoolHandle) || !mod.Handles.TryGetValue(name, out var now) || ReferenceEquals(now, made)) return;
        mod.Handles[name] = made;
        switch (made)
        {
            case AffixHandle a:
                if (a.Lowered is { } la) mod.Lowered[name] = la;
                AffixRuntime.Register(a);
                break;
            case AffixPoolHandle p:
                if (p.Lowered is { } lp) mod.Lowered[name] = lp;
                AffixRuntime.Register(p, p.Entries);
                break;
        }
        mod.ReportDirty = true;
        BlocksBridge.Log(LogLevel.Info, $"blocks: {mod.Id}: {file}'s {(made is AffixPoolHandle ? "affix pool" : "affix")} {name} replaces the one code defined after naming it, as data files' blocks do");
    }

    /// <summary>A pool entry's affix: of this mod by its name (or <c>"this-mod:name"</c>), defined in code or in a data file
    /// of the mod, which is compiled first when it comes later, as every block reference does; or another mod's, defined
    /// when that mod loaded. Null when it is not defined.</summary>
    private static AffixHandle? AffixOf(BlockContext ctx, string what, string affix, string path)
    {
        var colon = affix.IndexOf(':');
        if (colon > 0 && affix[..colon] != ctx.Mod.Id) return AffixRuntime.FindAffix(affix)?.Handle;
        var name = colon > 0 ? affix[(colon + 1)..] : affix;
        var h = ctx.Find(name, path);
        if (h is null) return null;
        if (h is not AffixHandle a) throw new BlockError(what, path, $"\"{name}\" is {AffixKindOf(h.BlockType)} block of this mod, not an affix");
        // a handle of a lifetime of the mod that has unloaded since is no definition
        return AffixRuntime.FindAffix(a.Id) is { } def && ReferenceEquals(def.Handle, a) ? a : null;
    }

    private static string AffixKindOf(Type t)
    {
        var kind = t == typeof(AffixPoolBlock) ? "affix pool" : t.Name.Replace("Block", "").ToLowerInvariant();
        return ("aeiou".Contains(kind[0]) ? "an " : "a ") + kind;
    }

    /// <summary>Compiler.Lower's rule for names that an affix or a pool has or wants. A name is one block of a mod: an affix or
    /// a pool takes no name another kind of block of the mod has, and no other block takes an affix's or a pool's, as rolls
    /// keep <c>"mod:name"</c> and a pool would lose the affix it names. The same kind again replaces, as for every block.</summary>
    internal static void AffixNameFree(ModBlocks mod, string name, IBlock block, string label)
    {
        if (!mod.Handles.TryGetValue(name, out var held) || held.BlockType == block.GetType()) return;
        if (held is not (AffixHandle or AffixPoolHandle) && block is not (AffixBlock or AffixPoolBlock)) return;
        throw new BlockError(block is AffixPoolBlock ? $"affix pool {name}" : label, null,
            $"\"{name}\" is {AffixKindOf(held.BlockType)} of this mod already: an affix and an affix pool need a name no other block of the mod has");
    }
}
