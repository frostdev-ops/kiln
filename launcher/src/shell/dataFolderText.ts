// The words of the data folder's row and dialogs (Settings, Deck Settings, the broken-pointer dialog, the notice after a
// move), apart from React so the unit tests read them (unit/data-folder.test.ts). The owner's rule: a title and
// buttons, and one line only where it changes the player's decision.
import type { DataFolder, DataFolderEntry, DataFolderMoved, DataFolderNotice, DataFolderPlan, DataFolderSize } from "../api/commands";
import { fmtBytes } from "../ui/format";
import { t } from "../i18n";

/** `p` relative to `base` when it is inside it ("tools/kiln"), else as it is. */
export function relativeTo(base: string, p: string): string {
  const b = base.replace(/[\\/]+$/, "");
  if (p.length > b.length + 1 && p.startsWith(b) && /[\\/]/.test(p[b.length])) return p.slice(b.length + 1);
  return p;
}

/** "Enshrouded", "Enshrouded and Server", "A, B and C" */
export function listWords(xs: string[]): string {
  return xs.length < 2 ? (xs[0] ?? "") : t("shell.dataFolderText.listAnd", { list: xs.slice(0, -1).join(", "), last: xs[xs.length - 1] });
}

// ---------------------------------------------------------------- the row

export interface RowView {
  /** The path to show (null: not known yet). */
  path: string | null;
  canChange: boolean;
  /** Why Change is off, shown beside it (null: nothing to say). */
  why: string | null;
}

/** The English of the row's "why" when KILN_DATA_DIR sets the folder (the unit tests compare with it; the row shows
 *  `t("shell.dataFolderText.envWhy")`). */
export const ENV_WHY = "Set by KILN_DATA_DIR.";

/** What the "Data folder" row shows: the data folder, or before data_folder_get answers (or on a backend without it)
 *  app_info's dataDir with Change off. */
export function rowView(f: DataFolder | null | undefined, error: unknown, fallbackDir: string | null | undefined): RowView {
  if (f?.source === "env") return { path: f.path, canChange: false, why: t("shell.dataFolderText.envWhy") };
  // an earlier move is not finished (the backend's sentence): the folder dialog would end in a refusal
  if (f?.pending) return { path: f.path, canChange: false, why: f.pending };
  if (f) return { path: f.path, canChange: true, why: null };
  if (error) return { path: fallbackDir ?? null, canChange: false, why: t("shell.dataFolderText.cannotMove") };
  return { path: fallbackDir ?? null, canChange: false, why: null };
}

/** The size beside the path ("3.4 GB"), and its title ("12,408 files"); null while it is measured or when it failed. */
export function sizeView(s: DataFolderSize | null | undefined): { text: string; title: string } | null {
  if (!s) return null;
  return { text: fmtBytes(s.bytes), title: t("shell.dataFolderText.files", { count: s.files }) };
}

// ---------------------------------------------------------------- the plan

export interface PlanView {
  title: string;
  /** Label and value; `path` values are shown in the mono style. */
  facts: { label: string; value: string; path?: boolean }[];
  /** The lines under the facts. */
  lines: string[];
  /** The primary button ("Move", "Use it", "Choose another"). */
  confirm: string;
  /** The other button ("Cancel", "Close"). */
  dismiss: string;
}

/** The dialog a data_folder_check answer becomes. */
export function planView(p: DataFolderPlan): PlanView {
  if (p.action === "refuse") {
    return {
      title: t("shell.dataFolderText.refuseTitle"),
      facts: [{ label: t("shell.dataFolderText.folder"), value: p.picked, path: true }],
      lines: [p.reason ?? t("shell.dataFolderText.refuseReason")],
      confirm: t("shell.dataFolderText.chooseAnother"),
      dismiss: t("common.close"),
    };
  }
  if (p.action === "use") {
    return {
      title: t("shell.dataFolderText.useTitle"),
      facts: [{ label: t("shell.dataFolderText.folder"), value: p.target, path: true }],
      lines: [t("shell.dataFolderText.useLine")],
      confirm: t("shell.dataFolderText.useIt"),
      dismiss: t("common.cancel"),
    };
  }
  const lines: string[] = [];
  if (p.target !== p.picked) lines.push(t("shell.dataFolderText.intoNewFolder"));
  if (p.copyMode.length) lines.push(t("shell.dataFolderText.copyMode", { games: listWords(p.copyMode) }));
  if (p.stays.length) lines.push(t("shell.dataFolderText.stays", { list: p.stays.map(sized).join(", ") }));
  lines.push(...(p.warnings ?? []));
  return {
    title: t("shell.dataFolderText.moveTitle"),
    facts: [
      { label: t("shell.dataFolderText.to"), value: p.target, path: true },
      { label: t("shell.dataFolderText.moves"), value: t("shell.dataFolderText.movesValue", { size: fmtBytes(p.bytes), count: p.files }) },
      ...(p.free !== null ? [{ label: t("shell.dataFolderText.freeThere"), value: fmtBytes(p.free) }] : []),
    ],
    lines,
    confirm: t("shell.dataFolderText.move"),
    dismiss: t("common.cancel"),
  };
}

// ---------------------------------------------------------------- after the move

/** "old-mods (1.2 GB)" */
export const sized = (e: Pick<DataFolderEntry, "name" | "bytes">) => `${e.name} (${fmtBytes(e.bytes)})`;

/** The move's last phase (the crate's task message "removing the old copy: <file>"): the data is in the new place,
 *  and Cancel now only stops the removal. Null in the phases before. */
export function movingLine(message: string | null | undefined): string | null {
  return message?.startsWith("removing the old copy") ? t("shell.dataFolderText.removingOldCopy") : null;
}

