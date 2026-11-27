// /servers/:id/overview (the Servers redesign, mockup A). While the server starts: the start's stages as cones
// (preparing, Wine where a Wine runs it, server process, loading the world, running; a restart stops first:
// startProgress.ts), the clock of the start itself, "about N s left" from the last start (or that it takes longer), the
// newest line, the console, and what can still be done meanwhile; a start that fails or is canceled shows in the
// activity panel above. Otherwise: the Kiln update when a newer runtime is here, the numbers that matter (players,
// health, uptime, versions), one line about each section, who is online and the console. Kiln missing from the folder:
// the Install Kiln banner first.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { NavLink, useParams } from "react-router-dom";
import { useAdminState, useInAdminProvider } from "../../api/admin";
import * as api from "../../api/commands";
import type { Task } from "../../api/commands";
import { Banner, Button, Cone, KeyValue, Sparkline, Toggle, fmtAgo, toast, useTasks } from "../../ui";
import { useTicks } from "../ticks";
import { ServerConsole } from "./ServerConsole";
import { elapsed, useServerFlow, type Flow } from "./serverActions";
import { startStep, startTrack, startView } from "./startProgress";
import { hasKiln, runnerLabel, sk, useDeployDialog, useServer, useServerStatus, type ServerModX } from "./serverApi";
import { kilnUpdateFor, shortKiln } from "./kilnUpdate";
import { alwaysLocal, offerHere, webPanelLines, webPanelOf } from "./webPanel";
import { useT } from "../../i18n";
import { webPanelMod } from "./webPanelMod";

/** Windows (WebView2): a Steam install starts without Wine there. */
const WIN = typeof navigator !== "undefined" && /Windows/i.test(navigator.userAgent);

export default function ServerOverviewPage() {
  const { id = "" } = useParams();
  const st = useServerStatus(id);
  const flow = useServerFlow(id);
  const startFlow = flow && (flow.kind === "start" || flow.kind === "restart") && flow.state === "running" ? flow : undefined;
  if (startFlow || st.data?.state === "starting") return <Starting id={id} flow={startFlow} />;
  return <Steady id={id} />;
}

function useNow(on: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!on) return;
    const h = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(h);
  }, [on]);
  return now;
}

function Starting({ id, flow }: { id: string; flow: Flow | undefined }) {
  const t = useT();
  const st = useServerStatus(id);
  const srv = useServer(id);
  const now = useNow(true);
  const step = startStep(flow);
  const task = useTasks((s) => (step?.taskId ? s.tasks[step.taskId] : undefined)) as Task | undefined;
  const s = srv.data;
  const remote = s?.runner === "remote" || s?.kind === "remote";
  const track = startTrack({ runner: s?.runner ?? "kilnctl", remote, onWindows: WIN, restart: flow?.kind === "restart" });
  const last = st.data?.lastStartS ?? null;
  // started outside the launcher (kilnctl, another launcher): only "loading the world" is known
  const v = startView(flow, task, now, { track, lastStartS: last, uptimeS: st.data?.uptimeS });
  const at = track.indexOf(v.stage);
  const stopping = v.stage === "stopping";
  const took = now - v.since;
  const newest = step?.lines[step.lines.length - 1]?.text;
  const name = s?.name ?? id;
  const label = (x: string) => t(`servers.overview.stage.${x}`);
  const hint = (x: string) => t(remote && x === "preparing" ? "servers.overview.stageHint.remote" : `servers.overview.stageHint.${x}`);
  return (
    <div className="k-sov k-sov-start">
      {/* what screen readers hear: the stage when it changes, not the clock */}
      <span className="k-sr-only" role="status" aria-live="polite">
        {stopping ? t("servers.overview.srStopping", { name }) : t("servers.overview.srStage", { name, stage: label(v.stage) })}
      </span>
      <div className="k-sov-live">
        <ol className="k-strack" aria-label={t("servers.overview.stages")} style={{ gridTemplateColumns: `repeat(${track.length}, minmax(0, 1fr))` }}>
          {track.map((x, i) => {
            const state = i < at ? "done" : i === at ? "running" : "pending";
            return (
              <li key={x} className={i < at ? "done" : i === at ? "now" : ""} aria-current={i === at ? "step" : undefined} data-stage={x}>
                <Cone state={i < at ? "done" : i === at ? "bend" : ""} />
                <b>{label(x)}</b>
                <span className="k-sr-only"> {t("servers.overview.stageState", { state: t(`servers.activity.step.${state}`) })}</span>
                <span className="d">{hint(x)}</span>
              </li>
            );
          })}
        </ol>
        <div className="k-sclockrow">
          <span className="k-sclock" aria-hidden>{elapsed(took)}</span>
          <span className="k-sclock-lab">
            <b>{stopping ? t("servers.overview.stoppingFirst") : t("servers.overview.elapsed")}</b>
            <span>{stopping ? t("servers.overview.stoppingHint")
              : v.over && last != null ? t("servers.overview.longer", { last: elapsed(last * 1000) })
              : v.left != null && last != null ? t("servers.overview.left", { left: elapsed(v.left * 1000), last: elapsed(last * 1000) })
              : t("servers.overview.noLast")}</span>
          </span>
        </div>
        {newest && <div className="k-slastline" title={newest}><span>{newest}</span></div>}
        <ServerConsole serverId={id} />
      </div>
      <aside className="k-sov-aside" aria-label={t("servers.overview.whileStarts")}>
        <div className="k-section-title">{t("servers.overview.whileStarts")}</div>
        <ul className="k-scan">
          <li><b>{t("servers.overview.can.watch")}</b><span>{t("servers.overview.can.watchHint")}</span></li>
          <li><b>{t("servers.overview.can.leave")}</b><span>{t("servers.overview.can.leaveHint")}</span></li>
          <li><b>{t("servers.overview.can.stop")}</b><span>{t("servers.overview.can.stopHint")}</span></li>
          <li className="no"><b>{t("servers.overview.can.join")}</b><span>{t("servers.overview.can.joinHint")}</span></li>
        </ul>
        <KeyValue rows={[
          s && [t("servers.overview.runBy"), runnerLabel(s.runner ?? "kilnctl")],
          s && [t("servers.overview.port"), <span className="k-mono">UDP {s.queryPort}</span>],
          (st.data?.installed || st.data?.kiln) && ["Kiln", <span className="k-mono">{st.data?.kiln ?? st.data?.installed}</span>],
        ]} />
      </aside>
    </div>
  );
}

