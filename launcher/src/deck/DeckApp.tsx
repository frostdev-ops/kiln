// The Steam Deck UI (docs/kiln/launcher/deck.md): one fullscreen frame at 1280×800 (scaled to other sizes), the tab
// bar (L1/R1), the current screen, the hint bar (the top layer's buttons), dialogs and toasts. Every input source
// (native gamepad, keyboard, browser Gamepad API, touch) ends in the same dispatcher:
//   D-pad / left stick: move focus (focus.ts) · A: click the focused control (hold for a HoldButton) · B: the top
//   layer's back, else Home · X / Y: the top layer's actions · L1 / R1: tabs · Start: the menu · View: Help ·
//   right stick: scroll.
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { Navigate, Route, Routes, useLocation, useNavigate } from "react-router-dom";
import * as api from "../api/commands";
import { useAppInfo, useBackendEvents, useSettings } from "../api/hooks";
import { useTaskFeed, Lockup } from "../ui";
import { useAutoDetect } from "../shell/firstRun";
import { clock, usePlayControl } from "../shell/play";
import { QuitDialog } from "./dialogs";
import { DeckFire, deckFire } from "./Fire";
import { focusable, focusFirst, move } from "./focus";
import { Glyph, useGlyphStyle } from "./glyphs";
import { isDir, useDeckInput, usePads, type Press } from "./input";
import { topLayer, useInertBelowTop, useLayers, type DeckAction } from "./layers";
import { DeckToaster, Dialog, Row } from "./ui";
import Home from "./screens/Home";
import Mods from "./screens/Mods";
import AddMods from "./screens/AddMods";
import Kiln from "./screens/Kiln";
import Help from "./screens/Help";
import Settings from "./screens/Settings";
import "./deck.css";

export const TABS = [
  { to: "/deck", label: "Home" },
  { to: "/deck/mods", label: "Mods" },
  { to: "/deck/add", label: "Add mods" },
  { to: "/deck/kiln", label: "Kiln" },
  { to: "/deck/help", label: "Help" },
  { to: "/deck/settings", label: "Settings" },
] as const;

const tabOf = (path: string) => Math.max(0, TABS.findIndex((t) => t.to === path.replace(/\/+$/, "")));

/** 1 rem = 16 px at 1280×800; other window sizes scale the whole frame. */
function useScale() {
  useEffect(() => {
    const html = document.documentElement;
    const before = html.style.fontSize;
    const fit = () => {
      const s = Math.min(window.innerWidth / 1280, window.innerHeight / 800);
      html.style.fontSize = `${(16 * Math.min(2.5, Math.max(0.6, s))).toFixed(2)}px`;
    };
    fit();
    window.addEventListener("resize", fit);
    return () => { window.removeEventListener("resize", fit); html.style.fontSize = before; };
  }, []);
}

/** Fullscreen and the native gamepad reader while the Deck UI is up (the Tauri app only). */
function useNative() {
  useEffect(() => {
    if (!api.isTauri()) return;
    api.deckFullscreen(true).catch(() => {});
    api.deckGamepad(true).then((st) => {
      usePads.setState({ native: Object.fromEntries(st.devices.map((d) => [d.device, { name: d.name, vendor: d.vendor, product: d.product }])) });
    }).catch(() => {});
    return () => {
      api.deckGamepad(false).catch(() => {});
      api.deckFullscreen(false).catch(() => {});
    };
  }, []);
}

/** Focus is never left on nothing: when the focused control goes away (a row removed, a screen changed under a
 *  closing dialog), the top layer's first stop takes it, as soon as that layer is live. */
function useKeepFocus(root: React.RefObject<HTMLDivElement | null>) {
  useEffect(() => {
    const t = setInterval(() => {
      const a = document.activeElement;
      if (a && a !== document.body && a.isConnected) return;
      const scope = topLayer()?.el.current ?? root.current;
      if (!scope || scope.hasAttribute("inert") || scope.closest("[inert]")) return;
      focusFirst(scope);
    }, 200);
    return () => clearInterval(t);
  }, [root]);
}

/** The focused control's word for A ("Play", "Turn off"), or null when nothing is focused. */
function useALabel(): string | null {
  const [label, setLabel] = useState<string | null>(null);
  useEffect(() => {
    const on = () => {
      const el = document.activeElement as HTMLElement | null;
      if (!el || el === document.body || !el.closest(".deck")) { setLabel(null); return; }
      const w = el.dataset.deckA;
      setLabel(w === "-" ? null : w ?? "Select");
    };
    const out = () => setTimeout(on, 0);
    // the focused control's word can change under it (a mod turned off: "Turn on")
    const mo = new MutationObserver(on);
    mo.observe(document.body, { subtree: true, attributes: true, attributeFilter: ["data-deck-a", "disabled"] });
    document.addEventListener("focusin", on);
    document.addEventListener("focusout", out);
    return () => { mo.disconnect(); document.removeEventListener("focusin", on); document.removeEventListener("focusout", out); };
  }, []);
  return label;
}

