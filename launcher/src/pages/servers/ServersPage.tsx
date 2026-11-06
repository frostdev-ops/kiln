// /servers: every dedicated server the launcher knows, one line each: kilnctl instances, Steam installs, Windows and
// remote servers. New server (SteamCMD install, optionally reflinked from another install) and import.
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import {
  Button, EmptyState, Field, Modal, NameId, NumberInput, Page, PageHeader, PathPicker, Select, Skeleton, Table,
  TextInput, Toggle, errorText, runTask, toast, type Column,
} from "../../ui";
import { runnerLabel, sk, useServerEvents, useServers, useServerStatus, type ServerInstanceX, type ServerSpecX } from "./serverApi";

/** Windows (WebView2): path examples in Windows form. */
const WIN = /Windows/i.test(navigator.userAgent);

function StateCell({ id }: { id: string }) {
  const st = useServerStatus(id);
  const state = st.data?.state;
  const tone = state === "running" ? "ok" : state === "starting" || state === "stopping" ? "warn" : "idle";
  return <span className={`k-badge k-tone-${tone}`}>{state ? state.charAt(0).toUpperCase() + state.slice(1) : "…"}</span>;
}
function PlayersCell({ id }: { id: string }) {
  const st = useServerStatus(id);
  return <span className="k-mono">{st.data?.players ?? "-"}</span>;
}
function KilnCell({ id }: { id: string }) {
  const st = useServerStatus(id);
  return <span className="k-mono k-muted">{st.data?.kiln ?? "-"}</span>;
}

const ID = /^[a-z0-9][a-z0-9_-]{0,47}$/;

function NewServer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const [spec, setSpec] = useState<ServerSpecX>({ id: "", name: "", dir: "", port: 15637, reflinkFrom: null, policy: "verified-only", skipSteamcmd: false });
  const [busy, setBusy] = useState(false);
  const idOk = ID.test(spec.id);
  const ok = idOk && spec.name.trim() && spec.dir.startsWith("/") && spec.port >= 1024;
  const create = async () => {
    setBusy(true);
    try {
      await runTask(() => api.serverCreate(spec), { done: `Server ${spec.name} created` });
      qc.invalidateQueries({ queryKey: sk.list });
      onClose();
    } catch {
      /* toasted */
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal open={open} onClose={onClose} locked={busy} title="New dedicated server" width={600}
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="primary" onClick={create} disabled={!ok} loading={busy}>Install</Button></>}>
      <div className="k-stack">
        <Field label="Id" hint="Lower-case letters, digits, - and _ (also the kilnctl instance name)." error={spec.id && !idOk ? "Not a valid id" : undefined}>
          <TextInput mono value={spec.id} onChange={(e) => setSpec({ ...spec, id: e.target.value })} placeholder="ember-isles" />
        </Field>
        <Field label="Name" hint="Shown in the server browser.">
          <TextInput value={spec.name} onChange={(e) => setSpec({ ...spec, name: e.target.value })} placeholder="Ember Isles" />
        </Field>
        <Field label="Folder" hint="An empty folder; SteamCMD installs the server here.">
          <PathPicker value={spec.dir} onChange={(dir: string) => setSpec({ ...spec, dir })} directory />
        </Field>
        <Field label="UDP port" hint="The query port players connect to.">
          <NumberInput integer min={1024} max={65535} value={spec.port} onCommit={(port) => setSpec({ ...spec, port })} />
        </Field>
        <Field label="Copy game files from" hint="Optional: an existing install on the same btrfs/XFS disk (reflink, no extra space). SteamCMD then only checks the files.">
          <PathPicker value={spec.reflinkFrom ?? ""} onChange={(p: string) => setSpec({ ...spec, reflinkFrom: p || null })} directory />
        </Field>
        <Field label="Kiln policy">
          <Select value={spec.policy ?? "verified-only"} onChange={(policy) => setSpec({ ...spec, policy })}
            options={[{ value: "verified-only", label: "verified-only: hooks on verified game builds only" }, { value: "any", label: "any: also unverified builds (labs)" }]} />
        </Field>
        <Toggle checked={!!spec.skipSteamcmd} onChange={(skipSteamcmd) => setSpec({ ...spec, skipSteamcmd })} label="Skip SteamCMD" hint="The copied files are up to date." />
      </div>
    </Modal>
  );
}

function ImportServer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const [dir, setDir] = useState("");
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const go = async () => {
    setBusy(true);
    try {
      const s = await api.serverImport(dir, name || "server");
      toast.ok(`Imported ${s.name}`);
      qc.invalidateQueries({ queryKey: sk.list });
      onClose();
    } catch (e) {
      toast.error(e, "Import failed");
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal open={open} onClose={onClose} title="Import a server folder"
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" onClick={go} disabled={!dir} loading={busy}>Import</Button></>}>
      <div className="k-stack">
        <Field label="Server folder" hint={`The folder with enshrouded_server.exe. A Steam library install is started through Steam; any other folder becomes ${WIN ? "a Windows server the launcher starts" : "a kilnctl instance"}.`}>
          <PathPicker value={dir} onChange={setDir} directory />
        </Field>
        <Field label="Name"><TextInput value={name} onChange={(e) => setName(e.target.value)} /></Field>
      </div>
    </Modal>
  );
}

export default function ServersPage() {
  useServerEvents();
  const nav = useNavigate();
  const q = useServers();
  const [creating, setCreating] = useState(false);
  const [importing, setImporting] = useState(false);
  const list = q.data ?? [];
  const cols: Column<ServerInstanceX>[] = [
    { key: "name", header: "Server", sort: (a, b) => a.name.localeCompare(b.name), render: (s) => <NameId name={s.name} id={s.id} /> },
    { key: "runner", header: "Runs as", width: "minmax(0, 150px)", render: (s) => <span className="k-muted" style={{ fontSize: 15.5 }}>{runnerLabel[s.runner ?? "kilnctl"]}{(s.source as string) === "steam" ? ", found in Steam" : ""}</span> },
    { key: "port", header: "Port", width: "90px", align: "right", render: (s) => <span className="k-mono">{s.queryPort}</span> },
    { key: "players", header: "Online", width: "64px", align: "right", render: (s) => <PlayersCell id={s.id} /> },
    { key: "kiln", header: "Kiln", width: "minmax(0, 150px)", render: (s) => <KilnCell id={s.id} /> },
    { key: "state", header: "State", width: "86px", render: (s) => <StateCell id={s.id} /> },
  ];
  const say = q.isLoading ? "Reading the servers." : q.error ? errorText(q.error) : !list.length ? "None yet. Install one with SteamCMD, or import a server folder." : `${list.length} dedicated server${list.length === 1 ? "" : "s"} on this PC and over SSH.`;
  return (
    <Page fill>
      <PageHeader title="Servers" sub={say}
        actions={<><Button onClick={() => setImporting(true)}>Import</Button><Button variant="primary" onClick={() => setCreating(true)}>New server</Button></>} />
      {q.isLoading ? <Skeleton lines={3} /> : q.error ? null : !list.length ? (
        <EmptyState page title="No servers" hint="Install one with SteamCMD, or import a folder." action={<Button onClick={() => setCreating(true)}>New server</Button>} />
      ) : (
        <Table aria-label="Servers" fill columns={cols} rows={list} rowKey={(s) => s.id} onRowClick={(s) => nav(`/servers/${encodeURIComponent(s.id)}/overview`)} />
      )}
      <NewServer open={creating} onClose={() => setCreating(false)} />
      <ImportServer open={importing} onClose={() => setImporting(false)} />
    </Page>
  );
}
