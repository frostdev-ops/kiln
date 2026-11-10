// /logs: kiln.log, the game log, proxy.log, mod logs, crash reports, tick stats. A full-height well; Follow, Pause and
// Filter in the toolbar; the support report (one zip for a bug report) above the tabs. LogsFor is reused by servers
// (their Logs page) with its own tabs.
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import * as api from "../api/commands";
import type { LogFile, LogTarget } from "../api/commands";
import { qk, useGame } from "../api/hooks";
import { Button, EmptyState, LogView, Page, PageHeader, Select, Skeleton, Table, Tabs, WellInput, errorText, fmtAgo, fmtBytes, toast, type Column } from "../ui";
import { TICKS, TickStats } from "./ticks";
import { EngineStatusList, RestoreVanillaOffer } from "../components/EngineStatus";
import { SupportReport } from "../components/SupportReport";

export type LogTab = "kiln" | "game" | "proxy" | "mods" | "crash" | "ticks" | "engines";
const TAB_LABEL: Record<LogTab, string> = { kiln: "Kiln", game: "Game", proxy: "Proxy", mods: "Mods", crash: "Crashes", ticks: "Tick stats", engines: "Engines" };

export default function LogsPage() {
  const { game } = useGame();
  if (!game) return <Page><PageHeader title="Logs" sub="No game yet. Find Enshrouded on Play first." /><SupportReport target={null} /></Page>;
  return <LogsFor target={{ game: game.id }} folder={`${game.path}/kiln/logs`} />;
}

/** Logs of a game or a server. `embedded`: inside a layout with its own title (a server); `tabs`: which tabs show. */
export function LogsFor({ target, folder, embedded, tabs: wanted = ["kiln", "game", "proxy", "mods", "crash", "ticks", "engines"] }: { target: LogTarget; folder?: string; embedded?: boolean; tabs?: LogTab[] }) {
  // Engines: the engine plugins' state files, a game's only (engine_status)
  const tabs = "game" in target ? wanted : wanted.filter((t) => t !== "engines");
  const files = useQuery({ queryKey: qk.logs(target), queryFn: () => api.logsList(target), refetchInterval: 10000 });
  const [tab, setTab] = useState<LogTab>(tabs[0]);
  const [chosen, setChosen] = useState<Partial<Record<LogTab, string>>>({});
  const [filter, setFilter] = useState("");
  const [paused, setPaused] = useState(false);
  const list = files.data ?? [];
  // the files of a tab, the usual one first (kiln.log, enshrouded.log, proxy.log), then the newest
  const MAIN: Partial<Record<LogTab, RegExp>> = { kiln: /(^|\/)kiln\.log$/, game: /(^|\/)enshrouded\.log$/, proxy: /(^|\/)proxy\.log$/ };
  const KIND: Partial<Record<LogTab, LogFile["kind"]>> = { kiln: "kiln", game: "game", proxy: "proxy", mods: "mod" };
  const ofTab = (t: LogTab) => list.filter((f) => f.kind === KIND[t] && !f.name.endsWith(".jsonl"))
    .sort((a, b) => Number(MAIN[t]?.test(b.name) ?? 0) - Number(MAIN[t]?.test(a.name) ?? 0) || b.modified.localeCompare(a.modified));
  const tabFiles = ofTab(tab);
  const mods = ofTab("mods");
  const file = tabFiles.find((f) => f.name === chosen[tab]) ?? tabFiles[0];
  const isFile = tab !== "crash" && tab !== "ticks" && tab !== "engines";
  const say = files.error ? errorText(files.error) : !isFile ? (tab === "ticks" ? "Kiln writes a line each minute a world runs." : tab === "engines" ? "Other engines' plugins: what each one did at the last start."
    : "Kiln writes one when a mod faults too often.")
    : file ? `${file.name.split("/").pop()}, ${fmtBytes(file.size)}, written ${fmtAgo(file.modified)}.` : "Not written yet: it appears after the next start.";
  const openFolder = folder && <Button onClick={() => api.appOpenPath(folder).catch((e) => toast.error(e))}>Open folder</Button>;
  const body = (
    <>
      {!embedded && <PageHeader title="Logs" sub={say} actions={openFolder} />}
      <SupportReport target={target} compact={embedded} />
      {tabs.length > 1 && <Tabs aria-label="Log" value={tab} onChange={setTab} items={tabs.map((t) => ({ id: t, label: TAB_LABEL[t], count: t === "mods" ? mods.length : undefined }))} />}
      {files.error ? <EmptyState title="The logs are not readable" hint={errorText(files.error)} /> : files.isLoading ? <Skeleton /> : (
        tab === "engines" && "game" in target ? (
          <div className="k-scroll k-stack" style={{ gap: 14 }}>
            <RestoreVanillaOffer gameId={target.game} />
            <EngineStatusList gameId={target.game} />
          </div>
        ) : tab === "crash" ? <Crashes target={target} /> : tab === "ticks" ? <TickStats target={target} fill /> : (
          <>
            <div className="k-console-bar">
              {embedded && <span className="k-mono" style={{ color: "var(--k-text-1)" }} title={file?.path}>{file ? file.name.split("/").pop() : "No file"}</span>}
              {tabFiles.length > 1 && <Select aria-label="Log file" value={file?.name ?? ""} onChange={(n) => setChosen({ ...chosen, [tab]: n })}
                options={tabFiles.map((m) => ({ value: m.name, label: m.name.split("/").pop() ?? m.name }))} style={{ height: 32, width: 220 }} />}
              <div className="k-seg">
                <button type="button" className={paused ? "" : "on"} aria-pressed={!paused} onClick={() => setPaused(false)}>Follow</button>
                <button type="button" className={paused ? "on" : ""} aria-pressed={paused} onClick={() => setPaused(true)}>Pause</button>
              </div>
              <WellInput label="Filter" compact value={filter} onChange={(e) => setFilter(e.target.value)} wellStyle={{ flex: "1 1 140px", marginLeft: "auto" }} />
            </div>
            {file ? <FollowedLog key={file.name} target={target} file={file.name} filter={filter} paused={paused} /> : <EmptyState title="Not written yet. It appears after the next start." />}
          </>
        )
      )}
    </>
  );
  return embedded ? <div className="k-page k-page-fill" style={{ padding: "0 0 18px" }}>{body}</div> : <Page fill>{body}</Page>;
}

