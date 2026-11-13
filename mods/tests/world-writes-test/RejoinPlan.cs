// SPDX-License-Identifier: MIT
// What world.tracked-water-rejoin leaves for world.tracked-water-rejoin-after (ClientChecks.cs), as plain functions with
// no game in them: managed/tests/Kiln.World.Tests compiles this file and tests them (RejoinPlanTests.cs).
using System.Text.Json;

namespace WorldWritesTest;

/// <summary>The first half's plan: the mark it put on the can it gave (instance data only this check sets), the world and
/// the character it is for, the session it ran in, the units it wrote, when.</summary>
public sealed record RejoinPlan(string Mark, string World, string Character, string Session, int Units, string When)
{
    /// <summary>The plan's file name: one per world and character, so another world or another character never sees it.</summary>
    public static string FileName(string world, string character) => $"rejoin-{Clean(world)}-{Clean(character)}.json";

    private static string Clean(string s) => new([.. s.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_')]);

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>A plan from its file's text; null for no text, or text that is not a whole plan.</summary>
    public static RejoinPlan? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<RejoinPlan>(json) is { Mark.Length: > 0, World.Length: > 0, Character.Length: > 0, Session.Length: > 0 } p ? p : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Whether the second half may judge.</summary>
public enum RejoinState
{
    /// <summary>The first half did not run in this world with this character (or its plan is unreadable).</summary>
    NoPlan,

    /// <summary>The plan is another world's or another character's.</summary>
    OtherWorldOrCharacter,

    /// <summary>The first half ran in this very session: the player has not left and come back yet.</summary>
    SameSession,

    /// <summary>A plan of this world and character from another session.</summary>
    Ready,
}

public static class Rejoin
{
    /// <summary>The second half judges only a plan of this world and character made in another session.</summary>
    public static RejoinState Judge(RejoinPlan? plan, string world, string character, string session) =>
        plan is null ? RejoinState.NoPlan
        : plan.World != world || plan.Character != character ? RejoinState.OtherWorldOrCharacter
        : plan.Session == session ? RejoinState.SameSession
        : RejoinState.Ready;

    /// <summary>The one carried item whose mark is <paramref name="mark"/>: its index in <paramref name="marks"/> (each
    /// item's mark, null for none), or -1 and why not. None or more than one: the caller takes nothing.</summary>
    public static (int Index, string? Why) Marked(IReadOnlyList<string?> marks, string mark)
    {
        var found = -1;
        for (var i = 0; i < marks.Count; i++)
        {
            if (marks[i] != mark) continue;
            if (found >= 0) return (-1, $"{marks.Count(m => m == mark)} items you carry have this check's mark");
            found = i;
        }
        return found >= 0 ? (found, null) : (-1, "no item you carry has this check's mark");
    }

    /// <summary>A session: this load of the mod (a hosted world loads its server-side mods anew each time it opens), the
    /// player's connection handle and the step they joined in (a player who leaves a server and joins again gets new
    /// ones).</summary>
    public static string Session(Guid load, uint handle, ulong joinedStep) => $"{load:N}/{handle}/{joinedStep}";
}
