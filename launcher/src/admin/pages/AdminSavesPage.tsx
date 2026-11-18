// /servers/:id/admin/saves: kiln.saves: status, "save now" (the game's own save), Kiln world
// backups (the world file with the mods' data for it), and a restore staged for the next start (backup_restore /
// backup_cancel_restore). Server-folder backups while the server is stopped are the Servers → Backups page's.
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { invalidateAdmin, useAdminCall, useAdminQuery, useAdminState } from "../../api/admin";
import { serverRestart } from "../../api/commands";
import { rich, t as tr, useT } from "../../i18n";
import { BACKUP_NAME, NeedsOp, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { BackupInfo, SaveStatus } from "../types";
import { Badge, Banner, Button, Card, EmptyState, KeyValue, Page, PageHeader, Table, TextInput, confirm, fmtAgo, fmtBytes, fmtTime, toastError, toastOk } from "../ui";

/** save_status dataMatch: the badge tone and what it means (unknown values: idle, the value itself). */
function matchInfo(dm: string): ["ok" | "warn" | "idle", string] {
  switch (dm) {
    case "Exact": return ["ok", tr("admin.saves.matchExact")];
    case "None": return ["idle", tr("admin.saves.matchNone")];
    case "Older": return ["warn", tr("admin.saves.matchOlder")];
    case "Newer": return ["warn", tr("admin.saves.matchNewer")];
    default: return ["idle", dm];
  }
}

function SavesPage() {
  const t = useT();
  const id = useServerId();
  const qc = useQueryClient();
  const st = useAdminState();
  const status = useAdminQuery<SaveStatus>("save_status", {}, { refetchInterval: 5000 });
  const backups = useAdminQuery<BackupInfo[]>("backup_list", {}, { refetchInterval: 15_000 });
  const [name, setName] = useState("before-changes");
  const refresh = () => invalidateAdmin(qc, id, ["save_status", "backup_list"]);
  const save = useAdminCall<{ revision: number; ms: number }>("save_now", { onSuccess: (r) => { toastOk(t("admin.saves.saved", { revision: r.revision, ms: r.ms })); refresh(); }, onError: (e) => toastError(e) });
  const backup = useAdminCall<BackupInfo>("backup_create", { onSuccess: (r) => { toastOk(t("admin.saves.backedUp", { folder: r.folder }), t("admin.saves.revisionDetail", { revision: r.revision })); refresh(); }, onError: (e) => toastError(e) });
  const restore = useAdminCall<{ staged: BackupInfo }>("backup_restore", { onSuccess: (r) => { toastOk(t("admin.saves.restoreStaged", { folder: r.staged.folder }), t("admin.saves.restoreStagedDetail")); refresh(); }, onError: (e) => toastError(e) });
  const cancel = useAdminCall<{ canceled: boolean }>("backup_cancel_restore", { onSuccess: (r) => { toastOk(r.canceled ? t("admin.saves.restoreCanceled") : t("admin.saves.nothingStaged")); refresh(); }, onError: (e) => toastError(e) });
  const s = status.data;
  const pending = s?.pendingRestore ?? null;
  const n = st.state?.players?.length ?? 0;
  const m = matchInfo(s?.dataMatch ?? "");
  const make = () => {
    const v = name.trim() || "launcher";
    if (!BACKUP_NAME.test(v)) return toastError({ message: t("admin.saves.nameRule") });
    backup.mutate({ name: v });
  };
  const stage = async (b: BackupInfo) => {
    const ok = await confirm({
      title: t("admin.saves.restoreTitle"), danger: true, ok: t("admin.saves.stageRestore"), typed: "restore",
      body: (
        <div className="k-stack">
          <p>{rich("admin.saves.restoreBody", { next: <b>{t("admin.saves.restoreNext")}</b>, folder: <b>{b.folder}</b>, revision: b.revision ?? "?", time: fmtTime(b.created) })}</p>
          <p>{n
            ? rich("admin.saves.restoreLostPlayers", { lost: <b>{t("admin.saves.restoreLost")}</b>, count: n, path: <span className="k-mono">kiln/backups/_before-restore-…</span> })
            : rich("admin.saves.restoreLostBody", { lost: <b>{t("admin.saves.restoreLost")}</b>, path: <span className="k-mono">kiln/backups/_before-restore-…</span> })}</p>
          <p>{t("admin.saves.restoreCancelHint")}</p>
        </div>
      ),
    });
    if (ok) restore.mutate({ name: b.folder || b.name });
  };
  const restart = async () => {
    if (!(await confirm({ title: t("admin.saves.restartTitle"), body: <p>{n ? t("admin.saves.restartOnline", { count: n }) : ""}{pending ? t("admin.saves.restartBodyRestore", { name: pending }) : t("admin.saves.restartBody")}</p>, ok: t("admin.saves.restart"), danger: true }))) return;
    try {
      await serverRestart(id);
      toastOk(t("admin.saves.restarting"));
    } catch (e) {
      toastError(e, t("admin.saves.restartFailed"));
    }
  };
  const rows = [...(backups.data ?? [])].reverse();
  return (
    <Page>
      <PageHeader title={t("admin.saves.title")} sub={t("admin.saves.sub")}
        actions={<Button variant="primary" loading={save.isPending} disabled={!st.session} onClick={() => save.mutate({})} title={t("admin.saves.saveNowTip")}>{t("admin.saves.saveNow")}</Button>} />
      <OfflineNote />
      <NeedsOp ops={["save_status", "save_now", "backup_list", "backup_create", "backup_restore"]} what={t("admin.saves.what")}>
        {pending ? (
          <Banner tone="warn" title={t("admin.saves.stagedTitle", { name: pending })} actions={<><Button size="s" loading={cancel.isPending} onClick={() => cancel.mutate({})}>{t("admin.saves.cancelRestore")}</Button><Button size="s" variant="danger" onClick={restart}>{t("admin.saves.restartNow")}</Button></>}>
            {t("admin.saves.stagedBody")}
          </Banner>
        ) : null}
        {s ? (
          <Card title={t("admin.saves.worldSave")}>
            <KeyValue rows={[
              [t("admin.saves.kvWorld"), <>{rich("admin.saves.worldValue", { world: <span className="k-mono">{String(s.world ?? "-").slice(0, 13)}</span>, revision: <b>{s.revision ?? "-"}</b>, state: s.state ?? "" })}</>],
              [t("admin.saves.kvLastSaved"), <>{fmtAgo(s.lastSaved)} <span className="k-muted">{t("admin.saves.lastSavedDetail", { reason: String(s.lastReason ?? "").toLowerCase(), seconds: Math.round(s.autosaveSeconds ?? 0) })}</span></>],
              [t("admin.saves.kvModData"), <><Badge tone={m[0]}>{s.dataMatch ?? "?"}</Badge> {m[1]} <span className="k-muted">{t("admin.saves.generation", { generation: s.dataGeneration ?? "-" })}</span></>],
              [t("admin.saves.kvSession"), <>{t("admin.saves.written", { written: s.written ?? 0 })}{s.failed ? <b style={{ color: "var(--k-err)" }}>{t("admin.saves.failed", { failed: s.failed })}</b> : null}</>],
            ]} />
          </Card>
        ) : status.error ? <Banner tone="warn">{status.error.message}</Banner> : null}
        <Card title={t("admin.saves.backups")} sub={t("admin.saves.backupsSub")}
          actions={<span className="ka-inline"><TextInput aria-label={t("admin.saves.backupName")} maxLength={64} value={name} onChange={(e) => setName(e.target.value)} onKeyDown={(e) => e.key === "Enter" && make()} style={{ width: 200 }} /><Button loading={backup.isPending} onClick={make}>{t("admin.saves.backUp")}</Button></span>} flush>
          <Table
            aria-label={t("admin.saves.backupsTable")}
            rows={rows}
            rowKey={(b) => b.folder || b.name}
            rowHeight={56}
            columns={[
              { key: "w", header: t("admin.saves.colWhen"), width: "190px", render: (b) => <div><div>{fmtTime(b.created)}</div><div className="k-hint">{fmtAgo(b.created)}</div></div>, sort: (a, b) => Date.parse(a.created) - Date.parse(b.created) },
              { key: "n", header: t("admin.saves.colBackup"), render: (b) => <div><Badge tone={b.reason === "Backup" ? "ember" : "idle"}>{b.name}</Badge>{pending === b.folder ? <> <Badge tone="warn">{t("admin.saves.staged")}</Badge></> : null}<div className="k-mono k-muted">{b.folder}</div></div> },
              { key: "r", header: t("admin.saves.colRevision"), width: "90px", align: "right", render: (b) => b.revision ?? "?" },
              { key: "s", header: t("admin.saves.colSize"), width: "150px", align: "right", render: (b) => <span>{b.dataBundles ? t("admin.saves.sizeData", { bytes: fmtBytes(b.bytes), files: b.files, data: b.dataBundles }) : t("admin.saves.size", { bytes: fmtBytes(b.bytes), files: b.files })}</span> },
              { key: "a", header: "", width: "190px", align: "right", render: (b) => <Button size="s" disabled={pending === b.folder || restore.isPending} onClick={() => stage(b)}>{t("admin.saves.restoreAtNext")}</Button> },
            ]}
            empty={<EmptyState title={t("admin.saves.empty")} hint={t("admin.saves.emptyHint")} />}
          />
        </Card>
      </NeedsOp>
    </Page>
  );
}

export default withAdmin(SavesPage);
