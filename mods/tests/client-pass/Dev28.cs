// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;

namespace ClientPass;

// 0.2.0-dev.28: the guided checks of the engine dossier. One file per work package (Dev28<Name>.cs, a part of this
// class), so packages do not meet in one file. The ids are the dossier's, lower-case (dev28-checks.json lists them all
// with their state); each package's orders are its own block of a hundred (2000 + (lab port - 28000) * 10, the port
// from the wave's package table), and "run all" walks the packages in that order.
//
// What every dev.28 check does:
//   - Since = the release its package ships in (the dossier ships as a release train; dev28-releases.json says which
//     release each id is played in, and tools/client-checks/plan.py names a check whose Since differs): Dev28 for a
//     package of dev.28, Dev29 ... Dev33 for a later one. Area = the chapter's short name, Needs = the session;
//   - Tags say what else it wants: TwoPlayers, VanillaClient, HostTools, Frameworks, Visual, Audio;
//   - it starts with `if (!await Need(c, "<fixture mod id>")) return;` when it uses a fixture or sample mod, so a
//     missing mod ends the check as skipped (c.Skip: the player cannot pass it);
//   - when the capability it tests is held by the client gate (native/core/gates), it starts with
//     `if (!await NeedOpen(c, "<capability>")) return;`: the pass runs with "preview": true in kiln.json, and a check
//     that ran against a capability still switched off would test nothing.
public sealed partial class ClientPassMod
{
    private const string Dev28 = "0.2.0-dev.28";
    private const string Dev29 = "0.2.0-dev.29";
    private const string Dev30 = "0.2.0-dev.30";
    private const string Dev31 = "0.2.0-dev.31";
    private const string Dev32 = "0.2.0-dev.32";
    private const string Dev33 = "0.2.0-dev.33";

    /// <summary>Needs a game client without Kiln joined to the server (a second Steam account or a second person;
    /// the body runs on the server and the answers come from that player).</summary>
    private const string VanillaClient = "vanilla-client";

    /// <summary>Done (partly) with the host's tools outside the game: the debugger, <c>kiln publish</c>, the update
    /// check.</summary>
    private const string HostTools = "host-tools";

    /// <summary>Runs in the frameworks profile (other loaders' mods next to Kiln's).</summary>
    private const string Frameworks = "frameworks";

    /// <summary>True when none of <paramref name="capabilities"/> is held by the client gate. Otherwise names the held
    /// ones, says how the pass switches them on, ends the check as skipped (the player cannot pass it) and returns
    /// false.</summary>
    private static async Task<bool> NeedOpen(CheckContext c, params string[] capabilities)
    {
        var held = capabilities.Where(Capabilities.IsHeld).ToArray();
        if (held.Length == 0) return true;
        var list = string.Join(", ", held);
        c.Log.Info($"client-pass: {c.Id} tests {list}, which is still held: {Capabilities.Why(held[0])}");
        c.Instruct($"This check tests {list}, which is switched off until this pass confirms it. The pass profile " +
                   "switches it on with \"preview\": true in kiln/kiln.json (docs/kiln/client-pass.md); this game was " +
                   "started without it. Add the key and start again.");
        await c.Skip($"{list} is still held: start the pass with \"preview\": true");
        return false;
    }
}
