// The admin session when it is not there: why, how to connect, and Reconnect with the transport choice. While connected
// the session shows in the server's live sentence (pages/servers/ServerLayout.tsx), not here.
import { reconnectAdmin, useAdminState } from "../api/admin";
import type { AdminTransport } from "../api/commands";
import { Banner, Button, Select } from "./ui";

const TRANSPORTS: { value: AdminTransport; label: string }[] = [
  { value: "auto", label: "Auto (TCP, then file bridge)" },
  { value: "tcp", label: "KAP/1 over TCP" },
  { value: "file", label: "File bridge" },
  { value: "ssh", label: "SSH tunnel" },
];

/** Shown by pages while there is no session: why, and what the live tools need. */
export function OfflineNote() {
  const st = useAdminState();
  if (st.session) return null;
  if (st.status === "connecting" || st.status === "idle") return <span className="k-empty-title">Connecting to the server's Kiln.Admin…</span>;
  return (
    <Banner tone="warn" title="Not connected"
      actions={<>
        <Select aria-label="Transport" value={st.want} onChange={(v) => reconnectAdmin(st.serverId, v as AdminTransport)} options={TRANSPORTS} style={{ width: "auto", height: 32 }} />
        <Button size="s" onClick={() => reconnectAdmin(st.serverId)}>Reconnect</Button>
      </>}>
      {st.error?.message ?? "Kiln.Admin isn't answering."} The live tools need the server running with Kiln and its Kiln.Admin mod.
    </Banner>
  );
}
