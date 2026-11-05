// Collapsible JSON tree.
import type { ReactNode } from "react";

export interface JsonViewProps {
  value: unknown;
  /** Levels open at first (default 1). */
  open?: number;
  /** Max body height in px. */
  maxHeight?: number;
}

export function JsonView({ value, open = 1, maxHeight = 420 }: JsonViewProps) {
  return <div className="k-json" style={{ maxHeight }}><Node value={value} depth={0} open={open} /></div>;
}

function Leaf({ value }: { value: unknown }) {
  if (value === null || value === undefined) return <span className="nul">null</span>;
  if (typeof value === "string") return <span className="str">"{value}"</span>;
  if (typeof value === "number") return <span className="num">{value}</span>;
  if (typeof value === "boolean") return <span className="bool">{String(value)}</span>;
  return <span>{String(value)}</span>;
}

function Node({ value, depth, open, label }: { value: unknown; depth: number; open: number; label?: ReactNode }) {
  const isArr = Array.isArray(value);
  if (value === null || typeof value !== "object") return <div>{label}<Leaf value={value} /></div>;
  const entries = isArr ? (value as unknown[]).map((v, i) => [String(i), v] as const) : Object.entries(value as Record<string, unknown>);
  const [o, c] = isArr ? ["[", "]"] : ["{", "}"];
  if (entries.length === 0) return <div>{label}<span className="meta">{o}{c}</span></div>;
  return (
    <details open={depth < open}>
      <summary>{label}<span className="meta">{o} {entries.length} {isArr ? "items" : "keys"} {c}</span></summary>
      <div className="ind">
        {entries.map(([k, v]) => (
          <Node key={k} value={v} depth={depth + 1} open={open} label={<><span className={isArr ? "meta" : "key"}>{isArr ? k : `"${k}"`}</span>: </>} />
        ))}
      </div>
    </details>
  );
}
