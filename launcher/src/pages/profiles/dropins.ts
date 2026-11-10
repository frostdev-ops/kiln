// A profile's drop-in folders: mods placed in the game folder by hand. Mirrors kiln_manage::profile (dropin_kinds,
// DROPIN_KINDS, dropins_file). A profile without `dropinsEngines` (made before dev.22) with all four Kiln kinds on loads
// every kind's folder; saving from here always writes the full list with `dropinsEngines`, so nothing changes for it.
import type { ModKind, Profile } from "../../api/commands";
import { CORE_KINDS, MOD_KINDS } from "../../api/kinds";

/** The kinds with a drop-in switch (their plugin reads the profile's `dropins`), in ModKind::ALL order. */
export const DROPIN_KINDS: ModKind[] = ["kiln", "native", "plugin", "emf", "eml", "shroudforge", "greeneye", "flame", "shroudtopia", "shroudlink"];

/** What the switches show: the kinds whose folder this profile switches on. */
export function dropinKinds(p: Pick<Profile, "dropins" | "dropinsEngines">): ModKind[] {
  if (!p.dropinsEngines && CORE_KINDS.every((k) => p.dropins.includes(k))) return [...MOD_KINDS];
  return MOD_KINDS.filter((k) => p.dropins.includes(k));
}

/** Writes `kinds` into the profile (as the dialog and "Load the game-folder mods" save it). */
export function setDropins(p: Profile, kinds: ModKind[]): void {
  p.dropins = MOD_KINDS.filter((k) => kinds.includes(k));
  p.dropinsEngines = true;
}

/** Switches one kind's folder on or off, keeping what the others show. */
export function withDropin(p: Profile, kind: ModKind, on: boolean): void {
  const cur = dropinKinds(p);
  setDropins(p, on ? [...cur, kind] : cur.filter((k) => k !== kind));
}

/** profile.json `dropins` true: every switch on. */
export const allOn = (kinds: ModKind[]) => DROPIN_KINDS.every((k) => kinds.includes(k));
