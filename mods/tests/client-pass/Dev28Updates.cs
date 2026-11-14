// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// 12c-update-check (chapter 12, part 12c): update notices for installed mods. A launcher check: nothing of it happens
// in the game, the check only shows the steps and takes the answer (as the launcher checks of Session1.cs do). It
// holds no capability. Fixtures: mods/tests/updates-fixtures/local-tweaks (no source) and github-sample (a manifest
// with "updates"), each a folder to drop on the launcher; the third mod is any mod installed from the launcher's
// Catalog. Orders 3600-3699.
public sealed partial class ClientPassMod
{
    [ClientCheck("12-updates-launcher", "Manual updates without installation", Area = "Tooling", Since = Dev28,
        Tags = [Launcher, HostTools], Order = 3600)]
    public Task UpdatesLauncher(CheckContext c) => Outside(c,
        "Done in the launcher before this start, no game needed. " +
        "1. Drop the two folders mods/tests/updates-fixtures/local-tweaks and github-sample on the launcher, and have one " +
        "mod installed from the Catalog (any, signed in to Nexus Mods). " +
        "2. Above your mods press Check for updates. In each mod's menu open Updates…: \"Updates fixture: no source\" says " +
        "No update source; the Catalog mod says Up to date or shows an update, with \"with your account\" in its source; " +
        "\"Updates fixture: GitHub\" names example-org/kiln-updates-fixture and says Unknown (no repository or no matching " +
        "release was found), never Up to date. " +
        "3. The fixture's repository is made up, so an update can only show for a repository of your own: in that " +
        "dialog set Repository to a public GitHub repository of yours that has a release tagged v<version> above 0.1.0, confirm \"Change where this mod is checked?\", and see Update <version> in its row. Click it: the " +
        "release page opens in the browser and nothing else happens. The row still shows version 0.1.0, the mod's switch " +
        "is as it was, and nothing new is in the list. " +
        "4. Disconnect the network and press Check for updates again: the update stays, marked stale, and no mod turns " +
        "to Up to date by it. Reconnect. " +
        "5. Settings, Launcher: \"Check for mod updates once a day\" is off unless you switched it on. " +
        "Did every step read as described, with nothing downloaded, installed or switched by a check?");
}
