// /servers/:id/*: one grouped list (Run, World, Data, Files) beside the server's name, its live sentence (state,
// players, the admin connection) and Start/Restart/Stop; then the page. Wraps the admin pages too
// (/servers/:id/admin/*). Server actions that need no room (update the game, install Kiln, reconnect, remove) sit in a
// labelled menu beside the buttons.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MoreHorizontal } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { NavLink, Outlet, useLocation, useNavigate, useParams } from "react-router-dom";
import { AdminProvider, reconnectAdmin, useAdminState } from "../../api/admin";
import * as api from "../../api/commands";
import type { AdminTransport } from "../../api/commands";
import { Button, ConfirmDanger, Field, Menu, Modal, Select, Skeleton, installNote, runTask, toast, type MenuItem } from "../../ui";
import { SERVER_NAV, serverNavFor } from "../../shell/serverNav";
import { notifyInstall } from "../../shell/notices";
import { ConfirmHost } from "../../admin/ui";
import { hasKiln, runnerLabel, sk, useDeployDialog, useServer, useServerEvents, useServers, useServerStatus } from "./serverApi";
import { rich, t, useT } from "../../i18n";

export default function ServerLayout() {
  const { id = "" } = useParams();
  useServerEvents();
  const st = useServerStatus(id);
  const live = st.data?.state === "running" || st.data?.state === "starting";
  const body = <ServerFrame id={id} live={live} />;
  // the shared admin session only while the server runs (no reconnect loop for a stopped one)
  return live ? <AdminProvider serverId={id}>{body}</AdminProvider> : body;
}

function ServerFrame({ id, live }: { id: string; live: boolean }) {
  const t = useT();
  const loc = useLocation();
  const rest = loc.pathname.replace(/^\/servers\/[^/]+\/?/, "");
  const cur = serverNavFor(rest);
  return (
    <div className="k-page k-page-fill k-server">
      <nav className="k-snav" aria-label={t("servers.head.navLabel")}>
        {SERVER_NAV.map((g) => (
          <div key={g.group} className="k-snav-group" role="group" aria-label={g.group}>
            <span className="k-snav-head">{g.group}</span>
            {g.items.map((i) => (
              <NavLink key={i.to} to={`/servers/${encodeURIComponent(id)}/${i.to}`} className={"k-sub" + (cur?.to === i.to ? " on" : "")} aria-current={cur?.to === i.to ? "page" : undefined}>
                <i aria-hidden />{i.label}
              </NavLink>
            ))}
          </div>
        ))}
      </nav>
      <div className="k-server-main">
        <ServerHead id={id} live={live} />
        <div className="k-server-page">
          <Outlet />
        </div>
      </div>
      <ConfirmHost />
    </div>
  );
}

function dur(s: number | null | undefined): string {
  if (s == null) return "";
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return h ? t("servers.head.durH", { h, m }) : m ? t("servers.head.durM", { m, s: sec }) : t("servers.head.durS", { s: sec });
}

/** "2 online, 2 slots free." / "Nobody online." (no leading space). */
function whoLine(n: number, max: number | null, free: number): string {
  const who = n === 0 ? t("servers.head.nobody") : t("servers.head.online", { count: n });
  if (max == null) return n === 0 ? t("servers.head.nobodyLine") : t("servers.head.onlineLine", { count: n });
  return t("servers.head.slotsFree", { who, count: free });
}

