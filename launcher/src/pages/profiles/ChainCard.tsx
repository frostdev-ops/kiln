// The profile's native page, closed DLL mods (dev.21): the DLLs chain.ini loads (the game's [chain] proxies and this
// profile's dllmain natives) with a "load after Kiln and ShroudForge" switch each, the last session's hook overlaps
// with a suggested fix, and the compat rules that apply. Backend: kiln_manage::compat::view (chain_view). dev.29: a
// rule that orders two DLLs (`before`) holds both switches and says why under each (chainOrder.ts).
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import * as api from "../../api/commands";
import type { ChainOverlap, ChainView, ChainViewEntry, CompatRuleResult, Profile } from "../../api/commands";
import { useGame } from "../../api/hooks";
import { Badge, Banner, Button, Panel, Skeleton, Toggle, errorText, fmtAgo, toast, type Tone } from "../../ui";
import { entryOrder, orderSides, orderWarnings } from "./chainOrder";
import { rich, t, useT } from "../../i18n";

function overlapKind(kind: ChainOverlap["kind"]): { word: string; tone: Tone; title: string } {
  switch (kind) {
    case "foreign-before": return { word: t("profiles.chain.hookedFirst"), tone: "warn", title: t("profiles.chain.hookedFirstHint") };
    case "foreign-over": return { word: t("profiles.chain.hookedOver"), tone: "err", title: t("profiles.chain.hookedOverHint") };
    case "refused": return { word: t("profiles.chain.refused"), tone: "err", title: t("profiles.chain.refusedHint") };
    default: return { word: t("profiles.chain.overlap"), tone: "warn", title: t("profiles.chain.overlapHint") };
  }
}
function actionWord(action: CompatRuleResult["action"]): string {
  switch (action) {
    case "warn": return t("profiles.chain.actionWarn");
    case "late": return t("profiles.chain.actionLate");
    case "disable": return t("profiles.chain.actionDisable");
    case "before": return t("profiles.chain.actionBefore");
    default: return action;
  }
}
const rule = (by: string | null) => (by?.startsWith("rule:") ? by.slice(5) : null);
// shroudlink's client addon (ChainViewEntry.gated): no late switch, only one back from late: profiles.chain.gated, gatedLate

