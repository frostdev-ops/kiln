// The profile's native page, closed DLL mods (dev.21): the DLLs chain.ini loads (the game's [chain] proxies and this
// profile's dllmain natives) with a "load after Kiln and ShroudForge" switch each, the last session's hook overlaps
// with a suggested fix, and the compat rules that apply. Backend: kiln_manage::compat::view (chain_view).
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import * as api from "../../api/commands";
import type { ChainOverlap, ChainView, ChainViewEntry, CompatRuleResult, Profile } from "../../api/commands";
import { useGame } from "../../api/hooks";
import { Badge, Banner, Button, Panel, Skeleton, Toggle, errorText, fmtAgo, toast, type Tone } from "../../ui";

const KIND: Record<ChainOverlap["kind"], { word: string; tone: Tone; title: string }> = {
  "foreign-before": { word: "Hooked first", tone: "warn", title: "This module hooked the function before Kiln; Kiln chained over it." },
  "foreign-over": { word: "Hooked over", tone: "err", title: "This module hooked over Kiln's hook after the start." },
  refused: { word: "Refused", tone: "err", title: "A hosted engine refused or failed its hook here." },
  other: { word: "Overlap", tone: "warn", title: "Both hook the same function." },
};
const ACTION: Record<CompatRuleResult["action"], string> = { warn: "Warns", late: "Loads late", disable: "Switches off" };
const rule = (by: string | null) => (by?.startsWith("rule:") ? by.slice(5) : null);

