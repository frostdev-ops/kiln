// /logs: kiln.log, the game log, proxy.log, mod logs, crash reports, tick stats.
import { useQuery } from "@tanstack/react-query";
import { FolderOpen, Search } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import * as api from "../api/commands";
import type { LogFile, LogTarget } from "../api/commands";
import { qk, useGame } from "../api/hooks";
import { Card, EmptyState, IconButton, KeyValue, LogView, Page, PageHeader, Select, Skeleton, Sparkline, Table, Tabs, TextInput, errorText, fmtAgo, fmtBytes, toast, type Column } from "../ui";

type Tab = "kiln" | "game" | "proxy" | "mods" | "crash" | "ticks";
const TICKS = "kiln/logs/tick-stats.jsonl";

export default function LogsPage() {
  const { game } = useGame();
  if (!game) return <Page><PageHeader title="Logs" /><EmptyState title="No game" hint="Pick a game on Play." /></Page>;
  return <LogsFor target={{ game: game.id }} folder={`${game.path}/kiln/logs`} />;
}

/** Reusable for servers: <LogsFor target={{ server: id }} folder=… embedded />. `embedded`: no page frame or title
 *  (inside a layout that has its own, like the server pages). */
export function LogsFor({ target, folder, embedded }: { target: LogTarget; folder?: string; embedded?: boolean }) {
  const files = useQuery({ queryKey: qk.logs(target), queryFn: () => api.logsList(target), refetchInterval: 10000 });
  const [tab, setTab] = useState<Tab>("kiln");
  const [chosen, setChosen] = useState<Partial<Record<Tab, string>>>({});
  const [filter, setFilter] = useState("");
  const list = files.data ?? [];
  // the files of a tab, the usual one first (kiln.log, enshrouded.log, proxy.log), then the newest
  const MAIN: Partial<Record<Tab, RegExp>> = { kiln: /(^|\/)kiln\.log$/, game: /(^|\/)enshrouded\.log$/, proxy: /(^|\/)proxy\.log$/ };
  const KIND: Partial<Record<Tab, LogFile["kind"]>> = { kiln: "kiln", game: "game", proxy: "proxy", mods: "mod" };
  const ofTab = (t: Tab) => list.filter((f) => f.kind === KIND[t] && !f.name.endsWith(".jsonl"))
    .sort((a, b) => Number(MAIN[t]?.test(b.name) ?? 0) - Number(MAIN[t]?.test(a.name) ?? 0) || b.modified.localeCompare(a.modified));
  const tabFiles = ofTab(tab);
  const mods = ofTab("mods");
  const file = tabFiles.find((f) => f.name === chosen[tab]) ?? tabFiles[0];
  const openFolder = folder && <IconButton label="Open folder" icon={<FolderOpen />} onClick={() => api.appOpenPath(folder).catch((e) => toast.error(e))} />;
  const body = (
    <>
      {!embedded && <PageHeader title="Logs" actions={openFolder} />}
      <Tabs aria-label="Log" value={tab} onChange={setTab} items={[
        { id: "kiln", label: "Kiln" }, { id: "game", label: "Game" }, { id: "proxy", label: "Proxy" },
        { id: "mods", label: "Mods", count: mods.length }, { id: "crash", label: "Crashes" }, { id: "ticks", label: "Tick stats" },
      ]} />
      {files.error ? <EmptyState title="Logs unavailable" hint={errorText(files.error)} /> : files.isLoading ? <Skeleton /> : (
        tab === "crash" ? <Crashes target={target} /> : tab === "ticks" ? <TickStats target={target} has={list.some((f) => f.name === TICKS)} /> : (
          <Card flush actions={<>
            {tabFiles.length > 1 && <Select aria-label="Log file" value={file?.name ?? ""} onChange={(n) => setChosen({ ...chosen, [tab]: n })}
              options={tabFiles.map((m) => ({ value: m.name, label: m.name.split("/").pop() ?? m.name }))} style={{ height: 28, width: 240 }} />}
            <div className="k-input-group" style={{ width: 200 }}><Search size={14} className="k-dim" /><TextInput placeholder="Filter" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ height: 28 }} aria-label="Filter lines" /></div>
          </>} title={file ? <span className="k-mono k-ellipsis" style={{ fontSize: 14 }} title={file.path}>{file.name}</span> : "No file"} sub={file ? `${fmtBytes(file.size)} · ${fmtAgo(file.modified)}` : undefined}>
            {file ? <FollowedLog key={file.name} target={target} file={file.name} filter={filter} /> : <EmptyState title="Not written yet" hint="It appears after the next start." />}
          </Card>
        )
      )}
    </>
  );
  return embedded ? <div className="k-stack" style={{ gap: 16 }}>{body}</div> : <Page wide>{body}</Page>;
}

