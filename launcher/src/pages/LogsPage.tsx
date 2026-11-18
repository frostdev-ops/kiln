// /logs: kiln.log, the game log, proxy.log, mod logs, crash reports, tick stats. A full-height well; Follow, Pause and
// Filter in the toolbar; the support report (one zip for a bug report) above the tabs. LogsFor is reused by servers
// (their Logs page) with its own tabs.
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import * as api from "../api/commands";
import type { LogFile, LogTarget } from "../api/commands";
import { qk, useGame, usePoll } from "../api/hooks";
import { useT, type TFunction } from "../i18n";
import { Button, EmptyState, LogView, Page, PageHeader, Select, Skeleton, Table, Tabs, WellInput, errorText, fmtAgo, fmtBytes, toast, type Column } from "../ui";
import { TICKS, TickStats } from "./ticks";
import { EngineStatusList, RestoreVanillaOffer } from "../components/EngineStatus";
import { SupportReport } from "../components/SupportReport";

export type LogTab = "kiln" | "game" | "proxy" | "mods" | "crash" | "ticks" | "engines";
function tabLabel(t: TFunction, tab: LogTab): string {
  switch (tab) {
    case "kiln": return "Kiln";
    case "game": return t("logs.tab.game");
    case "proxy": return t("logs.tab.proxy");
    case "mods": return t("logs.tab.mods");
    case "crash": return t("logs.tab.crash");
    case "ticks": return t("logs.tab.ticks");
    case "engines": return t("logs.tab.engines");
  }
}

export default function LogsPage() {
  const t = useT();
  const { game } = useGame();
  if (!game) return <Page><PageHeader title={t("logs.title")} sub={t("logs.noGame")} /><SupportReport target={null} /></Page>;
  return <LogsFor target={{ game: game.id }} folder={`${game.path}/kiln/logs`} />;
}

/** Logs of a game or a server. `embedded`: inside a layout with its own title (a server); `tabs`: which tabs show. */
export function LogsFor({ target, folder, embedded, tabs: wanted = ["kiln", "game", "proxy", "mods", "crash", "ticks", "engines"] }: { target: LogTarget; folder?: string; embedded?: boolean; tabs?: LogTab[] }) {
  const t = useT();
  // Engines: the engine plugins' state files, a game's only (engine_status)
  const tabs = "game" in target ? wanted : wanted.filter((tb) => tb !== "engines");
  const files = useQuery({ queryKey: qk.logs(target), queryFn: () => api.logsList(target), refetchInterval: usePoll(10000) });
  const [tab, setTab] = useState<LogTab>(tabs[0]);
  const [chosen, setChosen] = useState<Partial<Record<LogTab, string>>>({});
  const [filter, setFilter] = useState("");
  const [paused, setPaused] = useState(false);
  const list = files.data ?? [];
  // the files of a tab, the usual one first (kiln.log, enshrouded.log, proxy.log), then the newest
  const MAIN: Partial<Record<LogTab, RegExp>> = { kiln: /(^|\/)kiln\.log$/, game: /(^|\/)enshrouded\.log$/, proxy: /(^|\/)proxy\.log$/ };
  const KIND: Partial<Record<LogTab, LogFile["kind"]>> = { kiln: "kiln", game: "game", proxy: "proxy", mods: "mod" };
  const ofTab = (tb: LogTab) => list.filter((f) => f.kind === KIND[tb] && !f.name.endsWith(".jsonl"))
    .sort((a, b) => Number(MAIN[tb]?.test(b.name) ?? 0) - Number(MAIN[tb]?.test(a.name) ?? 0) || b.modified.localeCompare(a.modified));
  const tabFiles = ofTab(tab);
  const mods = ofTab("mods");
  const file = tabFiles.find((f) => f.name === chosen[tab]) ?? tabFiles[0];
  const isFile = tab !== "crash" && tab !== "ticks" && tab !== "engines";
  const say = files.error ? errorText(files.error) : !isFile ? (tab === "ticks" ? t("logs.say.ticks") : tab === "engines" ? t("logs.say.engines")
    : t("logs.say.crash"))
    : file ? t("logs.say.file", { name: file.name.split("/").pop(), size: fmtBytes(file.size), ago: fmtAgo(file.modified) }) : t("logs.say.notYet");
  const openFolder = folder && <Button onClick={() => api.appOpenPath(folder).catch((e) => toast.error(e))}>{t("logs.openFolder")}</Button>;
  const body = (
    <>
      {!embedded && <PageHeader title={t("logs.title")} sub={say} actions={openFolder} />}
      <SupportReport target={target} compact={embedded} />
      {tabs.length > 1 && <Tabs aria-label={t("logs.tabsLabel")} value={tab} onChange={setTab} items={tabs.map((tb) => ({ id: tb, label: tabLabel(t, tb), count: tb === "mods" ? mods.length : undefined }))} />}
      {files.error ? <EmptyState title={t("logs.unreadable")} hint={errorText(files.error)} /> : files.isLoading ? <Skeleton /> : (
        tab === "engines" && "game" in target ? (
          <div className="k-scroll k-stack" style={{ gap: 14 }}>
            <RestoreVanillaOffer gameId={target.game} />
            <EngineStatusList gameId={target.game} />
          </div>
        ) : tab === "crash" ? <Crashes target={target} /> : tab === "ticks" ? <TickStats target={target} fill /> : (
          <>
            <div className="k-console-bar">
              {embedded && <span className="k-mono" style={{ color: "var(--k-text-1)" }} title={file?.path}>{file ? file.name.split("/").pop() : t("logs.noFile")}</span>}
              {tabFiles.length > 1 && <Select aria-label={t("logs.fileLabel")} value={file?.name ?? ""} onChange={(n) => setChosen({ ...chosen, [tab]: n })}
                options={tabFiles.map((m) => ({ value: m.name, label: m.name.split("/").pop() ?? m.name }))} style={{ height: 32, width: 220 }} />}
              <div className="k-seg">
                <button type="button" className={paused ? "" : "on"} aria-pressed={!paused} onClick={() => setPaused(false)}>{t("logs.follow")}</button>
                <button type="button" className={paused ? "on" : ""} aria-pressed={paused} onClick={() => setPaused(true)}>{t("logs.pause")}</button>
              </div>
              <WellInput label={t("logs.filter")} compact value={filter} onChange={(e) => setFilter(e.target.value)} onClear={() => setFilter("")} wellStyle={{ flex: "1 1 140px", marginLeft: "auto" }} />
            </div>
            {file ? <FollowedLog key={file.name} target={target} file={file.name} filter={filter} paused={paused} /> : <EmptyState title={t("logs.notYetTitle")} />}
          </>
        )
      )}
    </>
  );
  return embedded ? <div className="k-page k-page-fill" style={{ padding: "0 0 18px" }}>{body}</div> : <Page fill>{body}</Page>;
}

