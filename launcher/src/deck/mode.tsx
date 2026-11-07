// Which UI the launcher starts in (docs/kiln/launcher/deck.md "How Deck mode is chosen"):
//   1. `--deck` / `--desktop` (or KILN_LAUNCHER_UI): app_info.deck.forced, for this start;
//   2. the setting `uiMode` ("deck" | "desktop"; "auto" or absent: go on), set in either UI's Settings;
//   3. app_info.deck.detected: a Steam Deck, SteamOS or a gamescope session;
//   4. else the desktop UI.
// The gate decides once, before the first render, so a Deck start never brings up the desktop shell (and its WebGL
// fire) first. Switching later is an explicit choice in Settings.
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { useLocation, useNavigate, type NavigateFunction } from "react-router-dom";
import * as api from "../api/commands";
import type { AppInfo, LauncherSettings, UiMode } from "../api/commands";
import { qk, useAppInfo, useSettings } from "../api/hooks";
import { Card, Segmented, Splash, toast } from "../ui";

export type Ui = "deck" | "desktop";
export interface UiChoice {
  ui: Ui;
  why: "flag" | "setting" | "detected" | "default";
}

export function chooseUi(info: AppInfo | null | undefined, settings: LauncherSettings | null | undefined): UiChoice {
  const forced = info?.deck?.forced;
  if (forced === "deck" || forced === "desktop") return { ui: forced, why: "flag" };
  const mode = settings?.uiMode;
  if (mode === "deck" || mode === "desktop") return { ui: mode, why: "setting" };
  if (info?.deck?.detected) return { ui: "deck", why: "detected" };
  return { ui: "desktop", why: "default" };
}

export const isDeckPath = (p: string) => p === "/deck" || p.startsWith("/deck/");

/** Renders the app once the start UI is known; a Deck start lands on /deck. Deep links (`#/settings`, `#/deck/mods`)
 *  are kept as they are. Never waits more than 1.5 s (a backend that does not answer gets the desktop UI). */
export function UiModeGate({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const nav = useNavigate();
  const loc = useLocation();
  // undefined: deciding; a path: going there (the router commits navigations in a transition: render nothing until
  // the location is there, or the desktop shell would mount for a frame); null: render as it is
  const [want, setWant] = useState<string | null | undefined>(undefined);
  useEffect(() => {
    let live = true;
    const settle = (info: AppInfo | null, settings: LauncherSettings | null) => {
      if (!live) return;
      live = false;
      const c = chooseUi(info, settings);
      const atStart = loc.pathname === "/" || loc.pathname === "";
      if (c.ui === "deck" && atStart) { setWant("/deck"); nav("/deck", { replace: true }); }
      else setWant(null);
    };
    const timer = setTimeout(() => settle(null, null), 1500);
    Promise.allSettled([api.appInfo(), api.appSettingsGet()]).then(([i, s]) => {
      clearTimeout(timer);
      const info = i.status === "fulfilled" ? i.value : null;
      const settings = s.status === "fulfilled" ? s.value : null;
      // the shell asks for both again: hand them over
      if (info) qc.setQueryData(qk.appInfo, info);
      if (settings) qc.setQueryData(qk.settings, settings);
      settle(info, settings);
    });
    return () => { clearTimeout(timer); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  // arrived: from now on the router's location is the only truth (Settings may switch UIs later)
  const arrived = !!want && isDeckPath(loc.pathname);
  useEffect(() => { if (arrived) setWant(null); }, [arrived]);
  // the splash while deciding (the desktop shell's own splash takes over in the same frame)
  if (want === undefined || (want && !arrived)) return <Splash done={false} />;
  return <>{children}</>;
}

/** Changes the launcher settings at once (the screen shows it before the backend answers, so quick presses build
 *  on each other), then saves; a failure puts the old values back (toasted). */
export async function saveSettings(qc: QueryClient, change: (cur: LauncherSettings) => Partial<LauncherSettings>): Promise<LauncherSettings | null> {
  const cur = qc.getQueryData<LauncherSettings>(qk.settings) ?? await api.appSettingsGet();
  const want = { ...cur, ...change(cur) };
  qc.setQueryData(qk.settings, want);
  try {
    const next = await api.appSettingsSet(want);
    if (qc.getQueryData<LauncherSettings>(qk.settings) === want) qc.setQueryData(qk.settings, next);
    return next;
  } catch (e) {
    qc.setQueryData(qk.settings, cur);
    toast.error(e);
    return null;
  }
}

/** The current start UI ("auto" when unset). */
export const uiModeOf = (s: LauncherSettings | null | undefined): UiMode => (s?.uiMode === "deck" || s?.uiMode === "desktop" ? s.uiMode : "auto");

/** Saves the start UI (`uiMode`). */
export const saveUiMode = (qc: QueryClient, mode: UiMode | ((cur: UiMode) => UiMode)) =>
  saveSettings(qc, (cur) => ({ uiMode: typeof mode === "function" ? mode(uiModeOf(cur)) : mode }));

/** Goes to the other UI now (this start). */
export function switchUi(nav: NavigateFunction, to: Ui) {
  nav(to === "deck" ? "/deck" : "/", { replace: true });
}

/** Desktop Settings: the Steam Deck UI (start in it: auto / always / never; open it now). */
export function DeckModeCard() {
  const qc = useQueryClient();
  const nav = useNavigate();
  const s = useSettings();
  const info = useAppInfo();
  if (!s.data) return null;
  const mode: UiMode = s.data.uiMode === "deck" || s.data.uiMode === "desktop" ? s.data.uiMode : "auto";
  const d = info.data?.deck;
  const forced = d?.forced;
  return (
    <Card title="Steam Deck" actions={<button type="button" className="k-btn k-btn-s" onClick={() => switchUi(nav, "deck")}>Open Deck mode</button>}>
      <Segmented<UiMode> aria-label="Start in" value={mode} onChange={(v) => void saveUiMode(qc, v)}
        options={[{ id: "auto", label: "Auto" }, { id: "deck", label: "Deck mode" }, { id: "desktop", label: "Desktop" }]} />
      <span className="k-hint">
        {forced ? `This start: --${forced}.` : d?.detected ? `Steam Deck found (${d.signals.join(", ")}).` : "No Steam Deck found."}
      </span>
    </Card>
  );
}
