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
import { rich, t, useT } from "../../i18n";

interface Ctx { aside: HTMLElement | null }

/** Renders `children` into the profile page's aside. */
export function ProfileAside({ children }: { children: ReactNode }) {
  const ctx = useOutletContext<Ctx | undefined>();
  return ctx?.aside ? createPortal(children, ctx.aside) : null;
}

/** Activation for the profile pages' asides: Activate (or Apply when changed since), and the settings dialog. */
export function useActivation() {
  const t = useT();
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
      toast.ok(stale ? t("profiles.layout.applied") : t("profiles.layout.loaded"), t("profiles.layout.steamToo"));
      notifyActivation(a);
    },
    onError: (e, _v, undo) => { undo?.(); toast.error(e); },
  });
  const running = !!status.data?.running;
  const button = game && (!isActive || stale) ? (
    <Button variant="primary" loading={activate.isPending} disabled={running} title={running ? t("mods.list.closeGameFirst") : undefined} onClick={() => activate.mutate()}>
      {stale ? t("profiles.layout.apply") : t("profiles.layout.activate")}
    </Button>
  ) : null;
  return { isActive, stale, button, needsAction: !!button };
}

function headOf(tab: string): { title: string; sub: string } {
  switch (tab) {
    case "native": return { title: t("mods.tabs.native"), sub: t("profiles.layout.nativeSub") };
    case "engines": return { title: t("mods.tabs.engines"), sub: t("profiles.layout.enginesSub") };
    case "configs": return { title: t("mods.tabs.configs"), sub: t("profiles.layout.configsSub") };
    default: return { title: t("mods.tabs.order"), sub: t("profiles.layout.orderSub") };
  }
}

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
  const t = useT();
  const id = useProfileId();
  const loc = useLocation();
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const { profile, query, update } = useProfileEdit(id);
  const { isActive, stale } = useActivation();
  const [aside, setAside] = useState<HTMLElement | null>(null);
  const [edit, setEdit] = useState(false);
  const tab = loc.pathname.split("/")[2] ?? "order";
  const head = headOf(tab);

  if (!game) return <Page><EmptyState page title={t("profiles.layout.noGame")} hint={t("profiles.layout.noGameHint")} action={<Link to="/"><Button>{t("profiles.layout.play")}</Button></Link>} /></Page>;
  if (active.isLoading || (id && query.isLoading)) return <Page><Skeleton lines={5} /></Page>;
  if (!id) return <Page><EmptyState page title={t("profiles.layout.noProfile")} hint={t("profiles.layout.noProfileHint")} action={<Link to="/settings#profiles"><Button>{t("profiles.layout.settings")}</Button></Link>} /></Page>;
  if (!profile) return <Page><EmptyState page title={t("profiles.layout.notReadable")} hint={query.error ? errorText(query.error) : id} action={<Link to="/settings#profiles"><Button>{t("profiles.layout.settings")}</Button></Link>} /></Page>;
  return (
    <Page fill>
      <div className="k-split-page">
        <div className="k-split-main">
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            <PageHeader title={head.title} sub={head.sub}
              meta={<>
                <Link to="/" className="k-link" style={{ fontSize: 16 }}>{t("mods.allMods")}</Link>
                {!isDefaultProfile(profile) && <span className="k-pill k-tone-gold" title={t("profiles.layout.loadedProfile")}>{profile.name}</span>}
                {isActive && stale && <span className="k-pill k-tone-warn">{t("profiles.layout.changedSince")}</span>}
                {profile.server && <span className="k-pill k-tone-info">{t("profiles.layout.forServer", { name: profile.server.name ?? profile.server.id })}</span>}
              </>} />
            <NavTabs aria-label={t("mods.list.mods")} items={[
              { to: "/mods/order", label: t("mods.tabs.order") },
              { to: "/mods/native", label: t("mods.tabs.native") },
              { to: "/mods/engines", label: t("mods.tabs.engines") },
              { to: "/mods/configs", label: t("mods.tabs.configs") },
            ]} />
          </div>
          {stale && <Banner tone="warn" title={t("profiles.layout.changed")}>{t("profiles.layout.changedText")}</Banner>}
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
const kilnDropins = (): DropinRow[] => [
  { kind: "kiln", label: t("profiles.dropins.kiln"), folder: "kiln/mods" },
  { kind: "native", label: t("profiles.dropins.native"), folder: "kiln/native" },
  { kind: "plugin", label: t("profiles.dropins.plugin"), folder: "kiln/plugins" },
  { kind: "emf", label: t("profiles.dropins.emf"), folder: "emf/mods" },
];
/** Other engines' folders (each engine plugin's own folder, where their mod pages say to extract). */
const engineDropins = (): DropinRow[] => [
  { kind: "eml", label: t("profiles.dropins.eml"), folder: "mods, kiln/eml/mods" },
  { kind: "shroudforge", label: t("profiles.dropins.shroudforge"), folder: t("profiles.dropins.shroudforgeFolder") },
  { kind: "greeneye", label: t("profiles.dropins.greeneye"), folder: "greeneye" },
  { kind: "flame", label: t("profiles.dropins.flame"), folder: "mods" },
  { kind: "shroudtopia", label: t("profiles.dropins.shroudtopia"), folder: "mods" },
  { kind: "enshwick", label: t("profiles.dropins.enshwick"), folder: "mods", hint: t("profiles.dropins.enshwickHint") },
  { kind: "shroudlink", label: t("profiles.dropins.shroudlink"), folder: "shroudlink_mods" },
];

export function ProfileSettingsModal({ open, onClose, onSave, ...init }: {
  open: boolean; onClose: () => void; name: string; notes: string; dropins: ModKind[]; dataDir: "shared" | "profile";
  onSave: (v: { name: string; notes: string; dropins: ModKind[]; dataDir: "shared" | "profile" }) => void;
}) {
  const t = useT();
  const [v, setV] = useState(init);
  const [seen, setSeen] = useState(open);
  if (open !== seen) { setSeen(open); if (open) setV(init); }
  const row = (d: DropinRow) => (
    <Toggle key={d.kind} checked={v.dropins.includes(d.kind)} label={d.label}
      hint={<><span className="k-mono">{d.folder}</span>{d.hint && <> · {d.hint}</>}</>}
      onChange={(on) => setV({ ...v, dropins: on ? [...v.dropins, d.kind] : v.dropins.filter((k) => k !== d.kind) })} />
  );
  return (
    <Modal open={open} onClose={onClose} title={t("profiles.settings.title", { name: init.name })} width={560}
      footer={<><Button variant="ghost" onClick={onClose}>{t("common.cancel")}</Button><Button variant="primary" disabled={!v.name.trim()} onClick={() => onSave({ ...v, name: v.name.trim() })}>{t("common.save")}</Button></>}>
      <Field label={t("profiles.settings.name")} htmlFor="ps-name"><TextInput id="ps-name" value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} /></Field>
      <Field label={t("profiles.settings.modData")} hint={t("profiles.settings.modDataHint")}>
        <Segmented aria-label={t("profiles.settings.modData")} value={v.dataDir} onChange={(d) => setV({ ...v, dataDir: d })} options={[{ id: "shared", label: t("profiles.settings.shared") }, { id: "profile", label: t("profiles.settings.thisProfile") }]} />
      </Field>
      <Field label={t("profiles.settings.dropins")} hint={t("profiles.settings.dropinsHint")}>
        <div className="k-stack">
          {kilnDropins().map(row)}
        </div>
      </Field>
      <Field label={t("profiles.settings.engines")} hint={t("profiles.settings.enginesHint")}>
        <div className="k-stack">
          {engineDropins().map(row)}
          <p className="k-hint">{rich("profiles.settings.alwaysLoad", { set: <span className="k-mono">shrouded_engine_tweaks/mods</span>, enshwick: <span className="k-mono">mods</span> })}</p>
        </div>
      </Field>
      <Field label={t("profiles.settings.notes")} htmlFor="ps-notes"><TextArea id="ps-notes" rows={3} value={v.notes} onChange={(e) => setV({ ...v, notes: e.target.value })} style={{ fontFamily: "var(--k-font-ui)", fontSize: 16 }} /></Field>
    </Modal>
  );
}

