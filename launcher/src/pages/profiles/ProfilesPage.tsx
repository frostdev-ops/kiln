// /profiles: list, create, duplicate, delete.
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Copy, Layers, MoreHorizontal, Plus, Trash2, Zap } from "lucide-react";
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { ProfileSummary } from "../../api/commands";
import { invalidateGame, qk, useGame, useProfiles } from "../../api/hooks";
import { Badge, Button, Card, ConfirmDanger, EmptyState, Field, Menu, Modal, Page, PageHeader, Select, Skeleton, StatusPill, TextInput, errorText, fmtAgo, toast } from "../../ui";

export default function ProfilesPage() {
  const profiles = useProfiles();
  const { game } = useGame();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [create, setCreate] = useState<{ copyFrom: string | null } | null>(null);
  const [del, setDel] = useState<ProfileSummary | null>(null);
  const activate = useMutation({
    mutationFn: (pid: string) => api.profileActivate(pid, game!.id),
    onSuccess: () => { if (game) invalidateGame(qc, game.id); toast.ok("Profile active"); },
    onError: (e) => toast.error(e),
  });

  const list = profiles.data ?? [];
  return (
    <Page>
      <PageHeader title="Profiles" sub="Mod sets. One is active per game." actions={<Button variant="primary" icon={<Plus />} onClick={() => setCreate({ copyFrom: null })}>New profile</Button>} />
      <Card flush>
        {profiles.isLoading ? <div style={{ padding: 16 }}><Skeleton /></div> : profiles.error ? (
          <EmptyState title="Profiles unavailable" hint={errorText(profiles.error)} />
        ) : list.length === 0 ? (
          <EmptyState icon={<Layers />} title="No profiles" hint="Make one, then add mods from the library." action={<Button variant="primary" icon={<Plus />} onClick={() => setCreate({ copyFrom: null })}>New profile</Button>} />
        ) : (
          <div className="k-list" role="list">
            {list.map((p) => {
              const isActive = !!game && p.active.includes(game.id);
              return (
                <div key={p.id} role="listitem" className="k-list-item clickable" tabIndex={0} onClick={() => nav(`/profiles/${p.id}/mods`)}
                  onKeyDown={(e) => { if (e.key === "Enter") nav(`/profiles/${p.id}/mods`); }}>
                  <div className="k-stack" style={{ gap: 2, flex: 1, minWidth: 0 }}>
                    <div className="k-row" style={{ gap: 8 }}>
                      <span className="k-item-title">{p.name}</span>
                      {isActive && <StatusPill tone="ember">active</StatusPill>}
                      {p.server && <Badge tone="info">{p.server.name ?? p.server.id}</Badge>}
                    </div>
                    <span className="k-item-sub">
                      {(["kiln", "native", "emf", "plugin"] as const).map((k) => `${p.counts[k]} ${k}`).join(" · ")} · {fmtAgo(p.updated)}
                    </span>
                  </div>
                  <div onClick={(e) => e.stopPropagation()} className="k-row">
                    {game && !isActive && <Button size="s" icon={<Zap />} loading={activate.isPending && activate.variables === p.id} onClick={() => activate.mutate(p.id)}>Activate</Button>}
                    <Menu label="Profile actions" icon={<MoreHorizontal />} items={[
                      { label: "Duplicate", icon: <Copy />, onSelect: () => setCreate({ copyFrom: p.id }) },
                      { label: "Delete", icon: <Trash2 />, danger: true, onSelect: () => setDel(p), disabled: p.active.length > 0 },
                    ]} />
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </Card>
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
    <Modal open={open} onClose={onClose} title={base ? `Duplicate ${base.name}` : "New profile"}
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
