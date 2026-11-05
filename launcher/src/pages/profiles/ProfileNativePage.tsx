// /profiles/:id/native: native DLL mods by load phase (drag to reorder inside a phase; the
// native resolver still applies after/before rules) and Kiln plugins.
import { useQuery } from "@tanstack/react-query";
import { Cpu, Plus, Trash2, TriangleAlert } from "lucide-react";
import { useState } from "react";
import * as api from "../../api/commands";
import type { NativePhase, ProfileMod } from "../../api/commands";
import { qk, useLibrary } from "../../api/hooks";
import { Badge, Banner, Button, Card, EmptyState, IconButton, Panel, SortableList, Toggle } from "../../ui";
import { libName, ModList } from "./ModList";
import { useProfileEdit, useProfileId } from "./useProfileEdit";

const PHASES: { id: NativePhase; label: string; hint: string }[] = [
  { id: "dllmain", label: "dllmain", hint: "In the proxy, before anything else. Can hang the start." },
  { id: "early", label: "early", hint: "At the start of Kiln's boot. Runs in safe idle too." },
  { id: "boot", label: "boot", hint: "After Kiln's services and plugins." },
  { id: "world", label: "world", hint: "At the first world. Needs hooks." },
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
    <div className="k-stack" style={{ gap: 16 }}>
      {profile.mods.native.some((m) => m.enabled && phaseOf(m) === "dllmain") && (
        <Banner tone="warn" title="dllmain mods">They load inside the proxy. A bad one can stop the game from starting.</Banner>
      )}
      <Card title="Native mods" sub="DLL mods. Drag to set the order inside a phase."
        actions={<Button size="s" icon={<Plus />} onClick={() => setAdding(true)}>Add</Button>}>
        {profile.mods.native.length === 0 ? (
          <EmptyState icon={<Cpu />} title="No native mods" hint="Import a DLL or a folder with native.json." />
        ) : PHASES.map((ph) => {
          const items = byPhase(ph.id);
          if (!items.length) return null;
          return (
            <Panel key={ph.id} title={<span className="k-phase-head"><span className="k-phase-name">{ph.label}</span><span className="k-hint" style={{ textTransform: "none", letterSpacing: 0, fontWeight: 400 }}>{ph.hint}</span></span>}>
              <SortableList aria-label={`${ph.label} phase`} items={items} itemKey={(m) => m.id} onReorder={(n) => reorder(ph.id, n)} render={(m) => (
                <div className="k-row" style={{ gap: 12 }}>
                  <Toggle checked={m.enabled} onChange={(on) => toggle("native", m.id, on)} aria-label={`Enable ${m.id}`} />
                  <span className="k-stack" style={{ gap: 0, flex: 1, minWidth: 0 }}>
                    <span className="k-item-title" style={{ opacity: m.enabled ? 1 : 0.55 }}>{libName(lib.data, "native", m.id)}</span>
                    <span className="k-dim k-mono" style={{ fontSize: 11 }}>{m.id} · {m.version}</span>
                  </span>
                  {m.enabled && reasons.get(m.id) && <span className="k-hint" title={reasons.get(m.id) ?? ""}><TriangleAlert size={13} color="var(--k-warn)" /> {reasons.get(m.id)}</span>}
                  {ph.id === "dllmain" && <Badge tone="warn">proxy</Badge>}
                  <IconButton label={`Remove ${m.id}`} size="s" icon={<Trash2 />} onClick={() => remove("native", m.id)} />
                </div>
              )} />
            </Panel>
          );
        })}
      </Card>
      {adding && <AddNative onDone={() => setAdding(false)} have={profile.mods.native} onAdd={(ms) => update((p) => { p.mods.native.push(...ms); })} />}
      <Card flush title="Kiln plugins" sub="DLLs with kiln_plugin_main.">
        <ModList kind="plugin" mods={profile.mods.plugin} onToggle={(m, on) => toggle("plugin", m, on)} onRemove={(m) => remove("plugin", m)}
          onAdd={(ms) => update((p) => { p.mods.plugin.push(...ms); })}
          onVersion={(m, version, hash) => update((p) => { p.mods.plugin = p.mods.plugin.map((x) => (x.id === m ? { ...x, version, hash } : x)); })}
          emptyHint="Plugins load after Kiln's services." />
      </Card>
    </div>
  );
}

/** Reuses ModList's add modal through a hidden list (opens straight away). */
function AddNative({ onDone, have, onAdd }: { onDone: () => void; have: ProfileMod[]; onAdd: (m: ProfileMod[]) => void }) {
  const lib = useLibrary("native");
  const [pick, setPick] = useState<string[]>([]);
  const avail = (lib.data ?? []).filter((l) => !have.some((h) => h.id === l.id));
  return (
    <Card title="Add native mods" actions={<>
      <Button size="s" variant="ghost" onClick={onDone}>Cancel</Button>
      <Button size="s" variant="primary" disabled={!pick.length} onClick={() => { onAdd(avail.filter((l) => pick.includes(l.id)).map((l) => ({ id: l.id, version: l.versions[0].version, hash: l.versions[0].hash, enabled: true }))); onDone(); }}>Add {pick.length || ""}</Button>
    </>}>
      {avail.length === 0 ? <span className="k-hint">Nothing else in the library.</span> : avail.map((l) => {
        const phase = (l.versions[0].manifest as { phase?: string } | null)?.phase ?? "early";
        return (
          <Toggle key={l.id} checked={pick.includes(l.id)} onChange={(on) => setPick(on ? [...pick, l.id] : pick.filter((x) => x !== l.id))}
            label={l.name} hint={<span className="k-mono">{l.id} · {l.versions[0].version} · {phase}</span>} />
        );
      })}
    </Card>
  );
}