export default function DeckApp() {
  useTaskFeed();
  useBackendEvents();
  useAutoDetect();
  useScale();
  useNative();
  useInertBelowTop();
  const nav = useNavigate();
  const loc = useLocation();
  const root = useRef<HTMLDivElement>(null);
  const holding = useRef<HTMLElement | null>(null);
  useKeepFocus(root);
  const [menu, setMenu] = useState(false);
  const info = useAppInfo();
  const settings = useSettings();
  // the Hearth fire, as on the desktop: unless Settings' Animated fire is off or KILN_LAUNCHER_NO_WEBGL=1
  const fire = info.isFetched && settings.isFetched && !info.data?.noWebgl && settings.data?.animatedFire !== false;
  const tab = tabOf(loc.pathname);
  const tabRef = useRef(tab);
  tabRef.current = tab;
  useEffect(() => { document.title = `${TABS[tab].label} · Kiln`; }, [tab]);

  const goTab = (i: number) => { if (i >= 0 && i < TABS.length && i !== tabRef.current) nav(TABS[i].to); };
  const back = () => {
    const top = topLayer();
    if (top?.back) top.back();
    else if (!top?.modal && tabRef.current !== 0) nav("/deck");
  };
  const runAction = (b: Press["button"]): boolean => {
    const act = topLayer()?.actions.find((a) => a.button === b);
    if (!act) return false;
    if (!act.disabled) act.run();
    return true;
  };

  const press = (p: Press) => {
    const top = topLayer();
    const scope = top?.el.current ?? root.current;
    if (!scope) return;
    if (!p.pressed) {
      if (p.button === "a" && holding.current) { holding.current.dispatchEvent(new CustomEvent("deck-hold", { detail: false })); holding.current = null; }
      return;
    }
    if (isDir(p.button)) { move(scope, p.button); return; }
    if (p.repeat) return;
    switch (p.button) {
      case "a": {
        const el = document.activeElement as HTMLElement | null;
        if (!el || !scope.contains(el) || el === scope || !focusable(el)) { focusFirst(scope); return; }
        if (el.hasAttribute("data-deck-hold")) { holding.current = el; el.dispatchEvent(new CustomEvent("deck-hold", { detail: true })); return; }
        el.click();
        return;
      }
      case "b": back(); return;
      case "lb": case "rb": if (!top?.modal) goTab(tabRef.current + (p.button === "rb" ? 1 : -1)); return;
      case "start": if (!runAction("start") && !top?.modal) setMenu(true); return;
      case "select": if (!runAction("select") && !top?.modal) nav("/deck/help"); return;
      default: runAction(p.button);
    }
  };

  useDeckInput({
    press,
    scroll: (axis, value) => {
      if (axis !== "ry" || !value) return;
      const scope = topLayer()?.el.current ?? root.current;
      const el = (document.activeElement?.closest(".deck-scroll") as HTMLElement | null) ?? scope?.querySelector<HTMLElement>(".deck-scroll");
      el?.scrollBy({ top: value * 36 });
    },
  });

  return (
    <div ref={root} className="deck" data-deck-root="">
      <DeckFire root={root} key={fire ? "fire" : "plain"} enabled={fire} />
      <div className="deck-glow" aria-hidden />
      <TopBar tab={tab} goTab={goTab} />
      <main className="deck-main">
        <Routes>
          <Route index element={<Home />} />
          <Route path="mods" element={<Mods />} />
          <Route path="add" element={<AddMods />} />
          <Route path="kiln" element={<Kiln />} />
          <Route path="help" element={<Help />} />
          <Route path="settings" element={<Settings />} />
          <Route path="*" element={<Navigate to="/deck" replace />} />
        </Routes>
      </main>
      <HintBar press={press} />
      {menu && <MenuDialog onClose={() => setMenu(false)} />}
      <div className="deck-overlays" />
      <DeckToaster />
    </div>
  );
}

