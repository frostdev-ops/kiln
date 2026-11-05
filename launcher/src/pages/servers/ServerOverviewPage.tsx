// /servers/:id/overview: state, process, Kiln, the admin endpoint, the A2S answer, and the install actions
// (SteamCMD update, Kiln runtime deploy, remove).
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, PackageCheck, Trash2 } from "lucide-react";
import { useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import * as api from "../../api/commands";
import { Banner, Button, Card, ConfirmDanger, KeyValue, Select, fmtAgo, runTask, toast } from "../../ui";
import { runnerLabel, sk, useServer, useServerStatus } from "./serverApi";

function dur(s: number | null | undefined) {
  if (s == null) return "-";
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
  return h ? `${h} h ${m} min` : `${m} min ${s % 60} s`;
}

export default function ServerOverviewPage() {
  const { id = "" } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const srv = useServer(id);
  const st = useServerStatus(id);
  const query = useQuery({ queryKey: sk.query(id), queryFn: () => api.serverQuery(id), enabled: st.data?.state === "running", refetchInterval: 15000, retry: false });
  const bundles = useQuery({ queryKey: ["runtime_list"], queryFn: api.runtimeList });
  const [version, setVersion] = useState("");
  const [removing, setRemoving] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const s = srv.data;
  const status = st.data;
  const running = !!status?.pid;
  const run = (what: string, f: () => Promise<string>, done: string) => async () => {
    setBusy(what);
    try { await runTask(f, { done }); } catch { /* toasted */ } finally { setBusy(null); st.refetch(); }
  };
  const remove = async () => {
    try {
      await api.serverRemove(id, false);
      toast.ok(`Removed ${s?.name ?? id} (the files stay)`);
      qc.invalidateQueries({ queryKey: sk.list });
      nav("/servers");
    } catch (e) {
      toast.error(e, "Remove failed");
    }
  };
  return (
    <div className="k-grid" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(380px, 1fr))", gap: 16 }}>
      <Card title="Status">
        <KeyValue rows={[
          ["State", status?.state ?? "…"],
          ["Process", status?.pid ? `pid ${status.pid}, up ${dur(status.uptimeS)}` : "not running"],
          ["Players", status?.players ?? (query.data ? query.data.players : "-")],
          ["Kiln", status?.kiln ?? (running ? "not loaded (vanilla, or still booting)" : "-")],
          status?.boot && ["Kiln boot", `${status.boot.side}, ${status.boot.safeIdle ? "safe idle" : status.boot.hooks ? "hooks on" : "hooks off"}, policy ${status.boot.policy}`],
          ["Admin (KAP/1)", status?.endpoint ? `127.0.0.1:${status.endpoint.port} since ${fmtAgo(status.endpoint.started)}` : running ? "no endpoint (Kiln.Admin off or older)" : "-"],
        ]} />
      </Card>
      <Card title="Server">
        <KeyValue rows={s ? [
          ["Id", <span className="k-mono">{s.id}</span>],
          ["Runs as", runnerLabel[s.runner ?? "kilnctl"]],
          ["Folder", <span className="k-mono k-ellipsis" title={s.dir ?? `${s.remote?.host}:${s.remote?.dir}`}>{s.dir ?? `${s.remote?.host}:${s.remote?.dir}`}</span>],
          s.prefix && ["Wine prefix", <span className="k-mono k-ellipsis" title={s.prefix}>{s.prefix}</span>],
          s.wine && ["Wine", <span className="k-mono">{s.wine}</span>],
          ["Kiln policy", s.policy],
          ["UDP port", s.queryPort],
          s.kilnctl && ["kilnctl", <span className="k-mono">kilnctl status {s.kilnctl}</span>],
        ] : []} />
      </Card>
      <Card title="Server browser (A2S)">
        {st.data?.state !== "running" ? <div className="k-hint">The server answers once it runs.</div> : query.error ? (
          <Banner tone="warn" title="No answer">The query port did not answer (firewall, or still loading).</Banner>
        ) : (
          <KeyValue rows={query.data ? [
            ["Name", query.data.name], ["Players", `${query.data.players} / ${query.data.maxPlayers}`],
            ["Game version", query.data.version ?? "-"], ["Ping", query.data.pingMs != null ? `${query.data.pingMs} ms` : "-"],
          ] : []} />
        )}
      </Card>
      <Card title="Install">
        <div className="k-stack">
          {s?.runner === "steam" ? (
            <div className="k-hint">Steam installs and updates this server. Kiln goes in through the Runtime install.</div>
          ) : (
            <Button icon={<Download size={16} />} variant="secondary" disabled={running || !!busy} loading={busy === "update"}
              onClick={run("update", () => api.serverUpdate(id), "Server files are up to date")}>Update the game (SteamCMD)</Button>
          )}
          <div className="k-row" style={{ gap: 8 }}>
            <Select value={version} onChange={setVersion} options={[{ value: "", label: "Kiln runtime…" }, ...(bundles.data ?? []).map((b) => ({ value: b.version, label: `${b.version} (${b.kiln})` }))]} />
            <Button icon={<PackageCheck size={16} />} variant="primary" disabled={!version || running || !!busy} loading={busy === "deploy"}
              onClick={run("deploy", () => api.serverDeploy(id, version), `Kiln ${version} installed`)}>Install Kiln</Button>
          </div>
          {running && <div className="k-hint">Stop the server to update it or install Kiln.</div>}
          <Button icon={<Trash2 size={16} />} variant="ghost" disabled={running} onClick={() => setRemoving(true)}>Remove from the launcher</Button>
        </div>
      </Card>
      <ConfirmDanger open={removing} onClose={() => setRemoving(false)} title="Remove server"
        message="The launcher forgets this server (its kilnctl instance file or record). The game files, saves and backups stay on disk."
        word={id} action="Remove" onConfirm={remove} />
    </div>
  );
}
