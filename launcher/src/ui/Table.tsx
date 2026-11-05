// Virtualized table. Fixed row height; columns are CSS grid tracks.
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDown, ArrowUp } from "lucide-react";
import { useMemo, useRef, useState, type ReactNode } from "react";

export interface Column<R> {
  key: string;
  header: ReactNode;
  /** CSS grid track, default `minmax(0, 1fr)` (e.g. "120px", "2fr", "max-content"). */
  width?: string;
  align?: "left" | "right";
  render?: (row: R, index: number) => ReactNode;
  /** Makes the header clickable; ascending comparator. */
  sort?: (a: R, b: R) => number;
}

export interface TableProps<R> {
  columns: Column<R>[];
  rows: R[];
  rowKey: (row: R, index: number) => string;
  /** Default 40. */
  rowHeight?: number;
  /** Fixed body height; default: fits the rows up to maxHeight. */
  height?: number;
  /** Default 520. */
  maxHeight?: number;
  onRowClick?: (row: R) => void;
  selectedKey?: string | null;
  empty?: ReactNode;
  "aria-label"?: string;
}

export function Table<R>({
  columns, rows, rowKey, rowHeight = 40, height, maxHeight = 520, onRowClick, selectedKey, empty, ...rest
}: TableProps<R>) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [sort, setSort] = useState<{ key: string; dir: 1 | -1 } | null>(null);
  const sorted = useMemo(() => {
    const col = sort && columns.find((c) => c.key === sort.key);
    if (!col?.sort) return rows;
    return [...rows].sort((a, b) => col.sort!(a, b) * sort!.dir);
  }, [rows, sort, columns]);
  const virt = useVirtualizer({ count: sorted.length, getScrollElement: () => scrollRef.current, estimateSize: () => rowHeight, overscan: 8 });
  const template = columns.map((c) => c.width ?? "minmax(0, 1fr)").join(" ");
  const bodyHeight = height ?? Math.min(maxHeight, Math.max(sorted.length, 1) * rowHeight);
  const keyNav = (e: React.KeyboardEvent, row: R) => {
    if (onRowClick && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); onRowClick(row); }
  };

  return (
    <div className="k-table" role="table" aria-label={rest["aria-label"]} aria-rowcount={sorted.length}>
      <div className="k-table-head" role="row" style={{ gridTemplateColumns: template }}>
        {columns.map((c) => (
          <div key={c.key} role="columnheader" className={"k-table-cell" + (c.align === "right" ? " right" : "")}
            aria-sort={sort?.key === c.key ? (sort.dir === 1 ? "ascending" : "descending") : undefined}>
            {c.sort ? (
              <button type="button" onClick={() => setSort((s) => (s?.key === c.key ? (s.dir === 1 ? { key: c.key, dir: -1 } : null) : { key: c.key, dir: 1 }))}>
                {c.header}
                {sort?.key === c.key && (sort.dir === 1 ? <ArrowUp size={12} /> : <ArrowDown size={12} />)}
              </button>
            ) : c.header}
          </div>
        ))}
      </div>
      {sorted.length === 0 ? (
        <div className="k-table-empty">{empty ?? <span className="k-muted">Nothing here.</span>}</div>
      ) : (
        <div ref={scrollRef} className="k-table-scroll" style={{ height: bodyHeight }}>
          <div style={{ height: virt.getTotalSize(), position: "relative" }}>
            {virt.getVirtualItems().map((v) => {
              const row = sorted[v.index];
              const key = rowKey(row, v.index);
              return (
                <div key={key} role="row" aria-rowindex={v.index + 1} tabIndex={onRowClick ? 0 : undefined}
                  className={"k-table-row" + (onRowClick ? " clickable" : "") + (selectedKey === key ? " selected" : "")}
                  style={{ gridTemplateColumns: template, height: rowHeight, transform: `translateY(${v.start}px)` }}
                  onClick={onRowClick ? () => onRowClick(row) : undefined} onKeyDown={(e) => keyNav(e, row)}>
                  {columns.map((c) => (
                    <div key={c.key} role="cell" className={"k-table-cell" + (c.align === "right" ? " right" : "")}>
                      {c.render ? c.render(row, v.index) : String((row as Record<string, unknown>)[c.key] ?? "")}
                    </div>
                  ))}
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
