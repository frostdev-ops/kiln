// /servers/:id/admin/console: Kiln chat commands run as the launcher (an admin) or as one of
// the players (their permissions), with completion (command_complete), history (↑ ↓), typed help from the command tree
// (commands_describe), and the server's log (log_tail: kiln.log or the game's log).
import { useEffect, useMemo, useRef, useState } from "react";
import { callAdmin, useAdminCall, useAdminQuery, useAdminState, useHasOp } from "../../api/admin";
import { NeedsOp, steamIdError, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { knownPlayers, useHistory } from "../history";
import type { CommandInfo, CommandNode, CommandRunResult, CompleteResult, LogTail, PermissionsModel } from "../types";
import { Badge, Button, Card, EmptyState, LogView, Page, PageHeader, Segmented, Select, SuggestInput, TextInput, Toggle, toastOk, type Suggestion } from "../ui";

interface Line {
  k: "in" | "out" | "err" | "meta";
  t: string;
}
const STORE = (id: string) => `kiln.admin.console.v1.${id}`;
const LEVEL_TONE: Record<string, "ok" | "warn" | "err"> = { everyone: "ok", admin: "warn", console: "err" };

function loadSaved(id: string): { lines: Line[]; history: string[] } {
  try {
    const v = JSON.parse(localStorage.getItem(STORE(id)) ?? "{}");
    return { lines: v.lines ?? [], history: v.history ?? [] };
  } catch {
    return { lines: [], history: [] };
  }
}

function ConsolePage() {
  const [tab, setTab] = useState<"console" | "log">("console");
  return (
    <Page>
      <PageHeader title="Console" sub="Kiln chat commands, run as the launcher (an admin) or as one of your players to see what they may do."
        actions={<Segmented value={tab} onChange={setTab} options={[{ id: "console", label: "Commands" }, { id: "log", label: "Server log" }]} aria-label="View" />} />
      <OfflineNote />
      {tab === "console" ? (
        <NeedsOp ops={["command_run", "command_complete"]} what="The console">
          <ConsoleView />
        </NeedsOp>
      ) : (
        <NeedsOp ops="log_tail" what="The server log">
          <LogTab />
        </NeedsOp>
      )}
    </Page>
  );
}

function ConsoleView() {
  const id = useServerId();
  const saved = useMemo(() => loadSaved(id), [id]);
  const [lines, setLines] = useState<Line[]>(saved.lines);
  const [history, setHistory] = useState<string[]>(saved.history);
  const [hi, setHi] = useState(-1);
  const [text, setText] = useState("");
  const [as, setAs] = useState("");
  const [sugg, setSugg] = useState<Suggestion<string>[]>([]);
  const [sel, setSel] = useState<string | null>(null);
  const [q, setQ] = useState("");
  const logRef = useRef<HTMLDivElement>(null);
  const st = useAdminState();
  const hist = useHistory(id);
  const perms = useAdminQuery<PermissionsModel>("permissions_get", {}, { staleTime: 60_000 });
  const cmds = useAdminQuery<CommandInfo[]>("commands_describe", {}, { staleTime: 60_000 });
  const runOp = useAdminCall<CommandRunResult>("command_run");
  const complSeq = useRef(0);
  const people = knownPlayers(hist, perms.data?.players ?? {});
  for (const p of st.state?.players ?? []) if (p.steamId && !people.some((x) => x.steamId === p.steamId)) people.unshift({ steamId: p.steamId, name: p.name, firstSeen: 0, lastSeen: Date.now() / 1000 });

  useEffect(() => {
    try {
      localStorage.setItem(STORE(id), JSON.stringify({ lines: lines.slice(-300), history: history.slice(-100) }));
    } catch {
      /* full */
    }
  }, [id, lines, history]);
  useEffect(() => {
    const el = logRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [lines]);

  const out = (add: Line[]) => setLines((l) => [...l, ...add].slice(-400));
  const complete = async (value: string, apply: boolean) => {
    const seq = ++complSeq.current;
    if (!value.trim()) return setSugg([]);
    const full = value.startsWith("/") ? value : "/" + value;
    try {
      const r = await callAdmin<CompleteResult>(id, "command_complete", { text: full, ...(as ? { steamId: as } : {}) });
      if (seq !== complSeq.current) return;
      const off = value.startsWith("/") ? 0 : 1;
      const start = Math.max(0, r.start - off);
      const pick = (s: string) => value.slice(0, start) + s + (/^<.*>$/.test(s) ? "" : " ");
      const list = (r.suggestions ?? []).filter((s) => s !== value.slice(start));
      if (apply && list.length === 1) {
        setText(pick(list[0]));
        setSugg([]);
        return;
      }
      setSugg(list.slice(0, 40).map((s) => ({ key: s, label: <span className="k-mono">{s}</span>, value: pick(s) })));
    } catch {
      setSugg([]);
    }
  };
  useEffect(() => {
    const t = setTimeout(() => complete(text, false), 120);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [text, as]);

  const run = (raw: string) => {
    const line = raw.trim();
    if (!line) return;
    if (line.length > 1000) return out([{ k: "err", t: "Type a command (up to 1000 characters)." }]);
    const shown = line.startsWith("/") ? line : "/" + line;
    setHistory((h) => [...h.filter((x) => x !== line), line].slice(-100));
    setHi(-1);
    const who = as ? people.find((p) => p.steamId === as)?.name || as : "";
    out([{ k: "in", t: `${who ? `[as ${who}] ` : ""}> ${shown}` }]);
    setText("");
    setSugg([]);
    runOp.mutate({ line: shown, ...(as ? { steamId: as } : {}) }, {
      onSuccess: (r) => {
        const add: Line[] = (r.output ?? []).map((t) => ({ k: "out", t }));
        if (!r.ok) add.push({ k: "err", t: r.denied ? `Denied: ${r.error || "no permission"}` : r.found === false ? `Unknown command. ${r.error ?? ""}` : r.error || "Failed" });
        else if (!add.length) add.push({ k: "meta", t: "ok" });
        out(add);
      },
      onError: (e) => out([{ k: "err", t: e.message }]),
    });
  };
  const histStep = (dir: -1 | 1) => {
    if (!history.length) return;
    const cur = hi < 0 ? history.length : hi;
    const next = Math.max(0, Math.min(history.length, cur + dir));
    setHi(next);
    setText(history[next] ?? "");
  };
  const list = (cmds.data ?? []).filter((c) => !q || `${c.name} ${(c.aliases ?? []).join(" ")} ${c.description ?? ""} ${c.owner ?? ""}`.toLowerCase().includes(q.toLowerCase()));
  const selCmd = (cmds.data ?? []).find((c) => c.name === sel);
  return (
    <div className="ka-console">
      <div>
        <div className="ka-console-log" ref={logRef} aria-live="polite">
          {lines.length ? lines.map((l, i) => <div key={i} className={l.k}>{l.t}</div>) : <div className="meta">Type a command below, e.g. /help. Tab completes, ↑ ↓ go through the history.</div>}
        </div>
        <div className="ka-cmdline">
          <SuggestInput aria-label="Command" value={text} onChange={setText} suggestions={sugg} placeholder="/help" mono autoFocus
            onPick={(s) => setText(s.value)} onEnter={() => run(text)} onTab={() => complete(text, true)} onHistory={histStep} />
          <Button variant="primary" loading={runOp.isPending} onClick={() => run(text)}>Run</Button>
        </div>
        <div className="ka-inline" style={{ marginTop: 8 }}>
          <span className="k-muted">Run as</span>
          <Select aria-label="Run as" value={as} onChange={(v) => { if (v && steamIdError(v)) return; setAs(v); }}
            options={[{ value: "", label: "the launcher (admin)" }, ...people.map((p) => ({ value: p.steamId, label: `${p.name || p.steamId}${(st.state?.players ?? []).some((x) => x.steamId === p.steamId) ? " (online)" : ""}` }))]} />
          <span className="k-spacer" />
          <Button size="s" variant="ghost" onClick={() => setLines([])}>Clear</Button>
        </div>
      </div>
      <Card title="Commands" actions={<TextInput aria-label="Filter commands" placeholder="Filter…" value={q} onChange={(e) => setQ(e.target.value)} style={{ width: 150 }} />} flush>
        <div className="ka-pick" style={{ padding: 6, maxHeight: 300 }}>
          {list.map((c) => (
            <button key={c.name} className={c.name === sel ? "active" : ""} onClick={() => setSel(sel === c.name ? null : c.name)}>
              <span className="k-mono">/{c.name}</span><small>{c.owner}</small>
            </button>
          ))}
          {cmds.data && !list.length ? <EmptyState title="No command matches" /> : null}
        </div>
        {selCmd ? <CommandHelp c={selCmd} use={(u) => { setText(u.replace(/\s[<[].*$/, "") + " "); toastOk(`Usage: ${u}`); }} /> : null}
      </Card>
    </div>
  );
}

function args(node: CommandNode | undefined, acc: CommandNode[] = [], seen = new Set<string>()): CommandNode[] {
  for (const ch of node?.children ?? []) {
    if (ch.kind === "argument" && ch.name && !seen.has(ch.name)) {
      seen.add(ch.name);
      acc.push(ch);
    }
    args(ch, acc, seen);
  }
  return acc;
}

function CommandHelp({ c, use }: { c: CommandInfo; use: (usage: string) => void }) {
  const a = args(c.tree);
  return (
    <div className="k-stack" style={{ padding: "12px 16px", borderTop: "1px solid var(--k-line)", gap: 10 }}>
      <div>
        <b className="k-mono">/{c.name}</b>{c.aliases?.length ? <span className="k-muted"> also {c.aliases.map((x) => "/" + x).join(", ")}</span> : null}
        <div className="k-hint">{c.description || "No description."} · from <span className="k-mono">{c.owner}</span></div>
        <div className="k-hint">Needs <span className="k-mono">{c.node}</span> <Badge tone={LEVEL_TONE[c.permission ?? ""] ?? "idle"}>{c.permission ?? "?"}</Badge></div>
      </div>
      <div className="ka-usage">{(c.usageLines ?? []).map((u) => <button key={u} type="button" title="Put it in the command line" onClick={() => use(u)}>{u}</button>)}</div>
      {a.length ? (
        <table className="ka-fields"><tbody>{a.map((x) => <tr key={x.name}><td className="path">&lt;{x.name}&gt;</td><td>{x.describe || x.type}{x.options ? <div className="k-hint">{x.options.join(", ")}</div> : null}</td></tr>)}</tbody></table>
      ) : null}
    </div>
  );
}

function LogTab() {
  const [file, setFile] = useState<"kiln" | "game">("kiln");
  const [follow, setFollow] = useState(true);
  const [filter, setFilter] = useState("");
  const has = useHasOp("log_tail");
  const q = useAdminQuery<LogTail>("log_tail", { file, lines: 2000 }, { enabled: has, refetchInterval: follow ? 2000 : false });
  return (
    <Card title={q.data?.file ?? "kiln.log"} sub={q.data ? `${q.data.lines.length} lines · ${Math.round(q.data.size / 1024)} KB` : undefined}
      actions={<>
        <Segmented value={file} onChange={setFile} options={[{ id: "kiln", label: "Kiln" }, { id: "game", label: "Game" }]} aria-label="Log file" />
        <TextInput aria-label="Filter lines" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 180 }} />
        <Toggle checked={follow} onChange={setFollow} label="Follow" />
      </>} flush>
      {q.error ? <div className="k-error-text" style={{ padding: 12 }}>{q.error.message}</div> : null}
      <LogView lines={q.data?.lines ?? []} filter={filter || undefined} follow={follow} height={520} aria-label="Server log" />
    </Card>
  );
}

export default withAdmin(ConsolePage);
