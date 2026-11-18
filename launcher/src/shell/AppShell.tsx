// The launcher frame ("Hearth"): the stage behind everything, the header (logo, nav, tasks, Settings, About, the game's
// state word), the Play page in front of the stage, the sheet that rises over it for every other page, the mouth (the
// Play button, which shrinks to a pilot light in the corner while a sheet is up), toasts and the splash.
// Mounts the backend event feeds once, the first run's game detection, the window-wide mod drop, the rows' right click
// (ui/Menu.tsx) and the Install Kiln confirm.
import { Suspense, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { NavLink, useLocation, useNavigate, useOutlet } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { useAppInfo, useBackendEvents, useNexusEnabled, useSettings } from "../api/hooks";
import { isTauri } from "../api/commands";
import { mockFallbackActive, useMockFallback } from "../api/fallback";
import { Lockup, Skeleton, Splash, TaskTray, Toaster, useRowContextMenu, useTaskFeed } from "../ui";
import { GameArtAuto } from "../components/GameArt";
import { UpdateTray } from "./UpdateNotice";
import { BetaMark, ChannelSwitchDialog } from "./ChannelSwitch";
import { RouteErrorBoundary } from "./ErrorBoundary";
import { DataFolderNotice } from "./DataFolderGate";
import { NAV_MAIN, NAV_SIDE, navFor } from "./nav";
import { DropAnywhere, ModpackDropOffer, useAddMods } from "./drop";
import { ImportDetails, PickProfile } from "./importResult";
import { NxmListener } from "./catalogInstall";
import { ModUpdatesWatcher } from "./modUpdates";
import { InstallKilnModal, detectGames, useAutoDetect, useDetect, useInstallPrompt, useKilnInstall } from "./firstRun";
import { clock, usePlayControl, type PlayControl } from "./play";
import { Stage, stageInput, type Heat } from "./Stage";
import { startWords } from "./startProgress";
import { useT } from "../i18n";

const reduced = () => typeof window !== "undefined" && !!window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;

/** Keeps the last value shown for `ms` after `show` turns false (so a sheet can drop with its page still on it). */
function useLinger<T>(value: T, show: boolean, ms: number): T | null {
  const last = useRef<T | null>(show ? value : null);
  const [, bump] = useState(0);
  if (show) last.current = value;
  useEffect(() => {
    if (show) return;
    const timer = setTimeout(() => { last.current = null; bump((n) => n + 1); }, reduced() ? 0 : ms);
    return () => clearTimeout(timer);
  }, [show, ms]);
  return show ? value : last.current;
}

function Header({ isPlay, ctl }: { isPlay: boolean; ctl: PlayControl }) {
  const t = useT();
  const loc = useLocation();
  const info = useAppInfo();
  const fallback = useMockFallback();
  const nav = useRef<HTMLElement>(null);
  const active = navFor(loc.pathname);
  const nexusEnabled = useNexusEnabled();
  const main = NAV_MAIN.filter((n) => nexusEnabled || n.to !== "/catalog");
  // the gold diamond sits under the active word (placed from its offset: words differ in width)
  useLayoutEffect(() => {
    const place = () => {
      const b = nav.current?.querySelector<HTMLElement>(".k-navb.on");
      nav.current?.style.setProperty("--dx", b ? `${(b.offsetLeft + b.offsetWidth / 2).toFixed(1)}px` : "-40px");
    };
    place();
    window.addEventListener("resize", place);
    document.fonts?.ready.then(place).catch(() => {});
    return () => window.removeEventListener("resize", place);
  }, [active?.to, nexusEnabled]);
  const v = ctl.view;
  // a start that says what it does: its step, and the time left (else how far, in percent)
  const tl = ctl.timeline;
  const tw = tl ? startWords(tl) : null;
  const stepNow = tl?.running != null ? tl.steps[tl.running].label : t("shell.gameState.starting");
  const word = tl && tw ? t("shell.appShell.headStart", { progress: tw.first ? t("shell.appShell.pct", { pct: tw.pct }) : tw.short, step: stepNow })
    : ctl.starting ? t("shell.gameState.starting") : v.phase === "running" || v.phase === "vanilla"
    ? (ctl.elapsed !== null ? t("shell.appShell.labelTime", { label: v.label, time: clock(ctl.elapsed) }) : v.label)
    : v.label;
  const tone = ctl.starting ? "ember" : v.phase === "running" ? (v.tone === "warn" ? "warn" : "ember") : v.phase === "vanilla" ? "info" : v.phase === "updating" ? "warn" : "idle";
  const mock = info.data?.mock || mockFallbackActive(fallback);
  return (
    <header className="k-head">
      <NavLink to="/" className="k-logo" aria-label={t("shell.appShell.logoAria")} title={info.data ? t("shell.appShell.logoTitle", { version: info.data.version }) : "Kiln"}><Lockup height={30} /></NavLink>
      <nav ref={nav} className="k-nav" aria-label={t("shell.appShell.navAria")}>
        {main.map((n) => (
          <NavLink key={n.to} to={n.to} end={n.to === "/"} className={"k-navb" + (active?.to === n.to ? " on" : "")} aria-current={active?.to === n.to ? "page" : undefined}>
            <span>{n.label}</span>
          </NavLink>
        ))}
        {active && main.includes(active) && <span className="k-nav-dia" aria-hidden><i className="k-dia" /></span>}
      </nav>
      <div className="k-head-right">
        <UpdateTray />
        <TaskTray />
        {NAV_SIDE.map((n) => (
          <NavLink key={n.to} to={n.to} className={({ isActive }) => "k-head-link" + (isActive ? " on" : "")}>{n.label}</NavLink>
        ))}
        {mock && <span className="k-mock-flag" title={info.data?.mock ? t("shell.appShell.browserMock") : t("shell.appShell.mockFor", { list: fallback.join(", ") })}>{t("shell.appShell.mock")}</span>}
        {info.data?.distribution !== "nexus" && <BetaMark />}
        <span role="status" aria-live={tl ? "off" : undefined} className={`k-head-state k-tone-${tone}${tl ? " k-head-start" : ""}`} style={{ opacity: isPlay ? 0 : 1 }}
          title={tw ? t("shell.appShell.lineLeft", { line: tw.line, left: tw.left }) : v.detail ?? undefined} data-testid="head-state">{word}</span>
      </div>
    </header>
  );
}

/** The mouth: the Play button on the Play page, the pilot light in the corner on every other page. */
function Mouth({ isPlay, ctl }: { isPlay: boolean; ctl: PlayControl }) {
  const t = useT();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [hot, setHot] = useState(false);
  const looking = useDetect((s) => s.busy);
  const kiln = useKilnInstall(ctl.game);
  const v = ctl.view;
  const name = ctl.profileName ?? "Enshrouded";
  let word: string, sub: string, heat: Heat, act: (() => void) | null, aria: string;
  if (!ctl.game && looking) {
    word = t("shell.appShell.looking"); sub = t("shell.appShell.lookingSub"); heat = "unlit"; act = null; aria = t("shell.appShell.lookingAria");
  } else if (!ctl.game) {
    word = t("shell.appShell.addGame"); sub = t("shell.appShell.addGameSub"); heat = "unlit"; act = () => void detectGames(qc); aria = t("shell.appShell.addGameAria");
  } else if (v.phase === "none") {
    word = t("nav.play"); sub = t("shell.appShell.checkingGame"); heat = "unlit"; act = null; aria = t("shell.appShell.checkingGame");
  } else if (v.phase === "updating") {
    word = t("shell.gameState.updating"); sub = t("shell.gameState.steamUpdating"); heat = "low"; act = null; aria = t("shell.gameState.steamUpdating");
  } else if (!ctl.installed && !ctl.running && kiln.installing) {
    word = t("shell.appShell.installing"); sub = t("shell.appShell.installSub"); heat = "low"; act = null; aria = t("shell.appShell.installingAria");
  } else if (!ctl.installed && !ctl.running) {
    // the main action of a first start: the install's confirm opens right here (ShroudForge's switch is typed, on Runtime)
    word = t("common.install"); sub = t("shell.appShell.installSub"); heat = "idle"; aria = t("shell.appShell.installKiln");
    act = kiln.shroudforge ? () => nav("/runtime") : () => useInstallPrompt.getState().show();
  } else if (ctl.timeline) {
    const tw = startWords(ctl.timeline);
    word = t("shell.gameState.starting"); sub = tw.first ? tw.stepOf : tw.short; heat = "flare"; act = null;
    aria = t("shell.appShell.startingAria", { line: tw.line, left: tw.left });
  } else if (ctl.starting) {
    word = t("shell.gameState.starting"); sub = t("shell.play.waitingForKiln"); heat = "flare"; act = null; aria = t("shell.gameState.starting");
  } else if (ctl.running) {
    const time = ctl.elapsed !== null ? clock(ctl.elapsed) : v.label;
    word = time; sub = v.label; heat = v.word === "safeIdle" ? "low" : "embers"; act = null;
    aria = ctl.elapsed !== null ? t("shell.appShell.labelFor", { label: v.label, time }) : v.label;
  } else {
    word = t("nav.play"); sub = ""; heat = "idle"; act = ctl.canPlay ? () => ctl.play("profile") : null; aria = t("shell.appShell.playName", { name });
  }
  stageInput.heat = heat;
  stageInput.hover = hot && !!act;
  stageInput.game = !!ctl.running && !ctl.starting;
  const subTone = v.phase === "vanilla" ? " k-tone-info" : v.phase === "updating" || v.word === "safeIdle" ? " k-tone-warn" : "";
  return (
    <button type="button" className={`k-door ${isPlay ? "play" : "pilot"} heat-${heat}${hot && act ? " hot" : ""}`} disabled={!act}
      onClick={() => act?.()} onMouseEnter={() => setHot(true)} onMouseLeave={() => setHot(false)} aria-label={aria} title={isPlay ? undefined : aria}>
      <span className="k-door-fire" aria-hidden />
      <span className="k-door-lab" aria-hidden>
        <span className={"k-door-word" + (word.length > 6 ? " long" : "")}>{word}</span>
        {sub && <span className={"k-door-sub" + subTone}>{sub}</span>}
      </span>
    </button>
  );
}

export function AppShell() {
  const t = useT();
  const info = useAppInfo();
  const settings = useSettings();
  // the WebGL fire, unless turned off (Settings, Appearance) or KILN_LAUNCHER_NO_WEBGL=1; decided once both are known
  // (the splash covers that), so a machine set to the plain button never starts the fire
  const fire = info.isFetched && settings.isFetched && !info.data?.noWebgl && settings.data?.animatedFire !== false;
  const loc = useLocation();
  const outlet = useOutlet();
  useTaskFeed();
  useBackendEvents();
  useAutoDetect();
  useRowContextMenu();
  const addMods = useAddMods();
  const ctl = usePlayControl();
  // browser-mock mode only (never in the app): tests start the game through this control's own start path, the one
  // Play uses (Play once with another profile has no button of its own yet)
  useEffect(() => {
    if (isTauri()) return;
    const w = window as unknown as { __kilnPlay?: PlayControl["play"] };
    w.__kilnPlay = ctl.play;
    return () => { delete w.__kilnPlay; };
  }, [ctl.play]);
  const [minShown, setMinShown] = useState(false);
  useEffect(() => { const timer = setTimeout(() => setMinShown(true), 650); return () => clearTimeout(timer); }, []);
  const ready = minShown && (info.isSuccess || info.isError);
  const isPlay = loc.pathname === "/" || loc.pathname === "";
  stageInput.play = isPlay;
  const page = (
    <RouteErrorBoundary key={loc.pathname}>
      <Suspense fallback={<div className="k-page"><Skeleton lines={4} /></div>}><div className="k-route">{outlet}</div></Suspense>
    </RouteErrorBoundary>
  );
  const sheet = useLinger<ReactNode>(page, !isPlay, 480);
  const hero = useLinger<ReactNode>(page, isPlay, 360);
  // the torch: a soft warm light follows the pointer across the sheet
  const root = useRef<HTMLDivElement>(null);
  const frame = useRef(0);
  const torch = (e: React.MouseEvent) => {
    if (frame.current || reduced()) return;
    const x = e.clientX, y = e.clientY;
    frame.current = requestAnimationFrame(() => {
      frame.current = 0;
      const b = root.current?.getBoundingClientRect();
      if (b?.width) { stageInput.mx = ((x - b.left) / b.width) * 2 - 1; stageInput.my = 1 - ((y - b.top) / b.height) * 2; }
      root.current?.style.setProperty("--mx", `${x}px`);
      root.current?.style.setProperty("--my", `${y}px`);
    });
  };
  // `t` changes with the language: the title follows it
  useEffect(() => { document.title = isPlay ? "Kiln" : t("shell.appShell.docTitle", { page: navFor(loc.pathname)?.label ?? "Kiln" }); }, [isPlay, loc.pathname, t]);
  return (
    <div ref={root} className={"k-app" + (isPlay ? " is-play" : " is-sheet")} onMouseMove={torch}>
      <div className="k-stage" aria-hidden><Stage root={root} key={fire ? "fire" : "plain"} enabled={fire} /><div className="k-fall" /></div>
      <Header isPlay={isPlay} ctl={ctl} />
      <div className={"k-hero-layer" + (isPlay ? " on" : "")} aria-hidden={!isPlay}>{hero}</div>
      <main className={"k-sheet" + (isPlay ? " down" : " up")} id="main" aria-hidden={isPlay}>
        <div className="k-sheet-in">{sheet}</div>
      </main>
      <Mouth isPlay={isPlay} ctl={ctl} />
      <GameArtAuto />
      <DropAnywhere add={(p) => void addMods(p)} />
      <InstallKilnModal onAdd={(p, g) => void addMods(p, g)} />
      <ChannelSwitchDialog />
      <ModpackDropOffer />
      <ImportDetails />
      <PickProfile />
      <NxmListener />
      <ModUpdatesWatcher />
      <DataFolderNotice />
      <Toaster />
      <Splash done={ready} />
    </div>
  );
}
