// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// Sessions 3 to 5: mods of other frameworks, hosted. The frameworks profile (client-pass and a mod of each framework the
// player has: EML, ShroudForge, Greeneye, Flame, Shroudtopia, Enshwick, shroudlink's singleplayer addon, an EMF mod with
// portraits under kiln-emf, and XHL-Core as a closed-source DLL mod). Session 3 is the first start with it, session 4
// the second, session 5 a start with ShroudForge's own loader file in the game folder. Orders 301-599.
public sealed partial class ClientPassMod
{
    // ---- session 3: first start with the frameworks profile ----

    [ClientCheck("eml.first-start", "The first start with EML mods takes seconds", Area = "Other frameworks", Since = Dev22,
        Tags = [Launcher], Order = 301)]
    public Task EmlFirstStart(CheckContext c) => Outside(c,
        "This was the first start with EML mods on this install (session 3). Between Play and the game's window: a small " +
        "progress window appears only if the step took longer than two seconds, never takes the focus, and is gone before " +
        "the game's window shows. Did the start take seconds, not minutes, with the progress window behaving so?");

    [ClientCheck("library.guard-running", "Remove is refused while the game runs", Area = "Launcher", Since = Dev21,
        Tags = [Launcher], Order = 302)]
    public Task LibraryGuardRunning(CheckContext c) => Outside(c,
        "Switch to the launcher now, with the game running. In the list on Play, open the EML mod's menu and press Remove " +
        "(or Clean up in Settings for its version). It should be refused: \"Close Enshrouded first: the game may be reading " +
        "this mod\". Was it refused with that message, and is the mod still in the list?");

    [ClientCheck("frameworks.eml", "An EML mod runs", Area = "Other frameworks", Since = Dev18, Order = 303)]
    public Task FrameworksEml(CheckContext c) => Outside(c,
        "Look at what your EML mod changes in the game (an item, a value, a recipe: what its page says it does). Open the " +
        "game's MODS page if you need its state. Does the EML mod's change show in the game, and does the MODS page list it " +
        "as running?");

    [ClientCheck("frameworks.shroudforge", "A ShroudForge mod runs, with its page in Kiln's menu", Area = "Other frameworks",
        Since = Dev18, Order = 304)]
    public Task FrameworksShroudForge(CheckContext c) => Outside(c,
        "Open Kiln's Mods menu: your ShroudForge mod has a page of its own there, and its shortcuts work. Use what the mod " +
        "adds once. Does the ShroudForge mod work, with its page and its shortcuts?");

    [ClientCheck("frameworks.eml-next-to-sf", "EML mods run while a ShroudForge mod is on", Area = "Other frameworks",
        Since = Dev24, Order = 305)]
    public Task FrameworksEmlNextToSf(CheckContext c) => Outside(c,
        "With both the EML mod and the ShroudForge mod on in this profile: does the EML mod's change still show (check " +
        "frameworks.eml), and do the launcher and the MODS page agree that it runs (or both say why ShroudForge does not " +
        "run it)?");

    [ClientCheck("frameworks.greeneye", "A Greeneye mod runs", Area = "Other frameworks", Since = Dev18, Order = 306)]
    public Task FrameworksGreeneye(CheckContext c) => Outside(c,
        "Use what your Greeneye mod adds (its page says what). It runs from its own folder in the game. Does it work in the " +
        "game, and does the MODS page list it as running?");

    [ClientCheck("frameworks.flame", "Flame (Creative Mode) runs from the game's mods folder", Area = "Other frameworks",
        Since = Dev24, Order = 307)]
    public Task FrameworksFlame(CheckContext c) => Outside(c,
        "Flame is in the profile and runs from the game's mods folder, where it reads its own files. Open Flame's own menu " +
        "and use one of its tools. (Without data for this game build Kiln's /creative stays available and kiln.log says " +
        "why: then answer fail with that line.) Did Flame open and its tool work?");

    [ClientCheck("frameworks.shroudtopia", "A Shroudtopia mod runs", Area = "Other frameworks", Since = Dev18, Order = 308)]
    public Task FrameworksShroudtopia(CheckContext c) => Outside(c,
        "Use what your Shroudtopia mod changes in the game (its page says what). Does it work, and does the MODS page list it " +
        "as running?");

    [ClientCheck("frameworks.enshwick", "An Enshwick pack runs", Area = "Other frameworks", Since = Dev18, Order = 309)]
    public Task FrameworksEnshwick(CheckContext c) => Outside(c,
        "Look at what your Enshwick pack changes (its pack.toml or page says what). Enshwick's own loader ships with Kiln. " +
        "Does the pack's change show in the game?");