export function ChainCard({ profile, update }: { profile: Profile; update: (edit: (p: Profile) => void) => void }) {
  const qc = useQueryClient();
  const { game } = useGame();
  const key = ["chain_view", profile.id, game?.id ?? "", profile.generation, (profile.late ?? []).join(",")] as const;
  const view = useQuery({ queryKey: key, queryFn: () => api.chainView(profile.id, game!.id), enabled: !!game });
  const setChain = useMutation({
    mutationFn: ({ path, late }: { path: string; late: boolean }) => api.chainSetLate(profile.id, game!.id, path, late),
    onSuccess: (v) => qc.setQueryData<ChainView>(key, v),
    onError: (e) => toast.error(e),
  });
  const setLate = (e: ChainViewEntry, late: boolean) => {
    if (e.section === "profile" && e.modId) {
      const id = e.modId;
      update((p) => { const s = new Set(p.late ?? []); if (late) s.add(id); else s.delete(id); p.late = [...s]; });
    } else setChain.mutate({ path: e.path, late });
  };

  if (!game) return null;
  const v = view.data;
  const fired = v?.rules.filter((r) => r.fired) ?? [];
  const idle = (v?.rules.length ?? 0) - fired.length;
  return (
    <section className="k-card" aria-label="Closed DLL mods">
      <header className="k-card-head">
        <div className="k-card-titles">
          <h2 className="k-card-title">Load order with other DLLs</h2>
          <span className="k-card-sub">DLLs chain.ini loads. Late ones load after Kiln and ShroudForge, so their hooks chain after ours.</span>
        </div>
      </header>
      {view.isLoading ? <Skeleton lines={3} /> : view.error ? <span className="k-hint">{errorText(view.error)}</span> : v && (
        <div className="k-stack" style={{ gap: 14 }}>
          {v.error && <Banner tone="warn" title="This profile's DLLs are not listed">{v.error}</Banner>}
          {v.active && !v.current && <span className="k-hint">Changed since the game's files were written: it applies at the next Activate or Play.</span>}
          {v.entries.length === 0 ? (
            <span className="k-hint">No proxy DLLs in the chain and no dllmain natives in this profile.</span>
          ) : (
            <div className="k-list" role="list" aria-label="Chain entries">
              {v.entries.map((e) => {
                const r = rule(e.lateBy);
                const off = rule(e.offBy);
                return (
                  <div key={e.path} className="k-list-item" role="listitem" style={{ flexWrap: "wrap" }}>
                    <span className="k-mono" title={e.path} style={{ minWidth: 0, overflow: "hidden", textOverflow: "ellipsis" }}>{e.file}</span>
                    <Badge tone="idle" title={e.section === "chain" ? "kiln/chain: a proxy the installer moved aside (every profile)" : "A native mod of this profile"}>
                      {e.section === "chain" ? "Game" : "Profile"}
                    </Badge>
                    {e.engine && <span className="k-hint">{e.engine}</span>}
                    {off && <Badge tone="err" title={v.rules.find((x) => x.id === off)?.message}>Off by rule {off}</Badge>}
                    <span className="k-spacer" />
                    <Toggle checked={e.late} disabled={!!r || !!off || setChain.isPending} onChange={(on) => setLate(e, on)}
                      label="Load after Kiln and ShroudForge"
                      hint={r ? `Rule ${r}` : e.section === "chain" && e.late ? "Every profile of this game" : undefined} />
                  </div>
                );
              })}
            </div>
          )}

          <Panel title={<>Overlaps last session {v.session?.generated && <span className="k-hint" style={{ fontFamily: "var(--k-font-ui)", marginLeft: 8 }}>{fmtAgo(v.session.generated)}</span>}</>}>
            {!v.session ? <span className="k-hint">No hook map yet. Start the game once with this Kiln.</span>
              : v.overlaps.length === 0 ? <span className="k-hint">No other DLL hooked a function Kiln hooks.</span> : (
                <div className="k-list" role="list" aria-label="Overlaps">
                  {v.overlaps.map((o) => {
                    const entry = v.entries.find((e) => e.path === o.entry);
                    return (
                      <div key={`${o.symbol}-${o.foreign?.module}-${o.kind}`} className="k-list-item" role="listitem" style={{ flexWrap: "wrap", alignItems: "flex-start", padding: "8px" }}>
                        <div className="k-stack" style={{ gap: 2, flex: "1 1 280px", minWidth: 0 }}>
                          <div className="k-row">
                            <span className="k-mono">{o.symbol || o.rva}</span>
                            <Badge tone={KIND[o.kind].tone} title={KIND[o.kind].title}>{KIND[o.kind].word}</Badge>
                          </div>
                          <span className="k-hint">
                            <span className="k-mono">{o.foreign?.module ?? "?"}</span>{o.library && <> ({o.library.name})</>} and {o.owners.length ? o.owners.join(", ") : "Kiln"}
                          </span>
                          <span className="k-hint">{o.hint}</span>
                        </div>
                        {o.fix === "late" && entry && (
                          <Button size="s" loading={setChain.isPending} onClick={() => setLate(entry, true)}>Load late</Button>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
          </Panel>

          <Panel title="Compat rules">
            {v.ruleErrors.map((e) => <Banner key={e} tone="warn" title="A rule file did not read">{e}</Banner>)}
            {fired.length === 0 ? <span className="k-hint">No rule applies to this profile{idle ? ` (${idle} known)` : ""}.</span> : (
              <div className="k-list" role="list" aria-label="Active rules">
                {fired.map((r) => (
                  <div key={r.id} className="k-list-item" role="listitem" style={{ flexWrap: "wrap", alignItems: "flex-start", padding: "8px" }}>
                    <div className="k-stack" style={{ gap: 2, flex: "1 1 280px", minWidth: 0 }}>
                      <div className="k-row">
                        <span className="k-mono">{r.id}</span>
                        <Badge tone={r.action === "warn" ? "warn" : r.action === "disable" ? "err" : "info"}>{ACTION[r.action]}</Badge>
                        <span className="k-hint">{r.source === "user" ? "Your rule" : "Kiln's rule"}</span>
                      </div>
                      {r.message && <span className="k-hint">{r.message}</span>}
                      {r.targets.length > 0 && <span className="k-hint k-mono">{r.targets.map((t) => t.split(/[\\/]/).pop()).join(", ")}</span>}
                      {r.note && <span className="k-hint" style={{ fontStyle: "italic" }}>{r.note}</span>}
                    </div>
                  </div>
                ))}
              </div>
            )}
            {idle > 0 && fired.length > 0 && <span className="k-hint">{idle} more {idle === 1 ? "rule does" : "rules do"} not apply.</span>}
          </Panel>
        </div>
      )}
    </section>
  );
}
