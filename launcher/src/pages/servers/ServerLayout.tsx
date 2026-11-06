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
import { ConfirmHost } from "../../admin/ui";
import { hasKiln, runnerLabel, sk, useDeployDialog, useServer, useServerEvents, useServers, useServerStatus } from "./serverApi";

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
  const loc = useLocation();
  const rest = loc.pathname.replace(/^\/servers\/[^/]+\/?/, "");
  const cur = serverNavFor(rest);
  return (
    <div className="k-page k-page-fill k-server">
      <nav className="k-snav" aria-label="Server">
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
  return h ? `${h} h ${m} min` : m ? `${m} min ${sec} s` : `${sec} s`;
}

function ServerHead({ id, live }: { id: string; live: boolean }) {
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
  const act = <R,>(what: string, f: () => Promise<string>, done: string, detail?: (r: R) => string | undefined) => async () => {
    setBusy(what);
    try { await runTask<R>(f, { done, detail }); } catch { /* toasted */ } finally { setBusy(null); st.refetch(); }
  };
  // the dialog belongs to this server: leaving it closes the dialog
  useEffect(() => () => setDeploy(false), [id, setDeploy]);
  const kiln = hasKiln(s, st.data);
  const remote = s?.runner === "remote" || s?.kind === "remote";
  const players = st.data?.players ?? query.data?.players ?? null;
  const max = query.data?.maxPlayers ?? null;
  const who = players == null ? "" : players === 0 ? "Nobody online" : `${players} online`;
  const slots = max != null && players != null ? `, ${max - players} slot${max - players === 1 ? "" : "s"} free` : "";
  let say: ReactNode;
  if (!st.data) say = "Reading the server's state.";
  else if (state === "running") say = <>Running{st.data.uptimeS != null ? ` for ${dur(st.data.uptimeS)}` : ""}.{live ? <LiveWho max={max} fallback={who ? ` ${who}${slots}.` : ""} /> : who ? ` ${who}${slots}.` : ""}{live && <Connection />}</>;
  else if (state === "starting") say = <>Starting.{live && <Connection />}</>;
  else if (state === "stopping") say = "Stopping.";
  else say = `Stopped. Start runs it ${s ? `with ${runnerLabel[s.runner ?? "kilnctl"]}` : ""}${st.data.kiln ? `, Kiln ${st.data.kiln}` : ""}.`;
  const running = state === "running" || state === "starting";
  const items: MenuItem[] = [
    ...(s?.runner !== "steam" ? [{ label: "Update the game (SteamCMD)", disabled: !!st.data?.pid || !!busy, onSelect: act("update", () => api.serverUpdate(id), "Server files are up to date") }] : []),
    ...(!remote ? [{ label: kiln ? "Update Kiln…" : "Install Kiln…", disabled: !!st.data?.pid || !!busy, onSelect: () => setDeploy(true) }] : []),
    ...(live ? transportItems(id) : []),
    { label: "Remove from the launcher", danger: true, disabled: !!st.data?.pid, onSelect: () => setRemoving(true) },
  ];
  return (
    <header className="k-page-head k-server-head">
      <div className="k-server-name" style={{ minWidth: 0 }}>
        <h1 className="k-page-title" style={{ fontSize: 52 }}>
          {s ? (
            <button type="button" className="k-name-btn" aria-haspopup="listbox" aria-expanded={pick} onClick={() => setPick((p) => !p)} aria-label={`Server: ${s.name}. Change server.`}>
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
            <Button disabled={!!busy || state !== "running"} loading={busy === "restart"} onClick={act("restart", () => api.serverRestart(id), "Server restarted")}>Restart</Button>
            <Button variant="danger" disabled={!!busy || !st.data?.pid} loading={busy === "stop"} onClick={act("stop", () => api.serverStop(id), "Server stopped")}>Stop</Button>
          </>
        ) : (
          <Button variant="primary" disabled={!!busy || !st.data} loading={busy === "start"} onClick={act("start", () => api.serverStart(id), "Server running")}>Start</Button>
        )}
        <Menu label="More server actions" icon={<MoreHorizontal />} items={items} />
      </div>
      <DeployModal open={deploy} update={kiln} onClose={() => setDeploy(false)}
        onDeploy={(v) => { setDeploy(false); act<api.InstallResult>("deploy", () => api.serverDeploy(id, v), `Kiln ${v} installed`, installNote)(); }} />
      <ConfirmDanger open={removing} onClose={() => setRemoving(false)} title="Remove this server?"
        message="The launcher forgets it. Its game files, saves and backups stay on disk."
        word={id} action="Remove" onConfirm={async () => {
          try {
            await api.serverRemove(id, false);
            toast.ok(`Removed ${s?.name ?? id}`, "The files stay.");
            qc.invalidateQueries({ queryKey: sk.list });
            nav("/servers");
          } catch (e) { toast.error(e, "Not removed"); throw e; }
        }} />
    </header>
  );
}