    [ClientCheck("frameworks.shroudlink", "shroudlink's MODS tab in singleplayer", Area = "Other frameworks", Since = Dev22,
        Order = 310)]
    public Task FrameworksShroudlink(CheckContext c) => Outside(c,
        "shroudlink's singleplayer zip was dropped on the launcher before this start (it imports as a mod of its own). Open " +
        "shroudlink's MODS tab in the game. Does the tab open and list the mods?");

    [ClientCheck("emf.portraits", "EMF portraits do not hold up entering the world", Area = "Other frameworks", Since = Dev21,
        Tags = [Visual], Order = 311)]
    public Task EmfPortraits(CheckContext c) => Outside(c,
        "With an EMF mod that has portraits (under kiln-emf): entering this world did not wait on the pictures when they " +
        "were current (from the second start on), and the portraits show where the mod puts them. Did you enter the world " +
        "without an extra wait, and do the portraits show?");

    [ClientCheck("chat.emf-bridge", "Kiln chat commands and EMF chat with kiln-emf's chat bridge", Area = "Other frameworks",
        Since = Dev21, Order = 312)]
    public Task ChatEmfBridge(CheckContext c) => Outside(c,
        "With kiln-emf's client chat bridge on: type a Kiln chat command (/help) and a chat command of your EMF mod, then a " +
        "plain chat line. Each command answers, and the plain line shows in chat. Did both kinds of command work and the " +
        "line show?");

    [ClientCheck("emf.current-mods", "A current EMF mod runs with no EMF installed", Area = "Other frameworks", Since = Dev27,
        Order = 314)]
    public Task EmfCurrentMods(CheckContext c) => Outside(c,
        "Put EMF 0.6.8's lua mod and its kill_counter in emf/mods (switched on in kiln/config/emf/<id>.ini), start through " +
        "Kiln with no EMF installed, and kill a creature. kiln.log then has a line \"[emf:lua] lua 1.33.0 ... loaded\" and " +
        "no line with \"built for EMF API\". Did the kill counter's line show on screen, with kiln.log as described?");

    [ClientCheck("foreign.autodoors-find", "Auto Doors and Find next to Kiln, installed by hand", Area = "Other frameworks",
        Since = Dev27, Order = 315)]
    public Task ForeignAutoDoorsFind(CheckContext c) => Outside(c,
        "On Windows, with Auto Doors 1.1.0 (powrprof.dll and the AutoDoors folder) and Find 1.2.12 with its dbgcore.dll put " +
        "into the game folder by hand, as their pages say. kiln.log has \"sigdb: scanning the exe file's code\", \"tick: " +
        "SimStep hooked\" and no line \"feature tick unavailable\"; AutoDoors\\logs has \"hooks on.\". In a base: a door " +
        "opens by itself as you walk up, and Shift+F lights the chests that hold what you type. Did the doors, Find and " +
        "this check's own mod all work, with those lines in the logs?");

    [ClientCheck("dll.xhl-late", "XHL-Core loaded late, after Kiln and ShroudForge", Area = "Other frameworks", Since = Dev21,
        Order = 313)]
    public Task DllXhlLate(CheckContext c) => Outside(c,
        "Before this start, in the launcher's Native and DLLs page, XHL-Core's switch Load after Kiln and ShroudForge was on. " +
        "Now: XHL works in the game, kiln.log has a line that it loaded late, and the overlaps panel on that page shows last " +
        "session's hook overlaps. Did XHL work, with the late load in kiln.log?");

    // ---- session 4: the second start with the frameworks profile, as a profile of its own ----

    [ClientCheck("library.guard-closed", "Remove works once the game is closed", Area = "Launcher", Since = Dev21,
        Tags = [Launcher], Order = 401)]
    public Task LibraryGuardClosed(CheckContext c) => Outside(c,
        "After session 3 had ended (game closed), you pressed Remove on the EML mod in the launcher and added the same " +
        "archive again before this start. Did Remove work with the game closed, and did the mod come back with the archive?");

    [ClientCheck("eml.nothing-changed", "Later EML starts change nothing", Area = "Other frameworks", Since = Dev21,
        Tags = [Launcher], Order = 402)]
    public Task EmlNothingChanged(CheckContext c) => Outside(c,
        "This is a later start with the same EML mods. Open kiln/logs/kiln.log in the game folder and search for \"nothing " +
        "changed\" in the EML lines of this start. Is it there, and did that step take a second or two?");

