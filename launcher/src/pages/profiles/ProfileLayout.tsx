// /profiles/:id/* frame: name, activation state, settings, the tabs.
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Settings2, Zap } from "lucide-react";
import { useState } from "react";
import { Link, Outlet } from "react-router-dom";
import * as api from "../../api/commands";
import type { ModKind } from "../../api/commands";
import { invalidateGame, useActiveProfile, useGame, useGameStatus } from "../../api/hooks";
import { Banner, Button, EmptyState, Field, Modal, NavTabs, Page, PageHeader, Segmented, Skeleton, StatusPill, TextArea, TextInput, Toggle, errorText, toast } from "../../ui";
import { useProfileEdit, useProfileId } from "./useProfileEdit";

export default function ProfileLayout() {
  const id = useProfileId();
  const qc = useQueryClient();
  const { profile, query, update } = useProfileEdit(id);
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const status = useGameStatus(game?.id);
  const [edit, setEdit] = useState(false);
  const isActive = !!game && active.data?.profileId === id;
  const stale = isActive && profile && active.data && Date.parse(profile.updated) > Date.parse(active.data.activated);
  const activate = useMutation({
    mutationFn: () => api.profileActivate(id, game!.id),
    onSuccess: () => { invalidateGame(qc, game!.id); toast.ok(stale ? "Applied" : "Profile active"); },
    onError: (e) => toast.error(e),
  });

  if (query.isLoading) return <Page><Skeleton lines={5} /></Page>;
  if (!profile) return <Page><EmptyState title="Profile not found" hint={query.error ? errorText(query.error) : id} action={<Link to="/profiles"><Button>All profiles</Button></Link>} /></Page>;
  const n = (k: ModKind) => profile.mods[k].length;
  return (
    <Page>
      <div className="k-row"><Link to="/profiles" className="k-link" style={{ fontSize: 12.5, display: "inline-flex", gap: 4, alignItems: "center" }}><ArrowLeft size={13} />Profiles</Link></div>
      <PageHeader title={profile.name}
        sub={<span className="k-row" style={{ gap: 8 }}>
          <span className="k-mono">{profile.id}</span>
          {isActive && <StatusPill tone="ember">active</StatusPill>}
          {profile.server && <span>· for {profile.server.name ?? profile.server.id}</span>}
        </span>}
        actions={<>
          <Button icon={<Settings2 />} onClick={() => setEdit(true)}>Settings</Button>
          {game && (!isActive || stale) && (
            <Button variant="primary" icon={<Zap />} loading={activate.isPending} disabled={!!status.data?.running} title={status.data?.running ? "Close the game first" : undefined}
              onClick={() => activate.mutate()}>{stale ? "Apply" : "Activate"}</Button>
          )}
        </>} />
      {stale && <Banner tone="warn" title="Changed since activation">Apply to write it to the game.</Banner>}
      <NavTabs aria-label="Profile" items={[
        { to: "mods", label: "Mods", count: n("kiln") },
        { to: "native", label: "Native", count: n("native") + n("plugin") },
        { to: "emf", label: "EMF", count: n("emf") },
        { to: "configs", label: "Configs" },
        { to: "saves", label: "Saves" },
      ]} />
      <Outlet />
      <ProfileSettingsModal open={edit} onClose={() => setEdit(false)} name={profile.name} notes={profile.notes} dropins={profile.dropins} dataDir={profile.dataDir}
        onSave={(v) => { update((p) => { p.name = v.name; p.notes = v.notes; p.dropins = v.dropins; p.dataDir = v.dataDir; }); setEdit(false); }} />
    </Page>
  );
}

const DROPIN_KINDS: { kind: ModKind; label: string; folder: string }[] = [
  { kind: "kiln", label: "Kiln mods", folder: "kiln/mods" },
  { kind: "native", label: "Native mods", folder: "kiln/native" },
  { kind: "plugin", label: "Plugins", folder: "kiln/plugins" },
  { kind: "emf", label: "EMF mods", folder: "emf/mods" },
];

function ProfileSettingsModal({ open, onClose, onSave, ...init }: {
  open: boolean; onClose: () => void; name: string; notes: string; dropins: ModKind[]; dataDir: "shared" | "profile";
  onSave: (v: { name: string; notes: string; dropins: ModKind[]; dataDir: "shared" | "profile" }) => void;
}) {
  const [v, setV] = useState(init);
  const [seen, setSeen] = useState(open);
  if (open !== seen) { setSeen(open); if (open) setV(init); }
  return (
    <Modal open={open} onClose={onClose} title="Profile settings" width={560}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!v.name.trim()} onClick={() => onSave({ ...v, name: v.name.trim() })}>Save</Button></>}>
      <Field label="Name" htmlFor="ps-name"><TextInput id="ps-name" value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} /></Field>
      <Field label="Mod data" hint="Per-mod data folders. World data is always shared.">
        <Segmented aria-label="Mod data" value={v.dataDir} onChange={(d) => setV({ ...v, dataDir: d })} options={[{ id: "shared", label: "Shared" }, { id: "profile", label: "This profile" }]} />
      </Field>
      <Field label="Drop-in folders" hint="Also load mods placed in the game folder by hand.">
        <div className="k-stack">
          {DROPIN_KINDS.map((d) => (
            <Toggle key={d.kind} checked={v.dropins.includes(d.kind)} label={d.label} hint={<span className="k-mono">{d.folder}</span>}
              onChange={(on) => setV({ ...v, dropins: on ? [...v.dropins, d.kind] : v.dropins.filter((k) => k !== d.kind) })} />
          ))}
        </div>
      </Field>
      <Field label="Notes" htmlFor="ps-notes"><TextArea id="ps-notes" rows={3} value={v.notes} onChange={(e) => setV({ ...v, notes: e.target.value })} style={{ fontFamily: "var(--k-font-ui)" }} /></Field>
    </Modal>
  );
}