/** " 2 online, 2 slots free." from the admin session's state when it has one (inside an AdminProvider). */
function LiveWho({ max, fallback }: { max: number | null; fallback: string }) {
  const st = useAdminState();
  const n = st.state?.players?.length;
  if (n == null) return <>{fallback}</>;
  return <>{` ${n === 0 ? "Nobody online" : `${n} online`}${max != null ? `, ${Math.max(0, max - n)} slot${max - n === 1 ? "" : "s"} free` : ""}.`}</>;
}

/** " Connected. KAP/1 TCP, 151 ops." (inside an AdminProvider). */
function Connection() {
  const st = useAdminState();
  const T: Record<string, string> = { tcp: "KAP/1 TCP", file: "file bridge", ssh: "SSH tunnel", auto: "auto" };
  if (st.ready && st.session) return <> <span className="k-tone-ok">Connected.</span> {T[st.session.transport] ?? st.session.transport}, {st.session.ops.length} ops.</>;
  if (st.connecting) return <> Connecting to Kiln.Admin.</>;
  if (st.status === "error") return <> <span className="k-tone-warn">Kiln.Admin is not answering.</span></>;
  return null;
}

function transportItems(id: string): MenuItem[] {
  const T: [AdminTransport, string][] = [["auto", "Auto (TCP, then file bridge)"], ["tcp", "KAP/1 over TCP"], ["file", "File bridge"], ["ssh", "SSH tunnel"]];
  return [
    { label: "Reconnect to Kiln.Admin", onSelect: () => reconnectAdmin(id) },
    ...T.map(([t, l]) => ({ label: `Connect over: ${l}`, onSelect: () => reconnectAdmin(id, t) })),
  ];
}

function ServerPicker({ current, servers, onClose, onPick }: { current: string; servers: { id: string; name: string }[]; onClose: () => void; onPick: (id: string) => void }) {
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
    <div ref={ref} className="k-popover" role="listbox" aria-label="Servers" style={{ left: 0, top: 60, width: 320 }}>
      {servers.map((s) => (
        <button key={s.id} type="button" role="option" aria-selected={s.id === current} className="k-menu-item" onClick={() => onPick(s.id)}>
          <span style={{ fontWeight: s.id === current ? 700 : 400 }}>{s.name}</span><span className="k-mono">{s.id}</span>
        </button>
      ))}
      <div className="k-menu-sep" />
      <button type="button" className="k-menu-item" style={{ color: "var(--k-ember-300)" }} onClick={() => { onClose(); nav("/servers"); }}>All servers</button>
    </div>
  );
}

function DeployModal({ open, update, onClose, onDeploy }: { open: boolean; update: boolean; onClose: () => void; onDeploy: (version: string) => void }) {
  const bundles = useQuery({ queryKey: ["runtime_list"], queryFn: api.runtimeList, enabled: open });
  const [v, setV] = useState("");
  const chosen = v || bundles.data?.[0]?.version || "";
  return (
    <Modal open={open} onClose={onClose} title={update ? "Update Kiln" : "Install Kiln"} width={480}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!chosen} onClick={() => onDeploy(chosen)}>{update ? "Update" : "Install"}</Button></>}>
      <Field label="Runtime bundle" hint="The server must be stopped." htmlFor="deploy-v">
        <Select id="deploy-v" value={chosen} onChange={setV} options={(bundles.data ?? []).map((b) => ({ value: b.version, label: `${b.version} (Kiln ${b.kiln})` }))} />
      </Field>
    </Modal>
  );
}
