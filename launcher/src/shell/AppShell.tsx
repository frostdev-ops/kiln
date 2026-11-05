// The launcher frame: sidebar, top bar (game-state pill + task tray), content outlet, toasts,
// splash. Mounts the backend event feeds once.
import { Check, ChevronDown } from "lucide-react";
import { Suspense, useEffect, useRef, useState } from "react";
import { NavLink, Outlet, useLocation, useNavigate } from "react-router-dom";
import { useAppInfo, useBackendEvents, useGame, useGameSel, useGameStatus, useLaunchState } from "../api/hooks";
import { mockFallbackActive, useMockFallback } from "../api/fallback";
import { Mark, Spinner, Splash, StatusPill, TaskTray, Toaster, useTaskFeed } from "../ui";
import { GameArtAuto } from "../components/GameArt";
import { RouteErrorBoundary } from "./ErrorBoundary";
import { gameState } from "./gameState";
import { NAV_FOOT, NAV_MAIN, titleFor, type NavItem } from "./nav";

function NavEntry({ item }: { item: NavItem }) {
  const Icon = item.icon;
  return (
    <NavLink to={item.to} end={item.to === "/"} className={({ isActive }) => "k-nav-item" + (isActive ? " active" : "")}>
      <Icon aria-hidden />
      {item.label}
    </NavLink>
  );
}

function gameName(path: string) {
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] ?? path;
}

function GamePill() {
  const { game, games } = useGame();
  const setGameId = useGameSel((s) => s.setGameId);
  const status = useGameStatus(game?.id);
  const launch = useLaunchState(game?.id);
  const view = gameState(game ? status.data : null, launch.data, !!game && status.isLoading);
  const nav = useNavigate();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const clients = games.filter((g) => g.kind === "client");
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, [open]);
  return (
    <div ref={ref} style={{ position: "relative" }} className="k-row">
      {clients.length > 1 && (
        <button type="button" className="k-btn k-btn-ghost k-btn-s" onClick={() => setOpen((o) => !o)} aria-haspopup="menu" aria-expanded={open}>
          {game ? gameName(game.path) : "Game"} <ChevronDown />
        </button>
      )}
      <button type="button" className="k-game-btn" onClick={() => nav("/")} title={view.detail ?? undefined} aria-label={`Game: ${view.label}`}>
        <StatusPill tone={view.tone} pulse={view.pulse}>{view.label}</StatusPill>
      </button>
      {open && (
        <div className="k-popover" role="menu" style={{ top: "calc(100% + 6px)", right: 0 }}>
          {clients.map((g) => (
            <button key={g.id} type="button" role="menuitem" className="k-menu-item" onClick={() => { setGameId(g.id); setOpen(false); }}>
              {g.id === game?.id ? <Check /> : <span style={{ width: 15 }} />}
              <span className="k-stack" style={{ gap: 0 }}>
                <span>{gameName(g.path)}</span>
                <span className="k-dim k-mono" style={{ fontSize: 11 }}>{g.source} · {g.platform}</span>
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export function AppShell() {
  const info = useAppInfo();
  const loc = useLocation();
  useTaskFeed();
  useBackendEvents();
  const fallback = useMockFallback();
  const [minShown, setMinShown] = useState(false);
  useEffect(() => { const t = setTimeout(() => setMinShown(true), 650); return () => clearTimeout(t); }, []);
  const ready = minShown && (info.isSuccess || info.isError);
  return (
    <div className="k-shell">
      <aside className="k-sidebar">
        <NavLink to="/" className="k-brand" aria-label="Kiln home">
          <Mark size={26} />
          <span className="k-brand-name">Kiln</span>
        </NavLink>
        <nav className="k-nav" aria-label="Main">
          {NAV_MAIN.map((n) => <NavEntry key={n.to} item={n} />)}
          <div className="k-nav-sep" />
          {NAV_FOOT.map((n) => <NavEntry key={n.to} item={n} />)}
        </nav>
        <div className="k-sidebar-foot">
          <span>{info.data ? `v${info.data.version}` : info.isError ? "backend offline" : "…"}</span>
          {(info.data?.mock || mockFallbackActive(fallback)) && <span className="k-mock-flag" title={info.data?.mock ? "Browser mock" : `Mock data for: ${fallback.join(", ")}`}>mock</span>}
        </div>
      </aside>
      <header className="k-topbar">
        <div className="k-topbar-title"><b>{titleFor(loc.pathname)}</b></div>
        <div className="k-topbar-right">
          <GameArtAuto />
          <GamePill />
          <TaskTray />
        </div>
      </header>
      <main className="k-main" id="main">
        <RouteErrorBoundary key={loc.pathname}>
          <Suspense fallback={<div className="k-page"><Spinner /></div>}>
            <Outlet />
          </Suspense>
        </RouteErrorBoundary>
      </main>
      <Toaster />
      <Splash done={ready} />
    </div>
  );
}
