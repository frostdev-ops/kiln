// The server's console: what a console window would show, live
// (server_console_open + kiln://console: the capture file of a windowless start, else the game log), with
// scrollback, follow/pause, filter, levels, copy, and a command line that runs Kiln commands over the admin session
// (op command_run) while Kiln.Admin is connected. Shown under /servers/:id/logs (Console | Files) until a `console`
// route exists.
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDownToLine, ClipboardCopy, Pause, Play, Search } from "lucide-react";
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import { useParams } from "react-router-dom";
import { callAdmin, useAdminState } from "../../api/admin";
import { toKilnError } from "../../api/commands";
import { Badge, Button, EmptyState, Segmented, TextInput, toast } from "../../ui";
import { onServerConsole, serverConsoleClose, serverConsoleOpen, useServerStatus, type ConsoleLevel, type ConsoleSource } from "./serverApi";
import "./servers.css";

/** A console row: the server's line, or a command typed here and its answer. */
interface Row {
  text: string;
  level: ConsoleLevel | "in" | "out";
}
type LevelFilter = "all" | "warn" | "error";
interface RunResult {
  ok: boolean;
  output?: string[];
  error?: string;
  denied?: boolean;
  found?: boolean;
}

const MAX_ROWS = 20_000;
const ROW = 19;
const CLASS: Record<Row["level"], string> = { info: "", warn: " l-warn", error: " l-err", debug: " l-dbg", meta: " l-meta", in: " l-in", out: " l-out" };
const capRows = (r: Row[]) => (r.length > MAX_ROWS ? r.slice(r.length - MAX_ROWS) : r);

export function ServerConsole({ serverId }: { serverId: string }) {
  const st = useServerStatus(serverId);
  const running = st.data?.state === "running" || st.data?.state === "starting";
  const [rows, setRows] = useState<Row[]>([]);
  const [source, setSource] = useState<ConsoleSource | null>(null);
  const [path, setPath] = useState<string | null>(null);
  const [truncated, setTruncated] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [paused, setPaused] = useState(false);
  const [held, setHeld] = useState<Row[]>([]);
  const [filter, setFilter] = useState("");
  const [levels, setLevels] = useState<LevelFilter>("all");
  const pausedRef = useRef(false);
  pausedRef.current = paused;

  const add = useCallback((more: Row[]) => {
    if (!more.length) return;
    if (pausedRef.current) setHeld((h) => capRows([...h, ...more]));
    else setRows((r) => capRows([...r, ...more]));
  }, []);

  // open the console, follow it, close it on leave (the server keeps running and writing; reopening reattaches)
  useEffect(() => {
    let sub: string | null = null;
    let gone = false;
    const early: Parameters<Parameters<typeof onServerConsole>[0]>[0][] = [];
    let off: (() => void) | undefined;
    const apply = (e: (typeof early)[number]) => {
      if (e.reset) {
        setRows(e.lines);
        setHeld([]);
        setSource(e.source);
        setPath(e.path);
        setTruncated(e.truncated);
      } else add(e.lines);
    };
    onServerConsole((e) => {
      if (sub === null) early.push(e);
      else if (e.subscription === sub) apply(e);
    }).then((f) => (gone ? f() : (off = f)), () => {});
    serverConsoleOpen(serverId, 5000).then(
      (o) => {
        if (gone) {
          serverConsoleClose(o.subscription).catch(() => {});
          return;
        }
        sub = o.subscription;
        setRows(o.lines);
        setSource(o.source);
        setPath(o.path);
        setTruncated(o.truncated);
        setError(null);
        early.filter((e) => e.subscription === sub).forEach(apply);
      },
      (e) => setError(toKilnError(e).message),
    );
    return () => {
      gone = true;
      off?.();
      if (sub) serverConsoleClose(sub).catch(() => {});
    };
  }, [serverId, add]);

  const resume = () => {
    setRows((r) => capRows([...r, ...held]));
    setHeld([]);
    setPaused(false);
  };

  const shown = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return rows.filter((r) => {
      if (levels === "error" && r.level !== "error") return false;
      if (levels === "warn" && r.level !== "error" && r.level !== "warn") return false;
      return !f || r.text.toLowerCase().includes(f);
    });
  }, [rows, filter, levels]);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(shown.map((r) => r.text).join("\n") + "\n");
      toast.ok(`Copied ${shown.length} line${shown.length === 1 ? "" : "s"}`);
    } catch (e) {
      toast.error(e, "Copy failed");
    }
  };

  return (
    <div className="ks-console">
      <div className="k-row">
        {source === "capture" && <Badge tone="ok" title={path ?? undefined}>Launcher output</Badge>}
        {source === "gamelog" && <Badge tone="warn" title={path ?? undefined}>Game log</Badge>}
        {source === "gamelog" && <span className="k-dim">Started outside the launcher: its game log. Start it here for the full output.</span>}
        {truncated && <span className="k-dim">Older lines left out.</span>}
        <span className="k-spacer" />
        <div className="ks-filter">
          <Search size={15} aria-hidden />
          <TextInput aria-label="Filter lines" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} />
        </div>
        <Segmented<LevelFilter> aria-label="Levels" value={levels} onChange={setLevels}
          options={[{ id: "all", label: "All" }, { id: "warn", label: "Warnings" }, { id: "error", label: "Errors" }]} />
        {paused ? (
          <Button size="s" icon={<Play size={15} />} onClick={resume}>{held.length ? `Resume (${held.length} new)` : "Resume"}</Button>
        ) : (
          <Button size="s" variant="secondary" icon={<Pause size={15} />} onClick={() => setPaused(true)}>Pause</Button>
        )}
        <Button size="s" variant="secondary" icon={<ClipboardCopy size={15} />} onClick={copy} disabled={!shown.length}>Copy</Button>
      </div>
      {error ? (
        <EmptyState title="No console" hint={error} />
      ) : source === "none" && !rows.length ? (
        <EmptyState title="No output yet" hint="Start the server: its output shows here, without a console window." />
      ) : (
        <ConsoleLines rows={shown} follow={!paused} filter={filter.trim()} />
      )}
      <CommandLine serverId={serverId} running={running} onRows={add} />
    </div>
  );
}

