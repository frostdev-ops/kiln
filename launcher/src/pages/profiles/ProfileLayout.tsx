// /mods/* frame (the advanced views of the loaded profile's mods), the aside portal, Activate or Apply, and the profile's
// settings dialog (name, mod data, drop-in folders, notes), which Settings, Profiles opens too.
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Link, Outlet, useLocation, useOutletContext } from "react-router-dom";
import * as api from "../../api/commands";
import type { ModKind } from "../../api/commands";
import { invalidateGame, showActive, useActiveProfile, useGame, useGameStatus } from "../../api/hooks";
import { Banner, Button, EmptyState, Field, Modal, NavTabs, Page, PageHeader, Segmented, Skeleton, TextArea, TextInput, Toggle, errorText, toast } from "../../ui";
import { notifyActivation } from "../../shell/notices";
import { isDefaultProfile } from "../../shell/useMods";
import { ProfileIdContext, useProfileEdit, useProfileId } from "./useProfileEdit";
import { KIND_SHORT, MOD_KINDS } from "../../api/kinds";
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
      toast.ok(stale ? "Applied" : "Loaded", "Steam's Play button uses it too.");
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

const TITLE: Record<string, { title: string; sub: string }> = {
  order: { title: "Load order", sub: "Every mod that is on, of every kind, in the order a start brings them up." },
  native: { title: "Native and DLLs", sub: "Native mods by load phase, the DLLs chain.ini loads, and Kiln plugins." },
  engines: { title: "Engines", sub: "Which loader runs which kind of mod in this game, and what each one said at the last start." },
  configs: { title: "Configs", sub: "The config files of your mods." },
};

/** /mods/*: the advanced views of the loaded profile's mods (the main list is where they are switched): Load order,
 *  Native and DLLs, Engines, Configs. The tab's page, and an aside the tab fills (through <ProfileAside>, a portal into
 *  it) or leaves empty (then the tab gets the full width). */
export default function ModsLayout() {
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const id = active.data?.profileId ?? "";
  return (
    <ProfileIdContext.Provider value={id}>
      <ModsFrame />
    </ProfileIdContext.Provider>
  );
}

function ModsFrame() {
  const id = useProfileId();
  const loc = useLocation();
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const { profile, query, update } = useProfileEdit(id);
  const { isActive, stale } = useActivation();
  const [aside, setAside] = useState<HTMLElement | null>(null);
  const [edit, setEdit] = useState(false);
  const tab = loc.pathname.split("/")[2] ?? "order";
  const head = TITLE[tab] ?? TITLE.order;

  if (!game) return <Page><EmptyState page title="No game yet" hint="Find Enshrouded on Play first." action={<Link to="/"><Button>Play</Button></Link>} /></Page>;
  if (active.isLoading || (id && query.isLoading)) return <Page><Skeleton lines={5} /></Page>;
  if (!id) return <Page><EmptyState page title="No profile is loaded" hint="Load one in Settings, Profiles: these views show the mods that are on." action={<Link to="/settings#profiles"><Button>Settings</Button></Link>} /></Page>;
  if (!profile) return <Page><EmptyState page title="Your mod list is not readable" hint={query.error ? errorText(query.error) : id} action={<Link to="/settings#profiles"><Button>Settings</Button></Link>} /></Page>;
  return (
    <Page fill>
      <div className="k-split-page">
        <div className="k-split-main">
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            <PageHeader title={head.title} sub={head.sub}
              meta={<>
                <Link to="/" className="k-link" style={{ fontSize: 16 }}>All mods</Link>
                {!isDefaultProfile(profile) && <span className="k-pill k-tone-gold" title="The loaded profile (Settings, Profiles)">{profile.name}</span>}
                {isActive && stale && <span className="k-pill k-tone-warn">Changed since written</span>}
                {profile.server && <span className="k-pill k-tone-info">For {profile.server.name ?? profile.server.id}</span>}
              </>} />
            <NavTabs aria-label="Mods" items={[
              { to: "/mods/order", label: "Load order" },
              { to: "/mods/native", label: "Native and DLLs" },
              { to: "/mods/engines", label: "Engines" },
              { to: "/mods/configs", label: "Configs" },
            ]} />
          </div>
          {stale && <Banner tone="warn" title="Changed">Your mods changed since they were written to the game. Apply writes them now; Play does too.</Banner>}
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

export function ProfileSettingsModal({ open, onClose, onSave, ...init }: {
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
    <Modal open={open} onClose={onClose} title={`${init.name}: folders and notes`} width={560}
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

/** The aside of the tabs without their own: what is on, one line about the tab, Apply and the folders and notes. */
export function ProfileFacts({ hint }: { hint: ReactNode }) {
  const id = useProfileId();
  const { profile } = useProfileEdit(id);
  const act = useActivation();
  const openSettings = useOpenSettings();
  if (!profile) return null;
  const on = MOD_KINDS.map((k) => [k, (profile.mods[k] ?? []).filter((m) => m.enabled).length] as const).filter(([, n]) => n);
  const total = on.reduce((n, [, c]) => n + c, 0);
  return (
    <ProfileAside>
      <span className="k-label" style={{ fontSize: 16 }}>On</span>
      <p style={{ fontSize: 19, lineHeight: 1.28, color: "var(--k-head)" }}>
        {total} mod{total === 1 ? "" : "s"}{on.length ? `: ${on.map(([k, n]) => `${n} ${KIND_SHORT[k]}`).join(", ")}` : ""}.
      </p>
      <p className="k-hint" style={{ fontSize: 16 }}>{hint}</p>
      <div className="k-row" style={{ marginTop: "auto", gap: 8 }}>
        {act.button}
        <Button onClick={openSettings}>Folders and notes</Button>
      </div>
    </ProfileAside>
  );
}
