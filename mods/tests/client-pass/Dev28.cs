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
//   - Since = Dev28, Area = the chapter's short name, Needs = the session it wants;
//   - Tags say what else it wants: TwoPlayers, VanillaClient, HostTools, Frameworks, Visual, Audio;
//   - it starts with `if (!await Need(c, "<fixture mod id>")) return;` when it uses a fixture or sample mod, so a
//     missing mod asks for skip and never passes;
//   - when the capability it tests is held by the client gate (native/core/gates), it starts with
//     `if (!await NeedOpen(c, "<capability>")) return;`: the pass runs with "preview": true in kiln.json, and a check
//     that ran against a capability still switched off would test nothing.
public sealed partial class ClientPassMod
{
    private const string Dev28 = "0.2.0-dev.28";

    /// <summary>Needs a game client without Kiln joined to the server (a second Steam account or a second person;
    /// the body runs on the server and the answers come from that player).</summary>
    private const string VanillaClient = "vanilla-client";

    /// <summary>Done (partly) with the host's tools outside the game: the debugger, <c>kiln publish</c>, the update
    /// check.</summary>
    private const string HostTools = "host-tools";

    /// <summary>Runs in the frameworks profile (other loaders' mods next to Kiln's).</summary>
    private const string Frameworks = "frameworks";

    /// <summary>True when none of <paramref name="capabilities"/> is held by the client gate. Otherwise names the held
    /// ones, says how the pass switches them on, asks for skip and returns false after the verdict.</summary>
    private static async Task<bool> NeedOpen(CheckContext c, params string[] capabilities)
    {
        var held = capabilities.Where(Capabilities.IsHeld).ToArray();
        if (held.Length == 0) return true;
        var list = string.Join(", ", held);
        c.Log.Info($"client-pass: {c.Id} tests {list}, which is still held: {Capabilities.Why(held[0])}");
        c.Instruct($"This check tests {list}, which is switched off until this pass confirms it. The pass profile " +
                   "switches it on with \"preview\": true in kiln/kiln.json (docs/kiln/client-pass.md); this game was " +
                   "started without it. Press skip now, add the key and start again.");
        await c.Verdict();
        return false;
    }
}