/** Reads the tail, then follows (kiln://log). */
function FollowedLog({ target, file, filter }: { target: LogTarget; file: string; filter: string }) {
  const [lines, setLines] = useState<string[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    let sub: string | null = null;
    let off: (() => void) | undefined;
    api.logsRead(target, file, null, 512 * 1024).then((c) => {
      if (!live) return;
      const ls = c.text.replace(/\n$/, "").split("\n");
      setLines(c.truncated ? ["… (older lines cut)", ...ls] : ls);
      return api.onKilnEvent("kiln://log", (e) => {
        if (e.subscription !== sub) return;
        setLines((prev) => { const next = [...(prev ?? []), ...e.lines]; return next.length > 50000 ? next.slice(-40000) : next; });
      }).then((f) => { off = f; return api.logsFollow(target, file); }).then((s) => { sub = s; if (!live) api.logsUnfollow(s).catch(() => {}); });
    }).catch((e) => live && setErr(errorText(e)));
    return () => { live = false; off?.(); if (sub) api.logsUnfollow(sub).catch(() => {}); };
  }, [file, JSON.stringify(target)]);
  if (err) return <EmptyState title="Can't read it" hint={err} />;
  if (!lines) return <div style={{ padding: 16 }}><Skeleton lines={6} /></div>;
  return <div style={{ padding: 10 }}><LogView lines={lines} filter={filter || undefined} height={Math.max(360, window.innerHeight - 330)} aria-label={file} /></div>;
}

function Crashes({ target }: { target: LogTarget }) {
  const q = useQuery({ queryKey: qk.crashes(target), queryFn: () => api.logsCrashReports(target) });
  const [open, setOpen] = useState<string | null>(null);
  const cols: Column<api.CrashReport>[] = [
    { key: "name", header: "Report", width: "minmax(0, 2fr)", render: (c) => <span className="k-mono">{c.name}</span> },
    { key: "created", header: "When", width: "140px", render: (c) => fmtAgo(c.created) },
    { key: "size", header: "Size", width: "90px", align: "right", render: (c) => fmtBytes(c.size) },
  ];
  return (
    <div className="k-stack" style={{ gap: 16 }}>
      <Card flush title="Crash reports">
        {q.error ? <EmptyState title="Unavailable" hint={errorText(q.error)} /> : (
          <Table aria-label="Crash reports" columns={cols} rows={q.data ?? []} rowKey={(c) => c.name} onRowClick={(c) => setOpen(c.name)} selectedKey={open}
            empty={<EmptyState title="No crashes" hint="Kiln writes one when a mod faults too often." />} />
        )}
      </Card>
      {open && <Card flush title={<span className="k-mono" style={{ fontSize: 13 }}>{open}</span>}><FollowedLog target={target} file={`kiln/crash-reports/${open}`} filter="" /></Card>}
    </div>
  );
}

interface Tick { time: string; steps: number; rate: number; step_ms: { p50: number; p99: number; max: number }; pre_ms?: { p99: number }; subs?: { pre: number; post: number }; world?: string }

function TickStats({ target, has }: { target: LogTarget; has: boolean }) {
  const q = useQuery({ queryKey: ["tick-stats", target], queryFn: () => api.logsRead(target, TICKS, null, 256 * 1024), enabled: has, refetchInterval: 30000 });
  const ticks = useMemo<Tick[]>(() => (q.data?.text ?? "").split("\n").flatMap((l) => { try { return l.trim() ? [JSON.parse(l) as Tick] : []; } catch { return []; } }), [q.data]);
  if (!has) return <EmptyState title="No tick stats" hint="Kiln writes them every 60 s while a world runs." />;
  if (q.isLoading) return <Skeleton />;
  if (q.error) return <EmptyState title="Can't read them" hint={errorText(q.error)} />;
  const last = ticks[ticks.length - 1];
  const recent = ticks.slice(-60);
  const cols: Column<Tick>[] = [
    { key: "time", header: "Minute", width: "minmax(0, 1.4fr)", render: (t) => <span className="k-mono">{t.time.replace("T", " ")}</span> },
    { key: "rate", header: "Steps/s", width: "90px", align: "right", render: (t) => <span className="k-num">{t.rate.toFixed(2)}</span> },
    { key: "p50", header: "Step p50", width: "100px", align: "right", render: (t) => <span className="k-num">{t.step_ms.p50.toFixed(2)} ms</span> },
    { key: "p99", header: "Step p99", width: "100px", align: "right", render: (t) => <span className="k-num">{t.step_ms.p99.toFixed(2)} ms</span> },
    { key: "pre", header: "Kiln p99", width: "100px", align: "right", render: (t) => <span className="k-num">{t.pre_ms ? `${t.pre_ms.p99.toFixed(2)} ms` : "-"}</span> },
  ];
  return (
    <div className="k-stack" style={{ gap: 16 }}>
      <div className="k-grid k-grid-3">
        <Card title="Step p99" sub={last ? `${last.step_ms.p99.toFixed(2)} ms now` : undefined}><Sparkline values={recent.map((t) => t.step_ms.p99)} width={300} height={44} aria-label="Step p99 trend" /></Card>
        <Card title="Steps per second" sub={last ? `${last.rate.toFixed(2)} now` : undefined}><Sparkline values={recent.map((t) => t.rate)} width={300} height={44} aria-label="Step rate trend" /></Card>
        <Card title="Last minute">{last ? <KeyValue rows={[["Steps", last.steps], ["Max step", `${last.step_ms.max.toFixed(1)} ms`], ["Subscribers", last.subs ? `${last.subs.pre} pre · ${last.subs.post} post` : "-"], last.world && ["World", last.world]]} /> : "-"}</Card>
      </div>
      <Card flush title="Per minute"><Table aria-label="Tick stats" columns={cols} rows={[...ticks].reverse()} rowKey={(t, i) => t.time + i} rowHeight={36} maxHeight={420} /></Card>
    </div>
  );
}
