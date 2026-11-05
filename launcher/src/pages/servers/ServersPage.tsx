// /servers: every dedicated server the launcher knows: kilnctl instances, Steam installs,
// Windows and remote servers. New server (SteamCMD install, optionally reflinked from another install) and import.
import { useQueryClient } from "@tanstack/react-query";
import { FolderInput, Plus, Server } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router-dom";
import * as api from "../../api/commands";
import {
  Badge, Button, Card, EmptyState, Field, Modal, NumberInput, Page, PageHeader, PathPicker, Select, Skeleton, StatusPill,
  TextInput, Toggle, errorText, runTask, toast,
} from "../../ui";
import { runnerLabel, sk, stateTone, useServerEvents, useServers, useServerStatus, type ServerInstanceX, type ServerSpecX } from "./serverApi";

/** Windows (WebView2): path examples in Windows form. */
const WIN = /Windows/i.test(navigator.userAgent);

function ServerCard({ s }: { s: ServerInstanceX }) {
  const st = useServerStatus(s.id);
  return (
    <Link to={`/servers/${s.id}/overview`} className="k-plain-link">
      <Card
        title={s.name}
        sub={<span className="k-mono k-ellipsis" title={s.id}>{s.id}</span>}
        actions={<StatusPill tone={stateTone(st.data?.state)} pulse={st.data?.state === "starting"}>{st.data?.state ?? "…"}</StatusPill>}
      >
        <div className="k-row" style={{ gap: 8, flexWrap: "wrap" }}>
          <Badge tone="info">{runnerLabel[s.runner ?? "kilnctl"]}</Badge>
          <Badge>UDP {s.queryPort}</Badge>
          {st.data?.players != null && <Badge tone="ok">{st.data.players} online</Badge>}
          {st.data?.kiln && <Badge tone="ember">Kiln {st.data.kiln}</Badge>}
          {(s.source as string) === "steam" && <Badge tone="warn" title="Found in a Steam library; not recorded yet">detected</Badge>}
        </div>
        <div className="k-hint k-mono k-ellipsis" style={{ marginTop: 8 }} title={s.dir ?? s.remote?.host}>{s.dir ?? s.remote?.host}</div>
      </Card>
    </Link>
  );
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
  const q = useServers();
  const [creating, setCreating] = useState(false);
  const [importing, setImporting] = useState(false);
  return (
    <Page wide>
      <PageHeader title="Servers" sub="Dedicated servers on this PC and over SSH"
        actions={<><Button icon={<FolderInput size={16} />} variant="secondary" onClick={() => setImporting(true)}>Import</Button>
          <Button icon={<Plus size={16} />} variant="primary" onClick={() => setCreating(true)}>New server</Button></>} />
      {q.isLoading ? <Skeleton lines={4} /> : q.error ? <EmptyState icon={<Server />} title="Servers unavailable" hint={errorText(q.error)} /> : !q.data?.length ? (
        <EmptyState icon={<Server />} title="No servers yet" hint="Install one with SteamCMD, or import a folder." action={<Button variant="primary" onClick={() => setCreating(true)}>New server</Button>} />
      ) : (
        <div className="k-grid" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(320px, 1fr))", gap: 16 }}>
          {q.data.map((s) => <ServerCard key={s.id} s={s} />)}
        </div>
      )}
      <NewServer open={creating} onClose={() => setCreating(false)} />
      <ImportServer open={importing} onClose={() => setImporting(false)} />
    </Page>
  );
}
