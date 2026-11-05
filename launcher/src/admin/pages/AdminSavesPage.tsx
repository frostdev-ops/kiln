// /servers/:id/admin/saves: kiln.saves: status, "save now" (the game's own save), Kiln world
// backups (the world file with the mods' data for it), and a restore staged for the next start (backup_restore /
// backup_cancel_restore). Server-folder backups while the server is stopped are the Servers → Backups page's.
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { invalidateAdmin, useAdminCall, useAdminQuery, useAdminState } from "../../api/admin";
import { serverRestart } from "../../api/commands";
import { BACKUP_NAME, NeedsOp, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { BackupInfo, SaveStatus } from "../types";
import { Badge, Banner, Button, Card, EmptyState, KeyValue, Page, PageHeader, Table, TextInput, confirm, fmtAgo, fmtBytes, fmtTime, toastError, toastOk } from "../ui";

const MATCH: Record<string, ["ok" | "warn" | "idle", string]> = {
  Exact: ["ok", "mod data matches the world file"], None: ["idle", "no mod data yet"],
  Older: ["warn", "mod data is older than the world file"], Newer: ["warn", "mod data is newer than the world file"],
};

function SavesPage() {
  const id = useServerId();
  const qc = useQueryClient();
  const st = useAdminState();
  const status = useAdminQuery<SaveStatus>("save_status", {}, { refetchInterval: 5000 });
  const backups = useAdminQuery<BackupInfo[]>("backup_list", {}, { refetchInterval: 15_000 });
  const [name, setName] = useState("before-changes");
  const refresh = () => invalidateAdmin(qc, id, ["save_status", "backup_list"]);
  const save = useAdminCall<{ revision: number; ms: number }>("save_now", { onSuccess: (r) => { toastOk(`World saved (revision ${r.revision}, ${r.ms} ms)`); refresh(); }, onError: (e) => toastError(e) });
  const backup = useAdminCall<BackupInfo>("backup_create", { onSuccess: (r) => { toastOk(`Backed up: ${r.folder}`, `revision ${r.revision}`); refresh(); }, onError: (e) => toastError(e) });
  const restore = useAdminCall<{ staged: BackupInfo }>("backup_restore", { onSuccess: (r) => { toastOk(`Restore of ${r.staged.folder} staged`, "It applies at the next server start."); refresh(); }, onError: (e) => toastError(e) });
  const cancel = useAdminCall<{ canceled: boolean }>("backup_cancel_restore", { onSuccess: (r) => { toastOk(r.canceled ? "Staged restore canceled" : "Nothing was staged"); refresh(); }, onError: (e) => toastError(e) });
  const s = status.data;
  const pending = s?.pendingRestore ?? null;
  const n = st.state?.players?.length ?? 0;
  const m = MATCH[s?.dataMatch ?? ""] ?? ["idle", s?.dataMatch ?? ""];
  const make = () => {
    const v = name.trim() || "launcher";
    if (!BACKUP_NAME.test(v)) return toastError({ message: "Backup names: letters, digits, - _ . (up to 64), not starting with a dot." });
    backup.mutate({ name: v });
  };
  const stage = async (b: BackupInfo) => {
    const ok = await confirm({
      title: "Restore this world at the next start?", danger: true, ok: "Stage the restore", typed: "restore",
      body: (
        <div className="k-stack">
          <p>Nothing changes now. The <b>next time the server starts</b>, Kiln replaces the world with <b>{b.folder}</b> (revision {b.revision ?? "?"}, {fmtTime(b.created)}) before the game opens it.</p>
          <p><b>Everything built, mined or found since then is lost</b>{n ? `, including what the ${n} player${n > 1 ? "s" : ""} online now are doing` : ""}. The files it replaces are kept in <span className="k-mono">kiln/backups/_before-restore-…</span>.</p>
          <p>Until then you can cancel it here. Restart the server to restore now.</p>
        </div>
      ),
    });
    if (ok) restore.mutate({ name: b.folder || b.name });
  };
  const restart = async () => {
    if (!(await confirm({ title: "Restart the server?", body: <p>{n ? `${n} player${n > 1 ? "s are" : " is"} online and will be disconnected. ` : ""}It saves, shuts down and starts again{pending ? `, restoring ${pending}` : ""}.</p>, ok: "Restart", danger: true }))) return;
    try {
      await serverRestart(id);
      toastOk("Restarting the server");
    } catch (e) {
      toastError(e, "Couldn't restart from here");
    }
  };
  const rows = [...(backups.data ?? [])].reverse();
  return (
    <Page>
      <PageHeader title="Saves" sub="The world file together with the mod data that belongs to it (kiln.saves)."
        actions={<Button variant="primary" loading={save.isPending} disabled={!st.session} onClick={() => save.mutate({})} title="Ask the game to save the world now">Save now</Button>} />
      <OfflineNote />
      <NeedsOp ops={["save_status", "save_now", "backup_list", "backup_create", "backup_restore"]} what="Kiln saves">
        {pending ? (
          <Banner tone="warn" title={`A restore is staged: ${pending}`} actions={<><Button size="s" loading={cancel.isPending} onClick={() => cancel.mutate({})}>Cancel restore</Button><Button size="s" variant="danger" onClick={restart}>Restart now</Button></>}>
            Nothing has changed yet. At the next server start Kiln puts that world back before the game opens it; the files it replaces are kept in kiln/backups/_before-restore-….
          </Banner>
        ) : null}
        {s ? (
          <Card title="World save">
            <KeyValue rows={[
              ["World", <><span className="k-mono">{String(s.world ?? "-").slice(0, 13)}</span> · revision <b>{s.revision ?? "-"}</b> · {s.state ?? ""}</>],
              ["Last saved", <>{fmtAgo(s.lastSaved)} <span className="k-muted">({String(s.lastReason ?? "").toLowerCase()}; the game saves every {Math.round(s.autosaveSeconds ?? 0)} s)</span></>],
              ["Mod data", <><Badge tone={m[0]}>{s.dataMatch ?? "?"}</Badge> {m[1]} <span className="k-muted">· generation {s.dataGeneration ?? "-"}</span></>],
              ["This session", <>{s.written ?? 0} saves written{s.failed ? <b style={{ color: "var(--k-err)" }}>, {s.failed} failed</b> : null}</>],
            ]} />
          </Card>
        ) : status.error ? <Banner tone="warn">{status.error.message}</Banner> : null}
        <Card title="Kiln backups" sub="kiln/backups: saves first, then copies the world file and its mod data"
          actions={<span className="ka-inline"><TextInput aria-label="Backup name" maxLength={64} value={name} onChange={(e) => setName(e.target.value)} onKeyDown={(e) => e.key === "Enter" && make()} style={{ width: 200 }} /><Button loading={backup.isPending} onClick={make}>Back up</Button></span>} flush>
          <Table
            aria-label="Backups"
            rows={rows}
            rowKey={(b) => b.folder || b.name}
            rowHeight={56}
            columns={[
              { key: "w", header: "When", width: "190px", render: (b) => <div><div>{fmtTime(b.created)}</div><div className="k-hint">{fmtAgo(b.created)}</div></div>, sort: (a, b) => Date.parse(a.created) - Date.parse(b.created) },
              { key: "n", header: "Backup", render: (b) => <div><Badge tone={b.reason === "Backup" ? "ember" : "idle"}>{b.name}</Badge>{pending === b.folder ? <> <Badge tone="warn">staged</Badge></> : null}<div className="k-mono k-muted">{b.folder}</div></div> },
              { key: "r", header: "Revision", width: "90px", align: "right", render: (b) => b.revision ?? "?" },
              { key: "s", header: "Size", width: "150px", align: "right", render: (b) => <span>{fmtBytes(b.bytes)} · {b.files} files{b.dataBundles ? ` · ${b.dataBundles} data` : ""}</span> },
              { key: "a", header: "", width: "190px", align: "right", render: (b) => <Button size="s" disabled={pending === b.folder || restore.isPending} onClick={() => stage(b)}>Restore at next start…</Button> },
            ]}
            empty={<EmptyState title="No Kiln backups yet" hint="Back up now, before trying something risky." />}
          />
        </Card>
      </NeedsOp>
    </Page>
  );
}

export default withAdmin(SavesPage);
