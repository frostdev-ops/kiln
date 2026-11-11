// /profiles/:id/* frame: the profile's name with its id and state word, the tabs, the tab's page, and an aside the tab
// fills (through <ProfileAside>, a portal into it) or leaves empty (then the tab gets the full width).
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Link, Outlet, useOutletContext } from "react-router-dom";
import * as api from "../../api/commands";
import type { ModKind } from "../../api/commands";
import { invalidateGame, showActive, useActiveProfile, useGame, useGameStatus } from "../../api/hooks";
import { Banner, Button, EmptyState, Field, Modal, NavTabs, Page, PageHeader, Segmented, Skeleton, TextArea, TextInput, Toggle, errorText, toast } from "../../ui";
import { notifyActivation } from "../../shell/notices";
import { useProfileEdit, useProfileId } from "./useProfileEdit";
import { ENGINE_KINDS, KIND_SHORT } from "../../api/kinds";
import { dropinKinds, setDropins } from "./dropins";

interface Ctx { aside: HTMLElement | null }

/** Renders `children` into the profile page's aside. */
export function ProfileAside({ children }: { children: ReactNode }) {
  const ctx = useOutletContext<Ctx | undefined>();
  return ctx?.aside ? createPortal(children, ctx.aside) : null;
}

/** Activation for the profile pages' asides: Activate (or Apply when changed since), and the settings dialog. */
export function useActivation() {
  const id = useProfileId();
  const qc = useQueryClient();
  const { profile } = useProfileEdit(id);
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const status = useGameStatus(game?.id);
  const isActive = !!game && active.data?.profileId === id;
  const stale = isActive && !!profile && !!active.data && Date.parse(profile.updated) > Date.parse(active.data.activated);
  const activate = useMutation({
    mutationFn: () => api.profileActivate(id, game!.id),
    onMutate: () => showActive(qc, game!.id, id),
    onSuccess: (a) => {
      invalidateGame(qc, game!.id);
      toast.ok(stale ? "Applied" : "Profile active", "Steam's Play button uses it too.");
      notifyActivation(a);
    },
    onError: (e, _v, undo) => { undo?.(); toast.error(e); },
  });
  const running = !!status.data?.running;
  const button = game && (!isActive || stale) ? (
    <Button variant="primary" loading={activate.isPending} disabled={running} title={running ? "Close the game first" : undefined} onClick={() => activate.mutate()}>
      {stale ? "Apply" : "Activate"}
    </Button>
  ) : null;
  return { isActive, stale, button, needsAction: !!button };
}

export default function ProfileLayout() {
  const id = useProfileId();
  const { profile, query, update } = useProfileEdit(id);
  const { isActive, stale } = useActivation();
  const [aside, setAside] = useState<HTMLElement | null>(null);
  const [edit, setEdit] = useState(false);

  if (query.isLoading) return <Page><Skeleton lines={5} /></Page>;
  if (!profile) return <Page><EmptyState page title="No such profile" hint={query.error ? errorText(query.error) : id} action={<Link to="/profiles"><Button>All profiles</Button></Link>} /></Page>;
  const n = (k: ModKind) => (profile.mods[k] ?? []).length;
  return (
    <Page fill>
      <div className="k-split-page">
        <div className="k-split-main">
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            <PageHeader title={profile.name}
              meta={<>
                <span className="k-mono k-muted">{profile.id}</span>
                {isActive && <span className={"k-pill " + (stale ? "k-tone-warn" : "k-tone-gold")}>{stale ? "Changed since active" : "Active"}</span>}
                {profile.server && <span className="k-pill k-tone-info">For {profile.server.name ?? profile.server.id}</span>}
              </>} />
            <NavTabs aria-label="Profile" items={[
              { to: "mods", label: "Mods", count: n("kiln") },
              { to: "native", label: "Native", count: n("native") + n("plugin") },
              { to: "emf", label: "EMF", count: n("emf") },
              { to: "engines", label: "Engines", count: ENGINE_KINDS.reduce((c, k) => c + n(k), 0) },
              { to: "configs", label: "Configs" },
              { to: "saves", label: "Saves" },
            ]} />
          </div>
          {stale && <Banner tone="warn" title="Changed">The profile changed since it was activated. Apply writes it to the game.</Banner>}
          <Outlet context={{ aside, openSettings: () => setEdit(true) }} />
        </div>
        <aside ref={setAside} className="k-aside k-profile-aside" />
      </div>
      <ProfileSettingsModal open={edit} onClose={() => setEdit(false)} name={profile.name} notes={profile.notes} dropins={dropinKinds(profile)} dataDir={profile.dataDir}
        onSave={(v) => { update((p) => { p.name = v.name; p.notes = v.notes; setDropins(p, v.dropins); p.dataDir = v.dataDir; }); setEdit(false); }} />
    </Page>
  );
}

/** Opens the profile's settings dialog (from an aside). */
export function useOpenSettings(): () => void {
  const ctx = useOutletContext<{ openSettings?: () => void } | undefined>();
  return ctx?.openSettings ?? (() => {});
}

