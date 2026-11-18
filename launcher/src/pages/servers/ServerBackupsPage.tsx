// /servers/:id/backups: world backups: Kiln.Saves backups (consistent, made through the
// admin op while the server runs) and kilnctl tarballs (savegame + kiln/data). Restore: at once when stopped (the
// current save moves aside first); while running, a Kiln.Saves backup is staged for the next start.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArchiveRestore } from "lucide-react";
import { useState } from "react";
import { useParams } from "react-router-dom";
import * as api from "../../api/commands";
import type { ServerBackup } from "../../api/commands";
import { Badge, Button, Card, ConfirmDanger, IconButton, Page, Table, fmtAgo, fmtBytes, fmtTime, runTask, type Column } from "../../ui";
import { sk, useServerStatus } from "./serverApi";
import { useT } from "../../i18n";

export default function ServerBackupsPage() {
  const t = useT();
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const st = useServerStatus(id);
  const q = useQuery({ queryKey: sk.backups(id), queryFn: () => api.serverBackups(id) });
  const [busy, setBusy] = useState(false);
  const [restore, setRestore] = useState<ServerBackup | null>(null);
  const running = !!st.data?.pid;
  const create = async () => {
    setBusy(true);
    try { await runTask(() => api.serverBackupCreate(id), { done: t("servers.backups.made") }); } catch { /* toasted */ } finally {
      setBusy(false);
      qc.invalidateQueries({ queryKey: sk.backups(id) });
    }
  };
  const doRestore = async () => {
    const b = restore!;
    setRestore(null);
    try {
      await runTask(() => api.serverBackupRestore(id, b.name), { done: running ? t("servers.backups.staged") : t("servers.backups.restored", { name: b.name }) });
    } catch { /* toasted */ }
    qc.invalidateQueries({ queryKey: sk.backups(id) });
  };
  const cols: Column<ServerBackup>[] = [
    { key: "name", header: t("servers.backups.backup"), render: (b) => <span className="k-mono">{b.name}</span> },
    { key: "source", header: t("servers.backups.kind"), width: "120px", render: (b) => (b.source === "kiln" ? <Badge tone="ember">Kiln.Saves</Badge> : <Badge>{t("servers.backups.tarball")}</Badge>) },
    { key: "created", header: t("servers.backups.madeCol"), width: "200px", render: (b) => <span title={fmtTime(b.created)}>{fmtAgo(b.created)}</span>, sort: (a, b) => a.created.localeCompare(b.created) },
    { key: "bytes", header: t("servers.backups.size"), width: "100px", align: "right", render: (b) => fmtBytes(b.bytes) },
    { key: "act", header: "", width: "56px", render: (b) => (
      <IconButton icon={<ArchiveRestore size={16} />} label={running && b.source !== "kiln" ? t("servers.backups.stopToRestore") : t("servers.backups.restore")}
        disabled={running && b.source !== "kiln"} onClick={() => setRestore(b)} />
    ) },
  ];
  return (
    <Page>
      <Card title={t("servers.backups.title")} sub={running ? t("servers.backups.subRunning") : t("servers.backups.subStopped")}
        actions={<Button loading={busy} onClick={create}>{t("servers.backups.now")}</Button>} flush>
        <Table columns={cols} rows={q.data ?? []} rowKey={(b) => b.source + b.name} empty={t("servers.backups.empty")} aria-label={t("servers.backups.title")} />
        <ConfirmDanger open={!!restore} onClose={() => setRestore(null)} title={t("servers.backups.restoreTitle")}
          message={running ? t("servers.backups.stageConfirm", { name: restore?.name }) : t("servers.backups.replaceConfirm", { name: restore?.name })}
          word={t("common.confirmWord.restore")} action={t("servers.backups.restore")} onConfirm={doRestore} />
      </Card>
    </Page>
  );
}
