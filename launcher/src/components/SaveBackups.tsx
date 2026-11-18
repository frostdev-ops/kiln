// Save backups of a game: list, manual backup, restore (refused while the game runs), delete.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, MoreHorizontal, RotateCcw, Save, Trash2 } from "lucide-react";
import { useState } from "react";
import * as api from "../api/commands";
import type { SaveBackup } from "../api/commands";
import { qk, useGameStatus } from "../api/hooks";
import { rich, t, useT } from "../i18n";
import { Badge, Banner, Button, Card, ConfirmDanger, EmptyState, Field, Menu, Modal, Table, TextInput, fmtAgo, fmtBytes, fmtTime, errorText, toast, type Column } from "../ui";

/** Why a backup was made, in words. */
function reasonText(reason: SaveBackup["reason"]): string {
  switch (reason) {
    case "launch": return t("saveBackups.reason.launch");
    case "modpack": return t("saveBackups.reason.modpack");
    case "manual": return t("saveBackups.reason.manual");
    case "restore": return t("saveBackups.reason.restore");
    default: return String(reason);
  }
}

export function SaveBackups({ gameId, profileId, title }: { gameId: string; profileId?: string; title?: string }) {
  const t = useT();
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
    { key: "created", header: t("saveBackups.when"), width: "minmax(0, 1.2fr)", sort: (a, b) => a.created.localeCompare(b.created), render: (b) => <span title={fmtTime(b.created)}>{fmtAgo(b.created)}</span> },
    { key: "reason", header: t("saveBackups.why"), width: "minmax(0, 1fr)", render: (b) => <span className="k-row" style={{ gap: 6 }}><Badge tone={b.reason === "manual" ? "ember" : "idle"}>{reasonText(b.reason)}</Badge>{b.note && <span className="k-muted k-ellipsis">{b.note}</span>}</span> },
    ...(profileId ? [] : [{ key: "profile", header: t("saveBackups.profile"), width: "minmax(0, 1fr)", render: (b: SaveBackup) => <span className="k-mono k-muted">{b.profileId ?? "-"}</span> }]),
    { key: "size", header: t("saveBackups.size"), width: "90px", align: "right", render: (b) => <span className="k-num">{fmtBytes(b.bytes)}</span> },
    { key: "act", header: "", width: "120px", align: "right", render: (b) => (
      <span className="k-row" style={{ justifyContent: "flex-end", gap: 4 }}>
        <Button size="s" icon={<RotateCcw />} disabled={running} title={running ? t("saveBackups.closeGame") : undefined} onClick={() => setRestore(b)}>{t("saveBackups.restore")}</Button>
        <Menu label={t("saveBackups.more")} icon={<MoreHorizontal />} items={[{ label: t("common.delete"), icon: <Trash2 />, danger: true, onSelect: () => setDel(b) }]} />
      </span>
    ) },
  ];
  return (
    <Card flush title={title ?? t("saveBackups.title")} actions={<Button size="s" icon={<Save />} onClick={() => setMaking(true)}>{t("saveBackups.backUpNow")}</Button>}>
      {running && <div style={{ padding: "0 16px 12px" }}><Banner tone="warn">{t("saveBackups.running")}</Banner></div>}
      {q.error ? <EmptyState title={t("saveBackups.unavailable")} hint={errorText(q.error)} /> : (
        <Table aria-label={t("saveBackups.label")} columns={cols} rows={rows} rowKey={(b) => b.id} rowHeight={46}
          empty={<EmptyState icon={<Archive />} title={t("saveBackups.none")} hint={t("saveBackups.noneHint")} />} />
      )}
      <BackupNow open={making} onClose={() => setMaking(false)} gameId={gameId} onDone={refresh} />
      <ConfirmDanger open={!!restore} onClose={() => setRestore(null)} title={t("saveBackups.restoreAsk")} word={t("common.confirmWord.restore")} action={t("saveBackups.restore")}
        message={rich("saveBackups.restoreMessage", { local: <b>{t("saveBackups.local")}</b> })}
        onConfirm={async () => {
          try { await api.savesRestore(gameId, restore!.id); toast.ok(t("saveBackups.restored")); refresh(); }
          catch (e) { toast.error(e); throw e; }
        }} />
      <ConfirmDanger open={!!del} onClose={() => setDel(null)} title={t("saveBackups.deleteAsk")} word={t("common.confirmWord.delete")} action={t("common.delete")}
        onConfirm={async () => { try { await api.savesDelete(gameId, del!.id); refresh(); } catch (e) { toast.error(e); throw e; } }} />
    </Card>
  );
}

function BackupNow({ open, onClose, gameId, onDone }: { open: boolean; onClose: () => void; gameId: string; onDone: () => void }) {
  const t = useT();
  const [note, setNote] = useState("");
  const m = useMutation({
    mutationFn: () => api.savesBackup(gameId, note.trim() || null),
    onSuccess: () => { toast.ok(t("saveBackups.backedUp")); setNote(""); onDone(); onClose(); },
    onError: (e) => toast.error(e),
  });
  return (
    <Modal open={open} onClose={onClose} title={t("saveBackups.backUpTitle")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("common.cancel")}</Button><Button variant="primary" loading={m.isPending} onClick={() => m.mutate()}>{t("saveBackups.backUp")}</Button></>}>
      <Field label={t("saveBackups.note")} hint={t("saveBackups.optional")} htmlFor="bk-note">
        <TextInput id="bk-note" data-autofocus value={note} onChange={(e) => setNote(e.target.value)} placeholder={t("saveBackups.notePlaceholder")} onKeyDown={(e) => { if (e.key === "Enter") m.mutate(); }} />
      </Field>
    </Modal>
  );
}