function ServerHead({ id, live }: { id: string; live: boolean }) {
  const t = useT();
  const qc = useQueryClient();
  const nav = useNavigate();
  const srv = useServer(id);
  const st = useServerStatus(id);
  const servers = useServers();
  const query = useQuery({ queryKey: sk.query(id), queryFn: () => api.serverQuery(id), enabled: st.data?.state === "running", refetchInterval: 15000, retry: false });
  const [busy, setBusy] = useState<string | null>(null);
  const { open: deploy, show: setDeploy } = useDeployDialog();
  const [removing, setRemoving] = useState(false);
  const [pick, setPick] = useState(false);
  const state = st.data?.state;
  const s = srv.data;
  const act = <R,>(what: string, f: () => Promise<string>, done: string, detail?: (r: R) => string | undefined, after?: (r: R) => void) => async () => {
    setBusy(what);
    try { after?.(await runTask<R>(f, { done, detail })); } catch { /* toasted */ } finally { setBusy(null); st.refetch(); }
  };
  // the dialog belongs to this server: leaving it closes the dialog
  useEffect(() => () => setDeploy(false), [id, setDeploy]);
  const kiln = hasKiln(s, st.data);
  const remote = s?.runner === "remote" || s?.kind === "remote";
  const players = st.data?.players ?? query.data?.players ?? null;
  const max = query.data?.maxPlayers ?? null;
  const who = players == null ? "" : ` ${whoLine(players, max, max != null ? max - players : 0)}`;
  let say: ReactNode;
  if (!st.data) say = t("servers.head.reading");
  else if (state === "running") say = <>{st.data.uptimeS != null ? t("servers.head.runningFor", { time: dur(st.data.uptimeS) }) : t("servers.head.running")}{live ? <LiveWho max={max} fallback={who} /> : who}{live && <Connection />}</>;
  else if (state === "starting") say = <>{t("servers.head.starting")}{live && <Connection />}</>;
  else if (state === "stopping") say = t("servers.head.stopping");
  else {
    const runner = s ? runnerLabel(s.runner ?? "kilnctl") : null;
    const kilnV = st.data.kiln;
    say = runner && kilnV ? t("servers.head.stoppedWithKiln", { runner, kiln: kilnV }) : runner ? t("servers.head.stoppedWith", { runner })
      : kilnV ? t("servers.head.stoppedKiln", { kiln: kilnV }) : t("servers.head.stopped");
  }
  const running = state === "running" || state === "starting";
  const items: MenuItem[] = [
    ...(s?.runner !== "steam" ? [{ label: t("servers.head.updateGame"), disabled: !!st.data?.pid || !!busy, onSelect: act("update", () => api.serverUpdate(id), t("servers.head.upToDate")) }] : []),
    ...(!remote ? [{ label: kiln ? t("servers.head.updateKilnMenu") : t("servers.head.installKilnMenu"), disabled: !!st.data?.pid || !!busy, onSelect: () => setDeploy(true) }] : []),
    ...(live ? transportItems(id) : []),
    { label: t("servers.head.removeMenu"), danger: true, disabled: !!st.data?.pid, onSelect: () => setRemoving(true) },
  ];
  return (
    <header className="k-page-head k-server-head">
      <div className="k-server-name" style={{ minWidth: 0 }}>
        <h1 className="k-page-title" style={{ fontSize: 52 }}>
          {s ? (
            <button type="button" className="k-name-btn" aria-haspopup="listbox" aria-expanded={pick} onClick={() => setPick((p) => !p)} aria-label={t("servers.head.changeServer", { name: s.name })}>
              {s.name}<svg viewBox="0 0 24 24" aria-hidden><path d="M5 9l7 7 7-7" /></svg>
            </button>
          ) : <Skeleton lines={1} width={260} height={46} />}
        </h1>
        {pick && <ServerPicker current={id} servers={servers.data ?? []} onClose={() => setPick(false)} onPick={(sid) => { setPick(false); nav(`/servers/${encodeURIComponent(sid)}/overview`); }} />}
        <p className="k-page-sub" style={{ marginTop: 1 }}>{say}</p>
      </div>
      <div className="k-page-actions" style={{ paddingTop: 8 }}>
        {running ? (
          <>
            <Button disabled={!!busy || state !== "running"} loading={busy === "restart"} onClick={act("restart", () => api.serverRestart(id), t("servers.head.restarted"))}>{t("servers.head.restart")}</Button>
            <Button variant="danger" disabled={!!busy || !st.data?.pid} loading={busy === "stop"} onClick={act("stop", () => api.serverStop(id), t("servers.head.stoppedToast"))}>{t("servers.head.stop")}</Button>
          </>
        ) : (
          <Button variant="primary" disabled={!!busy || !st.data} loading={busy === "start"} onClick={act("start", () => api.serverStart(id), t("servers.head.runningToast"))}>{t("servers.head.start")}</Button>
        )}
        <Menu label={t("servers.head.more")} icon={<MoreHorizontal />} items={items} />
      </div>
      <DeployModal open={deploy} update={kiln} onClose={() => setDeploy(false)}
        onDeploy={(v) => { setDeploy(false); act<api.InstallResult>("deploy", () => api.serverDeploy(id, v), t("servers.head.kilnInstalled", { version: v }), installNote, notifyInstall)(); }} />
      <ConfirmDanger open={removing} onClose={() => setRemoving(false)} title={t("servers.head.removeTitle")}
        message={t("servers.head.removeText")}
        word={id} action={t("common.remove")} onConfirm={async () => {
          try {
            await api.serverRemove(id, false);
            toast.ok(t("servers.head.removed", { name: s?.name ?? id }), t("servers.head.filesStay"));
            qc.invalidateQueries({ queryKey: sk.list });
            nav("/servers");
          } catch (e) { toast.error(e, t("servers.head.notRemoved")); throw e; }
        }} />
    </header>
  );
}

