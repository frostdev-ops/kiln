// The festive season's look (season.ts: from the Monday after Thanksgiving weekend to 7 January): a stone mantel over
// the kiln mouth on Play, dressed for the winter festivals that share light in the dark - an evergreen garland with warm
// string lights (Christmas, Yule), a kinara (Kwanzaa: three red, the black in the centre, three green), a nine-branched
// Hanukkah menorah with its shamash raised apart, clay diyas (Diwali) on the shelf and at the threshold, a red paper
// lantern (Lunar New Year), a fanous with a crescent and star (Ramadan, Eid) and a solstice sun - the season's wish cut
// into the lintel in the player's language, and a garland along the sheets' top edge. All drawn here as SVG; the snow is
// the fire's own shader (Stage.tsx FRAG_FESTIVE).
// Nothing here runs a timer of its own: the flames, the lanterns' sway and the bulbs move on the festive fire's frames
// (Stage.tsx stageFrame), with its clock and its flicker, so they burn in step with it and stand still where it does
// (reduced motion, the game running) or keep their first frame where it never draws (Animated fire off, no WebGL,
// drawing without the GPU). Where WebKit composites nothing (Stage.tsx flatDrawing) they stand still too: each change
// would be painted again on the CPU with everything round it; and while the fire burns there the shell leaves the
// mantel and the threshold's diyas out (AppShell.tsx: each frame of the fire paints the window again, them with it).
// The garland on the sheets never moves (the page under it is never repainted for it).
// The mantel's drawing is in mouth units: the mouth (the Play button, base.css --kiln) is 1000 wide and 880 tall, its
// apex at (500, 0); the mantel's SVGs lie over the mouth's box and draw outside it (overflow visible).
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { flatDrawing, stageFrame } from "./Stage";
import { useT } from "../i18n";

// ------------------------------------------------------------------ the frame: every moving part, on the fire's frames
type Updater = (t: number, F: number, k: number) => void;
const updaters = new Set<Updater>();
function frame(t: number, F: number, k: number) {
  for (const u of updaters) u(t, F, k);
}

/** Draws `make`'s frame once at once (the frame a still fire keeps), then on the fire's frames while mounted, unless
 *  `moving` is false (then that first frame stays). */
