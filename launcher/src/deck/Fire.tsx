// The Hearth fire in Deck mode: the same WebGL shader as the desktop stage (shell/Stage.tsx: the brick wall, the
// pointed arch, the chamber with its flames, the floor, the haze and the embers), laid out for the Deck frame. On Home
// the mouth stands where the Play button is (.deck-mouth, measured each frame, so it lifts with the focus); on every
// other tab it springs into the pilot light at the top right. The camera leans a little toward the focused control
// (there is no pointer). Heat follows the game: unlit without a game, a flare while it starts, embers while it runs,
// hotter while Play is focused.
// Budget as on the desktop: at most 1000 px wide, ~30 fps on Home, ~9 fps on other tabs, full rate only while the mouth
// moves or the heat changes; a still frame while the game runs or the launcher's window has no focus (the GPU is the
// game's), nothing while hidden. Off (the CSS mouth and glow stay): Settings' Animated fire off,
// KILN_LAUNCHER_NO_WEBGL=1, or no WebGL.
import { useEffect, useRef, type RefObject } from "react";
import { FRAG, UNIFORMS, VERT, type Heat } from "../shell/Stage";

/** Written by the Deck screens as they render; read by the loop each frame. */
export const deckFire = { play: false, heat: "idle" as Heat, hot: false, game: false };

function target(i: typeof deckFire): number {
  switch (i.heat) {
    case "flare": return 1.75;
    case "embers": return 0.85;
    case "low": return 0.45;
    case "unlit": return i.play ? 0.1 : 0.22;
    default: return i.hot ? 0.98 : i.play ? 0.64 : 0.8;
  }
}

