// The launcher frame ("Hearth"): the stage behind everything, the header (logo, nav, tasks, Settings, About, the game's
// state word), the Play page in front of the stage, the sheet that rises over it for every other page, the mouth (the
// Play button, which shrinks to a pilot light in the corner while a sheet is up), toasts and the splash.
// Mounts the backend event feeds once.
import { Suspense, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { NavLink, useLocation, useNavigate, useOutlet } from "react-router-dom";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import * as api from "../api/commands";
import { qk, useAppInfo, useBackendEvents, useSettings } from "../api/hooks";
import { mockFallbackActive, useMockFallback } from "../api/fallback";
import { Lockup, Skeleton, Splash, TaskTray, Toaster, toast, useTaskFeed } from "../ui";
import { GameArtAuto } from "../components/GameArt";
import { RouteErrorBoundary } from "./ErrorBoundary";
import { NAV_MAIN, NAV_SIDE, navFor } from "./nav";
import { clock, usePlayControl, type PlayControl } from "./play";
import { Stage, stageInput, type Heat } from "./Stage";

const reduced = () => typeof window !== "undefined" && !!window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;

/** Keeps the last value shown for `ms` after `show` turns false (so a sheet can drop with its page still on it). */
function useLinger<T>(value: T, show: boolean, ms: number): T | null {
  const last = useRef<T | null>(show ? value : null);
  const [, bump] = useState(0);
  if (show) last.current = value;
  useEffect(() => {
    if (show) return;
    const t = setTimeout(() => { last.current = null; bump((n) => n + 1); }, reduced() ? 0 : ms);
    return () => clearTimeout(t);
  }, [show, ms]);
  return show ? value : last.current;
}

function Header({ isPlay, ctl }: { isPlay: boolean; ctl: PlayControl }) {
  const loc = useLocation();
  const info = useAppInfo();
  const fallback = useMockFallback();
  const nav = useRef<HTMLElement>(null);
  const active = navFor(loc.pathname);
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
  }, [active?.to]);
  const v = ctl.view;
  const word = ctl.starting ? "Starting" : v.phase === "running" || v.phase === "vanilla"
    ? `${v.label}${ctl.elapsed !== null ? ` ${clock(ctl.elapsed)}` : ""}`
    : v.label;
  const tone = ctl.starting ? "ember" : v.phase === "running" ? (v.tone === "warn" ? "warn" : "ember") : v.phase === "vanilla" ? "info" : v.phase === "updating" ? "warn" : "idle";
  const mock = info.data?.mock || mockFallbackActive(fallback);
  return (
    <header className="k-head">
      <NavLink to="/" className="k-logo" aria-label="Kiln: Play" title={info.data ? `Kiln Launcher ${info.data.version}` : "Kiln"}><Lockup height={30} /></NavLink>
      <nav ref={nav} className="k-nav" aria-label="Main">
        {NAV_MAIN.map((n) => (
          <NavLink key={n.to} to={n.to} end={n.to === "/"} className={"k-navb" + (active?.to === n.to ? " on" : "")} aria-current={active?.to === n.to ? "page" : undefined}>
            <span>{n.label}</span>
          </NavLink>
        ))}
        {active && NAV_MAIN.includes(active) && <span className="k-nav-dia" aria-hidden><i className="k-dia" /></span>}
      </nav>
      <div className="k-head-right">
        <TaskTray />
        {NAV_SIDE.map((n) => (
          <NavLink key={n.to} to={n.to} className={({ isActive }) => "k-head-link" + (isActive ? " on" : "")}>{n.label}</NavLink>
        ))}
        {mock && <span className="k-mock-flag" title={info.data?.mock ? "Browser mock" : `Mock data for: ${fallback.join(", ")}`}>Mock</span>}
        <span role="status" className={`k-head-state k-tone-${tone}`} style={{ opacity: isPlay ? 0 : 1 }} title={v.detail ?? undefined}>{word}</span>
      </div>
    </header>
  );
}

/** The mouth: the Play button on the Play page, the pilot light in the corner on every other page. */
function Mouth({ isPlay, ctl }: { isPlay: boolean; ctl: PlayControl }) {
  const nav = useNavigate();
  const qc = useQueryClient();
  const [hot, setHot] = useState(false);
  const detect = useMutation({
    mutationFn: api.gameDetect,
    onSuccess: (g) => { qc.setQueryData(qk.games, g); if (!g.some((x) => x.kind === "client")) toast.info("No Enshrouded found", "Add its folder on Play."); },
    onError: (e) => toast.error(e),
  });
  const v = ctl.view;
  const name = ctl.profileName ?? "Enshrouded";
  let word: string, sub: string, heat: Heat, act: (() => void) | null, aria: string;
  if (!ctl.game) {
    word = "Add game"; sub = "Kiln looks in every Steam library"; heat = "unlit"; act = () => detect.mutate(); aria = "Add game: find Enshrouded in your Steam libraries";
  } else if (v.phase === "none") {
    word = "Play"; sub = "Checking the game"; heat = "unlit"; act = null; aria = "Checking the game";
  } else if (v.phase === "updating") {
    word = "Updating"; sub = "Steam is updating the game"; heat = "low"; act = null; aria = "Steam is updating the game";
  } else if (!ctl.installed && !ctl.running) {
    word = "Install"; sub = "Kiln goes into the game folder"; heat = "unlit"; act = () => nav("/runtime?install=1"); aria = "Install Kiln";
  } else if (ctl.starting) {
    word = "Starting"; sub = "Waiting for Kiln"; heat = "flare"; act = null; aria = "Starting";
  } else if (ctl.running) {
    const t = ctl.elapsed !== null ? clock(ctl.elapsed) : v.label;
    word = t; sub = v.label === "Running" ? "Running" : v.label; heat = v.label === "Safe idle" ? "low" : "embers"; act = null;
    aria = `${v.label}${ctl.elapsed !== null ? ` for ${t}` : ""}`;
  } else {
    word = "Play"; sub = ""; heat = "idle"; act = () => ctl.play("profile"); aria = `Play ${name}`;
  }
  stageInput.heat = heat;
  stageInput.hover = hot && !!act;
  stageInput.game = !!ctl.running && !ctl.starting;
  const subTone = v.phase === "vanilla" ? " k-tone-info" : v.phase === "updating" || v.label === "Safe idle" ? " k-tone-warn" : "";
  return (
    <button type="button" className={`k-door ${isPlay ? "play" : "pilot"} heat-${heat}${hot && act ? " hot" : ""}`} disabled={!act || detect.isPending}
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
  const info = useAppInfo();
  const settings = useSettings();
  // the WebGL fire, unless turned off (Settings, Appearance) or KILN_LAUNCHER_NO_WEBGL=1; decided once both are known
  // (the splash covers that), so a machine set to the plain button never starts the fire
  const fire = info.isFetched && settings.isFetched && !info.data?.noWebgl && settings.data?.animatedFire !== false;
  const loc = useLocation();
  const outlet = useOutlet();
  useTaskFeed();
  useBackendEvents();
  const ctl = usePlayControl();
  const [minShown, setMinShown] = useState(false);
  useEffect(() => { const t = setTimeout(() => setMinShown(true), 650); return () => clearTimeout(t); }, []);
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
  useEffect(() => { document.title = isPlay ? "Kiln" : `${navFor(loc.pathname)?.label ?? "Kiln"} · Kiln`; }, [isPlay, loc.pathname]);
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
      <Toaster />
      <Splash done={ready} />
    </div>
  );
}