/** Reads the tail, then follows (kiln://log). Paused: new lines wait. */
function FollowedLog({ target, file, filter, paused }: { target: LogTarget; file: string; filter: string; paused: boolean }) {
  const t = useT();
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
      setLines(c.truncated ? [t("logs.olderCut"), ...ls] : ls);
      return api.onKilnEvent("kiln://log", (e) => {
        if (e.subscription !== sub) return;
        setLines((prev) => { const next = [...(prev ?? []), ...e.lines]; return next.length > 50000 ? next.slice(-40000) : next; });
      }).then((f) => { off = f; return api.logsFollow(target, file); }).then((s) => { sub = s; if (!live) api.logsUnfollow(s).catch(() => {}); });
    }).catch((e) => live && setErr(errorText(e)));
    return () => { live = false; off?.(); if (sub) api.logsUnfollow(sub).catch(() => {}); };
  }, [file, JSON.stringify(target)]);
  if (err) return <EmptyState title={t("logs.cantRead")} hint={err} />;
  if (!lines) return <Skeleton lines={6} />;
  return <LogView lines={frozen.current ?? lines} filter={filter || undefined} fill follow={!paused} aria-label={file} />;
}

function Crashes({ target }: { target: LogTarget }) {
  const t = useT();
  const q = useQuery({ queryKey: qk.crashes(target), queryFn: () => api.logsCrashReports(target) });
  const [open, setOpen] = useState<string | null>(null);
  const cols: Column<api.CrashReport>[] = [
    { key: "name", header: t("logs.crashes.report"), render: (c) => <span className="k-mono">{c.name}</span> },
    { key: "created", header: t("logs.crashes.when"), width: "140px", render: (c) => <span className="k-muted">{fmtAgo(c.created)}</span> },
    { key: "size", header: t("logs.crashes.size"), width: "90px", align: "right", render: (c) => <span className="k-mono">{fmtBytes(c.size)}</span> },
  ];
  return (
    <div className="k-stack" style={{ gap: 14, flex: 1, minHeight: 0 }}>
      {q.error ? <EmptyState title={t("logs.crashes.unreadable")} hint={errorText(q.error)} /> : (
        <Table aria-label={t("logs.crashes.label")} columns={cols} rows={q.data ?? []} rowKey={(c) => c.name} onRowClick={(c) => setOpen(c.name === open ? null : c.name)} selectedKey={open} maxHeight={open ? 140 : 420}
          empty={<EmptyState title={t("logs.crashes.empty")} />} />
      )}
      {open && <FollowedLog target={target} file={`kiln/crash-reports/${open}`} filter="" paused={false} />}
    </div>
  );
}

export { TICKS };