/** Reads the tail, then follows (kiln://log). Paused: new lines wait. */
function FollowedLog({ target, file, filter, paused }: { target: LogTarget; file: string; filter: string; paused: boolean }) {
  const [lines, setLines] = useState<string[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const frozen = useRef<string[] | null>(null);
  if (paused && !frozen.current) frozen.current = lines;
  if (!paused) frozen.current = null;
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
  if (!lines) return <Skeleton lines={6} />;
  return <LogView lines={frozen.current ?? lines} filter={filter || undefined} fill follow={!paused} aria-label={file} />;
}

function Crashes({ target }: { target: LogTarget }) {
  const q = useQuery({ queryKey: qk.crashes(target), queryFn: () => api.logsCrashReports(target) });
  const [open, setOpen] = useState<string | null>(null);
  const cols: Column<api.CrashReport>[] = [
    { key: "name", header: "Report", render: (c) => <span className="k-mono">{c.name}</span> },
    { key: "created", header: "When", width: "140px", render: (c) => <span className="k-muted">{fmtAgo(c.created)}</span> },
    { key: "size", header: "Size", width: "90px", align: "right", render: (c) => <span className="k-mono">{fmtBytes(c.size)}</span> },
  ];
  return (
    <div className="k-stack" style={{ gap: 14, flex: 1, minHeight: 0 }}>
      {q.error ? <EmptyState title="The reports are not readable" hint={errorText(q.error)} /> : (
        <Table aria-label="Crash reports" columns={cols} rows={q.data ?? []} rowKey={(c) => c.name} onRowClick={(c) => setOpen(c.name === open ? null : c.name)} selectedKey={open} maxHeight={open ? 140 : 420}
          empty={<EmptyState title="No crash reports. Kiln writes one when a mod faults too often." />} />
      )}
      {open && <FollowedLog target={target} file={`kiln/crash-reports/${open}`} filter="" paused={false} />}
    </div>
  );
}

export { TICKS };
