// /servers/:id/*: header with the server's state and start/stop/restart, the server tabs, then the page.
// Also wraps the admin pages (/servers/:id/admin/*).
import { Play, RotateCw, Square } from "lucide-react";
import { useState } from "react";
import { Outlet, useParams } from "react-router-dom";
import * as api from "../../api/commands";
import { Badge, Button, NavTabs, Page, PageHeader, Skeleton, StatusPill, runTask } from "../../ui";
import { runnerLabel, stateTone, useServer, useServerEvents, useServerStatus } from "./serverApi";

export default function ServerLayout() {
  const { id = "" } = useParams();
  useServerEvents();
  const srv = useServer(id);
  const st = useServerStatus(id);
  const [busy, setBusy] = useState<string | null>(null);
  const state = st.data?.state;
  const act = (what: string, f: () => Promise<string>, done: string) => async () => {
    setBusy(what);
    try {
      await runTask(f, { done });
    } catch {
      /* toasted */
    } finally {
      setBusy(null);
      st.refetch();
    }
  };
  const s = srv.data;
  return (
    <Page wide>
      <PageHeader
        title={s ? s.name : <Skeleton lines={1} width={200} />}
        sub={s && (
          <span className="k-row" style={{ gap: 8 }}>
            <StatusPill tone={stateTone(state)} pulse={state === "starting"}>{state ?? "…"}</StatusPill>
            <Badge tone="info">{runnerLabel[s.runner ?? "kilnctl"]}</Badge>
            <Badge>UDP {s.queryPort}</Badge>
            {st.data?.players != null && <Badge tone="ok">{st.data.players} online</Badge>}
          </span>
        )}
        actions={
          <>
            <Button icon={<Play size={16} />} variant="primary" disabled={!!busy || state === "running" || state === "starting"} loading={busy === "start"}
              onClick={act("start", () => api.serverStart(id), "Server running")}>Start</Button>
            <Button icon={<RotateCw size={16} />} variant="secondary" disabled={!!busy || state !== "running"} loading={busy === "restart"}
              onClick={act("restart", () => api.serverRestart(id), "Server restarted")}>Restart</Button>
            <Button icon={<Square size={16} />} variant="danger" disabled={!!busy || !st.data?.pid} loading={busy === "stop"}
              onClick={act("stop", () => api.serverStop(id), "Server stopped")}>Stop</Button>
          </>
        }
      />
      <NavTabs aria-label="Server" items={[
        { to: `/servers/${id}/overview`, label: "Overview" },
        { to: `/servers/${id}/settings`, label: "Settings" },
        { to: `/servers/${id}/mods`, label: "Mods" },
        { to: `/servers/${id}/configs`, label: "Configs" },
        { to: `/servers/${id}/backups`, label: "Backups" },
        { to: `/servers/${id}/logs`, label: "Console" },
        { to: `/servers/${id}/modpack`, label: "Modpack" },
        { to: `/servers/${id}/admin`, label: "Admin" },
      ]} />
      <div style={{ marginTop: 16 }}>
        <Outlet />
      </div>
    </Page>
  );
}
