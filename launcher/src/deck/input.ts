// Deck input: every source becomes one stream of pad presses (docs/kiln/launcher/deck.md "Controls").
// - the native reader (`kiln://gamepad`, src-tauri/src/deck/gamepad.rs: evdev, with stick-to-direction and repeat);
// - the keyboard (what Steam Input's keyboard templates send): arrows, Enter/Space, Escape/Backspace, X, Y, Q/E,
//   PageUp/PageDown, M, F1; Tab keeps the browser's own focus order;
// - the browser Gamepad API (Windows' WebView2, a plain browser), only while the native reader sees no controller.
// Native events are ignored while the launcher's window has no focus (the game is in front: its input is its own).
import { useEffect, useRef } from "react";
import { create } from "zustand";
import { isTauri, onKilnEvent } from "../api/commands";
import type { GamepadEvent, PadButton } from "../api/commands";
import { candidates, focusEl } from "./focus";
import { topLayer } from "./layers";

export type { PadButton };
export type Dir = "up" | "down" | "left" | "right";
export const DIRS: readonly Dir[] = ["up", "down", "left", "right"];
export const isDir = (b: PadButton): b is Dir => (DIRS as readonly string[]).includes(b);

export interface Press {
  button: PadButton;
  pressed: boolean;
  repeat: boolean;
  source: "pad" | "browser" | "key";
}

/** Controllers the Deck UI knows of (native and browser), for the glyph style and the Settings line. */
interface PadState {
  native: Record<string, { name: string; vendor: number; product: number }>;
  browser: string[];
  /** A native event arrived: the browser Gamepad API stays off (the same controller would press twice). */
  nativeSeen: boolean;
  last: Press["source"] | null;
}
export const usePads = create<PadState>(() => ({ native: {}, browser: [], nativeSeen: false, last: null }));

// ---------------------------------------------------------------- keyboard

const KEYS: Record<string, PadButton> = {
  ArrowUp: "up", ArrowDown: "down", ArrowLeft: "left", ArrowRight: "right",
  Enter: "a", " ": "a", NumpadEnter: "a",
  Escape: "b", Backspace: "b", BrowserBack: "b",
  x: "x", X: "x", Delete: "x",
  y: "y", Y: "y",
  q: "lb", Q: "lb", PageUp: "lb", "[": "lb",
  e: "rb", E: "rb", PageDown: "rb", "]": "rb",
  m: "start", M: "start", ContextMenu: "start", F10: "start",
  F1: "select", v: "select", V: "select",
};

/** The pad button a key stands for (null: the key keeps its browser meaning, e.g. Tab). */
export function keyToButton(e: Pick<KeyboardEvent, "key" | "ctrlKey" | "altKey" | "metaKey">): PadButton | null {
  if (e.ctrlKey || e.altKey || e.metaKey) return null;
  return KEYS[e.key] ?? null;
}

const typing = (t: EventTarget | null) => t instanceof HTMLElement && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));

// ---------------------------------------------------------------- browser Gamepad API (standard mapping)

const STD: PadButton[] = ["a", "b", "x", "y", "lb", "rb", "lt", "rt", "select", "start", "ls", "rs", "up", "down", "left", "right", "guide"];
export const REPEAT_FIRST = 380;
export const REPEAT_EVERY = 120;
export const REPEAT_FAST = 70;
export const REPEAT_FAST_AFTER = 1500;

/** The browser pads' held buttons as presses, with the native reader's repeat timing for directions. */
export class BrowserPads {
  private held = new Map<PadButton, { since: number; next: number }>();
  private stick: Dir | null = null;

  poll(pads: readonly (Gamepad | null)[], now: number, out: (p: Press) => void) {
    const down = new Set<PadButton>();
    for (const g of pads) {
      if (!g || !g.connected) continue;
      g.buttons.forEach((b, i) => { if (STD[i] && (b.pressed || b.value > 0.6)) down.add(STD[i]); });
      const [x = 0, y = 0] = g.axes;
      const ax = Math.abs(x), ay = Math.abs(y);
      let want: Dir | null = null;
      if (Math.max(ax, ay) >= 0.6) want = ax >= ay ? (x < 0 ? "left" : "right") : y < 0 ? "up" : "down";
      else if (this.stick) {
        const along = this.stick === "left" ? -x : this.stick === "right" ? x : this.stick === "up" ? -y : y;
        if (along >= 0.4) want = this.stick;
      }
      this.stick = want;
      if (want) down.add(want);
    }
    for (const [b] of this.held) if (!down.has(b)) { this.held.delete(b); out({ button: b, pressed: false, repeat: false, source: "browser" }); }
    for (const b of down) {
      const h = this.held.get(b);
      if (!h) {
        this.held.set(b, { since: now, next: now + REPEAT_FIRST });
        out({ button: b, pressed: true, repeat: false, source: "browser" });
      } else if (isDir(b) && now >= h.next) {
        h.next = now + (now - h.since >= REPEAT_FAST_AFTER ? REPEAT_FAST : REPEAT_EVERY);
        out({ button: b, pressed: true, repeat: true, source: "browser" });
      }
    }
  }
}

