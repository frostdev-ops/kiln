// Tick stats (kiln/logs/tick-stats.jsonl: one line per minute while a world runs): the hook, the small aside chart of
// a server's overview, and the full view (chart, last minute, the per-minute table) of Logs and a server's Tick stats.
import { useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import * as api from "../api/commands";
import type { LogTarget } from "../api/commands";
import { qk } from "../api/hooks";
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

const ago = (i: number, n: number) => (i === n - 1 ? "now" : `${n - 1 - i} min ago`);

/** Step p99 over the last minutes: the value, when, the chart. */
export function StepChart({ ticks, minutes = 8, height = 96 }: { ticks: Tick[]; minutes?: number; height?: number }) {
  const recent = ticks.slice(-minutes);
  const [at, setAt] = useState(-1);
  const i = at >= 0 && at < recent.length ? at : recent.length - 1;
  const vals = recent.map((t) => t.step_ms.p99);
  if (!recent.length) return <span className="k-empty-title">No tick stats yet. Kiln writes them each minute a world runs.</span>;
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
      <span className="k-label" style={{ fontSize: 16 }}>Step p99, {recent.length} minute{recent.length === 1 ? "" : "s"}</span>
      <span className="k-stat-big">{vals[i].toFixed(2)}<small> ms</small></span>
      <span className="k-hint" style={{ fontSize: 15, minHeight: 20 }}>{ago(i, recent.length)}</span>
      <div style={{ marginTop: 4 }}>
        <TickChart values={vals} height={height} onHover={setAt}
          aria-label={`Step p99 over the last ${recent.length} minutes, from ${Math.min(...vals).toFixed(2)} to ${Math.max(...vals).toFixed(2)} milliseconds, now ${vals[vals.length - 1].toFixed(2)}.`} />
      </div>
    </div>
  );
}

/** The full tick-stats view. */
export function TickStats({ target, fill }: { target: LogTarget; fill?: boolean }) {
  const { has, ticks, loading, error } = useTicks(target);
  if (loading) return <Skeleton />;
  if (error) return <EmptyState title="The tick stats are not readable" hint={errorText(error)} />;
  if (!has || !ticks.length) return <EmptyState title="No tick stats yet." hint="Kiln writes one line each minute while a world runs." />;
  const last = ticks[ticks.length - 1];
  const cols: Column<Tick>[] = [
    { key: "time", header: "Minute", render: (t) => <span className="k-mono">{t.time.replace("T", " ")}</span> },
    { key: "rate", header: "Steps/s", width: "90px", align: "right", render: (t) => <span className="k-mono">{t.rate.toFixed(2)}</span> },
    { key: "p50", header: "Step p50", width: "100px", align: "right", render: (t) => <span className="k-mono">{t.step_ms.p50.toFixed(2)} ms</span> },
    { key: "p99", header: "Step p99", width: "100px", align: "right", render: (t) => <span className="k-mono">{t.step_ms.p99.toFixed(2)} ms</span> },
    { key: "max", header: "Max", width: "90px", align: "right", render: (t) => <span className="k-mono">{t.step_ms.max.toFixed(1)} ms</span> },
    { key: "pre", header: "Kiln p99", width: "100px", align: "right", render: (t) => <span className="k-mono">{t.pre_ms ? `${t.pre_ms.p99.toFixed(2)} ms` : "-"}</span> },
  ];
  return (
    <div className="k-stack" style={{ gap: 18, flex: fill ? 1 : undefined, minHeight: 0 }}>
      <div className="k-ticks-top">
        <div style={{ flex: 1, minWidth: 0 }}><StepChart ticks={ticks} minutes={60} height={120} /></div>
        <div style={{ flex: "none", width: 220 }}>
          <span className="k-label" style={{ fontSize: 16 }}>Last minute</span>
          <KeyValue rows={[
            ["Steps/s", <span className="k-mono">{last.rate.toFixed(2)}</span>],
            ["Steps", <span className="k-mono">{last.steps}</span>],
            ["Max step", <span className="k-mono">{last.step_ms.max.toFixed(1)} ms</span>],
            ["Subscribers", last.subs ? <span className="k-mono">{last.subs.pre} pre, {last.subs.post} post</span> : "-"],
            last.world && ["World", last.world],
          ]} />
        </div>
      </div>
      <Table aria-label="Tick stats per minute" columns={cols} rows={[...ticks].reverse()} rowKey={(t, i) => t.time + i} fill={fill} maxHeight={420} />
    </div>
  );
}