function TopBar({ tab, goTab }: { tab: number; goTab: (i: number) => void }) {
  const style = useGlyphStyle();
  const ctl = usePlayControl();
  const v = ctl.view;
  const word = ctl.starting ? "Starting" : ctl.running ? `${v.label}${ctl.elapsed !== null ? ` ${clock(ctl.elapsed)}` : ""}` : v.label;
  const tone = ctl.starting || v.phase === "running" ? "ember" : v.phase === "updating" ? "warn" : v.phase === "vanilla" ? "info" : "idle";
  // the fire's heat away from Home (Home sets its own)
  deckFire.game = ctl.running && !ctl.starting;
  deckFire.heat = !ctl.game ? "unlit" : v.phase === "updating" ? "low" : ctl.starting ? "flare" : ctl.running ? (v.label === "Safe idle" ? "low" : "embers") : "idle";
  // the gold diamond slides under the current tab
  const nav = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    const place = () => {
      const b = nav.current?.querySelector<HTMLElement>(".deck-tab.on");
      nav.current?.style.setProperty("--dx", b ? `${(b.offsetLeft + b.offsetWidth / 2).toFixed(1)}px` : "-40px");
    };
    place();
    window.addEventListener("resize", place);
    document.fonts?.ready.then(place).catch(() => {});
    return () => window.removeEventListener("resize", place);
  }, [tab]);
  return (
    <header className="deck-top">
      <div className="deck-logo"><Lockup height={30} /></div>
      <nav ref={nav} className="deck-tabs" aria-label="Deck tabs" data-deck-skip="">
        <span className="deck-tab-dia" aria-hidden><i /></span>
        <button type="button" tabIndex={-1} className="deck-tab-glyph" aria-label="Previous tab" onClick={() => goTab(tab - 1)}><Glyph button="lb" style={style} size={1.6} /></button>
        {TABS.map((t, i) => (
          <button key={t.to} type="button" tabIndex={-1} className={"deck-tab" + (i === tab ? " on" : "")} aria-current={i === tab ? "page" : undefined} onClick={() => goTab(i)}>{t.label}</button>
        ))}
        <button type="button" tabIndex={-1} className="deck-tab-glyph" aria-label="Next tab" onClick={() => goTab(tab + 1)}><Glyph button="rb" style={style} size={1.6} /></button>
      </nav>
      <span className={`deck-state deck-word ${tone}`} role="status" data-testid="deck-state">{word}</span>
    </header>
  );
}

function HintBar({ press }: { press: (p: Press) => void }) {
  const style = useGlyphStyle();
  const stack = useLayers((s) => s.stack);
  const a = useALabel();
  const top = stack.at(-1) ?? null;
  const loc = useLocation();
  const atHome = tabOf(loc.pathname) === 0;
  const bLabel = top?.back ? top.backLabel ?? "Back" : top?.modal ? null : atHome ? null : "Home";
  const acts: DeckAction[] = top?.actions ?? [];
  const tap = (button: Press["button"]) => () => { press({ button, pressed: true, repeat: false, source: "key" }); press({ button, pressed: false, repeat: false, source: "key" }); };
  const hint = (button: Press["button"], label: string, disabled = false) => (
    <button key={button + label} type="button" tabIndex={-1} className={"deck-hint" + (disabled ? " off" : "")} data-hint={button}
      onPointerDown={(e) => e.preventDefault()} onClick={tap(button)}>
      <Glyph button={button} style={style} size={1.75} /><span>{label}</span>
    </button>
  );
  return (
    <footer className="deck-hints" data-deck-skip="" aria-label="Buttons">
      <div className="deck-hints-left">
        <span className="deck-hint static"><Glyph button="dpad" style={style} size={1.6} /><span>Move</span></span>
      </div>
      <div className="deck-hints-right">
        {a && hint("a", a)}
        {acts.map((x) => hint(x.button, x.label, x.disabled))}
        {bLabel && hint("b", bLabel)}
        {!top?.modal && hint("start", "Menu")}
      </div>
    </footer>
  );
}

function MenuDialog({ onClose }: { onClose: () => void }) {
  const nav = useNavigate();
  const loc = useLocation();
  const [quit, setQuit] = useState(false);
  const cur = tabOf(loc.pathname);
  if (quit) return <QuitDialog onClose={() => { setQuit(false); onClose(); }} />;
  return (
    <Dialog name="menu" title="Menu" onClose={onClose} backLabel="Close" actions={[{ button: "start", label: "Close", run: onClose }]}>
      <div className="deck-list">
        {TABS.map((t, i) => <Row key={t.to} id={`menu-${i}`} title={t.label} a="Open" end={i === cur ? <span className="deck-dim">here</span> : undefined} onClick={() => { onClose(); nav(t.to); }} />)}
        <Row id="menu-quit" title="Quit Kiln" a="Quit" onClick={() => setQuit(true)} />
      </div>
    </Dialog>
  );
}
