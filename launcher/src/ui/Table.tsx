// Virtualized table: small-caps heads, rules at 13%, one line per row (46 px), no zebra; hover 6% warm, selected 12%.
// A sorted head shows a small gold diamond beside the word. Numbers right-aligned (use `align: "right"`).
import { useVirtualizer } from "@tanstack/react-virtual";
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
  /** Default 46. */
  rowHeight?: number;
  /** Fixed body height; default: fits the rows up to maxHeight. */
  height?: number;
  /** Default 520. */
  maxHeight?: number;
  /** Fill the space the parent gives it and scroll inside (lists scroll inside the sheet). */
  fill?: boolean;
  onRowClick?: (row: R) => void;
  selectedKey?: string | null;
  /** Extra class per row ("fresh": a new row arrives flushed with ember). */
  rowClass?: (row: R) => string | undefined;
  empty?: ReactNode;
  "aria-label"?: string;
}

export function Table<R>({
  columns, rows, rowKey, rowHeight = 46, height, maxHeight = 520, fill, onRowClick, selectedKey, rowClass, empty, ...rest
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
  const bodyHeight = fill ? undefined : height ?? Math.min(maxHeight, Math.max(sorted.length, 1) * rowHeight);
  const keyNav = (e: React.KeyboardEvent, row: R) => {
    if (onRowClick && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); onRowClick(row); }
  };

  return (
    <div className={"k-table" + (fill ? " k-table-fill" : "")} role="table" aria-label={rest["aria-label"]} aria-rowcount={sorted.length}>
      <div className="k-table-head" role="row" style={{ gridTemplateColumns: template }}>
        {columns.map((c) => (
          <div key={c.key} role="columnheader" className={"k-table-cell" + (c.align === "right" ? " right" : "")}
            aria-sort={sort?.key === c.key ? (sort.dir === 1 ? "ascending" : "descending") : undefined}>
            {c.sort ? (
              <button type="button" onClick={() => setSort((s) => (s?.key === c.key ? (s.dir === 1 ? { key: c.key, dir: -1 } : null) : { key: c.key, dir: 1 }))}>
                {c.header}
                {sort?.key === c.key && <i className="k-dia" aria-hidden title={sort.dir === 1 ? "Ascending" : "Descending"} />}
              </button>
            ) : c.header}
          </div>
        ))}
      </div>
      {sorted.length === 0 ? (
        <div className="k-table-empty">{empty ?? <span className="k-empty-title">Nothing here.</span>}</div>
      ) : (
        <div ref={scrollRef} className="k-table-scroll" style={{ height: bodyHeight }}>
          <div style={{ height: virt.getTotalSize(), position: "relative" }}>
            {virt.getVirtualItems().map((v) => {
              const row = sorted[v.index];
              const key = rowKey(row, v.index);
              return (
                <div key={key} role="row" aria-rowindex={v.index + 1} tabIndex={onRowClick ? 0 : undefined} aria-selected={onRowClick ? selectedKey === key : undefined}
                  className={"k-table-row" + (onRowClick ? " clickable" : "") + (selectedKey === key ? " selected" : "") + (rowClass?.(row) ? ` ${rowClass(row)}` : "")}
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

/** A name with its id beside it, on one line. */
export function NameId({ name, id, dim }: { name: ReactNode; id?: ReactNode; dim?: boolean }) {
  return (
    <span className="k-name-id" style={dim ? { opacity: 0.6 } : undefined}>
      <span>{name}</span>
      {id !== undefined && id !== null && id !== name && <span className="k-mono">{id}</span>}
    </span>
  );
}
