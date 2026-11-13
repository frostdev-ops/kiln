// The notes about mod folders of a game: what its last activation said (folders Kiln left as they are, mod files it
// kept, notes on the mods such as "the game uses your own folder"). Kiln raises each of those sentences once
// (shell/notices.ts); here they can be read again: "Notes (N)" above the main list opens them, and the Deck's Settings
// has the same list (deck/screens/Settings.tsx). The dialog also lists the player's own mod folders that serve a mod,
// each with "Let Kiln manage this folder" (OwnFolders.tsx).
import { useState } from "react";
import type { GameId } from "../api/commands";
import { useActiveProfile } from "../api/hooks";
import { folderNotesCount, folderNotesOf, retainedLine, useFolderNotes, type FolderNotes } from "../shell/notices";
import { Button, Modal } from "../ui";
import { OwnFoldersSection, useOwnFolders } from "./OwnFolders";

/** The game's notes: the newer of what this window last saw (an activation it showed) and the backend's record of
 *  the active profile's last activation (profile_active: also after a restart). */
export function useGameFolderNotes(gameId: GameId | null | undefined): FolderNotes {
  const seen = useFolderNotes((s) => (gameId ? s.byGame[gameId] : undefined));
  const active = useActiveProfile(gameId).data;
  if (seen && (!active || seen.activated >= active.activated)) return seen;
  return folderNotesOf(active);
}

/** The notes as sections (desktop dialog and Deck dialog alike). */
export function FolderNotesList({ notes }: { notes: FolderNotes }) {
  if (!folderNotesCount(notes)) return <p className="k-hint">Nothing to note: the last time Kiln wrote your mods to the game, it had nothing to say about their folders.</p>;
  const section = (title: string, lines: string[]) => lines.length > 0 && (
    <section className="k-folder-notes-section" aria-label={title}>
      <h3 className="k-label">{title}</h3>
      <ul>{lines.map((l) => <li key={l}>{l}</li>)}</ul>
    </section>
  );
  return (
    <div className="k-folder-notes" data-folder-notes="">
      {section("Left in the game folder", notes.retained.map(retainedLine))}
      {section("Mod files kept", notes.keptFiles)}
      {section("Notes on your mods", notes.warnings)}
    </div>
  );
}

/** "Notes (N)" for the main list's bar: nothing when the last activation said nothing. */
export function FolderNotesButton({ gameId }: { gameId: GameId }) {
  const notes = useGameFolderNotes(gameId);
  const [open, setOpen] = useState(false);
  const n = folderNotesCount(notes);
  // the profile the game uses: its entries are what the player's own folders serve
  const profileId = useActiveProfile(gameId).data?.profileId;
  const own = useOwnFolders(n || open ? profileId : null, gameId);
  if (!n && !open) return null;
  return (
    <>
      <button type="button" className="k-link" onClick={() => setOpen(true)} title="What Kiln said about your mod folders the last time it wrote your mods to the game">
        Notes ({n})
      </button>
      <Modal open={open} onClose={() => setOpen(false)} title="Notes about your mod folders" width={640} footer={<Button onClick={() => setOpen(false)}>Done</Button>}>
        <p className="k-hint">From the last time Kiln wrote your mods to the game. Kiln shows each note once when it is new; Settings, Launcher can turn that off.</p>
        <FolderNotesList notes={notes} />
        {!!own.data?.length && <div style={{ marginTop: 14 }}><OwnFoldersSection profileId={profileId} gameId={gameId} /></div>}
      </Modal>
    </>
  );
}
