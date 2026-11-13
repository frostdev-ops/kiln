// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// The groups one player on one Linux PC cannot do alone, at the end of the list: a second player (orders 901-909), a
// Windows PC (911-919), a Steam Deck (921-929), and another host: a rented server or a Fedora Atomic / Bazzite PC
// (931-939, tag other-host).
public sealed partial class ClientPassMod
{
    private const string OtherHost = "other-host";

    // ---- a second player ----

    [ClientCheck("chat.emf-bridge-two", "Chat between two players with kiln-emf's chat bridge", Area = "Other frameworks",
        Since = Dev21, Tags = [TwoPlayers], Order = 901)]
    public Task ChatEmfBridgeTwo(CheckContext c) => Outside(c,
        "Both players have kiln-emf with its client chat bridge on and the same EMF mod; one hosts, one joins. Each types a " +
        "plain line, a Kiln chat command (/help) and the EMF mod's chat command. Do both players see each other's lines, " +
        "and do both kinds of command answer on both sides?");

    [ClientCheck("glider.seen-by-other", "Another player's steered glider", Area = "Creative mode", Since = Dev24,
        Tags = [TwoPlayers, Visual], Order = 902)]
    public Task GliderSeenByOther(CheckContext c) => Outside(c,
        "Both players have Kiln; the host types /creative. The other player watches the host glide and steer with S and W. " +
        "Does the glider's nose rise and dip in the other player's game too, without jumping back and forth?");

    // ---- a Windows PC ----

    [ClientCheck("windows.eml-own-loader", "EML's own dbghelp.dll on Windows", Area = "Other frameworks", Since = Dev22,
        Tags = [Windows, Launcher], Order = 911)]
    public Task WindowsEmlOwnLoader(CheckContext c) => Outside(c,
        "On Windows with EML's dbghelp.dll in the game folder: add an EML mod through the launcher and press Play. The mod " +
        "runs once, kiln/state/eml.json lists it as own-loader, and mods/<id>/ holds Kiln's copy. Then remove dbghelp.dll " +
        "and press Play again: the copy is gone and the mod still runs once. Did both starts go so?");

    [ClientCheck("windows.non-ascii-user", "A Windows user name with non-ASCII letters", Area = "Other frameworks",
        Since = Dev22, Tags = [Windows], Order = 912)]
    public Task WindowsNonAsciiUser(CheckContext c) => Outside(c,
        "On Windows under a user name with non-ASCII letters (Cyrillic, Chinese or accented): the quests part starts (a " +
        "Kiln quest shows in the journal), a SET mod loads, and a build region you set survives a restart of the game. Did " +
        "all three work?");

    [ClientCheck("windows.dedicated-item", "A mod's new item on a native Windows dedicated server", Area = "Servers",
        Since = Dev22, Tags = [Windows], Needs = ClientCheckNeeds.Joined, Order = 913)]
    public async Task WindowsDedicatedItem(CheckContext c)
    {
        c.Instruct("Joined to a native Windows dedicated server that runs the newitems sample (issue 14): open the hand " +
                   "crafting menu and look for Frost Shards and the Frost Edge sword; craft one. Is the new item there and " +
                   "can you craft it?");
        await c.Verdict();
    }

    [ClientCheck("windows.nxm-vortex", "nxm:// links go back to Vortex", Area = "Launcher", Since = Dev21,
        Tags = [Windows, Launcher], Order = 914)]
    public Task WindowsNxmVortex(CheckContext c) => Outside(c,
        "On Windows, where dev.19 took over nxm:// links from Vortex, with this launcher (Nexus off) installed: press Mod " +
        "Manager Download on a Nexus mod page. Does Vortex open the link, not Kiln?");

    [ClientCheck("windows.cook", "The launcher's own Python cooks a mod on Windows", Area = "Launcher", Since = Dev27,
        Tags = [Windows, Launcher], Order = 916)]
    public Task WindowsCook(CheckContext c) => Outside(c,
        "On Windows, with no Python installed (or without touching the one you have): install the WeaponMoves sample " +
        "through the launcher and press Play. The launcher cooks it (\"Cooking Weapon Moves\") with the Python in its " +
        "own folder, the game starts and the moves animate. Then use Cook again, press Play and cancel the cook: no " +
        "python.exe is left in the Task Manager. Did the cook work, and did Cancel leave nothing running?");