function ConsoleLines({ rows, follow, filter }: { rows: Row[]; follow: boolean; filter: string }) {
  const scroll = useRef<HTMLDivElement>(null);
  const [atEnd, setAtEnd] = useState(true);
  const virt = useVirtualizer({ count: rows.length, getScrollElement: () => scroll.current, estimateSize: () => ROW, overscan: 30 });
  useLayoutEffect(() => {
    if (follow && atEnd && rows.length) virt.scrollToIndex(rows.length - 1, { align: "end" });
  }, [rows.length, follow, atEnd, virt]);
  useEffect(() => {
    const el = scroll.current;
    if (!el) return;
    const on = () => setAtEnd(el.scrollHeight - el.scrollTop - el.clientHeight < ROW * 2);
    el.addEventListener("scroll", on, { passive: true });
    return () => el.removeEventListener("scroll", on);
  }, []);
  const mark = (t: string) => {
    if (!filter) return t;
    const i = t.toLowerCase().indexOf(filter.toLowerCase());
    return i < 0 ? t : <>{t.slice(0, i)}<mark>{t.slice(i, i + filter.length)}</mark>{t.slice(i + filter.length)}</>;
  };
  return (
    <div style={{ position: "relative" }}>
      <div ref={scroll} className="k-log ks-console-log" role="log" aria-label="Server console" aria-live="off" tabIndex={0}>
        {rows.length === 0 ? (
          <div className="k-dim" style={{ padding: "10px 12px" }}>{filter ? "No matching lines." : "Empty."}</div>
        ) : (
          <div style={{ height: virt.getTotalSize(), position: "relative" }}>
            {virt.getVirtualItems().map((v) => {
              const r = rows[v.index];
              return (
                <div key={v.key} className={"k-log-line" + CLASS[r.level] + (r.level === "meta" && r.text.startsWith("---- ") ? " l-run" : "")}
                  style={{ transform: `translateY(${v.start}px)` }} title={r.text.length > 160 ? r.text : undefined}>
                  {mark(r.text) || " "}
                </div>
              );
            })}
          </div>
        )}
      </div>
      {follow && !atEnd && rows.length > 0 && (
        <div className="k-log-follow">
          <Button size="s" icon={<ArrowDownToLine size={15} />} onClick={() => { setAtEnd(true); virt.scrollToIndex(rows.length - 1, { align: "end" }); }}>Follow</Button>
        </div>
      )}
    </div>
  );
}