/** The aside of the tabs without their own: what is on, one line about the tab, Apply and the folders and notes. */
export function ProfileFacts({ hint }: { hint: ReactNode }) {
  const t = useT();
  const id = useProfileId();
  const { profile } = useProfileEdit(id);
  const act = useActivation();
  const openSettings = useOpenSettings();
  if (!profile) return null;
  const on = MOD_KINDS.map((k) => [k, (profile.mods[k] ?? []).filter((m) => m.enabled).length] as const).filter(([, n]) => n);
  const total = on.reduce((n, [, c]) => n + c, 0);
  return (
    <ProfileAside>
      <span className="k-label" style={{ fontSize: 16 }}>{t("profiles.facts.on")}</span>
      <p style={{ fontSize: 19, lineHeight: 1.28, color: "var(--k-head)" }}>
        {on.length ? t("profiles.facts.totalList", { count: total, list: on.map(([k, n]) => t("profiles.facts.kindCount", { n, kind: KIND_SHORT[k] })).join(", ") }) : t("profiles.facts.total", { count: total })}
      </p>
      <p className="k-hint" style={{ fontSize: 16 }}>{hint}</p>
      <div className="k-row" style={{ marginTop: "auto", gap: 8 }}>
        {act.button}
        <Button onClick={openSettings}>{t("mods.foldersAndNotes")}</Button>
      </div>
    </ProfileAside>
  );
}
