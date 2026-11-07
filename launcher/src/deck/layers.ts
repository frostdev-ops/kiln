// Layers of the Deck UI: the current screen, then each dialog opened over it. The top layer owns the controller:
// the D-pad moves inside it, B calls its `back`, X / Y / Start / View run its actions, and the hint bar shows them.
// Layers below the top are made inert (no focus, no Tab, no taps).
import { useEffect, useLayoutEffect, useRef, type RefObject } from "react";
import { create } from "zustand";
import type { PadButton } from "./input";

export interface DeckAction {
  button: PadButton;
  label: string;
  run: () => void;
  /** Shown greyed and not run. */
  disabled?: boolean;
}

export interface Layer {
  id: number;
  el: RefObject<HTMLElement | null>;
  actions: DeckAction[];
  /** B; absent: the app's default (a tab other than Home goes Home). */
  back?: () => void;
  /** The hint bar's word for B ("Back", "Cancel", "Up"). */
  backLabel?: string;
  /** A dialog: LB/RB do not switch tabs under it, the screen below goes inert. */
  modal: boolean;
  /** The element focused last (data-deck-id), restored when the layer is on top again. */
  memo: string | null;
}

interface Layers {
  stack: Layer[];
  add: (l: Layer) => void;
  patch: (id: number, p: Partial<Layer>) => void;
  remove: (id: number) => void;
}

export const useLayers = create<Layers>((set, get) => ({
  stack: [],
  add: (l) => set({ stack: [...get().stack, l] }),
  patch: (id, p) => set({ stack: get().stack.map((l) => (l.id === id ? { ...l, ...p } : l)) }),
  remove: (id) => set({ stack: get().stack.filter((l) => l.id !== id) }),
}));

export const topLayer = (): Layer | null => useLayers.getState().stack.at(-1) ?? null;

let seq = 0;

export interface LayerOptions {
  actions?: DeckAction[];
  back?: () => void;
  backLabel?: string;
  modal?: boolean;
}

/** Registers `el` as a layer while mounted; actions and back follow the latest render. */
export function useLayer(el: RefObject<HTMLElement | null>, o: LayerOptions = {}) {
  const id = useRef(0);
  if (!id.current) id.current = ++seq;
  useLayoutEffect(() => {
    const me = id.current;
    useLayers.getState().add({ id: me, el, actions: o.actions ?? [], back: o.back, backLabel: o.backLabel, modal: !!o.modal, memo: null });
    return () => useLayers.getState().remove(me);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  // a new array each render: compare what the hint bar shows, plus the functions' identity via a version counter
  const sig = JSON.stringify([(o.actions ?? []).map((a) => [a.button, a.label, !!a.disabled]), o.backLabel, !!o.back]);
  const latest = useRef(o);
  latest.current = o;
  useEffect(() => {
    const cur = latest.current;
    useLayers.getState().patch(id.current, {
      // the run functions read the latest render's closures
      actions: (cur.actions ?? []).map((a, i) => ({ ...a, run: () => (latest.current.actions?.[i] ?? a).run() })),
      back: cur.back ? () => latest.current.back?.() : undefined,
      backLabel: cur.backLabel,
    });
  }, [sig]);
  return id.current;
}

/** Makes every layer but the top one inert (no focus, no taps), and the top one live again. */
export function useInertBelowTop() {
  const stack = useLayers((s) => s.stack);
  useEffect(() => {
    const topModal = [...stack].reverse().find((l) => l.modal);
    stack.forEach((l) => {
      const el = l.el.current;
      if (!el) return;
      const below = topModal ? stack.indexOf(l) < stack.indexOf(topModal) : false;
      if (below) el.setAttribute("inert", ""); else el.removeAttribute("inert");
    });
  }, [stack]);
}
