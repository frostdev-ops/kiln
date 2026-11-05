// The admin session's status line: connection, transport, versions, state age, reconnect.
import { RefreshCw } from "lucide-react";
import { ADMIN_STALE_MS, reconnectAdmin, useAdminState } from "../api/admin";
import type { AdminTransport } from "../api/commands";
import { Banner, Button, Select, StatusPill, dur, useNow } from "./ui";

const TRANSPORTS: { value: AdminTransport; label: string }[] = [
  { value: "auto", label: "Auto (TCP, then file bridge)" },
  { value: "tcp", label: "KAP/1 over TCP" },
  { value: "file", label: "File bridge" },
  { value: "ssh", label: "SSH tunnel" },
];
const TRANSPORT_LABEL: Record<string, string> = { tcp: "KAP/1 TCP", file: "file bridge", ssh: "SSH tunnel", auto: "auto" };

export function ConnectionBar() {
  const st = useAdminState();
  const now = useNow(1000);
  const age = st.stateAt ? now - st.stateAt : null;
  const stale = st.ready && st.session?.topics.includes("state") && age != null && age > ADMIN_STALE_MS;
  const retryIn = st.retryAt ? Math.max(0, Math.ceil((st.retryAt - now) / 1000)) : null;
  const pill = st.ready ? (
    stale ? (
      <StatusPill tone="warn" title="The server stopped sending its state">No state for {dur(age! / 1000)}</StatusPill>
    ) : (
      <StatusPill tone="ok" pulse title={`state #${st.stateSeq}`}>Connected</StatusPill>
    )
  ) : st.status === "connecting" ? (
    <StatusPill tone="info" pulse>Connecting…</StatusPill>
  ) : st.status === "reconnecting" ? (
    <StatusPill tone="warn" pulse>Reconnecting…</StatusPill>
  ) : st.status === "error" ? (
    <StatusPill tone="err">Offline</StatusPill>
  ) : (
    <StatusPill tone="idle">Idle</StatusPill>
  );
  const s = st.session;
  const players = st.state?.players?.length;
  return (
    <div className="ka-connbar" data-status={st.status}>
      {pill}
      <span className="ka-connbar-name">{s?.server?.name ?? st.serverId}</span>
      {s ? (
        <span className="k-muted">
          {TRANSPORT_LABEL[s.transport] ?? s.transport} · Kiln {s.kiln ?? "?"} · {s.ops.length} ops
          {players != null ? ` · ${players} in the world` : ""}
          {st.gaps ? ` · ${st.gaps} gaps` : ""}
        </span>
      ) : st.error ? (
        <span className="k-muted ka-ellipsis" title={st.error.message}>
          {st.error.message}
          {retryIn != null ? ` Retrying in ${retryIn} s.` : ""}
        </span>
      ) : null}
      <span className="k-spacer" />
      <Select aria-label="Transport" value={st.want} onChange={(v) => reconnectAdmin(st.serverId, v as AdminTransport)} options={TRANSPORTS} className="ka-transport" />
      <Button size="s" variant="ghost" icon={<RefreshCw size={14} />} onClick={() => reconnectAdmin(st.serverId)}>Reconnect</Button>
    </div>
  );
}

/** Shown by pages while there is no session: why, and what the live tools need (a checklist). */
export function OfflineNote() {
  const st = useAdminState();
  if (st.session) return null;
  if (st.status === "connecting" || st.status === "idle") return <div className="k-muted">Connecting to the server's Kiln.Admin…</div>;
  return (
    <Banner tone="warn" title="Not connected to the server">
      <div className="k-stack">
        <span>{st.error?.message ?? "Kiln.Admin isn't answering."}</span>
        <span className="k-hint">
          The live tools need the server running with Kiln and its Kiln.Admin mod (kiln/mods/kiln-admin). The launcher talks to it
          over KAP/1 on 127.0.0.1 (kiln/data/kiln-admin/endpoint.json), or over the file bridge when TCP is off.
        </span>
      </div>
    </Banner>
  );
}
