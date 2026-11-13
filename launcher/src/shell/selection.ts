// Several mods selected in the main list (desktop and Deck), for one Switch on, Switch off or Remove over all of them.
// The selection is the rows' keys (ModRow.key), kept per game outside the list, so it survives the list being hidden
// (a start's progress bar, another page) and comes back with it. It never holds a row the list does not show: a filter
// or Kind change lets go of the rows it hides, and the bar says how many (a selection the player cannot see is not
// acted on). Pure functions below; the store at the end.
import { useEffect } from "react";
import { create } from "zustand";
import { onProfileSwitch } from "../api/oneActivation";

export interface Selection {
  /** Selected row keys, in the order they were selected. */
  keys: string[];
  /** The last row clicked without Shift: where a Shift+click range starts. */
  anchor: string | null;
}

export const NONE: Selection = { keys: [], anchor: null };

/** One row in or out; it becomes the range's start. */
export function toggle(s: Selection, key: string): Selection {
  const has = s.keys.includes(key);
  return { keys: has ? s.keys.filter((k) => k !== key) : [...s.keys, key], anchor: key };
}

/** Shift+click: every row from the anchor to `key` in the order shown, selected (added to what is selected). Without
 *  an anchor that is still shown, a plain toggle. */
export function range(s: Selection, key: string, order: string[]): Selection {
  const a = s.anchor ? order.indexOf(s.anchor) : -1;
  const b = order.indexOf(key);
  if (a < 0 || b < 0) return toggle(s, key);
  const span = order.slice(Math.min(a, b), Math.max(a, b) + 1);
  return { keys: [...s.keys, ...span.filter((k) => !s.keys.includes(k))], anchor: s.anchor };
}

/** Ctrl+A: every row shown (the same set All on / All off acts on). */
export function all(order: string[]): Selection {
  return { keys: [...order], anchor: order[0] ?? null };
}

/** Only the rows still shown stay; `dropped`: how many left. */
export function keepShown(s: Selection, shown: string[]): { sel: Selection; dropped: number } {
  const keys = s.keys.filter((k) => shown.includes(k));
  if (keys.length === s.keys.length) return { sel: s, dropped: 0 };
  return { sel: { keys, anchor: s.anchor && shown.includes(s.anchor) ? s.anchor : null }, dropped: s.keys.length - keys.length };
}

/** The selected rows in the order shown. */
export function pick<T extends { key: string }>(s: Selection, rows: T[]): T[] {
  return rows.filter((r) => s.keys.includes(r.key));
}

/** Whose selection it is: one loaded profile of one game, by its id and `created` (a profile deleted and made again
 *  under the same id is another one). "none": no profile is loaded; null: not known yet (the profile is being read),
 *  when nothing is shown and nothing is dropped. */
export function ownerOf(profileId: string | null, profile: { id: string; created: string } | null): string | null {
  if (!profileId) return "none";
  return profile && profile.id === profileId ? `${profile.id}\n${profile.created}` : null;
}

/** Per game: the selection, whose it is, and the last "N left the selection" line the bar shows (cleared by the next
 *  change). A read for another owner gets nothing; useOwnedSelection drops it then, and a Load or "No profile" of the
 *  game drops it at once (onProfileSwitch), whether or not the list is open. */
interface Kept { owner: string; sel: Selection; note: string | null }
export const useSelection = create<{ byGame: Record<string, Kept> }>(() => ({ byGame: {} }));

export function selectionOf(game: string, owner: string | null): Selection {
  const k = useSelection.getState().byGame[game];
  return k && owner !== null && k.owner === owner ? k.sel : NONE;
}
export function setSelection(game: string, owner: string | null, sel: Selection, note: string | null = null) {
  if (owner === null) return;
  useSelection.setState((st) => ({ byGame: { ...st.byGame, [game]: { owner, sel, note } } }));
}
export function dropSelection(game: string) {
  if (!useSelection.getState().byGame[game]) return;
  useSelection.setState((st) => { const byGame = { ...st.byGame }; delete byGame[game]; return { byGame }; });
}

/** The selection of `owner` in `game` and its note; another owner's is dropped as soon as the owner is known. */
export function useOwnedSelection(game: string, owner: string | null): { sel: Selection; note: string | null } {
  const k = useSelection((st) => st.byGame[game]);
  const mine = !!k && owner !== null && k.owner === owner;
  useEffect(() => { if (k && owner !== null && k.owner !== owner) dropSelection(game); }, [k, owner, game]);
  return { sel: mine ? k.sel : NONE, note: mine ? k.note : null };
}

// a Load (profile_activate) or "No profile" (profile_deactivate) of a game ends its selection, list open or not
onProfileSwitch((game) => dropSelection(game));