type DropinRow = { kind: ModKind; label: string; folder: string; hint?: string };
/** Kiln's own drop-in folders. */
const KILN_DROPINS: DropinRow[] = [
  { kind: "kiln", label: "Kiln mods", folder: "kiln/mods" },
  { kind: "native", label: "Native mods", folder: "kiln/native" },
  { kind: "plugin", label: "Plugins", folder: "kiln/plugins" },
  { kind: "emf", label: "EMF mods", folder: "emf/mods" },
];
/** Other engines' folders (each engine plugin's own folder, where their mod pages say to extract). */
const ENGINE_DROPINS: DropinRow[] = [
  { kind: "eml", label: "EML mods", folder: "mods, kiln/eml/mods" },
  { kind: "shroudforge", label: "ShroudForge mods", folder: "ShroudForge's mods folder (mods)" },
  { kind: "greeneye", label: "Greeneye mods", folder: "greeneye" },
  { kind: "flame", label: "Flame mods", folder: "mods" },
  { kind: "shroudtopia", label: "Shroudtopia mods", folder: "mods" },
  { kind: "enshwick", label: "Enshwick mods", folder: "mods", hint: "Enshwick's loader runs every folder in mods once this profile has an Enshwick mod." },
  { kind: "shroudlink", label: "shroudlink mods (servers)", folder: "shroudlink_mods" },
];

function ProfileSettingsModal({ open, onClose, onSave, ...init }: {
  open: boolean; onClose: () => void; name: string; notes: string; dropins: ModKind[]; dataDir: "shared" | "profile";
  onSave: (v: { name: string; notes: string; dropins: ModKind[]; dataDir: "shared" | "profile" }) => void;
}) {
  const [v, setV] = useState(init);
  const [seen, setSeen] = useState(open);
  if (open !== seen) { setSeen(open); if (open) setV(init); }
  const row = (d: DropinRow) => (
    <Toggle key={d.kind} checked={v.dropins.includes(d.kind)} label={d.label}
      hint={<><span className="k-mono">{d.folder}</span>{d.hint && <> · {d.hint}</>}</>}
      onChange={(on) => setV({ ...v, dropins: on ? [...v.dropins, d.kind] : v.dropins.filter((k) => k !== d.kind) })} />
  );
  return (
    <Modal open={open} onClose={onClose} title="Profile settings" width={560}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!v.name.trim()} onClick={() => onSave({ ...v, name: v.name.trim() })}>Save</Button></>}>
      <Field label="Name" htmlFor="ps-name"><TextInput id="ps-name" value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} /></Field>
      <Field label="Mod data" hint="Per-mod data folders. World data is always shared.">
        <Segmented aria-label="Mod data" value={v.dataDir} onChange={(d) => setV({ ...v, dataDir: d })} options={[{ id: "shared", label: "Shared" }, { id: "profile", label: "This profile" }]} />
      </Field>
      <Field label="Drop-in folders" hint="Also load mods placed in the game folder by hand.">
        <div className="k-stack">
          {KILN_DROPINS.map(row)}
        </div>
      </Field>
      <Field label="Other engines' folders" hint="Where other loaders' mod pages say to extract their mods.">
        <div className="k-stack">
          {ENGINE_DROPINS.map(row)}
          <p className="k-hint">SET mods (<span className="k-mono">shrouded_engine_tweaks/mods</span>) and Enshwick mods (<span className="k-mono">mods</span>) always load. Ember mods are found but do not run yet.</p>
        </div>
      </Field>
      <Field label="Notes" htmlFor="ps-notes"><TextArea id="ps-notes" rows={3} value={v.notes} onChange={(e) => setV({ ...v, notes: e.target.value })} style={{ fontFamily: "var(--k-font-ui)", fontSize: 16 }} /></Field>
    </Modal>
  );
}

/** The aside of the tabs without their own: what the profile holds, one line about the tab, Activate and Settings. */
export function ProfileFacts({ hint }: { hint: ReactNode }) {
  const id = useProfileId();
  const { profile } = useProfileEdit(id);
  const act = useActivation();
  const openSettings = useOpenSettings();
  if (!profile) return null;
  const m = profile.mods;
  return (
    <ProfileAside>
      <span className="k-label" style={{ fontSize: 16 }}>In this profile</span>
      <p style={{ fontSize: 19, lineHeight: 1.28, color: "var(--k-head)" }}>
        {m.kiln.length} Kiln, {m.native.length} native, {m.plugin.length} plugin{m.plugin.length === 1 ? "" : "s"}, {m.emf.length} EMF
        {ENGINE_KINDS.filter((k) => (m[k] ?? []).length).map((k) => `, ${(m[k] ?? []).length} ${KIND_SHORT[k]}`).join("")}.
      </p>
      <p className="k-hint" style={{ fontSize: 16 }}>{hint}</p>
      <div className="k-row" style={{ marginTop: "auto", gap: 8 }}>
        {act.button}
        <Button onClick={openSettings}>Settings</Button>
      </div>
    </ProfileAside>
  );
}
