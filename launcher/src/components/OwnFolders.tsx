// "Let Kiln manage this folder" (engine_own_folders, engine_adopt_folder; docs/kiln/engines.md). A ShroudForge folder
// the player put into the game themselves serves a mod of the profile, and Kiln leaves it alone: it neither switches,
// updates nor removes it. Here the player can hand it over, when its files are the mod's as the Library has them.
// Kiln never does that by itself: the button asks first and says what changes. Shown where the own-folder note is: the
// Notes dialog above the main list (FolderNotes.tsx) and the mod's settings (EngineSettings.tsx).
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import * as api from "../api/commands";
import type { Activation, GameId, OwnFolder, ProfileId } from "../api/commands";
import { invalidateGame, useGameStatus } from "../api/hooks";
import { notifyActivation } from "../shell/notices";
import { Button, Modal, toast } from "../ui";

export const ownFoldersKey = (profileId: ProfileId, gameId: GameId) => ["engine_own_folders", profileId, gameId] as const;

/** The player's own folders that serve mods of the profile in this game (none unless the game uses the profile). */
export function useOwnFolders(profileId: ProfileId | null | undefined, gameId: GameId | null | undefined) {
  return useQuery({
    queryKey: ownFoldersKey(profileId ?? "", gameId ?? ""),
    queryFn: () => api.engineOwnFolders(profileId!, gameId!),
    enabled: !!profileId && !!gameId,
  });
}

const leaf = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() ?? path;

/** One own folder: where it is, and the button, or why Kiln cannot take it over (the first file that differs).
 *  `compact`: only the button or the reason (the mod's settings already say whose folder it is and where). */
export function OwnFolderRow({ folder, profileId, gameId, compact }: { folder: OwnFolder; profileId: ProfileId; gameId: GameId; compact?: boolean }) {
  const qc = useQueryClient();
  const status = useGameStatus(gameId);
  const [ask, setAsk] = useState(false);
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ["engine_own_folders"] });
    void qc.invalidateQueries({ queryKey: ["engine_settings"] });
    void qc.invalidateQueries({ queryKey: ["found_scan"] });
    invalidateGame(qc, gameId);
  };
  const adopt = useMutation({
    mutationFn: async () => {
      const done = await api.engineAdoptFolder(folder.kind, folder.id, profileId, gameId);
      // The folder is Kiln's now. Writing the mods to the game again switches it as the profile says and brings the
      // notes of that activation, without the one about this folder. A failure there does not undo the handover.
      let activation: Activation | null = null;
      let failed: unknown = null;
      try { activation = await api.profileActivate(profileId, gameId); } catch (e) { failed = e; }
      return { done, activation, failed };
    },
    onSuccess: ({ done, activation, failed }) => {
      setAsk(false);
      refresh();
      if (done.already) toast.info("Kiln manages this folder already", done.path);
      else toast.ok("Kiln manages this folder now", `${done.path}${done.renamedFrom ? ` (it was ${leaf(done.renamedFrom)})` : ""}. ${
        done.settingsKept?.length ? "Your setting values stay as they are. " : ""}Kiln switches and updates it with the mod from now on.`);
      if (activation) void notifyActivation(activation);
      else if (failed) toast.error(failed, "Kiln could not write your mods to the game again");
    },
    onError: (e) => {
      setAsk(false);
      refresh();
      toast.error(e, "Kiln did not take the folder over");
    },
  });
  const running = !!status.data?.running;
  return (
    <div className="k-own-folder" data-own-folder-row={`${folder.kind}/${folder.id}`}>
      {!compact && <span className="k-mono k-ellipsis" title={folder.path}>{folder.path}</span>}
      {!compact && <span className="k-hint">{folder.id} {folder.version}: your own folder. Kiln neither switches, updates nor removes it.</span>}
      {folder.refusal ? (
        <span className="k-hint" data-own-folder-refusal="">{folder.refusal}</span>
      ) : (
        <span>
          <Button size="s" disabled={running} title={running ? "Close the game first" : "Its files are the mod's as your Library has them: Kiln can take it over"} onClick={() => setAsk(true)}>
            Let Kiln manage this folder
          </Button>
        </span>
      )}
      <Modal open={ask} onClose={() => setAsk(false)} locked={adopt.isPending} title="Let Kiln manage this folder?" width={560}
        footer={<>
          <Button onClick={() => setAsk(false)} disabled={adopt.isPending}>Cancel</Button>
          <Button variant="primary" loading={adopt.isPending} onClick={() => adopt.mutate()}>Let Kiln manage it</Button>
        </>}>
        <div className="k-stack" style={{ gap: 10 }} data-own-folder-confirm="">
          <span className="k-mono" style={{ overflowWrap: "anywhere" }}>{folder.path}</span>
          <p>Its files are the files of {folder.id} {folder.version} in your Library. Kiln will update, switch and remove this folder with the mod from now on.</p>
          <ul className="k-hint" style={{ margin: 0, paddingLeft: 18 }}>
            <li>It switches the mod in this folder on and off as your profile says.</li>
            <li>It replaces the folder when the mod updates, and carries your setting values over.</li>
            <li>It removes the folder when it takes its mods out of the game, unless the folder then holds setting values of yours.</li>
            {folder.renameTo && <li>It renames the folder to {leaf(folder.renameTo)}, the name its copies of this mod have. ShroudForge finds the mod by the id inside it, not by the folder's name.</li>}
          </ul>
          <p className="k-hint">Nothing in the folder is changed now, except one small file Kiln adds to know the folder again (.kiln-library.json). Kiln then writes your mods to the game again.</p>
        </div>
      </Modal>
    </div>
  );
}

/** The own folders of the profile the game uses, as a section of the Notes dialog. Nothing when there are none. */
export function OwnFoldersSection({ profileId, gameId }: { profileId: ProfileId | null | undefined; gameId: GameId }) {
  const q = useOwnFolders(profileId, gameId);
  if (!profileId || !q.data?.length) return null;
  return (
    <section className="k-folder-notes-section" aria-label="Your own mod folders" data-own-folders="">
      <h3 className="k-label">Your own mod folders</h3>
      <p className="k-hint">You put these into the game yourself, so Kiln leaves them as they are. When a folder's files are exactly the mod's, Kiln can manage it like a copy it placed. It never does that by itself.</p>
      <div className="k-stack" style={{ gap: 12 }}>
        {q.data.map((f) => <OwnFolderRow key={`${f.kind}/${f.id}`} folder={f} profileId={profileId} gameId={gameId} />)}
      </div>
    </section>
  );
}
