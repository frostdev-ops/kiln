// What an activation or a Kiln install leaves for the player to read, shown the same way at every call site that gets
// one: Play's profile switch, Profiles, a profile's page, the Deck's profile dialog, the add-a-mod flows (drop, picked
// profile, found mods, the catalog, the Deck's Add mods), a modpack install, Play itself (also when the start fails),
// a re-apply after a profile edit or at start-up (taken through activation_notices), Kiln's install, update, rollback
// and uninstall, and a server deploy. These notices name folders and recovery paths: they stay until closed.
//
// An activation's notices about mod folders (folders Kiln left as they are, mod files it kept, notes on the mods such as
// "the game uses your own folder") come with every activation, so each sentence is raised once per game: the backend
// remembers what the player closed (notices_unseen asks, notices_shown reports a notice the player closed; kept with
// the data folder). With "Show notes about mod folders" off in Settings none is raised, except the first notice of a folder
// that may still run although its mod is off (a kept copy, or the player's own folder of a mod that is off). What the
// last activation said is always in the notes the main list opens (useFolderNotes, FolderNotes.tsx).
import type { Activation, AddReport, InstallResult, NoticeLine, RetainedPlacement, UninstallResult } from "../api/commands";
import * as api from "../api/commands";
import { toKilnError } from "../api/commands";
import { create } from "zustand";
import { notifyPlacementCleanup, openLines, toast } from "../ui";
import { t } from "../i18n";

/** What the last activation of each game left in the game folder (its `retained`): the main list says "Left in the game
 *  folder" on a mod that is off but whose placed copy stayed. Every activation shown here replaces it. */
export const useRetained = create<{ byGame: Record<string, RetainedPlacement[]> }>(() => ({ byGame: {} }));

/** What one activation said about mod folders: the notes the main list's "Notes" opens. */
export interface FolderNotes {
  retained: RetainedPlacement[];
  keptFiles: string[];
  warnings: string[];
  /** The sentences of `warnings` that are safety notes (Activation.safetyNotes). */
  safetyNotes: string[];
  /** When that activation ran (Activation.activated); "" for none. */
  activated: string;
}
/** The notes of the last activation of each game this window saw (the backend's profile_active has them after a restart). */
export const useFolderNotes = create<{ byGame: Record<string, FolderNotes> }>(() => ({ byGame: {} }));
export const folderNotesOf = (a: Pick<Activation, "retained" | "keptFiles" | "warnings" | "safetyNotes" | "activated"> | null | undefined): FolderNotes =>
  ({ retained: a?.retained ?? [], keptFiles: a?.keptFiles ?? [], warnings: a?.warnings ?? [], safetyNotes: a?.safetyNotes ?? [], activated: a?.activated ?? "" });
export const folderNotesCount = (n: FolderNotes) => n.retained.length + n.keptFiles.length + n.warnings.length;
/** A kept folder in one line: its path and why it stays. */
export const retainedLine = (r: RetainedPlacement) => `${r.path}: ${r.reason}`;

/** Folders an activation left as they are. Each reason ends with what still runs from it (engine_mods::still_runs): an
 *  enabled mod's changed copy is simply what runs, a switched-off one may still be loaded by its own loader or by
 *  Kiln's plugin for that engine (SET's runs every mod in its folder). */
function notifyKept(retained: RetainedPlacement[], o: { onClosed?: () => void; lines?: string[] } = {}) {
  if (!retained.length) return;
  const detail = retained.map(retainedLine).join(" ");
  toast.info(t("shell.notices.keptTitle"), t("shell.notices.keptDetail", { folders: detail }), { ms: 0, ...o });
}

/** The lines notices_unseen is asked about, one per sentence, by what they are (the same text in two lists is two). */
function noticeLines(n: FolderNotes): NoticeLine[] {
  return [
    ...n.retained.map((r) => ({ text: `retained:${retainedLine(r)}`, safety: r.mayRun && !!r.off })),
    ...n.keptFiles.map((f) => ({ text: `kept:${f}` })),
    ...n.warnings.map((w) => ({ text: `note:${w}`, safety: n.safetyNotes.includes(w) })),
  ];
}

