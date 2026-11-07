// Spatial navigation for the Deck UI: the D-pad and the left stick move focus to the nearest focusable element in
// that direction, inside the top layer (an open dialog, else the screen). Rules (docs/kiln/launcher/deck.md):
// - candidates: enabled buttons, links, [data-deck-focus] and tabindex >= 0 elements that are visible, not
//   [data-deck-skip] and not under an [inert] layer;
// - only elements whose centre lies beyond the current one in that direction count;
// - nearest = the gap along the direction, plus three times the gap across it (0 when they overlap), plus a small
//   centre offset to break ties: a list moves row by row, a grid stays in its column;
// - no wrap-around: at the edge nothing moves (predictable);
// - nothing focused yet: the layer's [data-deck-autofocus], else its first candidate.
import type { Dir } from "./input";

const SELECTOR = 'button, a[href], input, select, textarea, [data-deck-focus], [tabindex]';

export function candidates(scope: Element): HTMLElement[] {
  return [...scope.querySelectorAll<HTMLElement>(SELECTOR)].filter((el) => focusable(el));
}

export function focusable(el: HTMLElement): boolean {
  if ((el as HTMLButtonElement).disabled) return false;
  if (el.tabIndex < 0) return false;
  if (el.closest("[data-deck-skip], [inert], [aria-hidden='true']")) return false;
  const r = el.getBoundingClientRect();
  if (r.width < 1 || r.height < 1) return false;
  const st = getComputedStyle(el);
  return st.visibility !== "hidden" && st.display !== "none";
}

interface Box { left: number; right: number; top: number; bottom: number }

/** Score of `r` as the next element from `c` going `dir` (lower is nearer); null: not in that direction. */
export function score(c: Box, r: Box, dir: Dir): number | null {
  const cx = (c.left + c.right) / 2, cy = (c.top + c.bottom) / 2;
  const rx = (r.left + r.right) / 2, ry = (r.top + r.bottom) / 2;
  const gap = (a0: number, a1: number, b0: number, b1: number) => Math.max(0, b0 - a1, a0 - b1);
  let along: number, across: number, off: number;
  switch (dir) {
    case "down":
      if (ry <= cy + 1 || r.bottom <= c.bottom) return null;
      along = Math.max(0, r.top - c.bottom); across = gap(c.left, c.right, r.left, r.right); off = Math.abs(rx - cx); break;
    case "up":
      if (ry >= cy - 1 || r.top >= c.top) return null;
      along = Math.max(0, c.top - r.bottom); across = gap(c.left, c.right, r.left, r.right); off = Math.abs(rx - cx); break;
    case "right":
      if (rx <= cx + 1 || r.right <= c.right) return null;
      along = Math.max(0, r.left - c.right); across = gap(c.top, c.bottom, r.top, r.bottom); off = Math.abs(ry - cy); break;
    case "left":
      if (rx >= cx - 1 || r.left >= c.left) return null;
      along = Math.max(0, c.left - r.right); across = gap(c.top, c.bottom, r.top, r.bottom); off = Math.abs(ry - cy); break;
  }
  return along + across * 3 + off * 0.05;
}

export function nearest<T extends { rect: Box }>(from: Box, items: T[], dir: Dir): T | null {
  let best: T | null = null, bestScore = Infinity;
  for (const it of items) {
    const s = score(from, it.rect, dir);
    if (s !== null && s < bestScore) { best = it; bestScore = s; }
  }
  return best;
}

export function focusEl(el: HTMLElement) {
  el.focus({ preventScroll: true });
  el.scrollIntoView({ block: "nearest", inline: "nearest" });
}

/** The layer's first stop: its [data-deck-autofocus], the element it remembers, else its first candidate. */
export function focusFirst(scope: Element, remembered?: string | null): HTMLElement | null {
  const list = candidates(scope);
  const pick = (remembered ? list.find((el) => el.dataset.deckId === remembered) : undefined)
    ?? list.find((el) => el.hasAttribute("data-deck-autofocus")) ?? list[0] ?? null;
  if (pick) focusEl(pick);
  return pick;
}

/** Moves focus one step; false when nothing lies that way. */
export function move(scope: Element, dir: Dir): boolean {
  const cur = document.activeElement as HTMLElement | null;
  if (!cur || !scope.contains(cur) || cur === scope || !focusable(cur)) return !!focusFirst(scope);
  const from = cur.getBoundingClientRect();
  const items = candidates(scope).filter((el) => el !== cur && !cur.contains(el)).map((el) => ({ el, rect: el.getBoundingClientRect() }));
  const next = nearest(from, items, dir);
  if (!next) return false;
  focusEl(next.el);
  return true;
}