// ---------------------------------------------------------------- the hook

export interface InputHandlers {
  press: (p: Press) => void;
  /** The right stick (native reader): -1..1, for scrolling. */
  scroll?: (axis: "rx" | "ry", value: number) => void;
}

/** Mounts every input source while the Deck UI is up. */
export function useDeckInput(h: InputHandlers) {
  const ref = useRef(h);
  ref.current = h;
  useEffect(() => {
    let focused = true;
    const onFocus = () => { focused = true; };
    const release = () => ref.current.press({ button: "a", pressed: false, repeat: false, source: "pad" });
    const onBlur = () => { focused = false; release(); };
    const visibility = () => { if (document.hidden) release(); };
    window.addEventListener("focus", onFocus);
    window.addEventListener("blur", onBlur);
    document.addEventListener("visibilitychange", visibility);

    // native
    let off: (() => void) | undefined;
    let live = true;
    onKilnEvent("kiln://gamepad", (e: GamepadEvent) => {
      if (e.kind === "connected") {
        usePads.setState((s) => ({ native: { ...s.native, [e.device]: { name: e.name, vendor: e.vendor, product: e.product } } }));
        return;
      }
      if (e.kind === "disconnected") {
        usePads.setState((s) => { const n = { ...s.native }; delete n[e.device]; return { native: n, nativeSeen: Object.keys(n).length > 0 }; });
        release();
        return;
      }
      if (!usePads.getState().nativeSeen) usePads.setState({ nativeSeen: true });
      if (!focused || document.hidden) return;
      if (e.kind === "axis") { ref.current.scroll?.(e.axis, e.value); return; }
      usePads.setState({ last: "pad" });
      ref.current.press({ button: e.button, pressed: e.pressed, repeat: e.repeat, source: "pad" });
    }).then((f) => (live ? (off = f) : f()));

    // keyboard
    const key = (down: boolean) => (e: KeyboardEvent) => {
      if (e.key === "Tab" && topLayer()?.modal) {
        if (down) {
          const scope = topLayer()?.el.current;
          const list = scope ? candidates(scope) : [];
          const at = list.indexOf(document.activeElement as HTMLElement);
          const next = list[(at + (e.shiftKey ? -1 : 1) + list.length) % list.length];
          if (next) focusEl(next);
        }
        e.preventDefault();
        return;
      }
      if (typing(e.target) && !(e.key === "Escape")) return;
      const b = keyToButton(e);
      if (!b) return;
      e.preventDefault();
      e.stopPropagation();
      if (down && b === "a" && e.repeat) return;
      if (usePads.getState().last !== "key") usePads.setState({ last: "key" });
      ref.current.press({ button: b, pressed: down, repeat: down && e.repeat, source: "key" });
    };
    const kd = key(true), ku = key(false);
    window.addEventListener("keydown", kd, true);
    window.addEventListener("keyup", ku, true);

    // browser Gamepad API
    const pads = new BrowserPads();
    let raf = 0;
    const tick = () => {
      raf = 0;
      const list = navigator.getGamepads?.() ?? [];
      const any = [...list].some((g) => g?.connected);
      const st = usePads.getState();
      const names = [...list].filter((g): g is Gamepad => !!g?.connected).map((g) => g.id);
      if (names.join("|") !== st.browser.join("|")) usePads.setState({ browser: names });
      // the native reader has the same controller: one source only
      const nativeHas = isTauri() && (st.nativeSeen || Object.keys(st.native).length > 0);
      if (any && !nativeHas && focused && !document.hidden) {
        pads.poll(list, performance.now(), (p) => { usePads.setState({ last: "browser" }); ref.current.press(p); });
        const pad = [...list].find((g) => g?.connected);
        const y = pad?.axes[3] ?? 0;
        if (Math.abs(y) >= 0.15) ref.current.scroll?.("ry", y);
      } else pads.poll([], performance.now(), (p) => ref.current.press(p));
      if (any) raf = requestAnimationFrame(tick);
    };
    const connected = () => { if (!raf) raf = requestAnimationFrame(tick); };
    window.addEventListener("gamepadconnected", connected);
    window.addEventListener("gamepaddisconnected", connected);
    connected();

    return () => {
      live = false;
      off?.();
      window.removeEventListener("focus", onFocus);
      window.removeEventListener("blur", onBlur);
      document.removeEventListener("visibilitychange", visibility);
      window.removeEventListener("keydown", kd, true);
      window.removeEventListener("keyup", ku, true);
      window.removeEventListener("gamepadconnected", connected);
      window.removeEventListener("gamepaddisconnected", connected);
      if (raf) cancelAnimationFrame(raf);
    };
  }, []);
}