function Steady({ id }: { id: string }) {
  const t = useT();
  const srv = useServer(id);
  const st = useServerStatus(id);
  const s = srv.data;
  const status = st.data;
  const running = status?.state === "running";
  const query = useQuery({ queryKey: sk.query(id), queryFn: () => api.serverQuery(id), enabled: running, refetchInterval: 15000, retry: false });
  const bundles = useQuery({ queryKey: ["runtime_list"], queryFn: api.runtimeList, staleTime: 60_000 });
  const mods = useQuery({ queryKey: sk.mods(id), queryFn: () => api.serverMods(id) as Promise<ServerModX[]>, retry: false, staleTime: 15_000 });
  const webPanel = running && webPanelMod(mods.data) !== null;
  const backups = useQuery({ queryKey: sk.backups(id), queryFn: () => api.serverBackups(id), retry: false, staleTime: 30_000 });
  const { ticks } = useTicks({ server: id });
  const showDeploy = useDeployDialog((d) => d.show);
  const flow = useServerFlow(id);
  const busy = flow?.state === "running" || status?.state === "stopping";
  const noKiln = !!status && !hasKiln(s, status);
  const remote = s?.runner === "remote" || s?.kind === "remote";
  const latest = status ? kilnUpdateFor(status.installed, bundles.data, remote) : null;
  const update = latest ? { latest: shortKiln(latest)!, current: shortKiln(status?.installed) ?? "" } : null;
  const recent = ticks.slice(-16);
  const last = recent[recent.length - 1];
  const players = status?.players ?? query.data?.players ?? null;
  const max = query.data?.maxPlayers ?? null;
  const lastStart = (status as { lastStartS?: number | null } | undefined)?.lastStartS ?? null;
  const base = `/servers/${encodeURIComponent(id)}`;
  const newestBackup = (backups.data ?? []).map((b) => Date.parse(b.created)).filter((n) => !Number.isNaN(n)).sort((a, b) => b - a)[0];
  return (
    <div className="k-sov">
      {noKiln && (
        <Banner tone="idle" title={t("servers.overview.noKiln")}
          actions={<Button size="s" disabled={busy} onClick={() => showDeploy(true)}>{t("servers.head.installKiln")}</Button>}>
          {status?.pid ? t("servers.activity.installStopsFirst") : null}
        </Banner>
      )}
      {update && !noKiln && (
        <Banner tone="warn" title={t("servers.overview.kilnUpdate")}
          actions={<Button size="s" variant="primary" disabled={busy} onClick={() => showDeploy(true)}>{t("servers.head.updateKiln")}</Button>}>
          {running ? t("servers.overview.kilnUpdateRunning", { latest: update.latest, current: update.current }) : t("servers.overview.kilnUpdateStopped", { latest: update.latest, current: update.current })}
        </Banner>
      )}
      {webPanel && <OpenWebPanel key={id} id={id} />}
      <dl className="k-sstats">
        <div className="k-sstat">
          <dt>{t("servers.overview.stat.players")}</dt>
          <dd>{running ? <OnlineCount fallback={players} /> : "-"}{max != null && <small> / {max}</small>}</dd>
          <p>{running ? <OnlineNames fallback={players ? t("servers.head.online", { count: players }) : t("servers.head.nobody")} /> : t("servers.overview.stat.notRunning")}</p>
        </div>
        <div className="k-sstat">
          <dt>{t("servers.overview.stat.health")}</dt>
          <dd>{last ? last.rate.toFixed(1) : "-"}<small> {t("servers.overview.stat.stepsPerS")}</small>{recent.length > 1 && <Sparkline values={recent.map((x) => x.rate)} width={90} height={24} aria-label={t("servers.overview.stat.health")} />}</dd>
          <p>{last ? t("servers.overview.stat.p99", { ms: last.step_ms.p99.toFixed(1) }) : t("servers.overview.stat.noTicks")}</p>
        </div>
        <div className="k-sstat">
          <dt>{t("servers.overview.stat.uptime")}</dt>
          <dd>{running && status?.uptimeS != null ? elapsed(status.uptimeS * 1000) : "-"}</dd>
          <p>{lastStart != null ? t("servers.overview.stat.startedIn", { time: elapsed(lastStart * 1000) }) : running ? t("servers.list.running") : t("servers.list.stopped")}</p>
        </div>
        <div className="k-sstat">
          <dt>{t("servers.overview.stat.versions")}</dt>
          <dd className="mono">{status?.kiln ?? status?.installed ?? t("servers.overview.stat.noKiln")}</dd>
          <p>{update ? <span className="k-tone-warn">{t("servers.overview.stat.isOut", { latest: update.latest })}</span> : null}{query.data?.version ? ` ${t("servers.overview.stat.game", { version: query.data.version })}` : ""}</p>
        </div>
      </dl>
      <nav className="k-slinks" aria-label={t("servers.overview.sections")}>
        <NavLink className="k-slink" to={`${base}/console`}><b>{t("shell.serverNav.console")}</b><span>{running ? t("servers.overview.link.consoleLive") : t("servers.overview.link.consoleLast")}</span></NavLink>
        <NavLink className="k-slink" to={`${base}/admin/players`}><b>{t("shell.serverNav.players")}</b><span>{players != null ? t("servers.head.online", { count: players }) : t("servers.overview.link.playersOff")}</span></NavLink>
        <NavLink className="k-slink" to={`${base}/mods`}><b>{t("shell.serverNav.mods")}</b><span>{mods.data ? t("servers.overview.link.mods", { count: mods.data.length }) : "…"}</span></NavLink>
        <NavLink className="k-slink" to={`${base}/backups`}><b>{t("shell.serverNav.backups")}</b><span>{backups.data ? (newestBackup ? t("servers.overview.link.backups", { count: backups.data.length, ago: fmtAgo(newestBackup) }) : t("servers.overview.link.noBackups")) : "…"}</span></NavLink>
        <NavLink className="k-slink" to={`${base}/settings`}><b>{t("shell.serverNav.settings")}</b><span>{s ? t("servers.overview.link.settings", { port: s.queryPort }) : "…"}</span></NavLink>
      </nav>
      <div className="k-sov-console">
        <ServerConsole serverId={id} />
      </div>
    </div>
  );
}

