// /profiles/:id/native: native DLL mods by load phase (drag to reorder inside a phase; the native resolver still
// applies after/before rules) and Kiln plugins.
import { useQuery } from "@tanstack/react-query";
import { MoreHorizontal } from "lucide-react";
import { useState } from "react";
import * as api from "../../api/commands";
import type { NativePhase, ProfileMod } from "../../api/commands";
import { qk, useLibrary } from "../../api/hooks";
import { Banner, Button, FlameToggle, Menu, Modal, NameId, Panel, SortableList, Toggle } from "../../ui";
import { ChainCard } from "./ChainCard";
import { libName, ModList } from "./ModList";
import { ProfileFacts } from "./ProfileLayout";
import { useProfileEdit, useProfileId } from "./useProfileEdit";

const PHASES: { id: NativePhase; label: string; hint: string }[] = [
  { id: "dllmain", label: "Dllmain", hint: "In the proxy, before anything else. Can hang the start." },
  { id: "early", label: "Early", hint: "At the start of Kiln's boot. Runs in safe idle too." },
  { id: "boot", label: "Boot", hint: "After Kiln's services and plugins." },
  { id: "world", label: "World", hint: "At the first world. Needs hooks." },
];

export default function ProfileNativePage() {
  const id = useProfileId();
  const { profile, toggle, remove, update } = useProfileEdit(id);
  const lib = useLibrary("native");
  const preview = useQuery({ queryKey: qk.resolve(id, "client"), queryFn: () => api.profileResolve(id, "client"), enabled: !!profile });
  const [adding, setAdding] = useState(false);
  if (!profile) return null;
  const phaseOf = (m: ProfileMod): NativePhase => {
    const p = preview.data?.native.find((n) => n.id === m.id)?.phase;
    if (p) return p;
    const man = lib.data?.find((l) => l.id === m.id)?.versions.find((v) => v.version === m.version)?.manifest as { phase?: NativePhase } | null;
    return man?.phase ?? "early";
  };
  const byPhase = (ph: NativePhase) => profile.mods.native.filter((m) => phaseOf(m) === ph);
  const reorder = (ph: NativePhase, next: ProfileMod[]) => update((p) => {
    // keep the other phases where they are; this phase's slots get the new order
    const q = [...next];
    p.mods.native = p.mods.native.map((m) => (phaseOf(m) === ph ? q.shift()! : m));
  });
  const reasons = new Map((preview.data?.native ?? []).map((n) => [n.id, n.reason]));

  return (
    <div className="k-scroll k-stack" style={{ gap: 18, paddingBottom: 20 }}>
      {profile.mods.native.some((m) => m.enabled && phaseOf(m) === "dllmain") && (
        <Banner tone="warn" title="Dllmain">These load inside the proxy. A bad one can stop the game from starting.</Banner>
      )}
      <section className="k-card">
        <header className="k-card-head">
          <div className="k-card-titles"><h2 className="k-card-title">Native mods</h2><span className="k-card-sub">Drag to set the order inside a phase.</span></div>
          <div className="k-card-actions"><Button size="s" onClick={() => setAdding(true)}>Add</Button></div>
        </header>
        {profile.mods.native.length === 0 ? (
          <div className="k-empty"><div className="k-empty-title">No native mods. Import a DLL or a folder with native.json on the Library page.</div></div>
        ) : PHASES.map((ph) => {
          const items = byPhase(ph.id);
          if (!items.length) return null;
          return (
            <Panel key={ph.id} title={<>{ph.label} <span className="k-hint" style={{ fontFamily: "var(--k-font-ui)", marginLeft: 8 }}>{ph.hint}</span></>}>
              <SortableList aria-label={`${ph.label} phase`} items={items} itemKey={(m) => m.id} onReorder={(n) => reorder(ph.id, n)} render={(m) => {
                const name = libName(lib.data, "native", m.id);
                const why = m.enabled ? reasons.get(m.id) : null;
                return (
                  <div className="k-row" style={{ gap: 12, flexWrap: "nowrap" }}>
                    <FlameToggle on={m.enabled} label={name} onChange={(on) => toggle("native", m.id, on)} />
                    <NameId name={name} id={m.id} dim={!m.enabled} />
                    <span className="k-mono k-muted" style={{ flex: "none" }}>{m.version}</span>
                    <span className="k-spacer" />
                    {why && <span className="k-hint" style={{ fontStyle: "italic", color: "var(--k-ember-300)" }} title={why}>{why}</span>}
                    <Menu label={`More for ${name}`} icon={<MoreHorizontal />} size="s" items={[{ label: "Remove from this profile", danger: true, onSelect: () => remove("native", m.id) }]} />
                  </div>
                );
              }} />
            </Panel>
          );
        })}
      </section>
      <ChainCard profile={profile} update={update} />
      <section className="k-card">
        <header className="k-card-head"><div className="k-card-titles"><h2 className="k-card-title">Kiln plugins</h2><span className="k-card-sub">DLLs with kiln_plugin_main. They load after Kiln's services.</span></div></header>
        <ModList kind="plugin" mods={profile.mods.plugin} onToggle={(m, on) => toggle("plugin", m, on)} onRemove={(m) => remove("plugin", m)}
          onAdd={(ms) => update((p) => { p.mods.plugin.push(...ms); })}
          onVersion={(m, version, hash) => update((p) => { p.mods.plugin = p.mods.plugin.map((x) => (x.id === m ? { ...x, version, hash } : x)); })}
          emptyHint="No plugins in this profile." />
      </section>
      <AddNative open={adding} onClose={() => setAdding(false)} have={profile.mods.native} onAdd={(ms) => update((p) => { p.mods.native.push(...ms); })} />
      <ProfileFacts hint="Native mods load by phase, in the order set here. Kiln still applies their after and before rules." />
    </div>
  );
}

function AddNative({ open, onClose, have, onAdd }: { open: boolean; onClose: () => void; have: ProfileMod[]; onAdd: (m: ProfileMod[]) => void }) {
  const lib = useLibrary("native");
  const [pick, setPick] = useState<string[]>([]);
  const avail = (lib.data ?? []).filter((l) => !have.some((h) => h.id === l.id));
  return (
    <Modal open={open} onClose={onClose} title="Add native mods" width={520}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={!pick.length} onClick={() => { onAdd(avail.filter((l) => pick.includes(l.id)).map((l) => ({ id: l.id, version: l.versions[0].version, hash: l.versions[0].hash, enabled: true }))); setPick([]); onClose(); }}>Add {pick.length || ""}</Button></>}>
      {avail.length === 0 ? <span className="k-empty-title">Nothing else in the library.</span> : avail.map((l) => {
        const phase = (l.versions[0].manifest as { phase?: string } | null)?.phase ?? "early";
        return (
          <Toggle key={l.id} checked={pick.includes(l.id)} onChange={(on) => setPick(on ? [...pick, l.id] : pick.filter((x) => x !== l.id))}
            label={l.name} hint={<span className="k-mono">{l.id} {l.versions[0].version}, {phase}</span>} />
        );
      })}
    </Modal>
  );
}