    [ClientCheck("launcher.kept-mod-files", "Files a mod wrote are kept when it is switched off", Area = "Launcher",
        Since = Dev24, Tags = [Launcher], Order = 403)]
    public Task LauncherKeptModFiles(CheckContext c) => Outside(c,
        "Between sessions 3 and 4 you switched off one mod that writes its own files in the game folder (a ShroudForge or " +
        "EML mod with saved settings) and switched it on again. Are its files in kiln/kept-mod-files while it was off, and " +
        "are its settings back now?");

    [ClientCheck("launcher.profile-rules", "A loaded profile's own rules apply at Play", Area = "Launcher", Since = Dev21,
        Tags = [Launcher], Order = 404)]
    public Task LauncherProfileRules(CheckContext c) => Outside(c,
        "Before this start you saved the frameworks profile under a second name in Settings, Profiles, switched XHL-Core's " +
        "Load after Kiln and ShroudForge off in it, loaded it and pressed Play. kiln.log should now show XHL-Core loading " +
        "early, not late. Did this start follow the loaded profile's rule?");

    [ClientCheck("cook.on-play", "Play cooks a mod that has a recipe", Area = "Launcher", Since = Dev27,
        Tags = [Launcher], Order = 405)]
    public Task CookOnPlay(CheckContext c) => Outside(c,
        "Before this start you installed the WeaponMoves sample through the launcher (a mod with a cook recipe and no " +
        "cooked files) and pressed Play. The launcher said \"Cooking Weapon Moves\" and showed the cook tool's lines in " +
        "its task, then the game started. In the game, the sample's moves animate. Did the launcher cook the mod by " +
        "itself, and do the moves animate?");

    [ClientCheck("cook.changed-file", "A changed cooked file is cooked again", Area = "Launcher", Since = Dev27,
        Tags = [Launcher], Order = 406)]
    public Task CookChangedFile(CheckContext c) => Outside(c,
        "With the game closed, change one byte of a file under the WeaponMoves mod's assets/cooked in the game folder and " +
        "press Play. The launcher cooks the mod again before the game starts, and the moves still animate. Did it?");

    [ClientCheck("cook.cancel", "Cancel stops a cook, and the game still starts", Area = "Launcher", Since = Dev27,
        Tags = [Launcher], Order = 407)]
    public Task CookCancel(CheckContext c) => Outside(c,
        "Use Cook again in the WeaponMoves mod's menu, then press Play and cancel the cook task while it runs. The game " +
        "still starts, a notice says the mod was not cooked and that it keeps running on the files it has, and no " +
        "python process of the cook is left running. Did all three hold?");

    [ClientCheck("cook.foreign-file", "A cooked file that no cook of this game made", Area = "Launcher", Since = Dev27,
        Tags = [Launcher], Order = 408)]
    public Task CookForeignFile(CheckContext c) => Outside(c,
        "With the game closed, copy a cooked file from another install or another mod into the WeaponMoves mod's " +
        "assets/cooked in the game folder, and start the game from Steam directly (Play would cook the mod again). " +
        "kiln.log names the file as not made here and it is loaded with a warning. Then set \"cooked\": \"local\" in " +
        "kiln.json and start from Steam again: kiln.log says the file was not loaded, and what it holds is missing in " +
        "the game. Did both starts go so? Take the file and the setting out again afterwards.");

    // ---- session 5: ShroudForge's own loader file in the game folder; after an uninstall and install of Kiln ----

    [ClientCheck("launcher.reinstall", "After uninstalling and installing Kiln, the same profile is loaded", Area = "Launcher",
        Since = Dev22, Tags = [Launcher], Order = 501)]
    public Task LauncherReinstall(CheckContext c) => Outside(c,
        "After session 4 you uninstalled Kiln in the launcher and installed it again. The launcher then had the profile of " +
        "session 4 loaded, and this start loaded its mods. Was it the same profile, with its mods running now?");

    [ClientCheck("loader.sf-dbghelp", "Mods run with ShroudForge's dbghelp.dll and no DLL override", Area = "Other frameworks",
        Since = Dev22, Order = 502)]
    public Task LoaderSfDbghelp(CheckContext c) => Outside(c,
        "On Linux or a Steam Deck: before this start ShroudForge's own dbghelp.dll was put in the game folder, with no DLL " +
        "override in the launch options. The profile's mods still run, and kiln.log says the folder's loader is not the " +
        "loaded code. Do the mods run, with that line in kiln.log? Take dbghelp.dll out again afterwards.");
}