/** "Open web panel" (0.2.0-dev.31.12): opens the panel in the browser through the launcher's gateway for this server,
 *  then says where it is open and offers to close it (it also closes by itself after its last tab).
 *  (0.2.0-dev.31.19) When the panel's file names its own address on the internet and the launcher's check of it passes,
 *  it opens there instead (no gateway), with "Open on this computer instead" one click away; when the check fails, the
 *  status says why it opened on this computer. (0.2.0-dev.31.20) The server's "Always open the web panel on this
 *  computer" switch sits under the buttons (kept in the launcher's data folder): while it is on, the launcher opens the
 *  panel here and never checks the address on the internet. */
function OpenWebPanel({ id }: { id: string }) {
  const t = useT();
  const qc = useQueryClient();
  const panels = useQuery({ queryKey: WEB_PANELS, queryFn: api.adminWebPanels, refetchInterval: 15_000, retry: false });
  const open = webPanelOf(panels.data, id);
  const settings = useQuery({ queryKey: webPanelSettingsKey(id), queryFn: () => api.adminWebPanelSettings(id), retry: false });
  const always = alwaysLocal(settings.data);
  const [saving, setSaving] = useState(false);
  async function setAlways(on: boolean) {
    setSaving(true);
    try {
      qc.setQueryData(webPanelSettingsKey(id), await api.adminWebPanelSettingsSet(id, { ...settings.data, alwaysLocal: on }));
    } catch (e) {
      toast.error(e, t("servers.overview.webPanelSettingNotSaved"));
    } finally {
      setSaving(false);
    }
  }
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  // the last open's answer: where it went (the hosted address has no gateway to list) and why it fell back, if it did
  const [last, setLast] = useState<api.WebPanelInfo | null>(null);
  async function run(action: () => Promise<api.WebPanelInfo | boolean>) {
    setBusy(true);
    setError(undefined);
    try {
      const r = await action();
      setLast(typeof r === "object" ? r : null);
    } catch (e) {
      setError((e as { message?: string } | null)?.message ?? t("servers.overview.webPanelError"));
    } finally {
      setBusy(false);
      void qc.invalidateQueries({ queryKey: WEB_PANELS });
    }
  }
  const hosted = last?.via === "hosted" ? last : null;
  const shown = hosted ?? (open && { ...open, fallback: last && last.via !== "hosted" ? last.fallback : null });
  return <div className="k-sov-webpanel">
    <div className="k-sov-webpanel-actions">
      {/* while the switch is being saved an open waits: the backend reads the setting at every open */}
      <Button size="s" disabled={busy || saving} onClick={() => void run(() => api.adminOpenWebPanel(id))}>{t("servers.overview.openWebPanel")}</Button>
      {offerHere(hosted, always) && <Button size="s" variant="ghost" disabled={busy || saving} onClick={() => void run(() => api.adminOpenWebPanel(id, "local"))}>{t("servers.overview.openWebPanelHere")}</Button>}
      {open && <Button size="s" variant="ghost" disabled={busy} onClick={() => void run(() => api.adminCloseWebPanel(id))}>{t("servers.overview.closeWebPanel")}</Button>}
    </div>
    <Toggle checked={always} disabled={busy || saving || !settings.data} onChange={(on) => void setAlways(on)}
      label={t("servers.overview.webPanelAlwaysLocal")} hint={t("servers.overview.webPanelAlwaysLocalHint")} />
    {shown && <ul className="k-sov-webpanel-status" aria-label={t("servers.overview.openWebPanel")}>
      {webPanelLines(shown, t).map((line) => <li key={line}>{line}</li>)}
    </ul>}
    {error && <Banner tone="err" title={t("servers.overview.webPanelError")}>{error}</Banner>}
  </div>;
}
const WEB_PANELS = ["admin_web_panels"] as const;
const webPanelSettingsKey = (id: string) => ["admin_web_panel_settings", id] as const;

/** How many are online: Kiln.Admin's list when it is connected, else the status / query count. */
function OnlineCount({ fallback }: { fallback: number | null }) {
  if (!useInAdminProvider()) return <>{fallback ?? "-"}</>;
  return <AdminCount fallback={fallback} />;
}
function AdminCount({ fallback }: { fallback: number | null }) {
  const st = useAdminState();
  return <>{st.state?.players?.length ?? fallback ?? "-"}</>;
}

/** The names of who is online, from Kiln.Admin when it is connected. */
function OnlineNames({ fallback }: { fallback: string }) {
  if (!useInAdminProvider()) return <>{fallback}</>;
  return <AdminNames fallback={fallback} />;
}
function AdminNames({ fallback }: { fallback: string }) {
  const st = useAdminState();
  const names = (st.state?.players ?? []).map((p: { name?: string }) => p.name).filter(Boolean) as string[];
  return <>{names.length ? names.slice(0, 4).join(", ") + (names.length > 4 ? ` +${names.length - 4}` : "") : fallback}</>;
}