    [ClientCheck("editor.keeps-changes", "The save editor keeps your changes and asks before it drops them", Area = "Launcher",
        Since = Dev27, Tags = [Launcher], Order = 409)]
    public Task EditorKeepsChanges(CheckContext c) => Outside(c,
        "In the launcher's window on the desktop, with the game closed: open Editor (the newest character opens, with " +
        "icons). Change an amount, go to Settings and back: it still says Not saved. Press Z (or Discard): it asks " +
        "\"Discard the changes?\" first. Change an amount again and pick another character from the name: it asks " +
        "first. Did the change stay, and did it ask both times?");

    [ClientCheck("editor.save-one-backup", "Saving in the save editor makes one backup", Area = "Launcher", Since = Dev27,
        Tags = [Launcher], Order = 410)]
    public Task EditorSaveOneBackup(CheckContext c) => Outside(c,
        "In the save editor with the game closed: change an amount and press Save. It says Saved. Open Backups: exactly " +
        "one new \"Kiln backup\" is at the top, and the game's own backups are named as the game's. In the game, is the " +
        "amount what you set? Was there one new backup, not two?");

    [ClientCheck("editor.save-the-game-wrote", "The save editor does not overwrite a save the game wrote meanwhile",
        Area = "Launcher", Since = Dev27, Tags = [Launcher], Order = 411)]
    public Task EditorSaveTheGameWrote(CheckContext c) => Outside(c,
        "Open the save editor and change nothing; play, let the game save, quit: within a few seconds the editor shows the " +
        "character as the game left it. Then change an amount in the editor, play and let the game save again, quit, and " +
        "press Save in the editor: it refuses and offers \"Read again\". Did it read by itself the first time and refuse " +
        "the second time?");

    [ClientCheck("editor.add-item-typing", "Typing in the save editor's Add item search", Area = "Launcher", Since = Dev27,
        Tags = [Launcher], Order = 412)]
    public Task EditorAddItemTyping(CheckContext c) => Outside(c,
        "In the save editor, Add item: type a search quickly. Do the letters appear as you type, with the list following " +
        "(the list itself may take as long as before)?");

    // ---- a Steam Deck ----

    [ClientCheck("deck.no-kiln", "\"No Kiln\" in the launcher on a Steam Deck", Area = "Steam Deck", Since = Dev16,
        Tags = [SteamDeck, Launcher], Order = 921)]
    public Task DeckNoKiln(CheckContext c) => Outside(c,
        "On a Steam Deck: take Kiln's launch option out of the game's properties in Steam, start the game from Steam with " +
        "the launcher open. The launcher shows No Kiln with what to do, not Starting forever. Put the option back, start " +
        "again from the launcher, and answer in the game. Did No Kiln show, and does Kiln run now?");

    [ClientCheck("deck.mode", "The launcher's Steam Deck mode", Area = "Steam Deck", Since = Dev16,
        Tags = [SteamDeck, Launcher], Order = 922)]
    public Task DeckMode(CheckContext c) => Outside(c,
        "On a Steam Deck in game mode, with the controller only: open a dialog in the launcher and close it with B, move " +
        "focus over every button of the Play page and Settings (focus never gets lost, also on disabled buttons), scroll a " +
        "long text, and hold to uninstall a mod but let go early (it stops). Did each of these work?");

    [ClientCheck("deck.cook-again", "Cook again on the Steam Deck", Area = "Steam Deck", Since = Dev27,
        Tags = [SteamDeck, Launcher], Order = 929)]
    public Task DeckCookAgain(CheckContext c) => Outside(c,
        "On a Steam Deck, in the launcher's Mods screen: move to the WeaponMoves mod and press Y (Cook again). The row " +
        "says Cooking while it runs, then a message says \"Weapon Moves cooked\". Did it?");

    [ClientCheck("deck.editor-items", "The save editor on the Steam Deck: items with the controller alone", Area = "Steam Deck",
        Since = Dev27, Tags = [SteamDeck, Launcher], Order = 923)]
    public Task DeckEditorItems(CheckContext c) => Outside(c,
        "On a Steam Deck, the game closed: R1 to Editor. Your character is there and the panel on the right follows the " +
        "D-pad over the backpack. A on a stack, A on its amount, 2, 0, 0, then Y: it says 200. A on an empty slot, Steam + " +
        "X: the keyboard types into the search; A on an item, A again: it is added. X saves. In the game, is the stack " +
        "200 and the added item there? Was anything too small to read or hard to reach?");

    [ClientCheck("deck.editor-skills-knowledge", "The save editor on the Steam Deck: skills and knowledge", Area = "Steam Deck",
        Since = Dev27, Tags = [SteamDeck, Launcher], Order = 924)]
    public Task DeckEditorSkillsKnowledge(CheckContext c) => Outside(c,
        "On a Steam Deck in the Editor tab: L2/R2 to Skills. The camera follows the D-pad, A raises a node and Y lowers " +
        "it. Then Knowledge: A on \"Above level\", 3, 0, Y: the list is shorter. Did both work with the controller alone?");

