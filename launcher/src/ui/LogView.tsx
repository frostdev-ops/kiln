// Virtualized log view. Follows the end while scrolled to the bottom; colours Kiln's
// `[E]`/`[W]`/`[D]` levels; an optional filter keeps matching lines and marks the match.
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDownToLine } from "lucide-react";
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
  /** Case-insensitive substring; non-matching lines are hidden. */
  filter?: string;
  lineNumbers?: boolean;
  classify?: (line: string) => LogLevel;
  "aria-label"?: string;
}

const ROW = 19;

export function LogView({ lines, text, follow = true, height = 420, filter, lineNumbers = true, classify = classifyLine, ...rest }: LogViewProps) {
  const all = useMemo(() => lines ?? (text ? text.replace(/\n$/, "").split("\n") : []), [lines, text]);
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
    <div style={{ position: "relative" }}>
      <div ref={scroll} className="k-log" style={{ height }} role="log" aria-label={rest["aria-label"] ?? "Log"} tabIndex={0}>
        {shown.length === 0 ? (
          <div className="k-dim" style={{ padding: "10px 12px" }}>{filter ? "No matching lines." : "Empty."}</div>
        ) : (
          <div style={{ height: virt.getTotalSize(), position: "relative" }}>
            {virt.getVirtualItems().map((v) => {
              const [line, n] = shown[v.index];
              const lvl = classify(line);
              return (
                <div key={v.key} className={"k-log-line" + (lvl ? ` l-${lvl}` : "")} style={{ transform: `translateY(${v.start}px)` }} title={line.length > 160 ? line : undefined}>
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
          <Button size="s" icon={<ArrowDownToLine />} onClick={() => { setAtEnd(true); virt.scrollToIndex(shown.length - 1, { align: "end" }); }}>Follow</Button>
        </div>
      )}
    </div>
  );
}
