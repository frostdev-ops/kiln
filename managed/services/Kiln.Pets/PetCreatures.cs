// SPDX-License-Identifier: MIT
// Kiln's translation of a pet's creature (a Kiln id) to what a body is made from. Mods name a creature only by its Kiln
// id: a game creature of the creatures registry ("game:<template debug name in lower case>", "game:<name>~<8 hex>" for a
// name two templates share, "game:template_<8 hex>" for a template with no name: the registry's id rules,
// docs/kiln/modding/registries/creatures.md) or a creature block of a mod ("<mod>:<block>", registered here by
// Kiln.Blocks). The template behind it is Kiln's part alone; a mod never sees it.
namespace Kiln;

/// <summary>What Kiln knows of a pet's creature: its Kiln id and what a body is made from (internal).</summary>
/// <param name="Id">The Kiln id as the definition names it.</param>
/// <param name="Template">The template a body spawns from.</param>
/// <param name="Variant">A creature block's kiln.creatures variant (a body spawns through it); null for a game creature.</param>
/// <param name="LevelMax">The creature's highest level; 0 when unknown.</param>
/// <param name="DisplayName">Its name for owners when the definition gives none.</param>
/// <param name="Problem">Why no body may be made of it (null: it may): a creature that is not an enemy, a boss.</param>
internal sealed record PetCreatureInfo(string Id, Guid Template, string? Variant, int LevelMax, string DisplayName, string? Problem = null);

/// <summary>Resolves creature Kiln ids. Tests replace <see cref="PetCreatures.Source"/>.</summary>
internal interface IPetCreatureSource
{
    /// <summary>False while the game's creatures are not readable here yet (a client's OnLoad, before content is ready):
    /// a definition then waits for its first body to be checked.</summary>
    bool Ready { get; }
    /// <summary>The creature, or null with <paramref name="reason"/>.</summary>
    PetCreatureInfo? Resolve(string id, out string? reason);
}

internal static class PetCreatures
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PetCreatureInfo> Blocks = new(StringComparer.Ordinal);

    /// <summary>The source of game creatures (the creature catalog of kiln.creatures); tests set their own.</summary>
    internal static IPetCreatureSource Source { get; set; } = new GameCreatureSource();

    /// <summary>Kiln.Blocks: a creature block a pet may name, by its Kiln id <c>&lt;mod&gt;:&lt;block&gt;</c>.</summary>
    internal static void RegisterBlock(string id, string variant, Guid template, string templateName, int levelMax, string displayName)
    {
        lock (Gate) Blocks[id] = new PetCreatureInfo(id, template, variant, levelMax, displayName);
    }

    internal static void ResetForTests()
    {
        lock (Gate) Blocks.Clear();
        Source = new GameCreatureSource();
    }

    /// <summary>True when the id names a creature block Kiln.Blocks registered.</summary>
    internal static bool IsBlock(string id)
    {
        lock (Gate) return Blocks.ContainsKey(id);
    }

    /// <summary>The creature, or null with the reason (<c>CreatureUnknown: ...</c>, <c>CreatureAmbiguous: ...</c>).</summary>
    internal static PetCreatureInfo? Resolve(string id, out string? reason)
    {
        lock (Gate)
            if (Blocks.TryGetValue(id, out var block))
            {
                reason = null;
                return block;
            }
        if (!id.StartsWith("game:", StringComparison.Ordinal))
        {
            reason = $"CreatureUnknown: no creature block \"{id}\" is defined (a pet names a game creature as game:<name>, or a creature block of a loaded mod)";
            return null;
        }
        return Source.Resolve(id, out reason);
    }

    /// <summary>The registry's id rules over a list of (template, name): the template a <c>game:</c> id names.</summary>
    internal static (Guid Template, string? Reason) Match(string id, IEnumerable<(Guid Template, string Name)> templates)
    {
        var name = id["game:".Length..];
        string? hex = null;
        var tilde = name.IndexOf('~');
        if (tilde >= 0)
        {
            hex = name[(tilde + 1)..];
            name = name[..tilde];
        }
        bool unnamed = hex is null && name.StartsWith("template_", StringComparison.Ordinal) && name.Length == "template_".Length + 8;
        if (unnamed) hex = name["template_".Length..];
        var found = new List<Guid>();
        foreach (var (g, n) in templates)
        {
            var ok = unnamed ? string.IsNullOrEmpty(n) : string.Equals(n, name, StringComparison.OrdinalIgnoreCase);
            if (!ok) continue;
            if (hex is not null && !g.ToString("N").StartsWith(hex, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(g);
        }
        return found.Count switch
        {
            1 => (found[0], null),
            0 => (Guid.Empty, $"CreatureUnknown: the game has no creature {id} on this build"),
            _ => (Guid.Empty, $"CreatureAmbiguous: {found.Count} templates are named {name}; name one as game:{name}~<first 8 hex digits of its GUID> (the creatures registry's id)"),
        };
    }

    /// <summary>The game's creature catalog (kiln.creatures).</summary>
    private sealed class GameCreatureSource : IPetCreatureSource
    {
        public bool Ready => Creatures.Available && Creatures.Templates.Count > 0;

        public PetCreatureInfo? Resolve(string id, out string? reason)
        {
            if (!Ready)
            {
                reason = "CreatureUnknown: the game's creatures are not readable here yet";
                return null;
            }
            var all = Creatures.Templates;
            var (g, why) = Match(id, all.Select(t => (t.Id, t.Name)));
            if (why is not null)
            {
                reason = why;
                return null;
            }
            var t = Creatures.Template(g)!;
            reason = null;
            string? problem = t.Kind != CreatureKind.Enemy ? $"CreatureUnsupported: {id} is {t.Kind.ToString().ToLowerInvariant()}, and pets are enemy creatures that walk in this Kiln"
                : t.IsBoss ? $"CreatureUnsupported: {id} is a boss" : null;
            return new PetCreatureInfo(id, g, null, t.LevelMax, t.DisplayName.Length > 0 ? t.DisplayName : t.Name, problem);
        }
    }
}
