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
import { useT } from "../i18n";
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
  const t = useT();
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
      if (done.already) toast.info(t("ownFolders.already"), done.path);
      else toast.ok(t("ownFolders.now"), [
        done.renamedFrom ? t("ownFolders.nowPathRenamed", { path: done.path, old: leaf(done.renamedFrom) }) : t("ownFolders.nowPath", { path: done.path }),
        done.settingsKept?.length ? t("ownFolders.nowSettingsKept") : null,
        t("ownFolders.nowSwitches"),
      ].filter(Boolean).join(" "));
      if (activation) void notifyActivation(activation);
      else if (failed) toast.error(failed, t("ownFolders.activateFailed"));
    },
    onError: (e) => {
      setAsk(false);
      refresh();
      toast.error(e, t("ownFolders.adoptFailed"));
    },
  });
  const running = !!status.data?.running;
  return (
    <div className="k-own-folder" data-own-folder-row={`${folder.kind}/${folder.id}`}>
      {!compact && <span className="k-mono k-ellipsis" title={folder.path}>{folder.path}</span>}
      {!compact && <span className="k-hint">{t("ownFolders.rowHint", { id: folder.id, version: folder.version })}</span>}
      {folder.refusal ? (
        <span className="k-hint" data-own-folder-refusal="">{folder.refusal}</span>
      ) : (
        <span>
          <Button size="s" disabled={running} title={running ? t("ownFolders.closeGame") : t("ownFolders.canTakeOver")} onClick={() => setAsk(true)}>
            {t("ownFolders.manage")}
          </Button>
        </span>
      )}
      <Modal open={ask} onClose={() => setAsk(false)} locked={adopt.isPending} title={t("ownFolders.confirmTitle")} width={560}
        footer={<>
          <Button onClick={() => setAsk(false)} disabled={adopt.isPending}>{t("common.cancel")}</Button>
          <Button variant="primary" loading={adopt.isPending} onClick={() => adopt.mutate()}>{t("ownFolders.confirm")}</Button>
        </>}>
        <div className="k-stack" style={{ gap: 10 }} data-own-folder-confirm="">
          <span className="k-mono" style={{ overflowWrap: "anywhere" }}>{folder.path}</span>
          <p>{t("ownFolders.confirmText", { id: folder.id, version: folder.version })}</p>
          <ul className="k-hint" style={{ margin: 0, paddingLeft: 18 }}>
            <li>{t("ownFolders.switches")}</li>
            <li>{t("ownFolders.replaces")}</li>
            <li>{t("ownFolders.removes")}</li>
            {folder.renameTo && <li>{t("ownFolders.renames", { name: leaf(folder.renameTo) })}</li>}
          </ul>
          <p className="k-hint">{t("ownFolders.nothingNow")}</p>
        </div>
      </Modal>
    </div>
  );
}

/** The own folders of the profile the game uses, as a section of the Notes dialog. Nothing when there are none. */
export function OwnFoldersSection({ profileId, gameId }: { profileId: ProfileId | null | undefined; gameId: GameId }) {
  const t = useT();
  const q = useOwnFolders(profileId, gameId);
  if (!profileId || !q.data?.length) return null;
  return (
    <section className="k-folder-notes-section" aria-label={t("ownFolders.section")} data-own-folders="">
      <h3 className="k-label">{t("ownFolders.section")}</h3>
      <p className="k-hint">{t("ownFolders.sectionHint")}</p>
      <div className="k-stack" style={{ gap: 12 }}>
        {q.data.map((f) => <OwnFolderRow key={`${f.kind}/${f.id}`} folder={f} profileId={profileId} gameId={gameId} />)}
      </div>
    </section>
  );
}
