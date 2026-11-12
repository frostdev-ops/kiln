// SPDX-License-Identifier: MIT
// Which attack slots a weapon class has, and how each is found on an item. A slot is an entry of the item's `sequences` list, and the same slot sits at another
// index on every starter, so a slot is never named by index: it is a selector (what triggers it, the requirements, the special interaction), matched against
// the made item's list when content is ready. The selectors are what the eleven starters' lists hold (docs/kiln/modding/item-blocks.md, "Moves").
namespace Kiln;

/// <summary>An attack slot of a weapon, as <see cref="WeaponMoves"/> names them.</summary>
internal enum MoveSlot { Primary, Heavy, HeavyAir, DodgeAttack, JumpAttack, PlungeAttack, Block, Parry, Special, Execution, SneakAttack }

/// <summary>How a slot is found in an item's <c>sequences</c>: <c>triggeredBy</c>, the set of <c>requirements</c>, <c>specialInteractionType</c>.</summary>
internal sealed record SlotSelector(string Trigger, IReadOnlyList<string> Requirements, string Special = "Invalid")
{
    public override string ToString() =>
        $"{Trigger}{(Requirements.Count > 0 ? " while " + string.Join(" + ", Requirements) : "")}{(Special != "Invalid" ? $" ({Special})" : "")}";
}

/// <summary>One entry of an item's <c>sequences</c>, as content ready reads it.</summary>
internal sealed record SlotRow(string Trigger, IReadOnlyList<string> Requirements, string Special, Guid Sequence);

internal static class MoveSlots
{
    /// <summary>JSON's key of a slot (<see cref="WeaponMoves"/>' fields, camelCase).</summary>
    public static string Key(MoveSlot s) => s switch
    {
        MoveSlot.Primary => "primary", MoveSlot.Heavy => "heavy", MoveSlot.HeavyAir => "heavyAir", MoveSlot.DodgeAttack => "dodgeAttack", MoveSlot.JumpAttack => "jumpAttack",
        MoveSlot.PlungeAttack => "plungeAttack", MoveSlot.Block => "block", MoveSlot.Parry => "parry", MoveSlot.Special => "special", MoveSlot.Execution => "execution",
        _ => "sneakAttack",
    };

    private static SlotSelector S(string trigger, params string[] req) => new(trigger, req);

    // What a melee weapon's starter has: sword, axe, mace, greatsword, greataxe, hammer, daggers.
    private static readonly Dictionary<MoveSlot, SlotSelector> Melee = new()
    {
        [MoveSlot.Primary] = S("Primary"),
        [MoveSlot.Heavy] = S("Heavy", "IsGrounded"),
        [MoveSlot.HeavyAir] = S("Heavy", "IsAirborne"),
        [MoveSlot.DodgeAttack] = S("Primary", "IsEvading"),
        [MoveSlot.JumpAttack] = S("Primary", "IsAirborne_Not_Falling"),
        [MoveSlot.PlungeAttack] = S("Heavy", "IsAboveTarget", "IsAirborne_Falling"),
        [MoveSlot.Block] = S("Block"),
        [MoveSlot.Parry] = S("Parry"),
        [MoveSlot.Special] = S("SpecialAbility"),
        [MoveSlot.Execution] = new("Interact", [], "Enemy_Execution"),
        [MoveSlot.SneakAttack] = new("Interact", [], "Sneak_Attack"),
    };

    // The two-handed blunt weapons' special needs the ground.
    private static readonly Dictionary<MoveSlot, SlotSelector> Heavies = new(Melee) { [MoveSlot.Special] = S("SpecialAbility", "IsGrounded") };

    private static readonly Dictionary<string, Dictionary<MoveSlot, SlotSelector>> ByClass = new()
    {
        ["sword"] = Melee, ["axe"] = Melee, ["mace"] = Melee, ["greatsword"] = Melee, ["daggers"] = Melee,
        ["greataxe"] = Heavies, ["hammer"] = Heavies,
        // a bow's and a staff's primary slot holds only the motion of having no ammo: their attacks come from the arrow and the spell
        ["bow"] = new() { [MoveSlot.Special] = S("SpecialAbility") },
        ["staff"] = new(),
        ["shield"] = new() { [MoveSlot.Block] = S("Block"), [MoveSlot.Parry] = S("Parry") },
    };

    /// <summary>The slots a class has, with their selectors. An own class (<c>like</c>) has its premade's: <see cref="WeaponClass.Key"/> stays the premade's.</summary>
    public static IReadOnlyDictionary<MoveSlot, SlotSelector> Of(WeaponClass c) => ByClass.TryGetValue(c.Key, out var m) ? m : new Dictionary<MoveSlot, SlotSelector>();

    /// <summary>Why a class lacks slots others have, for the error.</summary>
    public static string? Note(WeaponClass c) => c.Key switch
    {
        "bow" => "a bow's attacks are its arrow's: its primary slot only holds the motion of having none",
        "staff" => "a staff's attacks are its spells' (a spell block's cast takes animation and hitAt): its primary slot only holds the motion of having none",
        "shield" => "a shield only blocks and parries",
        _ => null,
    };

    /// <summary>Every index of <paramref name="rows"/> the selector matches. The requirements are a set; all slots that match count (the daggers list the jump attack twice).</summary>
    public static int[] Match(IReadOnlyList<SlotRow> rows, SlotSelector s)
    {
        var want = s.Requirements.ToHashSet(StringComparer.Ordinal);
        var found = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Trigger != s.Trigger || (r.Special is { Length: > 0 } sp ? sp : "Invalid") != s.Special) continue;
            if (r.Requirements.Count(x => x.Length > 0 && x != "None") != want.Count || !r.Requirements.Where(x => x.Length > 0 && x != "None").All(want.Contains)) continue;
            found.Add(i);
        }
        return found.ToArray();
    }
}
