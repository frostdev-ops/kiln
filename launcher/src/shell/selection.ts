// Several mods selected in the main list (desktop and Deck), for one Switch on, Switch off or Remove over all of them.
// The selection is the rows' keys (ModRow.key), kept per game outside the list, so it survives the list being hidden
// (a start's progress bar, another page) and comes back with it. It never holds a row the list does not show: a filter
// or Kind change lets go of the rows it hides, and the bar says how many (a selection the player cannot see is not
// acted on). Pure functions below; the store at the end.
import { create } from "zustand";

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

/** Per game: the selection, and the last "N left the selection" line the bar shows (cleared by the next change). */
interface Store { byGame: Record<string, Selection>; note: Record<string, string | null> }
export const useSelection = create<Store>(() => ({ byGame: {}, note: {} }));

export const selectionOf = (game: string): Selection => useSelection.getState().byGame[game] ?? NONE;
export function setSelection(game: string, sel: Selection, note: string | null = null) {
  useSelection.setState((st) => ({ byGame: { ...st.byGame, [game]: sel }, note: { ...st.note, [game]: note } }));
}