/** A move that ended as done: the lines before the restart (null: restart at once). */
export function doneLines(m: Pick<DataFolderMoved, "warnings" | "cleanup"> | null | undefined): string[] | null {
  if (!m) return null;
  const lines = [...(m.warnings ?? [])];
  // the crate did not run the removal, or refused it (a game's profile could not be written for the new place): its
  // reason says what the player does; else a stopped removal goes on at the next start
  if (m.cleanup?.reason) lines.push(m.cleanup.reason);
  else if (m.cleanup?.interrupted) lines.push(t("shell.dataFolderText.restNextStart"));
  return lines.length ? lines : null;
}

/** A move that did not finish: the old folder is in use as it was. */
export function stoppedView(how: "failed" | "canceled", error: string | null): { title: string; lines: string[] } {
  return {
    title: how === "canceled" ? t("shell.dataFolderText.moveCanceled") : t("shell.dataFolderText.moveFailed"),
    lines: [...(how === "failed" && error ? [error] : []), t("shell.dataFolderText.staysIn")],
  };
}

/** One list in the notice: a line, then its items (an item's `title`: its full path). */
export interface NoticeList {
  line: string;
  items: { text: string; title: string }[];
}

export interface NoticeView {
  title: string;
  facts: { label: string; value: string }[];
  lines: string[];
  lists: NoticeList[];
  /** Open (`openLabel`): something is still there. */
  open: boolean;
  openLabel: string;
}

/** The notice at the start after a move: where the data is now, what is still in the old folder (what Kiln left on
 *  purpose, with sizes, and what it could not remove, with why), whether the removal runs or goes on next time. An
 *  unfinished move undone at start, and a record that matches neither folder, have their own title. */
export function noticeView(n: DataFolderNotice): NoticeView {
  const stayed = (n.stayed ?? []).map((e) => ({ text: `${relativeTo(n.from, e.path)} (${fmtBytes(e.bytes)})`, title: e.path }));
  const kept = (n.kept ?? []).map((k) => ({ text: `${relativeTo(n.kind === "undone" ? n.to : n.from, k.path)} (${fmtBytes(k.bytes)}): ${k.reason}`, title: k.path }));
  // an older backend: only `left`
  const plain = !n.stayed && !n.kept ? n.left.map((p) => ({ text: relativeTo(n.from, p), title: p })) : [];
  const lists: NoticeList[] = [];
  const lines: string[] = [];
  if (n.kind === "undone") {
    lines.push(t("shell.dataFolderText.undoneLine", { folder: n.from }));
    if (n.unfinished) lines.push(t("shell.dataFolderText.undoneUnfinished"));
    if (kept.length) lists.push({ line: t("shell.dataFolderText.undoneKept"), items: kept });
    if (n.reason) lines.push(n.reason);
    // something is left there: where it is, and Open
    const left = kept.length > 0 || !!n.unfinished || (!n.folderRemoved && !!n.reason);
    return {
      title: t("shell.dataFolderText.undoneTitle"),
      facts: [{ label: t("shell.dataFolderText.dataFolder"), value: n.from }, ...(left ? [{ label: t("shell.dataFolderText.unfinishedCopy"), value: n.to }] : [])],
      lines, lists, open: left, openLabel: t("shell.dataFolderText.openUnfinished"),
    };
  }
  if (n.kind === "unclear") {
    if (n.reason) lines.push(n.reason);
    return { title: t("shell.dataFolderText.unclearTitle"), facts: n.from ? [{ label: t("shell.dataFolderText.oldFolder"), value: n.from }, { label: t("shell.dataFolderText.newFolder"), value: n.to }] : [], lines, lists, open: !!n.from, openLabel: t("shell.dataFolderText.openOld") };
  }
  if (n.removing) lines.push(t("shell.dataFolderText.removing"));
  else if (n.unfinished) lines.push(t("shell.dataFolderText.partStill"));
  if (stayed.length) lists.push({ line: t("shell.dataFolderText.unknownLeft", { count: stayed.length }), items: stayed });
  if (kept.length) lists.push({ line: t("shell.dataFolderText.couldNotRemove", { count: kept.length }), items: kept });
  if (plain.length) lists.push({ line: t("shell.dataFolderText.stayedOld", { count: plain.length }), items: plain });
  if (n.reason) lines.push(n.reason);
  const open = !n.folderRemoved && (lists.length > 0 || !!n.removing || !!n.unfinished || !!n.reason);
  return {
    title: t("shell.dataFolderText.movedTitle"),
    facts: [{ label: t("shell.dataFolderText.nowIn"), value: n.to }, ...(open ? [{ label: t("shell.dataFolderText.oldFolder"), value: n.from }] : [])],
    lines,
    lists,
    open,
    openLabel: t("shell.dataFolderText.openOld"),
  };
}

/** The dialog while an unfinished move's partial copy is removed (the folder is held). */
export const undoingTitle = () => t("shell.dataFolderText.undoingTitle");
export const undoingCancelLine = () => t("shell.dataFolderText.undoingCancelLine");

/** The dialog when another launcher holds the data folder (it is moving it). */
export const elsewhereTitle = () => t("shell.dataFolderText.elsewhereTitle");

/** The line where the loaded profile is shown (Play, Settings' Profiles, Deck Settings) when the game's profile is
 *  another data folder's. */
export const otherFolderProfile = () => t("shell.dataFolderText.otherFolderProfile");

/** Its title: the data folder that profile's paths name, when known. */
export const otherFolderTitle = (o: { id: string; root?: string | null }) =>
  o.root ? t("shell.dataFolderText.otherFolderTitleRoot", { id: o.id, root: o.root }) : t("shell.dataFolderText.otherFolderTitle", { id: o.id });

export const brokenTitle = () => t("shell.dataFolderText.brokenTitle");
