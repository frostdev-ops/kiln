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
import { useT } from "../../i18n";

/** Windows (WebView2): path examples in Windows form. */
const WIN = /Windows/i.test(navigator.userAgent);

function StateCell({ id }: { id: string }) {
  const t = useT();
  const st = useServerStatus(id);
  const state = st.data?.state;
  const tone = state === "running" ? "ok" : state === "starting" || state === "stopping" ? "warn" : "idle";
  const word = state === "running" ? t("servers.list.running") : state === "starting" ? t("servers.list.starting") : state === "stopping" ? t("servers.list.stopping")
    : state === "stopped" ? t("servers.list.stopped") : state ? state.charAt(0).toUpperCase() + state.slice(1) : "…";
  return <span className={`k-badge k-tone-${tone}`}>{word}</span>;
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
  const t = useT();
  const qc = useQueryClient();
  const [spec, setSpec] = useState<ServerSpecX>({ id: "", name: "", dir: "", port: 15637, reflinkFrom: null, policy: "verified-only", skipSteamcmd: false });
  const [busy, setBusy] = useState(false);
  const idOk = ID.test(spec.id);
  const ok = idOk && spec.name.trim() && spec.dir.startsWith("/") && spec.port >= 1024;
  const create = async () => {
    setBusy(true);
    try {
      await runTask(() => api.serverCreate(spec), { done: t("servers.list.created", { name: spec.name }) });
      qc.invalidateQueries({ queryKey: sk.list });
      onClose();
    } catch {
      /* toasted */
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal open={open} onClose={onClose} locked={busy} title={t("servers.list.newTitle")} width={600}
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>{t("common.cancel")}</Button>
        <Button variant="primary" onClick={create} disabled={!ok} loading={busy}>{t("common.install")}</Button></>}>
      <div className="k-stack">
        <Field label={t("servers.list.id")} hint={t("servers.list.idHint")} error={spec.id && !idOk ? t("servers.list.idBad") : undefined}>
          <TextInput mono value={spec.id} onChange={(e) => setSpec({ ...spec, id: e.target.value })} placeholder="ember-isles" />
        </Field>
        <Field label={t("servers.list.name")} hint={t("servers.list.nameHint")}>
          <TextInput value={spec.name} onChange={(e) => setSpec({ ...spec, name: e.target.value })} placeholder={t("servers.list.namePlaceholder")} />
        </Field>
        <Field label={t("servers.list.folder")} hint={t("servers.list.folderHint")}>
          <PathPicker value={spec.dir} onChange={(dir: string) => setSpec({ ...spec, dir })} directory />
        </Field>
        <Field label={t("servers.list.port")} hint={t("servers.list.portHint")}>
          <NumberInput integer min={1024} max={65535} value={spec.port} onCommit={(port) => setSpec({ ...spec, port })} />
        </Field>
        <Field label={t("servers.list.copyFrom")} hint={t("servers.list.copyFromHint")}>
          <PathPicker value={spec.reflinkFrom ?? ""} onChange={(p: string) => setSpec({ ...spec, reflinkFrom: p || null })} directory />
        </Field>
        <Field label={t("servers.list.policy")}>
          <Select value={spec.policy ?? "verified-only"} onChange={(policy) => setSpec({ ...spec, policy })}
            options={[{ value: "verified-only", label: t("servers.list.policyVerified") }, { value: "any", label: t("servers.list.policyAny") }]} />
        </Field>
        <Toggle checked={!!spec.skipSteamcmd} onChange={(skipSteamcmd) => setSpec({ ...spec, skipSteamcmd })} label={t("servers.list.skipSteamcmd")} hint={t("servers.list.skipSteamcmdHint")} />
      </div>
    </Modal>
  );
}

function ImportServer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const t = useT();
  const qc = useQueryClient();
  const [dir, setDir] = useState("");
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const go = async () => {
    setBusy(true);
    try {
      const s = await api.serverImport(dir, name || "server");
      toast.ok(t("servers.list.imported", { name: s.name }));
      qc.invalidateQueries({ queryKey: sk.list });
      onClose();
    } catch (e) {
      toast.error(e, t("servers.list.importFailed"));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal open={open} onClose={onClose} title={t("servers.list.importTitle")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("common.cancel")}</Button><Button variant="primary" onClick={go} disabled={!dir} loading={busy}>{t("servers.list.import")}</Button></>}>
      <div className="k-stack">
        <Field label={t("servers.list.serverFolder")} hint={WIN ? t("servers.list.serverFolderHintWin") : t("servers.list.serverFolderHint")}>
          <PathPicker value={dir} onChange={setDir} directory />
        </Field>
        <Field label={t("servers.list.name")}><TextInput value={name} onChange={(e) => setName(e.target.value)} /></Field>
      </div>
    </Modal>
  );
}

export default function ServersPage() {
  const t = useT();
  useServerEvents();
  const nav = useNavigate();
  const q = useServers();
  const [creating, setCreating] = useState(false);
  const [importing, setImporting] = useState(false);
  const list = q.data ?? [];
  const cols: Column<ServerInstanceX>[] = [
    { key: "name", header: t("servers.list.server"), sort: (a, b) => a.name.localeCompare(b.name), render: (s) => <NameId name={s.name} id={s.id} /> },
    { key: "runner", header: t("servers.list.runsAs"), width: "minmax(0, 150px)", render: (s) => <span className="k-muted" style={{ fontSize: 15.5 }}>{(s.source as string) === "steam" ? t("servers.list.foundInSteam", { runner: runnerLabel(s.runner ?? "kilnctl") }) : runnerLabel(s.runner ?? "kilnctl")}</span> },
    { key: "port", header: t("servers.list.portCol"), width: "90px", align: "right", render: (s) => <span className="k-mono">{s.queryPort}</span> },
    { key: "players", header: t("servers.list.online"), width: "64px", align: "right", render: (s) => <PlayersCell id={s.id} /> },
    { key: "kiln", header: "Kiln", width: "minmax(0, 150px)", render: (s) => <KilnCell id={s.id} /> },
    { key: "state", header: t("servers.list.state"), width: "86px", render: (s) => <StateCell id={s.id} /> },
  ];
  const say = q.isLoading ? t("servers.list.reading") : q.error ? errorText(q.error) : !list.length ? t("servers.list.noneYet") : t("servers.list.count", { count: list.length });
  return (
    <Page fill>
      <PageHeader title={t("servers.list.title")} sub={say}
        actions={<><Button onClick={() => setImporting(true)}>{t("servers.list.import")}</Button><Button variant="primary" onClick={() => setCreating(true)}>{t("servers.list.newServer")}</Button></>} />
      {q.isLoading ? <Skeleton lines={3} /> : q.error ? null : !list.length ? (
        <EmptyState page title={t("servers.list.noServers")} hint={t("servers.list.noServersHint")} action={<Button onClick={() => setCreating(true)}>{t("servers.list.newServer")}</Button>} />
      ) : (
        <Table aria-label={t("servers.list.title")} fill columns={cols} rows={list} rowKey={(s) => s.id} onRowClick={(s) => nav(`/servers/${encodeURIComponent(s.id)}/overview`)} />
      )}
      <NewServer open={creating} onClose={() => setCreating(false)} />
      <ImportServer open={importing} onClose={() => setImporting(false)} />
    </Page>
  );
}
