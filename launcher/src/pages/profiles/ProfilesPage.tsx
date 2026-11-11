// /profiles: every profile, one line each: name and id, what it holds, the server it is for, when it changed; activate,
// duplicate, delete (typed).
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { MoreHorizontal } from "lucide-react";
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { ProfileSummary } from "../../api/commands";
import { invalidateGame, qk, showActive, useActiveProfile, useGame, useGameStatus, useProfiles } from "../../api/hooks";
import { ENGINE_KINDS, KIND_SHORT } from "../../api/kinds";
import { Button, ConfirmDanger, EmptyState, Field, Menu, Modal, NameId, Page, PageHeader, Select, Skeleton, Table, TextInput, errorText, fmtAgo, toast, type Column } from "../../ui";
import { notifyActivation } from "../../shell/notices";

export default function ProfilesPage() {
  const profiles = useProfiles();
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const status = useGameStatus(game?.id);
  const nav = useNavigate();
  const qc = useQueryClient();
  const [create, setCreate] = useState<{ copyFrom: string | null } | null>(null);
  const [del, setDel] = useState<ProfileSummary | null>(null);
  const activate = useMutation({
    mutationFn: (pid: string) => api.profileActivate(pid, game!.id),
    onMutate: (pid) => (game ? showActive(qc, game.id, pid) : undefined),
    onSuccess: (a) => { if (game) invalidateGame(qc, game.id); toast.ok("Profile active", "Steam's Play button uses it too."); notifyActivation(a); },
    onError: (e, _pid, undo) => { undo?.(); toast.error(e); },
  });
  const running = !!status.data?.running;

  const list = profiles.data ?? [];
  const cur = list.find((p) => p.id === active.data?.profileId);
  const cols: Column<ProfileSummary>[] = [
    { key: "name", header: "Profile", sort: (a, b) => a.name.localeCompare(b.name), render: (p) => <NameId name={p.name} id={p.id} /> },
    { key: "mods", header: "Mods", width: "minmax(0, 220px)", render: (p) => (
      <span className="k-muted" style={{ fontSize: 15.5 }}>{([...(["kiln", "native", "emf", "plugin"] as const), ...ENGINE_KINDS]).filter((k) => p.counts[k]).map((k) => `${p.counts[k]} ${k === "emf" ? "EMF" : ENGINE_KINDS.includes(k) ? KIND_SHORT[k] : k}`).join(", ") || "None"}</span>
    ) },
    { key: "server", header: "For", width: "minmax(0, 150px)", render: (p) => (p.server ? <span className="k-tone-info">{p.server.name ?? p.server.id}</span> : <span className="k-muted">Any</span>) },
    { key: "when", header: "Changed", width: "110px", sort: (a, b) => a.updated.localeCompare(b.updated), render: (p) => <span className="k-muted" style={{ fontSize: 15.5 }}>{fmtAgo(p.updated)}</span> },
    { key: "state", header: <span className="k-sr-only">State</span>, width: "120px", align: "right", render: (p) => (
      game && p.active.includes(game.id) ? <span className="k-pill k-tone-gold">Active</span>
        : game ? <Button size="s" disabled={running} loading={activate.isPending && activate.variables === p.id} onClick={(e) => { e.stopPropagation(); activate.mutate(p.id); }}>Activate</Button> : null
    ) },
    { key: "act", header: <span className="k-sr-only">Actions</span>, width: "32px", align: "right", render: (p) => (
      <span onClick={(e) => e.stopPropagation()}>
        <Menu label={`More for ${p.name}`} icon={<MoreHorizontal />} size="s" items={[
          { label: "Open", onSelect: () => nav(`/profiles/${p.id}/mods`) },
          { label: "Duplicate", onSelect: () => setCreate({ copyFrom: p.id }) },
          { label: "Delete", danger: true, onSelect: () => setDel(p), disabled: p.active.length > 0 },
        ]} />
      </span>
    ) },
  ];
  const say = profiles.isLoading ? "Reading profiles." : profiles.error ? errorText(profiles.error)
    : !list.length ? "No profiles yet. A profile is a set of mods; one is active per game."
    : `${list.length} profile${list.length === 1 ? "" : "s"}. ${cur ? `${cur.name} is active.` : "None is active: the game loads its drop-in folders."}`;
  return (
    <Page fill>
      <PageHeader title="Profiles" sub={say} actions={<Button variant="primary" onClick={() => setCreate({ copyFrom: null })}>New profile</Button>} />
      {profiles.isLoading ? <Skeleton lines={4} /> : profiles.error ? null : !list.length ? (
        <EmptyState page title="No profiles" hint="Make one, then add mods from the library." action={<Button onClick={() => setCreate({ copyFrom: null })}>New profile</Button>} />
      ) : (
        <Table aria-label="Profiles" fill columns={cols} rows={list} rowKey={(p) => p.id} onRowClick={(p) => nav(`/profiles/${p.id}/mods`)} />
      )}
      <CreateProfileModal open={!!create} copyFrom={create?.copyFrom ?? null} profiles={list} onClose={() => setCreate(null)}
        onCreated={(id) => { qc.invalidateQueries({ queryKey: qk.profiles }); nav(`/profiles/${id}/mods`); }} />
      <ConfirmDanger open={!!del} onClose={() => setDel(null)} title={`Delete ${del?.name ?? ""}?`} message="Its configs go to backups. Library mods stay." word={del?.id ?? ""}
        action="Delete" onConfirm={async () => {
          try { await api.profileDelete(del!.id); toast.ok("Profile deleted"); qc.invalidateQueries({ queryKey: qk.profiles }); }
          catch (e) { toast.error(e); throw e; }
        }} />
    </Page>
  );
}

export function CreateProfileModal({ open, copyFrom, profiles, onClose, onCreated }: {
  open: boolean; copyFrom: string | null; profiles: ProfileSummary[]; onClose: () => void; onCreated: (id: string) => void;
}) {
  const [name, setName] = useState("");
  const [from, setFrom] = useState<string>("");
  const src = copyFrom ?? (from || null);
  const create = useMutation({
    mutationFn: () => api.profileCreate(name.trim(), src),
    onSuccess: (p) => { toast.ok(`${p.name} created`); setName(""); setFrom(""); onClose(); onCreated(p.id); },
    onError: (e) => toast.error(e),
  });
  const base = profiles.find((p) => p.id === copyFrom);
  return (
    <Modal open={open} onClose={onClose} title={base ? `Duplicate ${base.name}` : "New profile"} width={480}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!name.trim()} loading={create.isPending} onClick={() => create.mutate()}>Create</Button></>}>
      <Field label="Name" htmlFor="pf-name">
        <TextInput id="pf-name" data-autofocus value={name} onChange={(e) => setName(e.target.value)} placeholder={base ? `${base.name} copy` : "Ember run"}
          onKeyDown={(e) => { if (e.key === "Enter" && name.trim()) create.mutate(); }} />
      </Field>
      {!copyFrom && profiles.length > 0 && (
        <Field label="Start from" htmlFor="pf-from">
          <Select id="pf-from" value={from} onChange={setFrom} options={[{ value: "", label: "Empty" }, ...profiles.map((p) => ({ value: p.id, label: p.name }))]} />
        </Field>
      )}
    </Modal>
  );
}
