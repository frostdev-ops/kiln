// SPDX-License-Identifier: MIT
// Map reveal (game client): how much of the map the fog of war uncovers around the player, through kiln.rules'
// map_reveal (a hook on the fog's per-frame discover call; docs/re/creative-mode-146.md Part 3, section 1). For this
// session only, and only while creative mode is on: leaving the world or creative mode sets it back to the game's.
// What is uncovered stays uncovered for the character (the game saves it).
namespace Kiln;

internal static unsafe class CreativeMap
{
    public static readonly (string Name, uint Mode, float Radius)[] Choices =
    [
        ("The game's", RulesNative.MapDefault, 0f), ("200 m", RulesNative.MapRadius, 200f), ("400 m", RulesNative.MapRadius, 400f),
        ("Whole map", RulesNative.MapWhole, 0f),
    ];
    public static readonly string[] Names = Choices.Select(c => c.Name).ToArray();

    private static int _choice;

    /// <summary>The choice in effect (an index into <see cref="Choices"/>).</summary>
    public static int Choice => Volatile.Read(ref _choice);

    /// <summary>This game client has the hook (kiln.rules from 0.2.0-dev.16 on, a known build).</summary>
    public static bool Available
    {
        get
        {
            try
            {
                return RulesNative.HasCreative && (RulesNative.Hooks & RulesNative.HookMap) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Sets the reveal; null on success, else why not.</summary>
    public static string? Set(int choice)
    {
        if (choice < 0 || choice >= Choices.Length) return "unknown choice";
        if (choice != 0 && CreativeFlame.Running) return CreativeFlame.Note;
        if (!Available) return "this game build has no map reveal hook";
        var c = Choices[choice];
        var r = RulesNative.C->MapReveal(c.Mode, c.Radius);
        if (r != 0) return $"kiln.rules refused ({r})";
        var was = Interlocked.Exchange(ref _choice, choice);
        if (was != choice) RulesRuntime.Log(LogLevel.Info, $"creative map reveal: {c.Name}");
        return null;
    }

    /// <summary>Back to the game's range (world left, creative mode off).</summary>
    public static void Reset()
    {
        if (Interlocked.Exchange(ref _choice, 0) == 0) return;
        if (Available) RulesNative.C->MapReveal(RulesNative.MapDefault, 0);
    }
}
