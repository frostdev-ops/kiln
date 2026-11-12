// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>The advanced gate of kiln.creatures' raw entry points (job-thread rule handlers). Same rule, exception and
/// wording as kiln.hooks' gate (<c>HooksGate</c>, which this assembly does not reference): only a mod whose
/// <c>kiln.mod.json</c> has <c>"advanced": true</c> (<see cref="ModInfo.Advanced"/>) may call them.</summary>
internal static class CreaturesGate
{
    /// <summary>Returns the calling mod if it is advanced; throws otherwise.</summary>
    public static ModContext RequireAdvanced(string api)
    {
        var mod = ModContext.CurrentMod
            ?? throw new InvalidOperationException($"{api} must be called from a mod (on the tick thread or in OnLoad)");
        if (!mod.Info.Advanced)
            throw new InvalidOperationException(
                $"{api}: mod '{mod.Info.Id}' is not advanced. This raw entry point is powerful and build-specific; " +
                "add \"advanced\": true to its kiln.mod.json to allow it.");
        return mod;
    }
}
