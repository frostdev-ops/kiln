// The admin session when it is not there: why, how to connect, and Reconnect with the transport choice. While connected
// the session shows in the server's live sentence (pages/servers/ServerLayout.tsx), not here.
import { reconnectAdmin, useAdminState } from "../api/admin";
import type { AdminTransport } from "../api/commands";
import { t as tr, useT } from "../i18n";
import { Banner, Button, Select } from "./ui";

const transports = (): { value: AdminTransport; label: string }[] => [
  { value: "auto", label: tr("admin.connection.transportAuto") },
  { value: "tcp", label: tr("admin.connection.transportTcp") },
  { value: "file", label: tr("admin.connection.transportFile") },
  { value: "ssh", label: tr("admin.connection.transportSsh") },
];

/** Shown by pages while there is no session: why, and what the live tools need. */
export function OfflineNote() {
  const t = useT();
  const st = useAdminState();
  if (st.session) return null;
  if (st.status === "connecting" || st.status === "idle") return <span className="k-empty-title">{t("admin.connection.connecting")}</span>;
  return (
    <Banner tone="warn" title={t("admin.connection.notConnected")}
      actions={<>
        <Select aria-label={t("admin.connection.transport")} value={st.want} onChange={(v) => reconnectAdmin(st.serverId, v as AdminTransport)} options={transports()} style={{ width: "auto", height: 32 }} />
        <Button size="s" onClick={() => reconnectAdmin(st.serverId)}>{t("admin.connection.reconnect")}</Button>
      </>}>
      {t("admin.connection.offline", { reason: st.error?.message ?? t("admin.connection.notAnswering") })}
    </Banner>
  );
}