export function DeckFire({ root, enabled }: { root: RefObject<HTMLDivElement | null>; enabled: boolean }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const app = root.current, canvas = ref.current;
    if (!app || !canvas) return;
    const still = !!window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
    let gl: WebGLRenderingContext | null = null;
    const U: Partial<Record<(typeof UNIFORMS)[number], WebGLUniformLocation | null>> = {};
    let raf = 0;
    const off = (why: string) => {
      gl = null;
      cancelAnimationFrame(raf);
      delete app.dataset.gl;
      if (why) console.warn("Deck fire off:", why);
    };
    if (!enabled) { off(""); return; }
    try { gl = canvas.getContext("webgl", { antialias: false, alpha: false, depth: false, stencil: false, powerPreference: "low-power" }); } catch { gl = null; }
    if (!gl) { off("no WebGL"); return; }
    const mk = (type: number, src: string) => {
      const o = gl!.createShader(type)!;
      gl!.shaderSource(o, src);
      gl!.compileShader(o);
      if (!gl!.getShaderParameter(o, gl!.COMPILE_STATUS)) { console.warn(gl!.getShaderInfoLog(o)); return null; }
      return o;
    };
    const vs = mk(gl.VERTEX_SHADER, VERT), fs = mk(gl.FRAGMENT_SHADER, FRAG);
    if (!vs || !fs) { off("shader"); return; }
    const prog = gl.createProgram()!;
    gl.attachShader(prog, vs); gl.attachShader(prog, fs); gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) { off("link"); return; }
    gl.useProgram(prog);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, "p");
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    for (const n of UNIFORMS) U[n] = gl.getUniformLocation(prog, n);
    const lost = (e: Event) => { e.preventDefault(); off("context lost"); };
    canvas.addEventListener("webglcontextlost", lost);
    app.dataset.gl = "1";
    let focused = document.hasFocus?.() ?? true;
    const onFocus = () => { focused = true; };
    const onBlur = () => { focused = false; };
    window.addEventListener("focus", onFocus);
    window.addEventListener("blur", onBlur);

    let k = deckFire.play ? 0 : 1, kv = 0;
    let h = deckFire.play && !still ? 0.02 : target(deckFire);
    const m = [0, 0];
    // the last place the Play mouth stood (its button is gone on other tabs)
    let mouth = { x: 0, y: 0, w: 0 };
    let last = performance.now(), lastDraw = 0, lastSize = "";
    const loop = (ms: number) => {
      if (!gl) return;
      raf = requestAnimationFrame(loop);
      if (document.hidden) { last = ms; return; }
      const dt = Math.min(0.05, Math.max(0.001, (ms - last) / 1000));
      last = ms;
      const i = deckFire;
      const W = app.clientWidth, H = app.clientHeight;
      const rem = parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
      const btn = i.play ? app.querySelector<HTMLElement>(".deck-mouth") : null;
      if (btn) {
        const r = btn.getBoundingClientRect();
        const a = app.getBoundingClientRect();
        mouth = { x: r.left - a.left, y: r.top - a.top, w: r.width };
      } else if (!mouth.w) {
        const w = 25 * rem;
        mouth = { x: W - 3 * rem - w, y: H - 5.25 * rem - w * 0.88, w };
      }
      const tgt = i.play ? 0 : 1;
      if (still) { k = tgt; kv = 0; }
      else for (let s = 0; s < 2; s += 1) { kv += (-170 * (k - tgt) - 24 * kv) * (dt / 2); k += kv * (dt / 2); }
      const heat = target(i);
      h += (heat - h) * (1 - Math.exp(-dt * 2.2));
      // the camera leans toward the focused control
      const f = document.activeElement instanceof HTMLElement && app.contains(document.activeElement) ? document.activeElement.getBoundingClientRect() : null;
      const fx = f ? ((f.left + f.width / 2) / W) * 2 - 1 : 0, fy = f ? 1 - ((f.top + f.height / 2) / H) * 2 : 0;
      m[0] += (fx * 0.6 - m[0]) * 0.04; m[1] += (fy * 0.4 - m[1]) * 0.04;
      const t = ms / 1000;
      const F = Math.max(0, Math.min(1, 0.5 + 0.22 * Math.sin(t * 11.3) + 0.16 * Math.sin(t * 6.1 + 1.1) + 0.12 * Math.sin(t * 2.7 + 0.3)));
      // the pilot light: top right, in the tab bar
      const pw = 2.25 * rem, px = W - 1.75 * rem - pw, py = (4.5 * rem - pw * 0.88) / 2;
      const dw = Math.max(14, mouth.w + (pw - mouth.w) * k), dh = dw * 0.88;
      const dx = mouth.x + (px - mouth.x) * k, dy = mouth.y + (py - mouth.y) * k;
      const floor = Math.max(0, H - (mouth.y + mouth.w * 0.88));
      const moving = Math.abs(kv) > 0.004 || Math.abs(k - tgt) > 0.002 || Math.abs(h - heat) > 0.02;
      const size = `${W}x${H}`;
      const away = i.game || !focused;
      const gap = moving || size !== lastSize ? 0 : away ? Infinity : i.play ? 33 : 110;
      if (ms - lastDraw < gap) return;
      lastDraw = ms;
      lastSize = size;
      const S = Math.min(0.72, 1000 / Math.max(W, 1));
      const cw = Math.max(2, Math.round(W * S)), ch = Math.max(2, Math.round(H * S));
      if (canvas.width !== cw || canvas.height !== ch) { canvas.width = cw; canvas.height = ch; gl.viewport(0, 0, cw, ch); }
      gl.uniform2f(U.uR!, cw, ch);
      gl.uniform1f(U.uT!, still ? 12 : t % 1000);
      gl.uniform1f(U.uH!, h);
      gl.uniform1f(U.uF!, F);
      gl.uniform4f(U.uD!, (dx + dw / 2) * S, (H - (dy + dh)) * S, (dw / 2) * S, dh * S);
      gl.uniform2f(U.uM!, m[0], m[1]);
      gl.uniform1f(U.uS!, S);
      gl.uniform1f(U.uL!, floor);
      gl.uniform1f(U.uK!, k);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    };
    raf = requestAnimationFrame(loop);
    return () => {
      cancelAnimationFrame(raf);
      canvas.removeEventListener("webglcontextlost", lost);
      window.removeEventListener("focus", onFocus);
      window.removeEventListener("blur", onBlur);
      delete app.dataset.gl;
    };
  }, [root, enabled]);
  return <canvas ref={ref} className="deck-fire-gl" aria-hidden />;
}
