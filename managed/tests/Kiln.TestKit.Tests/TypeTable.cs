// SPDX-License-Identifier: MIT
// Kiln.Api's GameTypes, for tests that also reference Kiln.Game: inside a Kiln.* namespace the name GameTypes is both
// that class and Kiln.Game's legacy namespace (CS0434; docs/kiln/typegen.md). A mod's own namespace has no such clash.
using Kiln;

namespace TestKitTypes;

internal static class TypeTable
{
    public static bool Available => GameTypes.Available;
    public static GameType? Find(string name) => GameTypes.Find(name);
    public static IReadOnlyList<GameType> Components => GameTypes.Components;
}
