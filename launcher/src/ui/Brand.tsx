// Brand pieces: the mark and the lockup (branding/logo, never recoloured or stretched), the start splash, and the
// charts (Sparkline, TickChart: a flame line 2 px, a 16% area, a gold diamond at the end, a faint grid, a crosshair with
// the value on hover; never two y-scales in one chart).
import { useEffect, useState, type ReactNode } from "react";
import markSvg from "../../../branding/logo/mark.svg";
import lockupSvg from "../../../branding/logo/lockup-dark.svg";

/** The Kiln mark, from branding/logo/mark.svg. */
export function Mark({ size = 28, title = "Kiln" }: { size?: number; title?: string }) {
  return <img src={markSvg} width={size} height={size} alt={title} draggable={false} style={{ display: "block" }} />;
}

/** The lockup (mark and wordmark), from branding/logo/lockup-dark.svg. */
export function Lockup({ height = 30, title = "Kiln" }: { height?: number; title?: string }) {
  return <img src={lockupSvg} height={height} width={(height * 320) / 96} alt={title} draggable={false} style={{ display: "block" }} />;
}

/** Full-window splash: the lockup on soot; fades out when `done`. */
export function Splash({ done }: { done: boolean }) {
  const [gone, setGone] = useState(false);
  useEffect(() => {
    if (!done) return;
    const t = setTimeout(() => setGone(true), 400);
    return () => clearTimeout(t);
  }, [done]);
  if (gone) return null;
  return (
    <div className={"k-splash" + (done ? " gone" : "")} aria-hidden={done} role="status" aria-label="Starting">
      <img src={lockupSvg} alt="" draggable={false} />
    </div>
  );
}

export interface SparklineProps {
  values: number[];
  width?: number;
  height?: number;
  "aria-label"?: string;
}

/** A small trend line: flame line, 16% area, gold diamond at the last value. */
export function Sparkline({ values, width = 140, height = 32, ...rest }: SparklineProps) {
  if (values.length < 2) return <svg className="k-spark" width={width} height={height} aria-label={rest["aria-label"]} />;
  const min = Math.min(...values), max = Math.max(...values);
  const span = max - min || 1;
  const pts = values.map((v, i) => [(i / (values.length - 1)) * (width - 6), height - 3 - ((v - min) / span) * (height - 6)] as const);
  const line = pts.map(([x, y], i) => `${i ? "L" : "M"}${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
  const [lx, ly] = pts[pts.length - 1];
  return (
    <svg className="k-spark" width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={rest["aria-label"]}>
      <path d={`${line} L${(width - 6).toFixed(1)},${height} L0,${height} Z`} fill="rgba(255,106,26,.16)" />
      <path d={line} fill="none" stroke="var(--k-ember-400)" strokeWidth={2} strokeLinejoin="round" />
      <rect x={lx - 4} y={ly - 4} width={8} height={8} fill="var(--k-gold-400)" transform={`rotate(45 ${lx} ${ly})`} />
    </svg>
  );
}

export interface TickChartProps {
  values: number[];
  /** Grid lines (y values); default: 3 even steps up to the top. */
  grid?: number[];
  height?: number;
  /** The value (and its index) under the pointer, or the last one when the pointer leaves. */
  onHover?: (index: number) => void;
  "aria-label": string;
}

/** The tick-stats chart. The end diamond is never clipped (6 px right margin). */
export function TickChart({ values, grid, height = 96, onHover, ...rest }: TickChartProps) {
  const [hover, setHover] = useState(-1);
  if (values.length < 2) return <div className="k-chart" style={{ height }} role="img" aria-label={rest["aria-label"]} />;
  const step = niceStep(Math.max(...values) / 3);
  const top = Math.max(step, Math.ceil((Math.max(...values) * 1.05) / step) * step);
  const lines = grid ?? Array.from({ length: Math.round(top / step) }, (_, i) => Math.round((i + 1) * step * 100) / 100);
  const yPct = (v: number) => ((height - (v / top) * (height - 4)) / height) * 100;
  const pts = values.map((v, i) => [(i / (values.length - 1)) * 200, height - (v / top) * (height - 4)] as const);
  const poly = pts.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
  const at = hover >= 0 ? hover : values.length - 1;
  const set = (i: number) => { setHover(i); onHover?.(i < 0 ? values.length - 1 : i); };
  return (
    <div className="k-chart" style={{ height, marginRight: 6 }} role="img" aria-label={rest["aria-label"]}
      onMouseMove={(e) => {
        const b = e.currentTarget.getBoundingClientRect();
        const i = Math.max(0, Math.min(values.length - 1, Math.round(((e.clientX - b.left) / b.width) * (values.length - 1))));
        if (i !== hover) set(i);
      }}
      onMouseLeave={() => set(-1)}>
      {lines.map((v) => (
        <span key={v}><i className="k-chart-grid" style={{ top: `${yPct(v)}%` }} /><span className="k-chart-y" style={{ top: `${yPct(v)}%` }}>{fmtTick(v)}</span></span>
      ))}
      <svg viewBox={`0 0 200 ${height}`} preserveAspectRatio="none" aria-hidden>
        <polygon className="area" points={`0,${height} ${poly} 200,${height}`} />
        <polyline className="line" points={poly} />
      </svg>
      {hover >= 0 && <i className="k-chart-cursor" style={{ left: `${pts[at][0] / 2}%` }} />}
      <i className="k-chart-dot" style={{ left: `${pts[at][0] / 2}%`, top: `${(pts[at][1] / height) * 100}%` }} />
    </div>
  );
}

/** 1, 2, 2.5 or 5 times a power of ten, at least `raw`. */
function niceStep(raw: number): number {
  if (!(raw > 0)) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(raw)));
  for (const m of [1, 2, 2.5, 5, 10]) if (m * p >= raw) return m * p;
  return 10 * p;
}
const fmtTick = (v: number): ReactNode => (v >= 10 ? Math.round(v) : Number(v.toFixed(1)));
