// Removing a mod from the main list (desktop and Deck): out of the loaded profile, then its files out of the Library
// (every version, or the one the row's menu named). One plain confirmation; "Type a mod's name to remove it" (Settings)
// adds the typed word. library_remove goes past exactly the users the confirmation named (library_users), so another
// profile that lists the mod is named there and shows it as missing afterwards.
import { useQueries, useQuery, type QueryClient } from "@tanstack/react-query";
import * as api from "../api/commands";
import type { LibraryRef, ModKind, Profile, ProfileSummary } from "../api/commands";
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

/** The other profiles (and games) that list `users`, named: the loaded profile is left out, the removal takes the mod
 *  out of it first. */
export function othersOf(users: string[], loadedId: string | null, profiles: ProfileSummary[] | undefined): string[] {
  return [...new Set(users)].filter((u) => u !== loadedId && !(loadedId && u.startsWith(`${loadedId} (`))).map((u) => userLabel(u, profiles));
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
  const others = othersOf((users.data ?? []).flatMap((u) => u.users), loadedId, profiles.data);
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

// ---- several at once (the list's selection) ----

/** A selected row's part in a removal: its Library versions go (each past the users the confirmation showed), or only
 *  its entry leaves the profile (its files are gone already). A mod found in the game folder is not Kiln's to remove. */
export function removable(r: ModRow): boolean {
  return !r.found && (!!r.lib || !!r.entry);
}
export const NOT_REMOVABLE = "in the game folder, Kiln did not place it: remove it there";

/** The loaded profile without the rows' entries: one edit, one save. */
export function withoutRows(p: Profile, rows: ModRow[]): void {
  for (const r of rows) {
    if (!r.kind || !r.entry) continue;
    const kind: ModKind = r.kind;
    p.mods[kind] = (p.mods[kind] ?? []).filter((m) => m.id !== r.id);
  }
}

export interface ManyCheck {
  /** Per row: what uses its versions (library_users), once read. */
  users: Map<string, { ref: LibraryRef; users: string[] }[]>;
  /** Per row: the other profiles that list it, named (the single Remove's wording). */
  others: Map<string, string[]>;
  ready: boolean;
  error: string | null;
}

/** useRemoveCheck for many rows, read in parallel. */
export function useRemoveMany(rows: ModRow[], loadedId: string | null, enabled: boolean): ManyCheck {
  const profiles = useProfiles();
  const targets = rows.map((row) => ({ row, refs: refsOf({ row, version: null }) }));
  const qs = useQueries({
    queries: targets.map((t) => ({
      queryKey: ["library_users", t.refs], enabled: enabled && t.refs.length > 0, staleTime: 0, gcTime: 0, retry: 1,
      queryFn: async () => Promise.all(t.refs.map(async (r) => ({ ref: r, users: await api.libraryUsers(r) }))),
    })),
  });
  const users = new Map<string, { ref: LibraryRef; users: string[] }[]>();
  const others = new Map<string, string[]>();
  let error: string | null = null;
  targets.forEach((t, i) => {
    const q = qs[i];
    if (q.data) { users.set(t.row.key, q.data); others.set(t.row.key, othersOf(q.data.flatMap((u) => u.users), loadedId, profiles.data)); }
    if (q.isError && !error) error = api.toKilnError(q.error).message;
  });
  const ready = targets.every((t, i) => !t.refs.length || !!qs[i].data || qs[i].isError) && !qs.some((q) => q.isFetching);
  return { users, others, ready, error };
}

export interface ManyResult { removed: string[]; left: { name: string; why: string }[] }

/** Takes every row out of the loaded profile in one save (`dropAll` resolves once it is saved; nothing is removed when
 *  it fails), then removes each row's versions past exactly the users the confirmation showed (library_remove per
 *  version: library_remove_many takes only a force flag). A row whose removal fails stays, with why; the rest go on. */
export async function removeMany(qc: QueryClient, rows: ModRow[], shown: ManyCheck["users"], dropAll: () => Promise<boolean>): Promise<ManyResult> {
  const out: ManyResult = { removed: [], left: [] };
  if (rows.some((r) => r.entry) && !(await dropAll())) {
    throw { code: "conflict", message: "Your mod list could not be saved; nothing was removed." } satisfies api.KilnError;
  }
  try {
    for (const r of rows) {
      try {
        for (const ref of refsOf({ row: r, version: null })) {
          await api.libraryRemove(ref, false, shown.get(r.key)?.find((s) => s.ref.version === ref.version)?.users ?? []);
        }
        out.removed.push(r.name);
      } catch (e) {
        out.left.push({ name: r.name, why: api.toKilnError(e).message });
      }
    }
  } finally {
    void qc.invalidateQueries({ queryKey: ["library_list"] });
    void qc.invalidateQueries({ queryKey: ["found_scan"] });
  }
  return out;
}
