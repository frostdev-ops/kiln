// The server's console: what a console window would show, live (server_console_open + kiln://console: the capture
// file of a windowless start, else the game log), with scrollback, levels, pause, filter and copy; and the command line
// (kiln>) that runs Kiln commands over the admin session (op command_run) while Kiln.Admin is connected, with Tab
// completion (command_complete), history (Up, Down), "run as" a player, and the command list (commands_describe).
// The server's Overview shows it beside the tick stats.
import { useVirtualizer } from "@tanstack/react-virtual";
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import { callAdmin, useAdminQuery, useAdminState, useInAdminProvider } from "../../api/admin";
import { toKilnError } from "../../api/commands";
import type { CommandInfo, CommandNode, CompleteResult } from "../../admin/types";
import { MoreHorizontal } from "lucide-react";
import { Button, EmptyState, Menu, Modal, Segmented, WellInput, toast } from "../../ui";
import { onServerConsole, serverConsoleClose, serverConsoleOpen, useServerStatus, type ConsoleLevel, type ConsoleSource } from "./serverApi";
import { rich, useT } from "../../i18n";

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
const ROW = 21;
const COOL_MS = 900;
const CLASS: Record<Row["level"], string> = { info: "", warn: " l-warn", error: " l-err", debug: " l-dbg", meta: " l-meta", in: " l-in", out: " l-out" };
const capRows = (r: Row[]) => (r.length > MAX_ROWS ? r.slice(r.length - MAX_ROWS) : r);

export function ServerConsole({ serverId }: { serverId: string }) {
  const t = useT();
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
  const [cmds, setCmds] = useState(false);
  const inside = useInAdminProvider();
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

  const pause = (on: boolean) => {
    if (!on) {
      setRows((r) => capRows([...r, ...held]));
      setHeld([]);
    }
    setPaused(on);
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
      toast.ok(t("servers.console.copied", { count: shown.length }));
    } catch (e) {
      toast.error(e, t("servers.console.copyFailed"));
    }
  };
  const file = path?.split(/[\\/]/).pop() ?? (source === "gamelog" ? t("servers.console.gameLog") : t("servers.console.console"));
  const empty = levels === "warn" ? t("servers.console.noWarnings") : levels === "error" ? t("servers.console.noErrors") : filter ? t("servers.console.noMatch") : t("servers.console.nothingYet");

  return (
    <div className="k-console">
      <div className="k-console-bar">
        <span className="k-mono" style={{ color: "var(--k-text-1)" }} title={path ?? undefined}>{file}</span>
        <Segmented<LevelFilter> aria-label={t("servers.console.level")} value={levels} onChange={setLevels}
          options={[{ id: "all", label: t("servers.console.all") }, { id: "warn", label: t("servers.console.warnings") }, { id: "error", label: t("servers.console.errors") }]} />
        <div className="k-seg">
          <button type="button" className={paused ? "on" : ""} aria-pressed={paused} onClick={() => pause(!paused)}>{paused && held.length ? t("servers.console.pausedNew", { n: held.length }) : t("servers.console.pause")}</button>
        </div>
        <WellInput label={t("servers.console.filter")} compact value={filter} onChange={(e) => setFilter(e.target.value)} onClear={() => setFilter("")} wellStyle={{ flex: "1 1 110px", marginLeft: "auto", minHeight: 30 }} />
        <Menu label={t("servers.console.more")} icon={<MoreHorizontal />} size="s" items={[
          { label: t("servers.console.copyLines", { count: shown.length }), disabled: !shown.length, onSelect: copy },
          ...(running && inside ? [{ label: t("servers.console.kilnCommands"), onSelect: () => setCmds(true) }] : []),
        ]} />
      </div>
      {source === "gamelog" && <span className="k-hint">{t("servers.console.outside")}</span>}
      {error ? (
        <EmptyState title={t("servers.console.noConsole")} hint={error} />
      ) : (
        <ConsoleLines rows={shown} follow={!paused} filter={filter.trim()} empty={source === "none" && !rows.length ? t("servers.console.noOutput") : empty} truncated={truncated} />
      )}
      <CommandLine serverId={serverId} running={running} onRows={add} />
      {cmds && <CommandsDialog onClose={() => setCmds(false)} />}
    </div>
  );
}