export function ChainCard({ profile, update }: { profile: Profile; update: (edit: (p: Profile) => void) => void }) {
  const t = useT();
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
    if (late && e.gated) return;
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
    <section className="k-card" aria-label={t("profiles.chain.ariaLabel")}>
      <header className="k-card-head">
        <div className="k-card-titles">
          <h2 className="k-card-title">{t("profiles.chain.title")}</h2>
          <span className="k-card-sub">{t("profiles.chain.sub")}</span>
        </div>
      </header>
      {view.isLoading ? <Skeleton lines={3} /> : view.error ? <span className="k-hint">{errorText(view.error)}</span> : v && (
        <div className="k-stack" style={{ gap: 14 }}>
          {v.error && <Banner tone="warn" title={t("profiles.chain.notListed")}>{v.error}</Banner>}
          {v.active && !v.current && <span className="k-hint">{t("profiles.chain.changed")}</span>}
          {orderWarnings(v.rules).map((w) => <Banner key={w.id} tone="warn" title={t("profiles.chain.orderNotChanged")}>{t("profiles.chain.withRule", { text: w.text, id: w.id })}</Banner>)}
          {v.entries.length === 0 ? (
            <span className="k-hint">{t("profiles.chain.empty")}</span>
          ) : (
            <div className="k-list" role="list" aria-label={t("profiles.chain.entries")}>
              {v.entries.map((e) => {
                const r = rule(e.lateBy) ?? rule(e.earlyBy ?? null);
                const off = rule(e.offBy);
                // a `before` rule holds this switch: its sentence under the entry, no switch against it
                const order = entryOrder(e, v.rules);
                return (
                  <div key={e.path} className="k-list-item" role="listitem" style={{ flexWrap: "wrap" }}>
                    <span className="k-mono" title={e.path} style={{ minWidth: 0, overflow: "hidden", textOverflow: "ellipsis" }}>{e.file}</span>
                    <Badge tone="idle" title={e.section === "chain" ? t("profiles.chain.gameHint") : t("profiles.chain.profileHint")}>
                      {e.section === "chain" ? t("profiles.chain.game") : t("profiles.chain.profile")}
                    </Badge>
                    {e.engine && <span className="k-hint">{e.engine}</span>}
                    {e.loader && <span className="k-hint" title={t("profiles.chain.loaderHint")}>{t("profiles.chain.alwaysFirst")}</span>}
                    {off && <Badge tone="err" title={v.rules.find((x) => x.id === off)?.message}>{t("profiles.chain.offByRule", { id: off })}</Badge>}
                    <span className="k-spacer" />
                    {e.gated && <span className={e.late ? "k-hint k-tone-warn" : "k-hint"} data-gated style={{ flexBasis: "100%", order: 1 }}>{e.late ? t("profiles.chain.gatedLate") : t("profiles.chain.gated")}</span>}
                    {order && <span className="k-hint" data-order style={{ flexBasis: "100%", order: 1 }}>{order.text}</span>}
                    {!e.loader && (!e.gated || e.late) && (
                      <Toggle checked={e.late} disabled={!!r || !!off || setChain.isPending} onChange={(on) => setLate(e, on)}
                        label={t("profiles.chain.lateLabel")}
                        hint={order ? order.hint : r ? t("profiles.chain.rule", { id: r }) : e.section === "chain" && e.late ? t("profiles.chain.everyProfile") : undefined} />
                    )}
                  </div>
                );
              })}
            </div>
          )}

          <Panel title={<>{t("profiles.chain.overlapsTitle")} {v.session?.generated && <span className="k-hint" style={{ fontFamily: "var(--k-font-ui)", marginLeft: 8 }}>{fmtAgo(v.session.generated)}</span>}</>}>
            {!v.session ? <span className="k-hint">{t("profiles.chain.noHookMap")}</span>
              : v.overlaps.length === 0 ? <span className="k-hint">{t("profiles.chain.noOverlaps")}</span> : (
                <div className="k-list" role="list" aria-label={t("profiles.chain.overlapsLabel")}>
                  {v.overlaps.map((o) => {
                    const entry = v.entries.find((e) => e.path === o.entry);
                    const kind = overlapKind(o.kind);
                    const module = <span className="k-mono">{o.foreign?.module ?? "?"}</span>;
                    const owners = o.owners.length ? o.owners.join(", ") : "Kiln";
                    return (
                      <div key={`${o.symbol}-${o.foreign?.module}-${o.kind}`} className="k-list-item" role="listitem" style={{ flexWrap: "wrap", alignItems: "flex-start", padding: "8px" }}>
                        <div className="k-stack" style={{ gap: 2, flex: "1 1 280px", minWidth: 0 }}>
                          <div className="k-row">
                            <span className="k-mono">{o.symbol || o.rva || o.hook || "?"}</span>
                            <Badge tone={kind.tone} title={kind.title}>{kind.word}</Badge>
                          </div>
                          <span className="k-hint">
                            {o.library ? rich("profiles.chain.overlapWhoLib", { module, library: o.library.name, owners }) : rich("profiles.chain.overlapWho", { module, owners })}
                          </span>
                          <span className="k-hint">{o.hint}</span>
                        </div>
                        {o.fix === "late" && entry && (
                          <Button size="s" loading={setChain.isPending} onClick={() => setLate(entry, true)}>{t("profiles.chain.loadLate")}</Button>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
          </Panel>

          <Panel title={t("profiles.chain.rulesTitle")}>
            {v.ruleErrors.map((e) => <Banner key={e} tone="warn" title={t("profiles.chain.ruleFileBad")}>{e}</Banner>)}
            {fired.length === 0 ? <span className="k-hint">{idle ? t("profiles.chain.noRuleKnown", { count: idle }) : t("profiles.chain.noRule")}</span> : (
              <div className="k-list" role="list" aria-label={t("profiles.chain.activeRules")}>
                {fired.map((r) => (
                  <div key={r.id} className="k-list-item" role="listitem" style={{ flexWrap: "wrap", alignItems: "flex-start", padding: "8px" }}>
                    <div className="k-stack" style={{ gap: 2, flex: "1 1 280px", minWidth: 0 }}>
                      <div className="k-row">
                        <span className="k-mono">{r.id}</span>
                        <Badge tone={r.action === "warn" || r.warning ? "warn" : r.action === "disable" ? "err" : "info"}>{actionWord(r.action)}</Badge>
                        <span className="k-hint">{r.source === "user" ? t("profiles.chain.yourRule") : t("profiles.chain.kilnRule")}</span>
                      </div>
                      {r.message && <span className="k-hint">{r.message}</span>}
                      {orderSides(r) ? <span className="k-hint k-mono">{orderSides(r)}</span>
                        : r.targets.length > 0 && <span className="k-hint k-mono">{r.targets.map((target) => target.split(/[\\/]/).pop()).join(", ")}</span>}
                      {r.warning && <span className="k-hint k-tone-warn">{r.warning}</span>}
                      {r.note && <span className="k-hint" style={{ fontStyle: "italic" }}>{r.note}</span>}
                    </div>
                  </div>
                ))}
              </div>
            )}
            {idle > 0 && fired.length > 0 && <span className="k-hint">{t("profiles.chain.moreIdle", { count: idle })}</span>}
          </Panel>
        </div>
      )}
    </section>
  );
}
