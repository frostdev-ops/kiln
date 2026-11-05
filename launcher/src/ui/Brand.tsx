// Brand pieces: the mark (branding/logo/mark.svg, the "Firing Chamber"), the splash, a sparkline.
import { useEffect, useId, useState } from "react";
import markSvg from "../../../branding/logo/mark.svg";

/** The Kiln mark, from branding/logo/mark.svg. Never recoloured. */
export function Mark({ size = 28, title = "Kiln" }: { size?: number; title?: string }) {
  return <img src={markSvg} width={size} height={size} alt={title} draggable={false} style={{ display: "block" }} />;
}

/** Full-screen splash: the mark with a soft molten glow; fades out when `done`. */
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
      <div>
        <div className="k-splash-mark"><img src={markSvg} alt="" draggable={false} style={{ width: "100%", height: "100%", position: "relative" }} /></div>
        <div className="k-splash-word">KILN</div>
      </div>
    </div>
  );
}

export interface SparklineProps {
  values: number[];
  width?: number;
  height?: number;
  "aria-label"?: string;
}

export function Sparkline({ values, width = 140, height = 32, ...rest }: SparklineProps) {
  const gid = useId().replace(/:/g, "");
  if (values.length < 2) return <svg className="k-spark" width={width} height={height} aria-label={rest["aria-label"]} />;
  const min = Math.min(...values), max = Math.max(...values);
  const span = max - min || 1;
  const pts = values.map((v, i) => [(i / (values.length - 1)) * width, height - 2 - ((v - min) / span) * (height - 4)] as const);
  const line = pts.map(([x, y], i) => `${i ? "L" : "M"}${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
  return (
    <svg className="k-spark" width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={rest["aria-label"]}>
      <defs>
        <linearGradient id={gid} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0" stopColor="#ff6a1a" stopOpacity="0.35" />
          <stop offset="1" stopColor="#ff6a1a" stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={`${line} L${width},${height} L0,${height} Z`} fill={`url(#${gid})`} />
      <path className="line" d={line} />
    </svg>
  );
}