/** " 2 online, 2 slots free." from the admin session's state when it has one (inside an AdminProvider). */
function LiveWho({ max, fallback }: { max: number | null; fallback: string }) {
  useT();
  const st = useAdminState();
  const n = st.state?.players?.length;
  if (n == null) return <>{fallback}</>;
  return <>{` ${whoLine(n, max, max != null ? Math.max(0, max - n) : 0)}`}</>;
}

/** " Connected. KAP/1 TCP, 151 ops." (inside an AdminProvider). */
function Connection() {
  const t = useT();
  const st = useAdminState();
  const T: Record<string, string> = { tcp: "KAP/1 TCP", file: t("servers.head.fileBridge"), ssh: t("servers.overview.sshTunnel"), auto: t("servers.head.auto") };
  if (st.ready && st.session) return <> {rich("servers.head.connected", { connected: <span className="k-tone-ok">{t("servers.head.connectedWord")}</span>, transport: T[st.session.transport] ?? st.session.transport, ops: st.session.ops.length })}</>;
  if (st.connecting) return <> {t("servers.head.connecting")}</>;
  if (st.status === "error") return <> <span className="k-tone-warn">{t("servers.head.notAnswering")}</span></>;
  return null;
}

function transportItems(id: string): MenuItem[] {
  const T: [AdminTransport, string][] = [["auto", t("servers.head.transportAuto")], ["tcp", t("servers.head.transportTcp")], ["file", t("servers.overview.fileBridge")], ["ssh", t("servers.overview.sshTunnel")]];
  return [
    { label: t("servers.head.reconnect"), onSelect: () => reconnectAdmin(id) },
    ...T.map(([transport, l]) => ({ label: t("servers.head.connectOver", { transport: l }), onSelect: () => reconnectAdmin(id, transport) })),
  ];
}

function ServerPicker({ current, servers, onClose, onPick }: { current: string; servers: { id: string; name: string }[]; onClose: () => void; onPick: (id: string) => void }) {
  const t = useT();
  const ref = useRef<HTMLDivElement>(null);
  const nav = useNavigate();
  useEffect(() => {
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node) && !(e.target as HTMLElement).closest?.(".k-name-btn")) onClose(); };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    ref.current?.querySelector<HTMLElement>(".k-menu-item")?.focus();
    return () => { document.removeEventListener("mousedown", close); document.removeEventListener("keydown", esc); };
  }, [onClose]);
  return (
    <div ref={ref} className="k-popover" role="listbox" aria-label={t("servers.list.title")} style={{ left: 0, top: 60, width: 320 }}>
      {servers.map((s) => (
        <button key={s.id} type="button" role="option" aria-selected={s.id === current} className="k-menu-item" onClick={() => onPick(s.id)}>
          <span style={{ fontWeight: s.id === current ? 700 : 400 }}>{s.name}</span><span className="k-mono">{s.id}</span>
        </button>
      ))}
      <div className="k-menu-sep" />
      <button type="button" className="k-menu-item" style={{ color: "var(--k-ember-300)" }} onClick={() => { onClose(); nav("/servers"); }}>{t("servers.head.allServers")}</button>
    </div>
  );
}

function DeployModal({ open, update, onClose, onDeploy }: { open: boolean; update: boolean; onClose: () => void; onDeploy: (version: string) => void }) {
  const t = useT();
  const bundles = useQuery({ queryKey: ["runtime_list"], queryFn: api.runtimeList, enabled: open });
  const [v, setV] = useState("");
  const chosen = v || bundles.data?.[0]?.version || "";
  return (
    <Modal open={open} onClose={onClose} title={update ? t("servers.head.updateKiln") : t("servers.head.installKiln")} width={480}
      footer={<><Button variant="ghost" onClick={onClose}>{t("common.cancel")}</Button><Button variant="primary" disabled={!chosen} onClick={() => onDeploy(chosen)}>{update ? t("common.update") : t("common.install")}</Button></>}>
      <Field label={t("servers.head.bundle")} hint={t("servers.head.bundleHint")} htmlFor="deploy-v">
        <Select id="deploy-v" value={chosen} onChange={setV} options={(bundles.data ?? []).map((b) => ({ value: b.version, label: t("servers.head.bundleOption", { version: b.version, kiln: b.kiln }) }))} />
      </Field>
    </Modal>
  );
}