function ConsoleLines({ rows, follow, filter, empty, truncated }: { rows: Row[]; follow: boolean; filter: string; empty: string; truncated: boolean }) {
  const t = useT();
  const scroll = useRef<HTMLDivElement>(null);
  const [atEnd, setAtEnd] = useState(true);
  const born = useRef<WeakMap<Row, number>>(new WeakMap());
  const first = useRef(true);
  if (first.current && rows.length) { rows.forEach((r) => born.current.set(r, 0)); first.current = false; }
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
  const mark = (text: string) => {
    if (!filter) return text;
    const i = text.toLowerCase().indexOf(filter.toLowerCase());
    return i < 0 ? text : <>{text.slice(0, i)}<mark>{text.slice(i, i + filter.length)}</mark>{text.slice(i + filter.length)}</>;
  };
  const now = Date.now();
  return (
    <div style={{ position: "relative", flex: 1, minHeight: 120, display: "flex", flexDirection: "column" }}>
      <div ref={scroll} className="k-log k-console-log" role="log" aria-label={t("servers.console.label")} aria-live="off" tabIndex={0}>
        {truncated && <div className="k-log-note">{t("servers.console.truncated")}</div>}
        {rows.length === 0 ? (
          <div className="k-empty-title" style={{ padding: "10px 14px", fontFamily: "var(--k-font-ui)" }}>{empty}</div>
        ) : (
          <div style={{ height: virt.getTotalSize(), position: "relative" }}>
            {virt.getVirtualItems().map((v) => {
              const r = rows[v.index];
              if (!born.current.has(r)) born.current.set(r, now);
              const fresh = now - (born.current.get(r) ?? 0) < COOL_MS;
              return (
                <div key={v.key} className={"k-log-line" + CLASS[r.level] + (r.level === "meta" && r.text.startsWith("---- ") ? " l-run" : "") + (fresh ? " fresh" : "")}
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
          <Button size="s" onClick={() => { setAtEnd(true); virt.scrollToIndex(rows.length - 1, { align: "end" }); }}>{t("servers.console.follow")}</Button>
        </div>
      )}
    </div>
  );
}

/** Kiln commands over the admin session; live only while the server runs (no reconnect loop for a stopped one). */
function CommandLine({ serverId, running, onRows }: { serverId: string; running: boolean; onRows: (r: Row[]) => void }) {
  const t = useT();
  const inside = useInAdminProvider();
  if (!running || !inside) {
    return (
      <form className="k-cmd" onSubmit={(e) => e.preventDefault()}>
        <label className="k-cmd-prompt" htmlFor="k-cmd">kiln&gt;</label>
        <input id="k-cmd" disabled placeholder={running ? t("servers.console.connecting") : t("servers.console.startToSend")} />
      </form>
    );
  }
  return <LiveCommandLine serverId={serverId} onRows={onRows} />;
}

function LiveCommandLine({ serverId, onRows }: { serverId: string; onRows: (r: Row[]) => void }) {
  const t = useT();
  const admin = useAdminState(serverId);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [as, setAs] = useState("");
  const [sugg, setSugg] = useState<string[]>([]);
  const [sel, setSel] = useState(0);
  const history = useRef<string[]>([]);
  const at = useRef(-1);
  const can = admin.ready && admin.ops.has("command_run");
  const players = (admin.state?.players ?? []).filter((p) => p.steamId);
  const hint = admin.ready
    ? can ? t("servers.console.hint") : t("servers.console.noRunOp")
    : admin.connecting ? t("servers.console.connecting") : admin.error ? t("servers.console.notAnswering", { error: admin.error.message }) : t("servers.console.notConnected");

  const complete = async () => {
    if (!admin.ops.has("command_complete") || !text.trim()) return;
    const full = text.startsWith("/") ? text : "/" + text;
    try {
      const r = await callAdmin<CompleteResult>(serverId, "command_complete", { text: full, ...(as ? { steamId: as } : {}) });
      const off = text.startsWith("/") ? 0 : 1;
      const start = Math.max(0, r.start - off);
      const pick = (s: string) => text.slice(0, start) + s + (/^<.*>$/.test(s) ? "" : " ");
      const list = (r.suggestions ?? []).filter((s) => s !== text.slice(start)).map(pick);
      if (list.length === 1) { setText(list[0]); setSugg([]); }
      else { setSugg(list.slice(0, 12)); setSel(0); }
    } catch { setSugg([]); }
  };
  const run = async (e: FormEvent) => {
    e.preventDefault();
    const raw = text.trim();
    if (!raw || !can || busy) return;
    const line = raw.startsWith("/") ? raw : "/" + raw;
    history.current = [...history.current.filter((h) => h !== raw), raw].slice(-100);
    at.current = -1;
    setText("");
    setSugg([]);
    const who = as ? players.find((p) => p.steamId === as)?.name ?? as : "";
    onRows([{ text: who ? t("servers.console.echoAs", { who, line }) : `> ${line}`, level: "in" }]);
    setBusy(true);
    try {
      const r = await callAdmin<RunResult>(serverId, "command_run", { line, ...(as ? { steamId: as } : {}) });
      const out: Row[] = (r.output ?? []).map((text) => ({ text, level: "out" as const }));
      if (!r.ok) out.push({ text: r.denied ? t("servers.console.denied", { error: r.error || t("servers.console.noPermission") }) : r.found === false ? t("servers.console.unknown", { error: r.error ?? "" }).trim() : r.error || t("servers.mods.failed"), level: "error" });
      onRows(out.length ? out : [{ text: "ok", level: "meta" }]);
    } catch (err) {
      onRows([{ text: toKilnError(err).message, level: "error" }]);
    } finally {
      setBusy(false);
    }
  };
  const key = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Tab") { e.preventDefault(); if (sugg.length) { setText(sugg[sel]); setSugg([]); } else complete(); return; }
    if (e.key === "Escape" && sugg.length) { e.preventDefault(); setSugg([]); return; }
    if (sugg.length && (e.key === "ArrowUp" || e.key === "ArrowDown")) { e.preventDefault(); setSel((s) => (s + (e.key === "ArrowDown" ? 1 : -1) + sugg.length) % sugg.length); return; }
    if (sugg.length && e.key === "Enter") { e.preventDefault(); setText(sugg[sel]); setSugg([]); return; }
    const h = history.current;
    if ((e.key !== "ArrowUp" && e.key !== "ArrowDown") || !h.length) return;
    e.preventDefault();
    const cur = at.current < 0 ? h.length : at.current;
    const next = Math.max(0, Math.min(h.length, cur + (e.key === "ArrowUp" ? -1 : 1)));
    at.current = next;
    setText(h[next] ?? "");
  };
  return (
    <form className="k-cmd" onSubmit={run}>
      <label className="k-cmd-prompt" htmlFor="k-cmd">kiln&gt;</label>
      <input id="k-cmd" placeholder={hint} title={hint} value={text} disabled={!can} autoComplete="off" spellCheck={false} maxLength={1000}
        onChange={(e) => { setText(e.target.value); setSugg([]); }} onKeyDown={key} aria-autocomplete="list" aria-expanded={sugg.length > 0} />
      {players.length > 0 && (
        <select className="k-cmd-as" aria-label={t("servers.console.runAs")} value={as} onChange={(e) => setAs(e.target.value)}>
          <option value="">{t("servers.console.asAdmin")}</option>
          {players.map((p) => <option key={p.steamId} value={p.steamId!}>{t("servers.console.asPlayer", { name: p.name || p.steamId })}</option>)}
        </select>
      )}
      {busy && <span className="k-spin" aria-label={t("servers.console.running")} />}
      {sugg.length > 0 && (
        <div className="k-popover k-cmd-sugg" role="listbox" aria-label={t("servers.console.completions")}>
          {sugg.map((s, i) => (
            <button key={s} type="button" role="option" aria-selected={i === sel} className="k-menu-item" onMouseDown={(e) => { e.preventDefault(); setText(s); setSugg([]); }}>
              <span className="k-mono" style={{ color: "var(--k-text-0)", marginLeft: 0 }}>{s}</span>
            </button>
          ))}
        </div>
      )}
    </form>
  );
}

/** The server's Kiln commands with their usage (commands_describe). */
function CommandsDialog({ onClose }: { onClose: () => void }) {
  const t = useT();
  const cmds = useAdminQuery<CommandInfo[]>("commands_describe", {}, { staleTime: 60_000 });
  const [q, setQ] = useState("");
  const [sel, setSel] = useState<string | null>(null);
  const list = (cmds.data ?? []).filter((c) => !q || `${c.name} ${(c.aliases ?? []).join(" ")} ${c.description ?? ""} ${c.owner ?? ""}`.toLowerCase().includes(q.toLowerCase()));
  const cur = (cmds.data ?? []).find((c) => c.name === sel) ?? list[0];
  return (
    <Modal open onClose={onClose} title={t("servers.console.commands")} width={640} footer={<Button onClick={onClose}>{t("common.close")}</Button>}>
      <WellInput label={t("servers.console.filter")} data-autofocus value={q} onChange={(e) => setQ(e.target.value)} onClear={() => setQ("")} />
      {cmds.error ? <span className="k-error-text">{cmds.error.message}</span> : (
        <div className="k-cmd-dialog">
          <div className="k-list k-cmd-names">
            {list.map((c) => (
              <button key={c.name} type="button" className={"k-list-item clickable" + (c.name === cur?.name ? " selected" : "")} style={{ minHeight: 34, border: 0, background: c.name === cur?.name ? undefined : "none", color: "inherit", font: "inherit", textAlign: "left" }}
                onClick={() => setSel(c.name)}>
                <span className="k-mono" style={{ color: "var(--k-text-0)" }}>/{c.name}</span>
              </button>
            ))}
            {cmds.data && !list.length && <span className="k-empty-title">{t("servers.console.noCommand")}</span>}
          </div>
          {cur ? <CommandHelp c={cur} /> : <span />}
        </div>
      )}
    </Modal>
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

function CommandHelp({ c }: { c: CommandInfo }) {
  const t = useT();
  const a = args(c.tree);
  const tone = c.permission === "everyone" ? "ok" : c.permission === "admin" ? "warn" : "err";
  return (
    <div className="k-stack k-cmd-help" style={{ gap: 10 }}>
      <div>
        <span className="k-mono" style={{ fontSize: 17, color: "var(--k-text-0)" }}>/{c.name}</span>
        {c.aliases?.length ? <span className="k-muted"> {t("servers.console.also", { list: c.aliases.map((x) => "/" + x).join(", ") })}</span> : null}
        <div className="k-hint">{rich("servers.console.from", { description: c.description || t("servers.console.noDescription"), owner: <span className="k-mono">{c.owner}</span> })}</div>
        <div className="k-hint">{rich("servers.console.needs", { node: <span className="k-mono">{c.node}</span>, permission: <span className={`k-word-sm k-tone-${tone}`}>{c.permission ?? "?"}</span> })}</div>
      </div>
      {(c.usageLines ?? []).map((u) => <code key={u} className="k-code-block">{u}</code>)}
      {a.length ? (
        <dl className="k-kv">{a.map((x) => <div key={x.name} style={{ display: "contents" }}><dt className="k-mono">&lt;{x.name}&gt;</dt><dd>{x.describe || x.type}{x.options ? <div className="k-hint">{x.options.join(", ")}</div> : null}</dd></div>)}</dl>
      ) : null}
    </div>
  );
}
