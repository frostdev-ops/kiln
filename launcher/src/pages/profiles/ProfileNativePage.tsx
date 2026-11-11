// /mods/native: the loaded profile's native DLL mods by load phase (drag to reorder inside a phase; the native resolver
// still applies after/before rules), the DLLs chain.ini loads (ChainCard), and Kiln plugins. Switching on, adding and
// removing are the main list's (pages/mods/MainList.tsx).
import { useQuery } from "@tanstack/react-query";
import * as api from "../../api/commands";
import type { NativePhase, ProfileMod } from "../../api/commands";
import { qk, useGame, useLibrary } from "../../api/hooks";
import { Banner, FlameToggle, NameId, Panel, SortableList } from "../../ui";
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
  const { profile, toggle, update } = useProfileEdit(id);
  const { game } = useGame();
  const lib = useLibrary("native");
  const preview = useQuery({ queryKey: qk.resolve(id, "client", game?.id ?? null), queryFn: () => api.profileResolve(id, "client", game?.id ?? null), enabled: !!profile });
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
        </header>
        {profile.mods.native.length === 0 ? (
          <div className="k-empty"><div className="k-empty-title">No native mods on your list. Drop a DLL, or a folder with native.json, anywhere on the window.</div></div>
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
        <ModList kind="plugin" mods={profile.mods.plugin} onToggle={(m, on) => toggle("plugin", m, on)}
          onVersion={(m, version, hash) => update((p) => { p.mods.plugin = p.mods.plugin.map((x) => (x.id === m ? { ...x, version, hash } : x)); })}
          emptyHint="No plugins on your list." />
      </section>
      <ProfileFacts hint="Native mods load by phase, in the order set here. Kiln still applies their after and before rules." />
    </div>
  );
}