function useFrame(root: React.RefObject<Element>, make: (el: Element) => Updater, deps: unknown[], moving = true) {
  useLayoutEffect(() => {
    const el = root.current;
    if (!el) return;
    const u = make(el);
    u(12, 0.5, 0);
    if (!moving || flatDrawing()) return;
    updaters.add(u);
    stageFrame.fn = frame;
    return () => {
      updaters.delete(u);
      if (!updaters.size && stageFrame.fn === frame) stageFrame.fn = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
}

/** One flame's flicker: the fire's (F) and its own, so no two burn alike. */
function flick(t: number, F: number, ph: number) {
  return 0.55 * F + 0.45 * (0.5 + 0.28 * Math.sin(t * (6.3 + ph * 3.1) + ph * 17) + 0.22 * Math.sin(t * (13.7 + ph * 5) + ph * 5));
}

/** The flames, glows, lanterns and bulbs under `el` (data-fl, data-gl, data-sway, data-bulb), moved for (t, F). */
function animate(el: Element): Updater {
  const flames = [...el.querySelectorAll<SVGGElement>("[data-fl]")].map((e) => ({ e, ph: Number(e.dataset.fl) }));
  const glows = [...el.querySelectorAll<SVGElement>("[data-gl]")].map((e) => ({ e, ph: Number(e.dataset.gl), base: Number(e.dataset.base ?? 1) }));
  const sways = [...el.querySelectorAll<SVGGElement>("[data-sway]")].map((e) => {
    const [ph, amp, x, y] = (e.dataset.sway ?? "0 2 0 0").split(" ").map(Number);
    return { e, ph, amp, x, y };
  });
  const bulbs = [...el.querySelectorAll<SVGElement>("[data-bulb]")].map((e) => ({ e, ph: Number(e.dataset.bulb) }));
  const fire = [...el.querySelectorAll<SVGElement>("[data-firelit]")];
  return (t, F) => {
    for (const { e, ph } of flames) {
      const f = flick(t, F, ph);
      const lean = Math.sin(t * (1.1 + ph * 0.7) + ph * 9) * 5 + (f - 0.5) * 4;
      e.setAttribute("transform", `skewX(${lean.toFixed(2)}) scale(${(0.9 + 0.16 * f).toFixed(3)} ${(0.78 + 0.42 * f).toFixed(3)})`);
    }
    for (const { e, ph, base } of glows) e.setAttribute("opacity", (base * (0.62 + 0.38 * flick(t, F, ph))).toFixed(3));
    for (const { e, ph, amp, x, y } of sways) {
      const a = amp * (Math.sin(t * 0.83 + ph) * 0.8 + Math.sin(t * 1.91 + ph * 2.3) * 0.2);
      e.setAttribute("transform", `rotate(${a.toFixed(3)} ${x} ${y})`);
    }
    for (const { e, ph } of bulbs) {
      const tw = 0.72 + 0.18 * Math.sin(t * 0.9 + ph * 6.1) + 0.1 * F;
      e.setAttribute("opacity", tw.toFixed(3));
    }
    for (const e of fire) e.setAttribute("opacity", (0.55 + 0.45 * F).toFixed(3));
  };
}

// ------------------------------------------------------------------ evergreen, grown from a seed (the same every start)
function rng(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

interface Bough { shade: string[]; berries: [number, number][] }

/** Fir sprigs along a line (points every few units): needles as short strokes in four shades, the top ones warmer (lit
 *  from above by the candles), a few red berries. `dir` 1 lies the sprigs along the line, `droop` lets them hang. */
function growBough(seed: number, line: (u: number) => [number, number], n: number, opts: { len: [number, number]; needle: number; spread: number; droop: number; maxDown?: number; berries: number }): Bough {
  const r = rng(seed);
  const shade = ["", "", "", ""];
  const berries: [number, number][] = [];
  for (let i = 0; i < n; i += 1) {
    const u = (i + r() * 0.9) / n;
    const [x0, y0] = line(u);
    const side = r() < 0.5 ? -1 : 1;
    // how far the sprig points down (radians; up is negative): its spread, some droop, never past maxDown
    const down = Math.min(opts.maxDown ?? 9, (r() - 0.5) * opts.spread + (r() < 0.4 ? opts.droop * r() : -0.2 * r()));
    let ang = side < 0 ? Math.PI - down : down;
    const L = opts.len[0] + r() * (opts.len[1] - opts.len[0]);
    let x = x0 + (r() - 0.5) * 6, y = y0 + (r() - 0.5) * opts.needle;
    const steps = Math.max(4, Math.round(L / 4));
    const bend = (r() - 0.5) * 0.03 * side;
    for (let s = 0; s < steps; s += 1) {
      const dx = Math.cos(ang) * (L / steps), dy = Math.sin(ang) * (L / steps);
      const nx = x + dx, ny = y + dy;
      const nl = opts.needle * (1 - (s / steps) * 0.55);
      for (const sd of [-1, 1]) {
        const na = ang + sd * (0.95 + r() * 0.3);
        const ex = Math.cos(na) * nl, ey = Math.sin(na) * nl;
        // needles pointing up catch the candles' light; down ones stay in shadow
        const lit = -ey / nl;
        const b = lit > 0.45 ? (r() < 0.55 ? 3 : 2) : lit > -0.2 ? (r() < 0.5 ? 2 : 1) : r() < 0.7 ? 0 : 1;
        shade[b] += `M${x.toFixed(1)} ${y.toFixed(1)}l${ex.toFixed(1)} ${ey.toFixed(1)}`;
      }
      shade[s < steps / 2 ? 0 : 1] += `M${x.toFixed(1)} ${y.toFixed(1)}L${nx.toFixed(1)} ${ny.toFixed(1)}`;
      x = nx; y = ny; ang += bend;
    }
    if (r() < opts.berries) {
      const bx = x0 + (r() - 0.5) * 10, by = y0 - 2 + r() * 4;
      berries.push([bx, by], [bx + 4.5, by + 2.5], [bx - 1.5, by + 4.8]);
    }
  }
  return { shade, berries };
}

const NEEDLE = ["#0d1b12", "#18301f", "#24432a", "#3f5a2f"];

function BoughSvg({ b, w = 2.1, berry = 3.2 }: { b: Bough; w?: number; berry?: number }) {
  return (
    <g strokeLinecap="round" fill="none">
      {b.shade.map((d, i) => <path key={i} d={d} stroke={NEEDLE[i]} strokeWidth={w} />)}
      {b.berries.map(([x, y], i) => <circle key={i} cx={x} cy={y} r={berry} fill="url(#kf-berry)" />)}
    </g>
  );
}

// ------------------------------------------------------------------ the pieces (local units: base centre at 0,0, y down)
const FLAME = "M0 0C-.5-.06-.46-.52 0-1C.46-.52 .5-.06 0 0Z";

/** A candle's flame on top of (0,0): its glow on the wall (data-gl) and the flame itself (data-fl). */
function Flame({ x, y, s = 1, ph }: { x: number; y: number; s?: number; ph: number }) {
  return (
    <g transform={`translate(${x} ${y})`}>
      <circle cy={-11 * s} r={42 * s} fill="url(#kf-halo)" data-gl={ph} />
      <g data-fl={ph}>
        <path d={FLAME} transform={`scale(${9 * s} ${27 * s})`} fill="url(#kf-flame)" />
        <path d={FLAME} transform={`translate(0 ${-1.5 * s}) scale(${4.4 * s} ${13 * s})`} fill="#fff8e6" opacity=".92" />
      </g>
    </g>
  );
}

/** A taper: wax (a gradient across, lit on the left), a small wick, its flame. */
function Candle({ x, y, h, w = 9, wax, ph, s = 1 }: { x: number; y: number; h: number; w?: number; wax: string; ph: number; s?: number }) {
  return (
    <g>
      <rect x={x - w / 2} y={y - h} width={w} height={h} rx={1.6} fill={`url(#${wax})`} />
      <rect x={x - w / 2} y={y - h} width={w} height={h} rx={1.6} fill="url(#kf-waxlight)" />
      <path d={`M${x} ${y - h}l.6 -4`} stroke="#2a1608" strokeWidth={1.4} strokeLinecap="round" />
      <Flame x={x + 0.4} y={y - h - 2.5} s={s} ph={ph} />
    </g>
  );
}

/** Hanukkah menorah (a hanukkiah): eight lights in a row, the shamash raised apart in the middle. Brass. */
function Menorah({ x, y }: { x: number; y: number }) {
  const top = -124;
  const arms = [26, 50, 74, 98];
  return (
    <g transform={`translate(${x} ${y})`}>
      <ellipse cx={0} cy={-60} rx={150} ry={86} fill="url(#kf-wallglow)" data-gl={0.3} data-base={0.85} />
      {/* foot and stem */}
      <path d="M-40 0C-34-9-16-10-8-17L-6-60H6L8-17C16-10 34-9 40 0Z" fill="url(#kf-brass)" />
      <rect x={-5} y={top - 26} width={10} height={top * -1 + 26 - 56} fill="url(#kf-brass)" />
      <ellipse cx={0} cy={-34} rx={9} ry={4.5} fill="url(#kf-brass)" />
      <ellipse cx={0} cy={-58} rx={11} ry={5} fill="url(#kf-brass)" />
      {/* the eight arms: half ellipses from the stem up to one height */}
      {arms.map((r) => (
        <path key={r} d={`M${-r} ${top}A${r} ${r * 0.62} 0 0 0 ${r} ${top}`} fill="none" stroke="url(#kf-brassv)" strokeWidth={5.2} />
      ))}
      {/* cups: eight in the row, the shamash's above them in the middle */}
      {arms.flatMap((r) => [-r, r]).map((cx) => (
        <path key={cx} d={`M${cx - 8} ${top - 6}h16l-3 7h-10Z`} fill="url(#kf-brass)" />
      ))}
      <path d={`M-9 ${top - 32}h18l-3 7h-12Z`} fill="url(#kf-brass)" />
      {arms.flatMap((r) => [-r, r]).map((cx, i) => (
        <Candle key={cx} x={cx} y={top - 6} h={26} w={6.5} wax="kf-wax-cream" ph={i * 0.37 + 0.1} s={0.92} />
      ))}
      <Candle x={0} y={top - 32} h={26} w={6.5} wax="kf-wax-cream" ph={0.93} s={0.96} />
    </g>
  );
}

/** Kwanzaa kinara: seven candles, three red on the left, the black one in the centre, three green on the right. Wood. */
function Kinara({ x, y }: { x: number; y: number }) {
  const xs = [-72, -48, -24, 0, 24, 48, 72];
  const wax = ["kf-wax-red", "kf-wax-red", "kf-wax-red", "kf-wax-black", "kf-wax-green", "kf-wax-green", "kf-wax-green"];
  return (
    <g transform={`translate(${x} ${y})`}>
      <ellipse cx={0} cy={-70} rx={130} ry={80} fill="url(#kf-wallglow)" data-gl={0.6} data-base={0.8} />
      <path d="M-96 0V-10Q-96-16-90-16H90Q96-16 96-10V0Z" fill="url(#kf-wood)" />
      <path d="M-84-16C-80-24-76-30-70-30H70C76-30 80-24 84-16Z" fill="url(#kf-wood)" />
      <rect x={-88} y={-40} width={176} height={10} rx={3} fill="url(#kf-wood)" />
      {xs.map((cx) => <path key={cx} d={`M${cx - 8} -40l2-8h12l2 8Z`} fill="url(#kf-wood)" />)}
      <path d="M-88-39H88" stroke="#d9a066" strokeOpacity=".35" strokeWidth={1.2} />
      {xs.map((cx, i) => <Candle key={cx} x={cx} y={-48} h={i === 3 ? 58 : 54} w={10} wax={wax[i]} ph={i * 0.29 + 0.5} />)}
    </g>
  );
}

/** A diya: a small clay oil lamp, its spout pinched to one side with the flame on the wick. `flip` turns the spout left. */
function Diya({ x, y, ph, flip = false, s = 1 }: { x: number; y: number; ph: number; flip?: boolean; s?: number }) {
  return (
    <g transform={`translate(${x} ${y}) scale(${flip ? -s : s} ${s})`}>
      <ellipse cx={4} cy={-10} rx={52} ry={34} fill="url(#kf-wallglow)" data-gl={ph} data-base={0.75} />
      <ellipse cx={0} cy={0.5} rx={17} ry={3} fill="#000" opacity=".45" />
      <path d="M-24-12C-23-2-12 2 0 2S20-1 25-9L36-17C30-16 26-15 22-15H-24Z" fill="url(#kf-clay)" />
      <path d="M-24-12C-23-2-12 2 0 2S20-1 25-9L36-17C30-16 26-15 22-15H-24Z" fill="url(#kf-claylight)" />
      <ellipse cx={-1} cy={-13.4} rx={23} ry={3.6} fill="#3a1608" />
      <ellipse cx={-1} cy={-13.1} rx={19} ry={2.2} fill="#7a4a16" opacity=".8" />
      <path d="M-24-13.2C-14-11.2 12-11.2 24-13.6" stroke="#e8a060" strokeWidth={1.3} fill="none" opacity=".75" />
      {[-15, -8, -1, 6, 13].map((dx, i) => <circle key={dx} cx={dx} cy={-6 + Math.abs(dx) * 0.06} r={1.35} fill={i % 2 ? "#f1d27a" : "#f4ead6"} opacity=".8" />)}
      <path d="M33.5-17.5l.8-2.4" stroke="#2a1608" strokeWidth={1.4} strokeLinecap="round" />
      <Flame x={34.2} y={-19} s={0.95} ph={ph} />
    </g>
  );
}

/** The solstice sun on the wall: long and short rays round a disc, brass and straw gold. */
function Sun({ x, y }: { x: number; y: number }) {
  const rays = Array.from({ length: 24 }, (_, i) => {
    const a = (i / 24) * Math.PI * 2 - Math.PI / 2;
    const long = i % 2 === 0;
    const r0 = 36, r1 = long ? 74 : 54, w = long ? 0.07 : 0.06;
    const p = (r: number, aa: number) => `${(Math.cos(aa) * r).toFixed(1)} ${(Math.sin(aa) * r).toFixed(1)}`;
    return `M${p(r0, a - w)}L${p(r1, a)}L${p(r0, a + w)}Z`;
  }).join("");
  return (
    <g transform={`translate(${x} ${y})`}>
      <circle r={120} fill="url(#kf-sunglow)" data-gl={0.21} data-base={0.9} />
      <path d={rays} fill="url(#kf-sunray)" />
      <circle r={36} fill="url(#kf-sundisc)" />
      <circle r={29} fill="none" stroke="#6b4210" strokeWidth={1.6} opacity=".7" />
      <circle r={20} fill="none" stroke="#ffe9a8" strokeWidth={1.1} opacity=".5" />
      <path d="M0-12L3.2-3.2 12 0 3.2 3.2 0 12-3.2 3.2-12 0-3.2-3.2Z" fill="#fff1c4" opacity=".85" />
    </g>
  );
}

/** A red paper lantern on a cord from (0,0): ribs, gold caps, a tassel; it sways from where it hangs (data-sway). */
function PaperLantern({ x, y, cord, ph, s = 1 }: { x: number; y: number; cord: number; ph: number; s?: number }) {
  const c = cord;
  return (
    <g transform={`translate(${x} ${y}) scale(${s})`}>
      <g data-sway={`${ph} 2.6 0 0`}>
        <ellipse cx={0} cy={c + 44} rx={110} ry={96} fill="url(#kf-redglow)" data-gl={ph} data-base={0.9} />
        <path d={`M0 0V${c}`} stroke="#1a0c06" strokeWidth={1.8} />
        <rect x={-15} y={c} width={30} height={9} rx={2} fill="url(#kf-gold)" />
        <ellipse cx={0} cy={c + 44} rx={42} ry={37} fill="url(#kf-paper)" />
        {[14, 28].map((rx) => <ellipse key={rx} cx={0} cy={c + 44} rx={rx} ry={37} fill="none" stroke="#7a0d0a" strokeWidth={1.4} opacity=".55" />)}
        <path d={`M0 ${c + 7}V${c + 81}`} stroke="#7a0d0a" strokeWidth={1.4} opacity=".55" />
        <ellipse cx={-12} cy={c + 30} rx={14} ry={10} fill="#ffd9a0" opacity=".22" />
        <rect x={-17} y={c + 79} width={34} height={8} rx={2} fill="url(#kf-gold)" />
        <circle cx={0} cy={c + 93} r={4} fill="url(#kf-gold)" />
        <path d={`M-5 ${c + 96}C-6 ${c + 112} -5 ${c + 124} -6 ${c + 136}M0 ${c + 96}V${c + 140}M5 ${c + 96}C6 ${c + 112} 5 ${c + 124} 6 ${c + 136}`} stroke="#c0221a" strokeWidth={2.4} strokeLinecap="round" fill="none" />
        <path d={`M-2 ${c + 97}V${c + 138}M2 ${c + 97}V${c + 138}`} stroke="#e14b2e" strokeWidth={1.2} strokeLinecap="round" opacity=".7" />
      </g>
    </g>
  );
}

/** A fanous: a metal lantern with coloured glass and a candle inside, a crescent and star on its crown. Sways. */
function Fanous({ x, y, chain, ph, s = 1 }: { x: number; y: number; chain: number; ph: number; s?: number }) {
  const c = chain;
  return (
    <g transform={`translate(${x} ${y}) scale(${s})`}>
      <g data-sway={`${ph} 2 0 0`}>
        <ellipse cx={0} cy={c + 100} rx={105} ry={110} fill="url(#kf-amberglow)" data-gl={ph} data-base={0.85} />
        <path d={`M0 0V${c}`} stroke="#2a1a0a" strokeWidth={1.6} strokeDasharray="3 2" />
        {/* the crescent (horns up) and the star in it */}
        <path d={`M-13 ${c + 2}A14 14 0 1 0 13 ${c + 2}A11 11 0 1 1 -13 ${c + 2}Z`} fill="url(#kf-gold)" />
        <path d={`M0 ${c + 6}l1.9 3.9 4.3.6-3.1 3 .7 4.3L0 ${c + 15.8}l-3.8 2 .7-4.3-3.1-3 4.3-.6Z`} fill="#ffe7a0" />
        <path d={`M-4 ${c + 24}H4V${c + 30}H-4Z`} fill="url(#kf-brass)" />
        {/* the crown: a pierced dome */}
        <path d={`M-30 ${c + 62}C-30 ${c + 46} -14 ${c + 34} 0 ${c + 29}C14 ${c + 34} 30 ${c + 46} 30 ${c + 62}Z`} fill="url(#kf-brassdark)" />
        {[-18, -9, 0, 9, 18].map((dx) => <circle key={dx} cx={dx} cy={c + 52 - Math.abs(dx) * 0.2} r={1.7} fill="#ffc46a" opacity=".85" />)}
        <rect x={-34} y={c + 61} width={68} height={6} rx={2} fill="url(#kf-brass)" />
        {/* the glass: amber in the middle, ruby and teal at the sides */}
        <path d={`M-31 ${c + 67}L-24 ${c + 67}L-24 ${c + 136}L-31 ${c + 136}Z`} fill="#c9334a" />
        <path d={`M24 ${c + 67}L31 ${c + 67}L31 ${c + 136}L24 ${c + 136}Z`} fill="#2f9e92" />
        <rect x={-24} y={c + 67} width={48} height={69} fill="url(#kf-amberglass)" />
        <ellipse cx={0} cy={c + 112} rx={22} ry={26} fill="#fff1c0" opacity=".35" data-gl={ph + 0.4} data-base={0.6} />
        <path d={`M-24 ${c + 67}V${c + 136}M24 ${c + 67}V${c + 136}M-31 ${c + 67}V${c + 136}M31 ${c + 67}V${c + 136}M-31 ${c + 100}H31`} stroke="#3a2610" strokeWidth={2.4} />
        <path d={`M-12 ${c + 67}C-12 ${c + 84} 12 ${c + 84} 12 ${c + 67}`} stroke="#3a2610" strokeWidth={1.8} fill="none" />
        <rect x={-34} y={c + 135} width={68} height={6} rx={2} fill="url(#kf-brass)" />
        <path d={`M-28 ${c + 141}L0 ${c + 168}L28 ${c + 141}Z`} fill="url(#kf-brassdark)" />
        <circle cx={0} cy={c + 171} r={3.5} fill="url(#kf-gold)" />
      </g>
    </g>
  );
}

/** Warm string lights on a wire through the garland: bulbs on short stems, each with its own slow twinkle. */
function Lights({ pts, ph0 = 0, r = 4.2 }: { pts: [number, number][]; ph0?: number; r?: number }) {
  return (
    <g>
      {pts.map(([x, y], i) => (
        <g key={i} transform={`translate(${x} ${y})`}>
          <circle r={r * 6} fill="url(#kf-bulbglow)" data-bulb={ph0 + i * 0.61} />
          <ellipse cy={r * 0.4} rx={r * 0.75} ry={r} fill={i % 3 === 1 ? "#ffc874" : "#ffe1a8"} data-bulb={ph0 + i * 0.61} />
        </g>
      ))}
    </g>
  );
}

/** The gradients every piece draws with. */
function Defs() {
  return (
    <defs>
      <radialGradient id="kf-flame" cx=".5" cy=".78" r=".62">
        <stop offset="0" stopColor="#fff6d8" />
        <stop offset=".35" stopColor="#ffd27a" />
        <stop offset=".75" stopColor="#ff8a2c" />
        <stop offset="1" stopColor="#d1400c" stopOpacity=".2" />
      </radialGradient>
      <radialGradient id="kf-halo">
        <stop offset="0" stopColor="#ffb860" stopOpacity=".55" />
        <stop offset=".35" stopColor="#ff9a40" stopOpacity=".16" />
        <stop offset="1" stopColor="#ff7a20" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="kf-wallglow">
        <stop offset="0" stopColor="#ff9a48" stopOpacity=".2" />
        <stop offset=".5" stopColor="#ff7a28" stopOpacity=".07" />
        <stop offset="1" stopColor="#ff6a1a" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="kf-redglow">
        <stop offset="0" stopColor="#ff5a32" stopOpacity=".3" />
        <stop offset=".45" stopColor="#e0301a" stopOpacity=".1" />
        <stop offset="1" stopColor="#c01a10" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="kf-amberglow">
        <stop offset="0" stopColor="#ffb04a" stopOpacity=".26" />
        <stop offset=".5" stopColor="#ff8a2c" stopOpacity=".08" />
        <stop offset="1" stopColor="#ff8a2c" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="kf-sunglow">
        <stop offset="0" stopColor="#ffc860" stopOpacity=".2" />
        <stop offset=".55" stopColor="#ff9a40" stopOpacity=".05" />
        <stop offset="1" stopColor="#ff9a40" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="kf-bulbglow">
        <stop offset="0" stopColor="#ffd690" stopOpacity=".5" />
        <stop offset=".4" stopColor="#ffb050" stopOpacity=".12" />
        <stop offset="1" stopColor="#ff9a40" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="kf-paper" cx=".45" cy=".42" r=".62">
        <stop offset="0" stopColor="#ffc070" />
        <stop offset=".35" stopColor="#f2582a" />
        <stop offset=".8" stopColor="#b51d12" />
        <stop offset="1" stopColor="#6e0c08" />
      </radialGradient>
      <linearGradient id="kf-amberglass" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#e88a2a" />
        <stop offset=".6" stopColor="#ffc35a" />
        <stop offset="1" stopColor="#ffdf9a" />
      </linearGradient>
      <radialGradient id="kf-berry" cx=".35" cy=".35" r=".7">
        <stop offset="0" stopColor="#ff7a6a" />
        <stop offset=".5" stopColor="#b3202a" />
        <stop offset="1" stopColor="#5a0c10" />
      </radialGradient>
      <linearGradient id="kf-brass" x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stopColor="#7a5216" />
        <stop offset=".35" stopColor="#e2b04e" />
        <stop offset=".55" stopColor="#f6d68c" />
        <stop offset="1" stopColor="#6a4410" />
      </linearGradient>
      <linearGradient id="kf-brassv" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#f6d68c" />
        <stop offset=".5" stopColor="#c99434" />
        <stop offset="1" stopColor="#7a5216" />
      </linearGradient>
      <linearGradient id="kf-brassdark" x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stopColor="#3a260c" />
        <stop offset=".45" stopColor="#a07230" />
        <stop offset="1" stopColor="#2e1e0a" />
      </linearGradient>
      <linearGradient id="kf-gold" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#ffe08a" />
        <stop offset="1" stopColor="#b8801c" />
      </linearGradient>
      <linearGradient id="kf-sunray" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#f0c060" />
        <stop offset=".6" stopColor="#c88a2c" />
        <stop offset="1" stopColor="#ffd27a" />
      </linearGradient>
      <radialGradient id="kf-sundisc" cx=".42" cy=".4" r=".65">
        <stop offset="0" stopColor="#ffeaa8" />
        <stop offset=".55" stopColor="#e3a540" />
        <stop offset="1" stopColor="#8a5a18" />
      </radialGradient>
      <linearGradient id="kf-wood" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#8a5530" />
        <stop offset=".4" stopColor="#5a321a" />
        <stop offset="1" stopColor="#2a160c" />
      </linearGradient>
      <linearGradient id="kf-clay" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#d27a3e" />
        <stop offset=".55" stopColor="#a4471f" />
        <stop offset="1" stopColor="#4e1a0a" />
      </linearGradient>
      <radialGradient id="kf-claylight" cx=".85" cy=".1" r=".7">
        <stop offset="0" stopColor="#ffc078" stopOpacity=".55" />
        <stop offset="1" stopColor="#ffc078" stopOpacity="0" />
      </radialGradient>
      {[["cream", "#fbefd6", "#c9b48e"], ["red", "#e2414a", "#7e1218"], ["black", "#3a3432", "#0c0a0a"], ["green", "#3fa45e", "#124a24"]].map(([n, a, b]) => (
        <linearGradient key={n} id={`kf-wax-${n}`} x1="0" y1="0" x2="1" y2="0">
          <stop offset="0" stopColor={b} />
          <stop offset=".35" stopColor={a} />
          <stop offset="1" stopColor={b} />
        </linearGradient>
      ))}
      <linearGradient id="kf-waxlight" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#ffd890" stopOpacity=".45" />
        <stop offset=".3" stopColor="#ffd890" stopOpacity="0" />
      </linearGradient>
      <linearGradient id="kf-stone" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#4a3326" />
        <stop offset=".1" stopColor="#2c1d15" />
        <stop offset=".75" stopColor="#1b120d" />
        <stop offset="1" stopColor="#261810" />
      </linearGradient>
      <linearGradient id="kf-stonetop" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#6e4c36" />
        <stop offset="1" stopColor="#3a271b" />
      </linearGradient>
      <linearGradient id="kf-underlit" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#ff7a2c" stopOpacity="0" />
        <stop offset=".7" stopColor="#ff7a2c" stopOpacity=".08" />
        <stop offset="1" stopColor="#ff8a3c" stopOpacity=".42" />
      </linearGradient>
      <linearGradient id="kf-letter" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#ffe9b0" />
        <stop offset="1" stopColor="#d9a24a" />
      </linearGradient>
    </defs>
  );
}

// ------------------------------------------------------------------ the mantel on Play
const SHELF_TOP = -180, SHELF_BOT = -118;

/** The greeting's room on the lintel, between its joints (mouth units). */
const GREET_W = 600, GREET_SIZE = 37;

export function FestiveMantel() {
  const t = useT();
  const root = useRef<HTMLDivElement>(null);
  const greet = useRef<SVGTextElement>(null);
  const words = t("shell.appShell.festive");
  const boughs = useMemo(() => ({
    shelf: growBough(7, (u) => [-96 + u * 1260, SHELF_TOP - 4 - Math.sin(u * 40) * 2], 165, { len: [28, 60], needle: 10.5, spread: 1.1, droop: 0.4, maxDown: 0.22, berries: 0.18 }),
    sun: growBough(19, (u) => [500 + Math.cos(Math.PI * (0.12 + u * 0.76)) * 78, -270 + Math.sin(Math.PI * (0.12 + u * 0.76)) * 56], 22, { len: [18, 30], needle: 8, spread: 1.2, droop: 0.3, maxDown: 0.3, berries: 0.25 }),
    left: growBough(31, (u) => [-104 + Math.sin(u * 3) * 3, SHELF_TOP + 4 + u * 84], 16, { len: [16, 30], needle: 8.5, spread: 1.4, droop: 1.2, berries: 0.2 }),
  }), []);
  // the wire through the garland: sags between tacks, bulbs along it
  const wire = useMemo(() => {
    const pts: [number, number][] = [];
    const tacks = [-90, 60, 230, 400, 600, 770, 940, 1110];
    let d = "";
    for (let i = 0; i < tacks.length - 1; i += 1) {
      const a = tacks[i], b = tacks[i + 1], sag = 10;
      const y0 = SHELF_TOP - 4;
      d += `${i ? "" : `M${a} ${y0}`}Q${(a + b) / 2} ${y0 + sag * 2} ${b} ${y0}`;
      for (let j = 1; j <= 4; j += 1) {
        const u = j / 5, x = a + (b - a) * u, y = y0 + sag * 4 * u * (1 - u) * 0.98;
        pts.push([x, y + 3]);
      }
    }
    return { d, pts };
  }, []);
  // the mantel moves only while it shows (the Play page): behind a sheet the mouth is the pilot light (k 1)
  useFrame(root, (el) => {
    const run = animate(el);
    return (ft, F, k) => { if (k < 0.5) run(ft, F, k); };
  }, []);
  // a longer wish (some languages) is set smaller to keep between the lintel's joints
  useLayoutEffect(() => {
    const el = greet.current;
    if (!el) return;
    const fit = () => {
      el.style.removeProperty("font-size");
      const w = el.getComputedTextLength?.() ?? 0;
      if (w > GREET_W) el.style.fontSize = `${((GREET_SIZE * GREET_W) / w).toFixed(2)}px`;
    };
    fit();
    let gone = false;
    document.fonts?.ready.then(() => { if (!gone) fit(); }).catch(() => {});
    return () => { gone = true; };
  }, [words]);
  // three layers (festive.css: each its own, will-change), so what moves never draws the garland again: its thousands of
  // needles cost the most to draw, and in one picture with the flames every frame of the fire drew the whole mantel
  // again (about 0.9 s of drawing a second where the CPU draws, as in WebKit). Under the garland: the wall's light, the
  // sun, the lintel and its wish; the garland, still; over it: the lamps, the lights and the lanterns.
  return (
    <div ref={root} className="k-fest-mantel" aria-hidden>
      <svg viewBox="0 0 1000 880" focusable="false">
        <Defs />
        {/* the wall behind, lit by the candles (a light layer: festive.css blends it) */}
        <g className="kf-light">
          <ellipse cx={500} cy={-250} rx={640} ry={170} fill="url(#kf-wallglow)" data-gl={0.15} data-base={0.9} />
        </g>
        <Sun x={500} y={-276} />
        {/* the shelf: a stone lintel, its top caught by the candles, its underside by the fire */}
        <g>
          <rect x={-112} y={SHELF_TOP} width={1460} height={SHELF_BOT - SHELF_TOP} fill="url(#kf-stone)" />
          <rect x={-120} y={SHELF_TOP - 9} width={1480} height={11} rx={2} fill="url(#kf-stonetop)" />
          <rect x={-120} y={SHELF_TOP - 9} width={1480} height={1.5} fill="#c89a72" opacity=".5" />
          <rect x={-112} y={SHELF_TOP} width={1460} height={SHELF_BOT - SHELF_TOP} fill="url(#kf-underlit)" data-firelit="" />
          <path d={`M-112 ${SHELF_TOP + 8}H1348M-112 ${SHELF_BOT - 7}H1348`} stroke="#000" strokeOpacity=".35" strokeWidth={1.2} />
          <path d={`M-112 ${SHELF_TOP + 9}H1348`} stroke="#8a6448" strokeOpacity=".25" strokeWidth={1} />
          {/* the lintel's blocks and its squared end */}
          <path d={`M178 ${SHELF_TOP + 1}V${SHELF_BOT}M822 ${SHELF_TOP + 1}V${SHELF_BOT}M1180 ${SHELF_TOP + 1}V${SHELF_BOT}`} stroke="#000" strokeOpacity=".45" strokeWidth={2} />
          <path d={`M180.5 ${SHELF_TOP + 1}V${SHELF_BOT}M824.5 ${SHELF_TOP + 1}V${SHELF_BOT}M1182.5 ${SHELF_TOP + 1}V${SHELF_BOT}`} stroke="#9a7050" strokeOpacity=".18" strokeWidth={1} />
          <rect x={-112} y={SHELF_TOP} width={4} height={SHELF_BOT - SHELF_TOP} fill="#000" opacity=".35" />
        </g>
        {/* the greeting, cut into the lintel and gilded */}
        <g className="kf-greet" textAnchor="middle">
          <text ref={greet} className="kf-greet-a" x={500} y={SHELF_BOT - 19}>{words}</text>
        </g>
      </svg>
      <svg className="kf-still" viewBox="0 0 1000 880" focusable="false">
        <BoughSvg b={boughs.sun} w={1.9} />
        <BoughSvg b={boughs.shelf} />
        <BoughSvg b={boughs.left} />
      </svg>
      <svg viewBox="0 0 1000 880" focusable="false">
        <Kinara x={130} y={SHELF_TOP - 6} />
        <Diya x={300} y={SHELF_TOP - 5} ph={0.71} s={1.4} />
        <Diya x={372} y={SHELF_TOP - 5} ph={0.27} s={1.4} />
        <Diya x={628} y={SHELF_TOP - 5} ph={0.47} flip s={1.4} />
        <Diya x={700} y={SHELF_TOP - 5} ph={0.83} flip s={1.4} />
        <Menorah x={868} y={SHELF_TOP - 6} />
        {/* the wire and its bulbs, over the garland's front */}
        <path d={wire.d} stroke="#120a06" strokeWidth={1.6} fill="none" opacity=".9" />
        <Lights pts={wire.pts} />
        {/* hung from the lintel: the paper lantern over the left shoulder, the fanous over the right */}
        <PaperLantern x={20} y={SHELF_BOT} cord={50} ph={0.4} s={1.15} />
        <Fanous x={978} y={SHELF_BOT} chain={30} ph={2.1} s={1.05} />
      </svg>
    </div>
  );
}

/** Diyas at the threshold, on the floor in front of the jambs, their spouts toward the mouth: over the Play button (the
 *  shell puts this after it; it takes no clicks), as lamps stand in front of a door. */
export function FestiveHearth() {
  const root = useRef<SVGSVGElement>(null);
  // like the mantel, they move only on Play: behind a sheet they are hidden (festive.css) and the mouth is the pilot light
  useFrame(root, (el) => {
    const run = animate(el);
    return (ft, F, k) => { if (k < 0.5) run(ft, F, k); };
  }, []);
  return (
    <svg ref={root} className="k-fest-mantel k-fest-hearth" viewBox="0 0 1000 880" aria-hidden focusable="false">
      <Diya x={-34} y={906} ph={0.12} s={1.5} />
      <Diya x={20} y={912} ph={0.58} s={1.5} />
      <Diya x={980} y={912} ph={0.36} flip s={1.5} />
      <Diya x={1034} y={906} ph={0.91} flip s={1.5} />
    </svg>
  );
}

// ------------------------------------------------------------------ the garland on a sheet's top edge
/** Along the sheet's lit top edge: an evergreen garland in shallow swags with warm bulbs (the sheet's corners are too
 *  tight for the lanterns: they stay on the mantel). Its width follows the sheet's. */
export function FestiveRim() {
  const root = useRef<SVGSVGElement>(null);
  const [w, setW] = useState(0);
  useEffect(() => {
    const el = root.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setW(Math.round(el.getBoundingClientRect().width)));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const g = useMemo(() => {
    if (w < 100) return null;
    const span = 230, n = Math.max(2, Math.round(w / span)), step = w / n, sag = 7;
    const y = (x: number) => { const u = ((x % step) + step) % step / step; return 3 + sag * 4 * u * (1 - u); };
    const bough = growBough(3, (u) => { const x = -10 + u * (w + 20); return [x, y(x)]; }, Math.round(w / 9), { len: [16, 30], needle: 6.5, spread: 0.7, droop: 0.5, berries: 0.12 });
    const pts: [number, number][] = [];
    for (let i = 0; i < n; i += 1) for (const u of [0.2, 0.4, 0.6, 0.8]) { const x = (i + u) * step; pts.push([x, y(x) + 4]); }
    return { bough, pts, n, step };
  }, [w]);
  // drawn once, never moved: the page under it is never repainted for it
  useFrame(root, (el) => animate(el), [g], false);
  return (
    <svg ref={root} className="k-fest-rim" aria-hidden focusable="false">
      <Defs />
      {g && (
        <>
          <BoughSvg b={g.bough} w={1.6} berry={2.4} />
          <Lights pts={g.pts} r={2.6} ph0={1.3} />
        </>
      )}
    </svg>
  );
}
