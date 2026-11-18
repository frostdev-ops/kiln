// The Steam Deck UI (docs/kiln/launcher/deck.md): one fullscreen frame at 1280×800 (scaled to other sizes), the tab
// bar (L1/R1), the current screen, the hint bar (the top layer's buttons), dialogs and toasts. Every input source
// (native gamepad, keyboard, browser Gamepad API, touch) ends in the same dispatcher:
//   D-pad / left stick: move focus (focus.ts) · A: click the focused control (hold for a HoldButton) · B: the top
//   layer's back, else Home · X / Y: the top layer's actions · L1 / R1: tabs · Start: the menu · View: Help ·
//   right stick: scroll.
import { lazy, Suspense, useEffect, useLayoutEffect, useRef, useState } from "react";
import { Navigate, Route, Routes, useLocation, useNavigate } from "react-router-dom";
import * as api from "../api/commands";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAppInfo, useBackendEvents, useDistribution, useNexusEnabled, useSettings } from "../api/hooks";
import type { UpdateStatus } from "../api/commands";
import { useTaskFeed, Lockup } from "../ui";
import { useAutoDetect } from "../shell/firstRun";
import { clock, usePlayControl } from "../shell/play";
import { startWords } from "../shell/startProgress";
import { QuitDialog } from "./dialogs";
import { DeckFire, deckFire } from "./Fire";
import { focusable, focusFirst, move } from "./focus";
import { Glyph, useGlyphStyle } from "./glyphs";
import { isDir, useDeckInput, usePads, type Press } from "./input";
import { topLayer, useInertBelowTop, useLayers, type DeckAction } from "./layers";
import { DeckToaster, Dialog, Row } from "./ui";
import { DeckDataFolderNotice } from "./DataFolder";
import { NexusRoute } from "../components/NexusGate";
import Home from "./screens/Home";
import Mods from "./screens/Mods";
import AddMods from "./screens/AddMods";
import Catalog, { DeckNxm } from "./screens/Catalog";
import Kiln from "./screens/Kiln";
import Help from "./screens/Help";
import Settings from "./screens/Settings";
import { t, useT } from "../i18n";
import "./deck.css";

// the save editor carries its item data (about 2 MB): loaded on the first visit to its tab, as on the desktop
const DeckEditor = lazy(() => import("../editor/deck/DeckEditor"));

export const TABS = [
  { to: "/deck" },
  { to: "/deck/mods" },
  { to: "/deck/add" },
  { to: "/deck/catalog" },
  { to: "/deck/kiln" },
  { to: "/deck/editor" },
  { to: "/deck/help" },
  { to: "/deck/settings" },
] as const;

type Tab = typeof TABS[number];
/** A tab's word, in the current language. */
export function tabLabel(tab: Tab): string {
  switch (tab.to) {
    case "/deck": return t("deck.app.tabs.home");
    case "/deck/mods": return t("deck.app.tabs.mods");
    case "/deck/add": return t("deck.app.tabs.addMods");
    case "/deck/catalog": return t("deck.app.tabs.catalog");
    case "/deck/kiln": return t("deck.app.tabs.kiln");
    case "/deck/editor": return t("deck.app.tabs.editor");
    case "/deck/help": return t("deck.app.tabs.help");
    case "/deck/settings": return t("deck.app.tabs.settings");
  }
}
const WITHOUT_NEXUS = TABS.filter((x) => x.to !== "/deck/catalog");
const tabsFor = (enabled: boolean): readonly Tab[] => enabled ? TABS : WITHOUT_NEXUS;
const tabOf = (path: string, tabs: readonly Tab[] = TABS) => Math.max(0, tabs.findIndex((x) => x.to === path.replace(/\/+$/, "")));

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
    let live = true;
    api.deckFullscreen(true).catch(() => {});
    api.deckGamepad(true).then((st) => {
      if (!live) return;
      usePads.setState({ native: Object.fromEntries(st.devices.map((d) => [d.device, { name: d.name, vendor: d.vendor, product: d.product }])) });
    }).catch(() => {});
    return () => {
      live = false;
      usePads.setState({ native: {}, nativeSeen: false });
      api.deckGamepad(false).catch(() => {});
      api.deckFullscreen(false).catch(() => {});
    };
  }, []);
}

/** Focus is never left on nothing: when the focused control goes away (a row removed, a screen changed under a
 *  closing dialog), the top layer's first stop takes it, as soon as that layer is live. */
