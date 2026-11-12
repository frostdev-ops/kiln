// Removing a mod from the main list (desktop and Deck): out of the loaded profile, then its files out of the Library
// (every version, or the one the row's menu named). One plain confirmation; "Type a mod's name to remove it" (Settings)
// adds the typed word. library_remove goes past exactly the users the confirmation named (library_users), so another
// profile that lists the mod is named there and shows it as missing afterwards.
import { useQuery, type QueryClient } from "@tanstack/react-query";
import * as api from "../api/commands";
import type { LibraryRef, ProfileSummary } from "../api/commands";
import { useProfiles } from "../api/hooks";
import type { ModRow } from "./mods";

export interface RemoveTarget {
  row: ModRow;
  /** One Library version; null: the whole mod. */
  version: string | null;
}

/** The Library versions a removal takes out. */
export function refsOf(t: RemoveTarget | null): LibraryRef[] {
  const lib = t?.row.lib;
  if (!t || !lib) return [];
  return lib.versions.filter((v) => t.version === null || v.version === t.version).map((v) => ({ kind: lib.kind, id: lib.id, version: v.version, hash: v.hash }));
}

/** "work" or "work (active in Enshrouded)" from library_users, with the profile's name. */
export function userLabel(u: string, profiles: ProfileSummary[] | undefined): string {
  const name = (id: string) => profiles?.find((p) => p.id === id)?.name ?? id;
  const m = /^(.*) \((active|play-once) in (.*)\)$/.exec(u);
  return m ? `${name(m[1])} (${m[2] === "active" ? "loaded" : m[2]} in ${m[3]})` : name(u);
}

/** What uses the versions (library_users of each), and the sentence for the confirmation. The loaded profile is not
 *  named: the removal takes the mod out of it first. */
export function useRemoveCheck(t: RemoveTarget | null, loadedId: string | null) {
  const profiles = useProfiles();
  const refs = refsOf(t);
  const users = useQuery({
    queryKey: ["library_users", refs], enabled: refs.length > 0, staleTime: 0, gcTime: 0, retry: 1,
    queryFn: async () => Promise.all(refs.map(async (r) => ({ ref: r, users: await api.libraryUsers(r) }))),
  });
  const others = [...new Set((users.data ?? []).flatMap((u) => u.users))]
    .filter((u) => u !== loadedId && !(loadedId && u.startsWith(`${loadedId} (`)))
    .map((u) => userLabel(u, profiles.data));
  const message = !t ? "" : !t.row.lib ? "Its files are gone from the Library already; it leaves your list."
    : users.isError ? `Kiln could not check what else uses it (${api.toKilnError(users.error).message}). Remove stops if something does.`
    : !users.data ? "Checking what uses it."
    : others.length ? `Its files leave the Library. ${others.join(", ")} ${others.length === 1 ? "lists" : "list"} it too and will show it as missing.`
    : "Its files leave the Library.";
  return { users, message, ready: !t?.row.lib || !!users.data || users.isError };
}

/** Takes the mod out of the loaded profile (`dropFromProfile` resolves once that is saved), then removes the versions,
 *  each past the users the confirmation saw. Throws the first failure (the caller toasts it). */
export async function removeMod(qc: QueryClient, t: RemoveTarget, shown: { ref: LibraryRef; users: string[] }[] | undefined,
  dropFromProfile: () => Promise<boolean>): Promise<void> {
  const whole = t.version === null || (t.row.entry?.version === t.version);
  if (t.row.entry && whole) {
    if (!(await dropFromProfile())) throw { code: "conflict", message: "Your mod list could not be saved; nothing was removed." } satisfies api.KilnError;
  }
  try {
    for (const r of refsOf(t)) await api.libraryRemove(r, false, shown?.find((s) => s.ref.version === r.version)?.users ?? []);
  } finally {
    void qc.invalidateQueries({ queryKey: ["library_list"] });
    void qc.invalidateQueries({ queryKey: ["found_scan"] });
  }
}