/** Raises the sentences of `a` the backend says are new for its game (all of them when it cannot say: an older backend),
 *  except those a notice still open says. A sentence counts as seen when the player closes its notice (Toast.tsx
 *  `close`: its close button, a button of it, the Deck's dialog), never when it is only on screen: one still open when
 *  the launcher closes comes again at a later activation. A failed report is not retried (it comes again later). */
async function raise(gameId: string, n: FolderNotes) {
  const lines = noticeLines(n);
  if (!lines.length) return;
  let show: Set<string>;
  try { show = new Set(await api.noticesUnseen(gameId, lines)); }
  catch { show = new Set(lines.map((l) => l.text)); }
  const open = openLines();
  const key = (text: string) => `${gameId}\n${text}`;
  const fresh = (text: string) => show.has(text) && !open.has(key(text));
  const toastOf = (texts: string[]) => ({
    lines: texts.map(key),
    onClosed: () => { api.noticesShown(gameId, texts).catch(() => {}); },
  });
  const retained = n.retained.filter((r) => fresh(`retained:${retainedLine(r)}`));
  notifyKept(retained, toastOf(retained.map((r) => `retained:${retainedLine(r)}`)));
  const kept = n.keptFiles.filter((f) => fresh(`kept:${f}`));
  if (kept.length) toast.info(t("shell.notices.settingsKept"), kept.join(" "), { ms: 0, ...toastOf(kept.map((f) => `kept:${f}`)) });
  const notes = n.warnings.filter((w) => fresh(`note:${w}`));
  if (notes.length) toast.info(t("shell.notices.modNotes"), notes.join(" "), { ms: 0, ...toastOf(notes.map((w) => `note:${w}`)) });
}

/** Copies left in the game folder, mod settings kept or put back, and profile notes: kept as the game's notes, and
 *  raised when new (each sentence once per game). Resolves once the notices are raised. */
export function notifyActivation(a: Activation | null | undefined): Promise<void> {
  if (!a) return Promise.resolve();
  const n = folderNotesOf(a);
  if (a.gameId) {
    useRetained.setState((s) => ({ byGame: { ...s.byGame, [a.gameId]: n.retained } }));
    useFolderNotes.setState((s) => ({ byGame: { ...s.byGame, [a.gameId]: n } }));
  }
  return raise(a.gameId, n);
}

/** An add's result (library_add, library_add_to_profile, found_add, a catalog install): what writing the profile again
 *  kept, and what the add did in the profile besides adding (`notices`: an entry replaced, a second copy of a proxy
 *  DLL switched off, an XHL-Core build kept). */
export function notifyAdd(r: AddReport | null | undefined) {
  if (!r) return;
  void notifyActivation(r.activation);
  if (r.notices?.length) toast.info(t("shell.notices.listChanges"), r.notices.join(" "), { ms: 0 });
}

/** A failed command whose backend wrote the profile again first (a start after Play's re-apply): what it kept. */
export function notifyActivationOf(e: unknown) {
  const d = toKilnError(e).detail as { activation?: Activation } | null | undefined;
  void notifyActivation(d?.activation);
}

/** A runtime install, update, rollback or server deploy ("Install notes"), or an uninstall ("Some files were kept"):
 *  the folders and files it kept, and the activation of the profile written again or made active after it. */
export function notifyInstall(r: InstallResult | UninstallResult | null | undefined) {
  if (!r) return;
  if ("placements" in r) {
    notifyPlacementCleanup(r.placements);
    if (r.kept.length) toast.info(t("shell.notices.runtimeKeptTitle"), t("shell.notices.runtimeKeptDetail", { files: r.kept.join(", ") }), { ms: 0 });
  } else if (r.changedBackedUp?.length) {
    const files = r.changedBackedUp.join(", ");
    toast.info(t("shell.notices.backedUpTitle"), r.backup
      ? t("shell.notices.backedUpDetailIn", { files, backup: r.backup })
      : t("shell.notices.backedUpDetail", { files }), { ms: 0 });
  }
  if (r.warnings?.length) toast.info("placements" in r ? t("shell.notices.filesKept") : t("shell.notices.installNotes"), r.warnings.join(" "), { ms: 0 });
  if ("activation" in r) void notifyActivation(r.activation);
}