function useKeepFocus(root: React.RefObject<HTMLDivElement | null>) {
  useEffect(() => {
    const timer = setInterval(() => {
      const a = document.activeElement;
      const scope = topLayer()?.el.current ?? root.current;
      if (!scope || scope.hasAttribute("inert") || scope.closest("[inert]")) return;
      if (a instanceof HTMLElement && scope.contains(a) && focusable(a)) return;
      focusFirst(scope);
    }, 200);
    return () => clearInterval(timer);
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
      setLabel(w === "-" ? null : w ?? t("deck.app.select"));
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

/** (dev.29) A test build is running: "Beta <n>" under the logo (Settings > Channel switches back). The Nexus build has
 *  no self-update and calls no update command. */
function DeckBetaMark() {
  const t = useT();
  const qc = useQueryClient();
  const direct = useDistribution() === "direct";
  const up = useQuery({ queryKey: ["update_status"], queryFn: api.updateStatus, retry: false, enabled: direct });
  useEffect(() => {
    if (!direct) return;
    let off: (() => void) | undefined, live = true;
    api.onKilnEvent("kiln://update", (s: UpdateStatus) => qc.setQueryData(["update_status"], s)).then((f) => (live ? (off = f) : f()));
    return () => { live = false; off?.(); };
  }, [qc, direct]);
  const s = up.data;
  if (!direct || s?.buildChannel !== "beta") return null;
  return <span className="deck-beta deck-word warn" data-testid="deck-beta-mark" title={t("deck.shared.testBuild", { beta: s.beta ?? "", current: s.current })}>{t("deck.shared.beta", { beta: s.beta ?? "" })}</span>;
}

export default function DeckApp() {
  const t = useT();
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
  const tabs = tabsFor(useNexusEnabled());
  const tab = tabOf(loc.pathname, tabs);
  const tabRef = useRef(tab);
  tabRef.current = tab;
  // the tab L1/R1 has asked for and the router has not shown yet: the next press counts from it, so a tab that takes
  // long to draw (the save editor's inventory) does not swallow a press. Forgotten once shown, or after a moment (a
  // route that sends the player elsewhere).
  const asked = useRef<{ tab: number; at: number } | null>(null);
  if (asked.current?.tab === tab) asked.current = null;
  useEffect(() => { document.title = t("deck.app.docTitle", { tab: tabLabel(tabs[tab]) }); }, [tab, tabs, t]);

  const goTab = (i: number) => { if (i >= 0 && i < tabs.length && i !== tabRef.current) nav(tabs[i].to); };
  const stepTab = (d: number) => {
    const from = asked.current && performance.now() - asked.current.at < 1500 ? asked.current.tab : tabRef.current;
    const i = from + d;
    if (i < 0 || i >= tabs.length) return;
    asked.current = { tab: i, at: performance.now() };
    nav(tabs[i].to);
  };
  const back = () => {
    const top = topLayer();
    if (top?.back) top.back();
    else if (!top?.modal && tabRef.current !== 0) { asked.current = null; nav("/deck"); }
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
      case "lb": case "rb": if (!top?.modal) stepTab(p.button === "rb" ? 1 : -1); return;
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
      <TopBar tab={tab} tabs={tabs} goTab={goTab} />
      <main className="deck-main">
        <Routes>
          <Route index element={<Home />} />
          <Route path="mods" element={<Mods />} />
          <Route path="add" element={<AddMods />} />
          <Route path="catalog" element={<NexusRoute home="/deck"><Catalog /></NexusRoute>} />
          <Route path="kiln" element={<Kiln />} />
          <Route path="editor" element={<Suspense fallback={<p className="deck-note">{t("deck.app.openingEditor")}</p>}><DeckEditor /></Suspense>} />
          <Route path="help" element={<Help />} />
          <Route path="settings" element={<Settings />} />
          <Route path="*" element={<Navigate to="/deck" replace />} />
        </Routes>
      </main>
      <HintBar press={press} />
      {menu && <MenuDialog onClose={() => setMenu(false)} />}
      <div className="deck-overlays" />
      <DeckNxm />
      <DeckToaster />
      <DeckDataFolderNotice />
    </div>
  );
}

function TopBar({ tab, tabs, goTab }: { tab: number; tabs: readonly Tab[]; goTab: (i: number) => void }) {
  const t = useT();
  const style = useGlyphStyle();
  const ctl = usePlayControl();
  const v = ctl.view;
  // a start that says what it does: "Starting · about 40 s left" ("Starting · 42%" while it is being timed)
  const tw = ctl.timeline ? startWords(ctl.timeline) : null;
  const word = tw ? (tw.first ? t("deck.app.startingPct", { pct: tw.pct }) : t("deck.app.startingLeft", { left: tw.short })) : ctl.starting ? t("deck.shared.starting")
    : ctl.running ? (ctl.elapsed !== null ? t("deck.app.stateTime", { state: v.label, time: clock(ctl.elapsed) }) : v.label) : v.label;
  const tone = ctl.kilnMissing || v.tone === "warn" ? "warn" : ctl.starting || v.phase === "running" ? "ember" : v.phase === "vanilla" ? "info" : "idle";
  // the fire's heat away from Home (Home sets its own)
  deckFire.game = ctl.running && !ctl.starting;
  deckFire.heat = !ctl.game ? "unlit" : v.phase === "updating" ? "low" : ctl.starting ? "flare" : ctl.running ? (v.word === "safeIdle" ? "low" : "embers") : "idle";
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
  }, [tab, tabs]);
  return (
    <header className="deck-top">
      <div className="deck-logo"><Lockup height={30} /><DeckBetaMark /></div>
      <nav ref={nav} className="deck-tabs" aria-label={t("deck.app.tabsAria")} data-deck-skip="">
        <span className="deck-tab-dia" aria-hidden><i /></span>
        <button type="button" tabIndex={-1} className="deck-tab-glyph" aria-label={t("deck.app.previousTab")} onClick={() => goTab(tab - 1)}><Glyph button="lb" style={style} size={1.6} /></button>
        {tabs.map((x, i) => (
          <button key={x.to} type="button" tabIndex={-1} className={"deck-tab" + (i === tab ? " on" : "")} aria-current={i === tab ? "page" : undefined} onClick={() => goTab(i)}>{tabLabel(x)}</button>
        ))}
        <button type="button" tabIndex={-1} className="deck-tab-glyph" aria-label={t("deck.app.nextTab")} onClick={() => goTab(tab + 1)}><Glyph button="rb" style={style} size={1.6} /></button>
      </nav>
      <span className={`deck-state deck-word ${tone}`} role="status" aria-live={tw ? "off" : undefined} data-testid="deck-state">{word}</span>
    </header>
  );
}

function HintBar({ press }: { press: (p: Press) => void }) {
  const t = useT();
  const style = useGlyphStyle();
  const stack = useLayers((s) => s.stack);
  const a = useALabel();
  const top = stack.at(-1) ?? null;
  const loc = useLocation();
  const atHome = tabOf(loc.pathname) === 0;
  const bLabel = top?.back ? top.backLabel ?? t("common.back") : top?.modal ? null : atHome ? null : t("deck.shared.home");
  const acts: DeckAction[] = top?.actions ?? [];
  const tap = (button: Press["button"]) => () => { press({ button, pressed: true, repeat: false, source: "key" }); press({ button, pressed: false, repeat: false, source: "key" }); };
  const hint = (button: Press["button"], label: string, disabled = false) => (
    <button key={button + label} type="button" tabIndex={-1} className={"deck-hint" + (disabled ? " off" : "")} data-hint={button}
      disabled={disabled} onPointerDown={(e) => e.preventDefault()} onClick={tap(button)}>
      <Glyph button={button} style={style} size={1.75} /><span>{label}</span>
    </button>
  );
  return (
    <footer className="deck-hints" data-deck-skip="" aria-label={t("deck.app.buttonsAria")}>
      <div className="deck-hints-left">
        <span className="deck-hint static"><Glyph button="dpad" style={style} size={1.6} /><span>{t("deck.app.move")}</span></span>
      </div>
      <div className="deck-hints-right">
        {a && hint("a", a)}
        {acts.map((x) => hint(x.button, x.label, x.disabled))}
        {bLabel && hint("b", bLabel)}
        {!top?.modal && hint("start", t("deck.app.menu"))}
      </div>
    </footer>
  );
}

function MenuDialog({ onClose }: { onClose: () => void }) {
  const t = useT();
  const nav = useNavigate();
  const loc = useLocation();
  const [quit, setQuit] = useState(false);
  const tabs = tabsFor(useNexusEnabled());
  const cur = tabOf(loc.pathname, tabs);
  if (quit) return <QuitDialog onClose={() => { setQuit(false); onClose(); }} />;
  return (
    <Dialog name="menu" title={t("deck.app.menu")} onClose={onClose} backLabel={t("common.close")} actions={[{ button: "start", label: t("common.close"), run: onClose }]}>
      <div className="deck-list">
        {tabs.map((x, i) => <Row key={x.to} id={`menu-${i}`} title={tabLabel(x)} a={t("common.open")} end={i === cur ? <span className="deck-dim">{t("deck.app.here")}</span> : undefined} onClick={() => { onClose(); nav(x.to); }} />)}
        <Row id="menu-quit" title={t("deck.app.quitKiln")} a={t("deck.dialogs.quit")} onClick={() => setQuit(true)} />
      </div>
    </Dialog>
  );
}
