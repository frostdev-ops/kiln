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

export default function ServerBackupsPage() {
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const st = useServerStatus(id);
  const q = useQuery({ queryKey: sk.backups(id), queryFn: () => api.serverBackups(id) });
  const [busy, setBusy] = useState(false);
  const [restore, setRestore] = useState<ServerBackup | null>(null);
  const running = !!st.data?.pid;
  const create = async () => {
    setBusy(true);
    try { await runTask(() => api.serverBackupCreate(id), { done: "Backup made" }); } catch { /* toasted */ } finally {
      setBusy(false);
      qc.invalidateQueries({ queryKey: sk.backups(id) });
    }
  };
  const doRestore = async () => {
    const b = restore!;
    setRestore(null);
    try {
      await runTask(() => api.serverBackupRestore(id, b.name), { done: running ? "Staged: restart the server to restore it" : `Restored ${b.name}` });
    } catch { /* toasted */ }
    qc.invalidateQueries({ queryKey: sk.backups(id) });
  };
  const cols: Column<ServerBackup>[] = [
    { key: "name", header: "Backup", render: (b) => <span className="k-mono">{b.name}</span> },
    { key: "source", header: "Kind", width: "120px", render: (b) => (b.source === "kiln" ? <Badge tone="ember">Kiln.Saves</Badge> : <Badge>tarball</Badge>) },
    { key: "created", header: "Made", width: "200px", render: (b) => <span title={fmtTime(b.created)}>{fmtAgo(b.created)}</span>, sort: (a, b) => a.created.localeCompare(b.created) },
    { key: "bytes", header: "Size", width: "100px", align: "right", render: (b) => fmtBytes(b.bytes) },
    { key: "act", header: "", width: "56px", render: (b) => (
      <IconButton icon={<ArchiveRestore size={16} />} label={running && b.source !== "kiln" ? "Stop the server to restore a tarball" : "Restore"}
        disabled={running && b.source !== "kiln"} onClick={() => setRestore(b)} />
    ) },
  ];
  return (
    <Page>
      <Card title="Backups" sub={running ? "A backup now is made right after a fresh save." : "The server is stopped: a backup is a tarball of the save."}
        actions={<Button loading={busy} onClick={create}>Back up now</Button>} flush>
        <Table columns={cols} rows={q.data ?? []} rowKey={(b) => b.source + b.name} empty="No backups yet" aria-label="Backups" />
        <ConfirmDanger open={!!restore} onClose={() => setRestore(null)} title="Restore backup"
          message={running ? `Stage ${restore?.name} for the next start? Restart the server to apply it.` : `Replace the world with ${restore?.name}? The current save moves to the backup folder first.`}
          word="restore" action="Restore" onConfirm={doRestore} />
      </Card>
    </Page>
  );
}
