// Button glyphs for the hint bar, the tab bar and Help: Steam Deck style (dark faces, L1/R1, ☰ and ⧉) or Xbox style
// (coloured letters, LB/RB). The style follows the controller (a Valve pad or a Deck: Deck glyphs), unless Settings
// picks one.
import { create } from "zustand";
import type { PadButton } from "./input";
import { usePads } from "./input";
import { useAppInfo } from "../api/hooks";

export type GlyphStyle = "deck" | "xbox";
export type GlyphPref = "auto" | GlyphStyle;

const KEY = "kiln.deck.glyphs";
export const useGlyphPref = create<{ pref: GlyphPref; set: (p: GlyphPref) => void }>((set) => ({
  pref: ((typeof localStorage !== "undefined" && localStorage.getItem(KEY)) as GlyphPref) || "auto",
  set: (p) => { localStorage.setItem(KEY, p); set({ pref: p }); },
}));

const VALVE = 0x28de;

/** The glyph style in use: the setting, else Deck glyphs on a Deck or with a Valve controller, else Xbox. */
export function useGlyphStyle(): GlyphStyle {
  const pref = useGlyphPref((s) => s.pref);
  const info = useAppInfo();
  const native = usePads((s) => s.native);
  const browser = usePads((s) => s.browser);
  if (pref !== "auto") return pref;
  const pads = Object.values(native);
  if (pads.some((p) => p.vendor === VALVE || /steam/i.test(p.name))) return "deck";
  if (pads.length || browser.length) {
    if (browser.some((b) => /28de|steam/i.test(b))) return "deck";
    // a Steam Deck's own controls always come through Steam's virtual (Xbox-like) pad: the hardware decides
    return info.data?.deck?.device ? "deck" : "xbox";
  }
  return info.data?.deck?.detected ? "deck" : "xbox";
}

const FACE: Record<string, { fill: string }> = {
  a: { fill: "#6fcf5b" }, b: { fill: "#ff6b5c" }, x: { fill: "#55a6ff" }, y: { fill: "#f7c844" },
};

const LABEL: Record<GlyphStyle, Partial<Record<PadButton, string>>> = {
  deck: { lb: "L1", rb: "R1", lt: "L2", rt: "R2", ls: "L3", rs: "R3" },
  xbox: { lb: "LB", rb: "RB", lt: "LT", rt: "RT", ls: "LS", rs: "RS" },
};

/** One button's glyph (decorative: the hint's word says what it does). */
export function Glyph({ button, style, size = 1.75 }: { button: PadButton | "dpad" | "stick-l" | "stick-r"; style: GlyphStyle; size?: number }) {
  const h = `${size}rem`;
  const common = { className: `deck-glyph deck-glyph-${button}`, "aria-hidden": true as const, style: { height: h } };
  if (button === "a" || button === "b" || button === "x" || button === "y") {
    const letter = button.toUpperCase();
    const color = style === "xbox" ? FACE[button].fill : "#f5e9db";
    return (
      <svg viewBox="0 0 32 32" {...common} style={{ height: h, width: h }}>
        <circle cx="16" cy="16" r="14.5" fill={style === "xbox" ? "#1c1714" : "#2a2522"} stroke={style === "xbox" ? color : "#8d7f74"} strokeWidth="1.5" />
        <text x="16" y="21.6" textAnchor="middle" fontSize="16" fontWeight="700" fontFamily="Alegreya Sans, sans-serif" fill={color}>{letter}</text>
      </svg>
    );
  }
  if (button === "lb" || button === "rb" || button === "lt" || button === "rt" || button === "ls" || button === "rs") {
    const t = LABEL[style][button] ?? button.toUpperCase();
    return (
      <svg viewBox="0 0 44 28" {...common} style={{ height: h, width: `calc(${h} * 44 / 28)` }}>
        <rect x="1" y="2" width="42" height="24" rx={button.endsWith("b") ? 12 : 7} fill="#2a2522" stroke="#8d7f74" strokeWidth="1.5" />
        <text x="22" y="19.4" textAnchor="middle" fontSize="13" fontWeight="700" fontFamily="Alegreya Sans, sans-serif" fill="#f5e9db">{t}</text>
      </svg>
    );
  }
  if (button === "start" || button === "select") {
    return (
      <svg viewBox="0 0 32 32" {...common} style={{ height: h, width: h }}>
        <rect x="2" y="5" width="28" height="22" rx="11" fill="#2a2522" stroke="#8d7f74" strokeWidth="1.5" />
        {button === "start" ? (
          <g stroke="#f5e9db" strokeWidth="2" strokeLinecap="round"><path d="M10.5 12h11M10.5 16h11M10.5 20h11" /></g>
        ) : (
          <g fill="none" stroke="#f5e9db" strokeWidth="1.8"><rect x="9.5" y="10.5" width="9" height="7.5" rx="1" /><rect x="13.5" y="14" width="9" height="7.5" rx="1" fill="#2a2522" /></g>
        )}
      </svg>
    );
  }
  if (button === "stick-l" || button === "stick-r") {
    return (
      <svg viewBox="0 0 32 32" {...common} style={{ height: h, width: h }}>
        <circle cx="16" cy="16" r="14" fill="#2a2522" stroke="#8d7f74" strokeWidth="1.5" />
        <circle cx="16" cy="16" r="8" fill="none" stroke="#f5e9db" strokeWidth="1.6" />
        <text x="16" y="20" textAnchor="middle" fontSize="10" fontWeight="700" fontFamily="Alegreya Sans, sans-serif" fill="#f5e9db">{button === "stick-l" ? "L" : "R"}</text>
      </svg>
    );
  }
  // the D-pad (or one of its directions highlighted)
  const hot = button === "dpad" ? null : button;
  const arm = (d: string, path: string) => <path d={path} fill={hot === d ? "#f9c74f" : "#2a2522"} stroke="#8d7f74" strokeWidth="1.3" />;
  return (
    <svg viewBox="0 0 32 32" {...common} style={{ height: h, width: h }}>
      {arm("up", "M12 2h8v9l-4 4-4-4z")}
      {arm("down", "M12 30h8v-9l-4-4-4 4z")}
      {arm("left", "M2 12v8h9l4-4-4-4z")}
      {arm("right", "M30 12v8h-9l-4-4 4-4z")}
    </svg>
  );
}
