// The words of the data folder's row and dialogs (Settings, Deck Settings, the broken-pointer dialog, the notice after a
// move), apart from React so the unit tests read them (unit/data-folder.test.ts). The owner's rule: a title and
// buttons, and one line only where it changes the player's decision.
import type { DataFolder, DataFolderNotice, DataFolderPlan, DataFolderSize } from "../api/commands";
import { fmtBytes } from "../ui/format";

/** `p` relative to `base` when it is inside it ("tools/kiln"), else as it is. */
export function relativeTo(base: string, p: string): string {
  const b = base.replace(/[\\/]+$/, "");
  if (p.length > b.length + 1 && p.startsWith(b) && /[\\/]/.test(p[b.length])) return p.slice(b.length + 1);
  return p;
}

const count = (n: number) => n.toLocaleString("en-US");
/** "Enshrouded", "Enshrouded and Server", "A, B and C" */
export function listWords(xs: string[]): string {
  return xs.length < 2 ? (xs[0] ?? "") : `${xs.slice(0, -1).join(", ")} and ${xs[xs.length - 1]}`;
}

// ---------------------------------------------------------------- the row

export interface RowView {
  /** The path to show (null: not known yet). */
  path: string | null;
  canChange: boolean;
  /** Why Change is off, shown beside it (null: nothing to say). */
  why: string | null;
}

export const ENV_WHY = "Set by KILN_DATA_DIR.";

/** What the "Data folder" row shows: the data folder, or before data_folder_get answers (or on a backend without it)
 *  app_info's dataDir with Change off. */
export function rowView(f: DataFolder | null | undefined, error: unknown, fallbackDir: string | null | undefined): RowView {
  if (f) return { path: f.path, canChange: f.source !== "env", why: f.source === "env" ? ENV_WHY : null };
  if (error) return { path: fallbackDir ?? null, canChange: false, why: "This launcher cannot move its data folder." };
  return { path: fallbackDir ?? null, canChange: false, why: null };
}

/** The size beside the path ("3.4 GB"), and its title ("12,408 files"); null while it is measured or when it failed. */
export function sizeView(s: DataFolderSize | null | undefined): { text: string; title: string } | null {
  if (!s) return null;
  return { text: fmtBytes(s.bytes), title: `${count(s.files)} file${s.files === 1 ? "" : "s"}` };
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
      title: "Kiln cannot use this folder",
      facts: [{ label: "Folder", value: p.picked, path: true }],
      lines: [p.reason ?? "This folder cannot hold the data folder."],
      confirm: "Choose another",
      dismiss: "Close",
    };
  }
  if (p.action === "use") {
    return {
      title: "Use this data folder?",
      facts: [{ label: "Folder", value: p.target, path: true }],
      lines: ["It already holds Kiln data. The current folder stays where it is."],
      confirm: "Use it",
      dismiss: "Cancel",
    };
  }
  const lines: string[] = [];
  if (p.target !== p.picked) lines.push("That folder has other files in it, so the data goes into a new folder inside it.");
  if (p.copyMode.length) lines.push(`From there, mods are copied into the game folder of ${listWords(p.copyMode)}.`);
  if (p.stays.length) lines.push(`Also kept in the old folder: ${p.stays.join(", ")}.`);
  return {
    title: "Move the data folder?",
    facts: [
      { label: "To", value: p.target, path: true },
      { label: "Moves", value: `${fmtBytes(p.bytes)}, ${count(p.files)} file${p.files === 1 ? "" : "s"}` },
      ...(p.free !== null ? [{ label: "Free there", value: fmtBytes(p.free) }] : []),
    ],
    lines,
    confirm: "Move",
    dismiss: "Cancel",
  };
}

// ---------------------------------------------------------------- after the move

/** A move that did not finish: the old folder is in use as it was. */
export function stoppedView(how: "failed" | "canceled", error: string | null): { title: string; lines: string[] } {
  return {
    title: how === "canceled" ? "Move canceled" : "Move failed",
    lines: [...(how === "failed" && error ? [error] : []), "Nothing changed. The data stays in"],
  };
}

/** The notice at the first start after a move: where the data is now, and what stayed in the old folder (relative
 *  to it). */
export function noticeView(n: DataFolderNotice): { title: string; left: string[]; leftLine: string | null } {
  const left = n.left.map((p) => relativeTo(n.from, p));
  return {
    title: "Data folder moved",
    left,
    leftLine: left.length ? (left.length === 1 ? "This stayed in the old folder:" : "These stayed in the old folder:") : null,
  };
}

export const BROKEN_TITLE = "Kiln cannot open its data folder";
