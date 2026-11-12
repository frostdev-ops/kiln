// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>The advanced gate of kiln.abilities' raw entry points (a program blob, the raw bytes of a wrapped call).
/// Same rule, exception and wording as kiln.hooks' gate (<c>HooksGate</c>, which this assembly does not reference):
/// only a mod whose <c>kiln.mod.json</c> has <c>"advanced": true</c> (<see cref="ModInfo.Advanced"/>) may call them.
/// Kiln's own code calls the internal cores behind them, so an ordinary mod that uses buffs is never refused.</summary>
internal static class AbilitiesGate
{
    internal const string Why = "This raw entry point is powerful and build-specific";

    /// <summary>Returns the calling mod if it is advanced; throws otherwise.</summary>
    public static ModContext RequireAdvanced(string api) => Require(api, ModContext.CurrentMod);

    /// <summary>The same check for a mod known in advance (a handler's, decided where it was registered).</summary>
    public static ModContext Require(string api, ModContext? mod)
    {
        if (mod is null) throw new InvalidOperationException($"{api} must be called from a mod (on the tick thread or in OnLoad)");
        if (!mod.Info.Advanced) throw NotAdvanced(api, mod);
        return mod;
    }

    public static InvalidOperationException NotAdvanced(string api, ModContext mod) =>
        new($"{api}: mod '{mod.Info.Id}' is not advanced. {Why}; add \"advanced\": true to its kiln.mod.json to allow it.");
}
