// Save backups of a game: list, manual backup, restore (refused while the game runs), delete.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, MoreHorizontal, RotateCcw, Save, Trash2 } from "lucide-react";
import { useState } from "react";
import * as api from "../api/commands";
import type { SaveBackup } from "../api/commands";
import { qk, useGameStatus } from "../api/hooks";
import { Badge, Banner, Button, Card, ConfirmDanger, EmptyState, Field, Menu, Modal, Table, TextInput, fmtAgo, fmtBytes, fmtTime, errorText, toast, type Column } from "../ui";

const REASON: Record<SaveBackup["reason"], string> = { launch: "before launch", modpack: "modpack", manual: "manual", restore: "before restore" };

export function SaveBackups({ gameId, profileId, title = "Backups" }: { gameId: string; profileId?: string; title?: string }) {
  const qc = useQueryClient();
  const status = useGameStatus(gameId);
  const q = useQuery({ queryKey: qk.backups(gameId), queryFn: () => api.savesBackups(gameId) });
  const [restore, setRestore] = useState<SaveBackup | null>(null);
  const [del, setDel] = useState<SaveBackup | null>(null);
  const [making, setMaking] = useState(false);
  const running = !!status.data?.running;
  const rows = (q.data ?? []).filter((b) => !profileId || b.profileId === profileId);
  const refresh = () => qc.invalidateQueries({ queryKey: qk.backups(gameId) });

  const cols: Column<SaveBackup>[] = [
    { key: "created", header: "When", width: "minmax(0, 1.2fr)", sort: (a, b) => a.created.localeCompare(b.created), render: (b) => <span title={fmtTime(b.created)}>{fmtAgo(b.created)}</span> },
    { key: "reason", header: "Why", width: "minmax(0, 1fr)", render: (b) => <span className="k-row" style={{ gap: 6 }}><Badge tone={b.reason === "manual" ? "ember" : "idle"}>{REASON[b.reason]}</Badge>{b.note && <span className="k-muted k-ellipsis">{b.note}</span>}</span> },
    ...(profileId ? [] : [{ key: "profile", header: "Profile", width: "minmax(0, 1fr)", render: (b: SaveBackup) => <span className="k-mono k-muted">{b.profileId ?? "-"}</span> }]),
    { key: "size", header: "Size", width: "90px", align: "right", render: (b) => <span className="k-num">{fmtBytes(b.bytes)}</span> },
    { key: "act", header: "", width: "120px", align: "right", render: (b) => (
      <span className="k-row" style={{ justifyContent: "flex-end", gap: 4 }}>
        <Button size="s" icon={<RotateCcw />} disabled={running} title={running ? "Close the game first" : undefined} onClick={() => setRestore(b)}>Restore</Button>
        <Menu label="More" icon={<MoreHorizontal />} items={[{ label: "Delete", icon: <Trash2 />, danger: true, onSelect: () => setDel(b) }]} />
      </span>
    ) },
  ];
  return (
    <Card flush title={title} actions={<Button size="s" icon={<Save />} onClick={() => setMaking(true)}>Back up now</Button>}>
      {running && <div style={{ padding: "0 16px 12px" }}><Banner tone="warn">The game is running. Restore waits until it closes.</Banner></div>}
      {q.error ? <EmptyState title="Backups unavailable" hint={errorText(q.error)} /> : (
        <Table aria-label="Save backups" columns={cols} rows={rows} rowKey={(b) => b.id} rowHeight={46}
          empty={<EmptyState icon={<Archive />} title="No backups" hint="Kiln backs up before a launch with different mods." />} />
      )}
      <BackupNow open={making} onClose={() => setMaking(false)} gameId={gameId} onDone={refresh} />
      <ConfirmDanger open={!!restore} onClose={() => setRestore(null)} title="Restore this backup?" word="restore" action="Restore"
        message={<>Your current saves are backed up first. Steam Cloud may then ask which copy to keep: pick <b>local</b>.</>}
        onConfirm={async () => {
          try { await api.savesRestore(gameId, restore!.id); toast.ok("Restored"); refresh(); }
          catch (e) { toast.error(e); throw e; }
        }} />
      <ConfirmDanger open={!!del} onClose={() => setDel(null)} title="Delete this backup?" word="delete" action="Delete"
        onConfirm={async () => { try { await api.savesDelete(gameId, del!.id); refresh(); } catch (e) { toast.error(e); throw e; } }} />
    </Card>
  );
}

function BackupNow({ open, onClose, gameId, onDone }: { open: boolean; onClose: () => void; gameId: string; onDone: () => void }) {
  const [note, setNote] = useState("");
  const m = useMutation({
    mutationFn: () => api.savesBackup(gameId, note.trim() || null),
    onSuccess: () => { toast.ok("Backed up"); setNote(""); onDone(); onClose(); },
    onError: (e) => toast.error(e),
  });
  return (
    <Modal open={open} onClose={onClose} title="Back up saves"
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" loading={m.isPending} onClick={() => m.mutate()}>Back up</Button></>}>
      <Field label="Note" hint="Optional." htmlFor="bk-note">
        <TextInput id="bk-note" data-autofocus value={note} onChange={(e) => setNote(e.target.value)} placeholder="before the boss" onKeyDown={(e) => { if (e.key === "Enter") m.mutate(); }} />
      </Field>
    </Modal>
  );
}
