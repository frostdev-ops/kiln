// A mod whose files a player changed in its Library folder (kiln_manage::library::take_over): the mods list marks it
// "Changed on disk", and Play or an activation that refuses it names it (`detail.libraryChanged`). Either way one
// action, "Use the changed files", makes the changed files the mod's entry; a refused Play then starts again.
import type { QueryClient } from "@tanstack/react-query";
import * as api from "../api/commands";
import type { LibraryRef } from "../api/commands";
import { toKilnError } from "../api/commands";
import { qk } from "../api/hooks";
import { toast, useToasts } from "../ui";

export const USE_CHANGED = "Use the changed files";

/** The Library entry a refusal names as changed in its folder, with the mod's name; null for any other error. */
export function libraryChangedOf(e: unknown): { entry: LibraryRef; name: string } | null {
  const k = toKilnError(e);
  const d = k.detail as { libraryChanged?: LibraryRef; name?: string } | undefined;
  if (k.code !== "hash_mismatch" || !d?.libraryChanged?.kind || !d.libraryChanged.id || !d.libraryChanged.hash) return null;
  return { entry: d.libraryChanged, name: d.name ?? d.libraryChanged.id };
}

/** "Use the changed files": takes the entry over, then refreshes what shows the Library and the profiles. True when done
 *  (an error is toasted). */
export async function takeOver(qc: QueryClient, entry: LibraryRef, name: string, quiet = false): Promise<boolean> {
  try {
    const r = await api.libraryTakeOver(entry);
    await Promise.all([
      qc.invalidateQueries({ queryKey: ["library_list"] }),
      qc.invalidateQueries({ queryKey: qk.profiles }),
      qc.invalidateQueries({ queryKey: ["profile_get"] }),
      qc.invalidateQueries({ queryKey: ["profile_resolve"] }),
      qc.invalidateQueries({ queryKey: ["found_scan"] }),
    ]);
    if (!quiet) toast.ok(`${r.name} uses the changed files`, r.to.version !== r.from.version ? `Now version ${r.to.version}.` : undefined);
    // an editor saved once more while Kiln took the files over: those newer files were kept, not deleted
    if (r.note) toast.info(`${r.name} changed again`, r.note, { ms: 0 });
    return true;
  } catch (e) {
    toast.error(e, `Could not use the changed files of ${name}`);
    return false;
  }
}

/** A refusal that names a changed mod: said in plain words, with "Use the changed files" and, after it, `again` (Play
 *  once more). False for any other error (the caller shows it as before). */
export function notifyLibraryChanged(qc: QueryClient, e: unknown, again?: () => void): boolean {
  const c = libraryChangedOf(e);
  if (!c) return false;
  useToasts.getState().push({
    kind: "err",
    title: `${c.name} was changed in its folder`,
    detail: "Its files are not the ones Kiln imported, so Kiln does not load it as it is. Use the changed files, or put the old files back.",
    actions: [{ label: USE_CHANGED, onClick: () => { void takeOver(qc, c.entry, c.name, !!again).then((ok) => { if (ok) again?.(); }); } }],
  });
  return true;
}
