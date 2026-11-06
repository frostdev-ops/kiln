// Virtualized log view: Inconsolata 14 on 21 in a well. Follows the end while scrolled to the bottom; colours Kiln's
// levels ([E] iron red, [W] amber, [I] Ash 1, [D] Ash 2); a filter keeps matching lines and gives the match a 22% flame
// fill; new lines cool from flame to ash over 900 ms.
import { useVirtualizer } from "@tanstack/react-virtual";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Button } from "./Button";

export type LogLevel = "err" | "warn" | "dbg" | null;

/** Kiln: `[E]`, `[W]`, `[D]`/`[T]`; also ERROR/WARN words (game, Wine). */
export function classifyLine(line: string): LogLevel {
  // Kiln: "[W]" or "[W 22:53:50.565]"
  if (/\[(E|F)( [\d:.]+)?\]/.test(line) || (/\bERROR\b|\berror:|\bFATAL\b|\bexception\b/i.test(line) && !/\b0 errors?\b/i.test(line))) return "err";
  if (/\[W( [\d:.]+)?\]/.test(line) || /\bWARN(ING)?\b|\bwarning:/i.test(line)) return "warn";
  if (/\[(D|T)( [\d:.]+)?\]/.test(line)) return "dbg";
  return null;
}

export interface LogViewProps {
  /** Lines (or text, split on newlines). */
  lines?: string[];
  text?: string;
  /** Auto-scroll at the end (default true). */
  follow?: boolean;
  /** Body height in px (default 420). */
  height?: number;
  /** Fill the parent's height instead (the parent is a flex column). */
  fill?: boolean;
  /** Case-insensitive substring; non-matching lines are hidden. */
  filter?: string;
  lineNumbers?: boolean;
  classify?: (line: string) => LogLevel;
  "aria-label"?: string;
}

const ROW = 21;
const COOL_MS = 900;

export function LogView({ lines, text, follow = true, height = 420, fill, filter, lineNumbers = true, classify = classifyLine, ...rest }: LogViewProps) {
  const all = useMemo(() => lines ?? (text ? text.replace(/\n$/, "").split("\n") : []), [lines, text]);
  // when each line arrived (lines there at the start count as old), so a line cools once, not again on scroll
  const born = useRef<number[]>([]);
  if (born.current.length > all.length) born.current = [];
  if (born.current.length < all.length) {
    const now = born.current.length === 0 ? 0 : Date.now();
    for (let i = born.current.length; i < all.length; i++) born.current.push(now);
  }
  const shown = useMemo(() => {
    const idx = all.map((l, i) => [l, i] as const);
    if (!filter) return idx;
    const f = filter.toLowerCase();
    return idx.filter(([l]) => l.toLowerCase().includes(f));
  }, [all, filter]);
  const scroll = useRef<HTMLDivElement>(null);
  const [atEnd, setAtEnd] = useState(true);
  const virt = useVirtualizer({ count: shown.length, getScrollElement: () => scroll.current, estimateSize: () => ROW, overscan: 20 });

  useLayoutEffect(() => {
    if (follow && atEnd && shown.length) virt.scrollToIndex(shown.length - 1, { align: "end" });
  }, [shown.length, follow, atEnd, virt]);
  useEffect(() => {
    const el = scroll.current;
    if (!el) return;
    const on = () => setAtEnd(el.scrollHeight - el.scrollTop - el.clientHeight < ROW * 2);
    el.addEventListener("scroll", on, { passive: true });
    return () => el.removeEventListener("scroll", on);
  }, []);

  const mark = (line: string) => {
    if (!filter) return line;
    const i = line.toLowerCase().indexOf(filter.toLowerCase());
    if (i < 0) return line;
    return <>{line.slice(0, i)}<mark>{line.slice(i, i + filter.length)}</mark>{line.slice(i + filter.length)}</>;
  };

  return (
    <div style={fill ? { position: "relative", flex: 1, minHeight: 0, display: "flex", flexDirection: "column" } : { position: "relative" }}>
      <div ref={scroll} className="k-log" style={fill ? { flex: 1, minHeight: 0 } : { height }} role="log" aria-label={rest["aria-label"] ?? "Log"} tabIndex={0}>
        {shown.length === 0 ? (
          <div className="k-empty-title" style={{ padding: "10px 14px", fontFamily: "var(--k-font-ui)" }}>{filter ? "Nothing matches that filter." : "Nothing written yet."}</div>
        ) : (
          <div style={{ height: virt.getTotalSize(), position: "relative" }}>
            {virt.getVirtualItems().map((v) => {
              const [line, n] = shown[v.index];
              const lvl = classify(line);
              return (
                <div key={v.key} className={"k-log-line" + (lvl ? ` l-${lvl}` : "") + (Date.now() - (born.current[n] ?? 0) < COOL_MS ? " fresh" : "")} style={{ transform: `translateY(${v.start}px)` }} title={line.length > 160 ? line : undefined}>
                  {lineNumbers && <span className="ln">{n + 1}</span>}
                  {mark(line)}
                </div>
              );
            })}
          </div>
        )}
      </div>
      {follow && !atEnd && shown.length > 0 && (
        <div className="k-log-follow">
          <Button size="s" onClick={() => { setAtEnd(true); virt.scrollToIndex(shown.length - 1, { align: "end" }); }}>Follow</Button>
        </div>
      )}
    </div>
  );
}
