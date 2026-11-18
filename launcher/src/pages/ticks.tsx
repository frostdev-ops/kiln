// Tick stats (kiln/logs/tick-stats.jsonl: one line per minute while a world runs): the hook, the small aside chart of
// a server's overview, and the full view (chart, last minute, the per-minute table) of Logs and a server's Tick stats.
import { useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import * as api from "../api/commands";
import type { LogTarget } from "../api/commands";
import { qk } from "../api/hooks";
import { useT, type TFunction } from "../i18n";
import { EmptyState, KeyValue, Skeleton, Table, TickChart, errorText, type Column } from "../ui";

export const TICKS = "kiln/logs/tick-stats.jsonl";

export interface Tick { time: string; steps: number; rate: number; step_ms: { p50: number; p99: number; max: number }; pre_ms?: { p99: number }; subs?: { pre: number; post: number }; world?: string }

/** The tick stats of a game or server (null while there is no file). */
export function useTicks(target: LogTarget, poll = 30000) {
  const files = useQuery({ queryKey: qk.logs(target), queryFn: () => api.logsList(target), refetchInterval: 15000 });
  const has = !!files.data?.some((f) => f.name === TICKS);
  const q = useQuery({ queryKey: ["tick-stats", target], queryFn: () => api.logsRead(target, TICKS, null, 256 * 1024), enabled: has, refetchInterval: poll });
  const ticks = useMemo<Tick[]>(() => (q.data?.text ?? "").split("\n").flatMap((l) => { try { return l.trim() ? [JSON.parse(l) as Tick] : []; } catch { return []; } }), [q.data]);
  return { has, ticks, loading: files.isLoading || q.isLoading, error: files.error ?? q.error };
}

const ago = (t: TFunction, i: number, n: number) => (i === n - 1 ? t("ticks.now") : t("ticks.minAgo", { n: n - 1 - i }));

/** Step p99 over the last minutes: the value, when, the chart. */
export function StepChart({ ticks, minutes = 8, height = 96 }: { ticks: Tick[]; minutes?: number; height?: number }) {
  const t = useT();
  const recent = ticks.slice(-minutes);
  const [at, setAt] = useState(-1);
  const i = at >= 0 && at < recent.length ? at : recent.length - 1;
  const vals = recent.map((tick) => tick.step_ms.p99);
  if (!recent.length) return <span className="k-empty-title">{t("ticks.noneYetShort")}</span>;
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
      <span className="k-label" style={{ fontSize: 16 }}>{t("ticks.stepMinutes", { count: recent.length })}</span>
      <span className="k-stat-big">{vals[i].toFixed(2)}<small>{t("ticks.msSuffix")}</small></span>
      <span className="k-hint" style={{ fontSize: 15, minHeight: 20 }}>{ago(t, i, recent.length)}</span>
      <div style={{ marginTop: 4 }}>
        <TickChart values={vals} height={height} onHover={setAt}
          aria-label={t("ticks.chartLabel", { count: recent.length, min: Math.min(...vals).toFixed(2), max: Math.max(...vals).toFixed(2), now: vals[vals.length - 1].toFixed(2) })} />
      </div>
    </div>
  );
}

/** The full tick-stats view. */
export function TickStats({ target, fill }: { target: LogTarget; fill?: boolean }) {
  const t = useT();
  const { has, ticks, loading, error } = useTicks(target);
  if (loading) return <Skeleton />;
  if (error) return <EmptyState title={t("ticks.unreadable")} hint={errorText(error)} />;
  if (!has || !ticks.length) return <EmptyState title={t("ticks.noneYet")} hint={t("ticks.noneYetHint")} />;
  const last = ticks[ticks.length - 1];
  const cols: Column<Tick>[] = [
    { key: "time", header: t("ticks.col.minute"), render: (tick) => <span className="k-mono">{tick.time.replace("T", " ")}</span> },
    { key: "rate", header: t("ticks.col.rate"), width: "90px", align: "right", render: (tick) => <span className="k-mono">{tick.rate.toFixed(2)}</span> },
    { key: "p50", header: t("ticks.col.p50"), width: "100px", align: "right", render: (tick) => <span className="k-mono">{t("ticks.ms", { v: tick.step_ms.p50.toFixed(2) })}</span> },
    { key: "p99", header: t("ticks.col.p99"), width: "100px", align: "right", render: (tick) => <span className="k-mono">{t("ticks.ms", { v: tick.step_ms.p99.toFixed(2) })}</span> },
    { key: "max", header: t("ticks.col.max"), width: "90px", align: "right", render: (tick) => <span className="k-mono">{t("ticks.ms", { v: tick.step_ms.max.toFixed(1) })}</span> },
    { key: "pre", header: t("ticks.col.kilnP99"), width: "100px", align: "right", render: (tick) => <span className="k-mono">{tick.pre_ms ? t("ticks.ms", { v: tick.pre_ms.p99.toFixed(2) }) : "-"}</span> },
  ];
  return (
    <div className="k-stack" style={{ gap: 18, flex: fill ? 1 : undefined, minHeight: 0 }}>
      <div className="k-ticks-top">
        <div style={{ flex: 1, minWidth: 0 }}><StepChart ticks={ticks} minutes={60} height={120} /></div>
        <div style={{ flex: "none", width: 220 }}>
          <span className="k-label" style={{ fontSize: 16 }}>{t("ticks.lastMinute")}</span>
          <KeyValue rows={[
            [t("ticks.col.rate"), <span className="k-mono">{last.rate.toFixed(2)}</span>],
            [t("ticks.steps"), <span className="k-mono">{last.steps}</span>],
            [t("ticks.maxStep"), <span className="k-mono">{t("ticks.ms", { v: last.step_ms.max.toFixed(1) })}</span>],
            [t("ticks.subscribers"), last.subs ? <span className="k-mono">{t("ticks.subs", { pre: last.subs.pre, post: last.subs.post })}</span> : "-"],
            last.world && [t("ticks.world"), last.world],
          ]} />
        </div>
      </div>
      <Table aria-label={t("ticks.perMinute")} columns={cols} rows={[...ticks].reverse()} rowKey={(tick, i) => tick.time + i} fill={fill} maxHeight={420} />
    </div>
  );
}