    [ClientCheck("deck.editor-backups-quit", "The save editor on the Steam Deck: backups, and Quit names unsaved changes",
        Area = "Steam Deck", Since = Dev27, Tags = [SteamDeck, Launcher], Order = 925)]
    public Task DeckEditorBackupsQuit(CheckContext c) => Outside(c,
        "On a Steam Deck in the Editor tab: Backups, A on one, then Restore: the character is read again. Then change an " +
        "amount and press Start, Quit: the dialog names the unsaved changes. Did the restore read the character again, " +
        "and did Quit name the changes?");

    [ClientCheck("deck.tabs-fast", "L1 and R1 pressed fast on the Steam Deck", Area = "Steam Deck", Since = Dev27,
        Tags = [SteamDeck, Launcher], Order = 926)]
    public Task DeckTabsFast(CheckContext c) => Outside(c,
        "On a Steam Deck, from Home: press R1 several times fast, counting. Do you land on the tab you counted to (before " +
        "0.2.0-dev.27 fast presses were skipped)?");

    // ---- another host ----

    [ClientCheck("server.remote-mods", "A remote server with server mods", Area = "Servers", Since = Dev10,
        Tags = [OtherHost], Needs = ClientCheckNeeds.Joined, Order = 931)]
    public async Task ServerRemoteMods(CheckContext c)
    {
        c.Expect(c.Session == ClientCheckSession.Joined, "this check runs joined to a server", c.Session.ToString());
        c.Instruct("Joined to a Kiln server that runs on another machine (a rented host or another PC), with server mods " +
                   "and client-pass on it. The join went through and the server's mods act in the game (a command of theirs " +
                   "answers, their content shows). Do the server's mods work for you there?");
        await c.Verdict();
    }

    [ClientCheck("server.pack-rented", "The server pack on a rented host", Area = "Servers", Since = Dev18,
        Tags = [OtherHost, Launcher], Order = 932)]
    public Task ServerPackRented(CheckContext c) => Outside(c,
        "Upload kiln-server-<version>.zip to a rented Enshrouded host as its page says, with one server mod, start the " +
        "server and join it. Did the server start with Kiln (its log names Kiln) and could you join and see the mod act?");

    [ClientCheck("emf.mods-check", "An EMF server's mod check on join", Area = "Other frameworks", Since = Dev27,
        Tags = [OtherHost], Order = 934)]
    public Task EmfModsCheck(CheckContext c) => Outside(c,
        "Join a server that runs EMF 0.6.8 with [net] mods_check=refuse and at least one mod with a channel, with the same " +
        "EMF mods in your own game. After 90 seconds you are still connected, and kiln.log has \"EMF mods check: against " +
        "the server's mods: same mods\". Then, with the server on mods_check=warn and one of its \"side\": \"both\" mods " +
        "missing in your game: one note at the top of the screen names the mod and ends \"Install the same versions in " +
        "the Kiln launcher.\", in at most three rows. Did you stay connected, and did the note show as described?");

    [ClientCheck("launcher.own-eml-folder-under-sf", "The launcher names an EML mod in your own folder that ShroudForge has off",
        Area = "Launcher", Since = Dev27, Tags = [Visual], Order = 935)]
    public Task LauncherOwnEmlFolderUnderSf(CheckContext c) => Outside(c,
        "In a profile with a ShroudForge mod on and Auto Loot Plus on: put Auto Loot Plus by hand into the game's mods " +
        "folder as well (its mods/auto-loot-plus folder from the mod's zip, without extended.mod.json), press Play and " +
        "close the game at the main menu. On the Play page, the EML line under the engines says Auto Loot Plus runs from " +
        "your own folder and that ShroudForge has it switched off, with where to switch it on (Kiln's in-game menu, Mods, " +
        "ShroudForge, or ShroudForge's own Modloader); it does not say to activate the profile again to copy it. Switch it " +
        "on in the in-game menu, start the game again and look once more: is the switched-off part gone? Then remove the " +
        "folder you added. Did the line say this both times?");

    [ClientCheck("launcher.linked-home", "An EML mod on a host where /home links to /var/home", Area = "Launcher",
        Since = Dev21, Tags = [OtherHost], Order = 933)]
    public Task LauncherLinkedHome(CheckContext c) => Outside(c,
        "On a Fedora Atomic or Bazzite PC, where /home links to /var/home: add an EML mod through the launcher and start " +
        "the game. Does the EML mod load (MODS page: running)?");
}