/** Kiln commands over the admin session; mounted only while the server runs (no reconnect loop for a stopped one). */
function CommandLine({ serverId, running, onRows }: { serverId: string; running: boolean; onRows: (r: Row[]) => void }) {
  if (!running) {
    return (
      <form className="ks-cmd" onSubmit={(e) => e.preventDefault()}>
        <span className="ks-prompt" aria-hidden>/</span>
        <TextInput mono disabled aria-label="Command" placeholder="Start the server to send Kiln commands" />
      </form>
    );
  }
  return <LiveCommandLine serverId={serverId} onRows={onRows} />;
}

function LiveCommandLine({ serverId, onRows }: { serverId: string; onRows: (r: Row[]) => void }) {
  const admin = useAdminState(serverId);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const history = useRef<string[]>([]);
  const at = useRef(-1);
  const can = admin.ready && admin.ops.has("command_run");
  const hint = admin.ready
    ? can ? "Kiln command, e.g. help (Enter runs it, ↑ ↓ history)" : "This Kiln.Admin has no command_run op"
    : admin.connecting ? "Connecting to Kiln.Admin…" : admin.error ? `Kiln.Admin not reachable: ${admin.error.message}` : "Kiln.Admin is not connected";

  const run = async (e: FormEvent) => {
    e.preventDefault();
    const raw = text.trim();
    if (!raw || !can || busy) return;
    const line = raw.startsWith("/") ? raw : "/" + raw;
    history.current = [...history.current.filter((h) => h !== raw), raw].slice(-100);
    at.current = -1;
    setText("");
    onRows([{ text: `> ${line}`, level: "in" }]);
    setBusy(true);
    try {
      const r = await callAdmin<RunResult>(serverId, "command_run", { line });
      const out: Row[] = (r.output ?? []).map((t) => ({ text: t, level: "out" as const }));
      if (!r.ok) out.push({ text: r.denied ? `Denied: ${r.error || "no permission"}` : r.found === false ? `Unknown command. ${r.error ?? ""}`.trim() : r.error || "Failed", level: "error" });
      onRows(out.length ? out : [{ text: "ok", level: "meta" }]);
    } catch (err) {
      onRows([{ text: toKilnError(err).message, level: "error" }]);
    } finally {
      setBusy(false);
    }
  };
  const key = (e: KeyboardEvent<HTMLInputElement>) => {
    const h = history.current;
    if ((e.key !== "ArrowUp" && e.key !== "ArrowDown") || !h.length) return;
    e.preventDefault();
    const cur = at.current < 0 ? h.length : at.current;
    const next = Math.max(0, Math.min(h.length, cur + (e.key === "ArrowUp" ? -1 : 1)));
    at.current = next;
    setText(h[next] ?? "");
  };
  return (
    <form className="ks-cmd" onSubmit={run}>
      <span className="ks-prompt" aria-hidden>/</span>
      <TextInput mono aria-label="Command" placeholder={hint} title={hint} value={text} disabled={!can} onChange={(e) => setText(e.target.value)} onKeyDown={key} maxLength={1000} />
      <Button type="submit" size="s" variant="primary" disabled={!can || !text.trim()} loading={busy}>Run</Button>
    </form>
  );
}

export default function ServerConsolePage() {
  const { id = "" } = useParams();
  return <ServerConsole serverId={id} />;
}
