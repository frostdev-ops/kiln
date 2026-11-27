// Thanksgiving weekend (shell/thanksgiving.ts): the Play page's fire roasts a turkey (shell/Stage.tsx FRAG_TURKEY), the
// pilot light shows a drumstick and "Happy Thanksgiving" sits over the mouth. The date decides; ?turkey=1 / ?turkey=0
// force it here. Off, the launcher compiles and draws the same fire as every other day. Reduced motion: the bird at rest,
// no drips. Animated fire off: no turkey and no greeting, the CSS mouth as always. And the bird's extra shader cost on
// the slow path (WebGL drawn on the CPU, as the headless browser's SwiftShader does) is measured against every day's.
import { expect, test, type Page } from "@playwright/test";

// a smaller window than the suite's: where the CPU draws WebGL (the headless browser) the turkey's frames are dear, and the
// specs beside these should not starve for it
test.use({ viewport: { width: 1024, height: 640 } });

type Rec = { __frags: string[]; __uV: number[]; __draws: number };

/** Records the fragment shaders compiled, the values given to the turkey's uV and the frames drawn. */
async function record(page: Page) {
  await page.addInitScript(() => {
    const w = window as unknown as Rec;
    w.__frags = [];
    w.__uV = [];
    w.__draws = 0;
    const P = WebGLRenderingContext.prototype;
    const src = P.shaderSource, loc = P.getUniformLocation, u1 = P.uniform1f, draw = P.drawArrays;
    const named = new WeakMap<WebGLUniformLocation, string>();
    P.shaderSource = function (s: WebGLShader, text: string) {
      if (text.includes("gl_FragColor")) w.__frags.push(text);
      return src.call(this, s, text);
    };
    P.getUniformLocation = function (p: WebGLProgram, name: string) {
      const l = loc.call(this, p, name);
      if (l) named.set(l, name);
      return l;
    };
    P.uniform1f = function (l: WebGLUniformLocation | null, v: number) {
      if (l && named.get(l) === "uV") w.__uV.push(v);
      return u1.call(this, l, v);
    };
    P.drawArrays = function (m: number, f: number, c: number) {
      w.__draws += 1;
      return draw.call(this, m, f, c);
    };
  });
}
/** The fragment shaders compiled, once each (dev mode's StrictMode starts the fire twice). */
const kinds = (frags: string[]) => [...new Set(frags)];
const rec = (page: Page) => page.evaluate(() => { const w = window as unknown as Rec; return { frags: w.__frags.slice(), uV: w.__uV.slice(), draws: w.__draws }; });
const hasGl = (page: Page) => page.evaluate(() => !!document.createElement("canvas").getContext("webgl"));

async function open(page: Page, query: string, route = "/") {
  await page.goto(`/${query}#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

test("forced on: the greeting shows over the mouth and the fire draws the turkey's shader", async ({ page }) => {
  await record(page);
  await open(page, "?turkey=1");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts (and so no turkey)");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  const hello = page.locator(".k-thanks");
  await expect(hello).toHaveText("Happy Thanksgiving");
  await expect(hello).toBeVisible();
  // over the mouth (the Play button's arch), inside the window
  const door = (await page.locator(".k-door").boundingBox())!, box = (await hello.boundingBox())!;
  expect(box.y + box.height).toBeLessThan(door.y + 4);
  expect(box.x).toBeGreaterThanOrEqual(door.x - 40);
  expect(box.x + box.width).toBeLessThanOrEqual(page.viewportSize()!.width);
  // it compiled (no fallback to the CSS mouth) and draws frame after frame, the bird rocking (uV 1)
  const a = await rec(page);
  expect(kinds(a.frags).length).toBe(1);
  expect(a.frags[0]).toContain("roast(");
  await expect.poll(async () => (await rec(page)).draws - a.draws).toBeGreaterThan(2);
  const b = await rec(page);
  expect(new Set(b.uV)).toEqual(new Set([1]));
  // another page: the greeting goes with the Play page, the pilot light (the drumstick) keeps the turkey's shader
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  await expect(hello).toBeHidden();
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  expect((await rec(page)).frags.length).toBe(a.frags.length);
});

test("forced off: today's Play page, every day's fire", async ({ page }) => {
  await record(page);
  await open(page, "?turkey=0");
  await expect(page.locator(".k-door")).toBeVisible();
  await expect(page.locator(".k-thanks")).toHaveCount(0);
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await page.waitForTimeout(600);
  const r = await rec(page);
  expect(kinds(r.frags).length).toBe(1);
  for (const w of ["roast(", "uV", "drum(", "smoke("]) expect(r.frags[0]).not.toContain(w);
  expect(r.uV).toEqual([]);
  expect(r.draws).toBeGreaterThan(3);
  // the same text as the shipped every-day shader
  const plain = await page.evaluate(async () => (await import("/src/shell/Stage.tsx")).FRAG as string);
  expect(r.frags[0]).toBe(plain);
});

test("reduced motion: the turkey stands still (uV 0: no rock, no drips), the greeting with it", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await record(page);
  await open(page, "?turkey=1");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await expect(page.locator(".k-thanks")).toBeVisible();
  await expect(page.locator(".k-thanks")).toHaveCSS("animation-name", "none");
  await page.waitForTimeout(800);
  const r = await rec(page);
  expect(r.uV.length).toBeGreaterThan(0);
  expect(new Set(r.uV)).toEqual(new Set([0]));
  // a still frame: no frame loop meanwhile
  await page.waitForTimeout(600);
  expect((await rec(page)).draws).toBe(r.draws);
});

test("a GPU that cannot build the turkey's shader keeps every day's fire, without the greeting", async ({ page }) => {
  await record(page);
  // a driver that refuses the bigger shader (the turkey's) but builds every day's
  await page.addInitScript(() => {
    const P = WebGLRenderingContext.prototype;
    const texts = new WeakMap<WebGLShader, string>();
    const src = P.shaderSource, param = P.getShaderParameter;
    P.shaderSource = function (s: WebGLShader, text: string) { texts.set(s, text); return src.call(this, s, text); };
    P.getShaderParameter = function (s: WebGLShader, name: number) {
      if (name === this.COMPILE_STATUS && texts.get(s)?.includes("roast(")) return false;
      return param.call(this, s, name);
    };
  });
  await open(page, "?turkey=1");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  const plain = await page.evaluate(async () => (await import("/src/shell/Stage.tsx")).FRAG as string);
  const a = await rec(page);
  expect(kinds(a.frags)).toContain(plain);
  await expect.poll(async () => (await rec(page)).draws - a.draws).toBeGreaterThan(2);
  expect((await rec(page)).uV).toEqual([]);
  await expect(page.locator(".k-thanks")).toBeHidden();
});

test("Animated fire off: no turkey and no greeting, the CSS mouth as always", async ({ page }) => {
  await record(page);
  await open(page, "?turkey=1", "/settings");
  await page.getByText("Animated fire", { exact: true }).click();
  await expect(page.getByRole("switch", { name: /^Animated fire/ })).not.toBeChecked();
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  await expect(page.locator(".k-door")).toBeVisible();
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-gl");
  await expect(page.locator(".k-thanks")).toHaveCount(0);
});

test("the greeting in Polish (the longest) wraps over the mouth and stays in the window", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" }); // a still fire: the layout is the point here, not the frames
  await open(page, "?turkey=1", "/settings");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await page.locator("#st-lang").selectOption("pl");
  await expect.poll(() => page.evaluate(() => document.documentElement.lang)).toBe("pl");
  await page.evaluate(() => { location.hash = "#/"; });
  const hello = page.locator(".k-thanks");
  await expect(hello).toHaveText("Szczęśliwego Święta Dziękczynienia");
  const box = (await hello.boundingBox())!, door = (await page.locator(".k-door").boundingBox())!;
  expect(box.x + box.width).toBeLessThanOrEqual(page.viewportSize()!.width);
  expect(box.y).toBeGreaterThan(76); // under the header
  expect(box.y + box.height).toBeLessThan(door.y + 4);
  // its text does not spill past its box (it wraps)
  expect(await hello.evaluate((el) => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
});

test("the turkey's cost: frame time of each shader at Play and as the pilot light (the band behind a sheet)", async ({ page }) => {
  await open(page, "?turkey=0", "/settings");
  test.skip(!(await hasGl(page)), "no WebGL in this browser");
  const r = await page.evaluate(async () => {
    const { FRAG, FRAG_TURKEY, VERT, stageCanvas } = await import("/src/shell/Stage.tsx");
    const W = 1280, H = 800;
    /** ms per frame for one shader, `n` frames queued and then waited for: Play (the whole window, the mouth at its Play
     *  size) or the pilot light (the header band behind a sheet). */
    const time = (frag: string, pilot: boolean, n: number) => {
      const geo = stageCanvas(W, H, pilot);
      const c = document.createElement("canvas");
      c.width = geo.cw; c.height = geo.ch;
      const gl = c.getContext("webgl", { antialias: false, alpha: false, depth: false, stencil: false })!;
      const sh = (t: number, s: string) => { const o = gl.createShader(t)!; gl.shaderSource(o, s); gl.compileShader(o); if (!gl.getShaderParameter(o, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(o) ?? "compile"); return o; };
      const p = gl.createProgram()!;
      gl.attachShader(p, sh(gl.VERTEX_SHADER, VERT)); gl.attachShader(p, sh(gl.FRAGMENT_SHADER, frag)); gl.linkProgram(p); gl.useProgram(p);
      gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
      gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
      const u = (name: string) => gl.getUniformLocation(p, name);
      const S = geo.S, k = pilot ? 1 : 0;
      const bw = Math.min(800, W * 0.4, H * 0.625), bx = W - 48 - bw, by = H - 84 - bw * 0.88;
      const dw = bw + (36 - bw) * k, dh = dw * 0.88, dx = bx + (W - 60 - bx) * k, dy = by + (7 - by) * k;
      gl.viewport(0, 0, geo.cw, geo.ch);
      gl.uniform2f(u("uR"), geo.cw, geo.fullCh); gl.uniform1f(u("uO"), geo.offset); gl.uniform1f(u("uH"), 0.64); gl.uniform1f(u("uF"), 0.6);
      gl.uniform4f(u("uD"), (dx + dw / 2) * S, (H - (dy + dh)) * S, (dw / 2) * S, dh * S); gl.uniform2f(u("uM"), 0.2, -0.1);
      gl.uniform1f(u("uS"), S); gl.uniform1f(u("uL"), 84); gl.uniform1f(u("uK"), k); gl.uniform1f(u("uV"), 1);
      const px = new Uint8Array(4);
      const run = (from: number, count: number) => { for (let i = 0; i < count; i += 1) { gl.uniform1f(u("uT"), 8 + (from + i) * 0.033); gl.drawArrays(gl.TRIANGLES, 0, 3); } gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px); };
      run(0, 2);
      const t0 = performance.now();
      run(2, n);
      const ms = (performance.now() - t0) / n;
      gl.getExtension("WEBGL_lose_context")?.loseContext();
      return ms;
    };
    const renderer = (() => { const gl = document.createElement("canvas").getContext("webgl")!; const d = gl.getExtension("WEBGL_debug_renderer_info"); return d ? String(gl.getParameter(d.UNMASKED_RENDERER_WEBGL)) : "?"; })();
    const cpu = /SwiftShader|llvmpipe|softpipe|Software/i.test(renderer);
    // interleaved, so a busy moment of the machine falls on both
    // on the CPU a short sample (about a second of work): longer would starve the specs beside it
    const out = { renderer, cpu, play: 0, playTurkey: 0, pilot: 0, pilotTurkey: 0 };
    const n = cpu ? 3 : 60, rounds = cpu ? 1 : 3;
    for (let round = 0; round < rounds; round += 1) {
      out.play += time(FRAG, false, n) / rounds; out.playTurkey += time(FRAG_TURKEY, false, n) / rounds;
      out.pilot += time(FRAG, true, n * 2) / rounds; out.pilotTurkey += time(FRAG_TURKEY, true, n * 2) / rounds;
    }
    return out;
  });
  console.log(`turkey cost (${r.renderer}, 1280x800 window): Play ${r.play.toFixed(3)} -> ${r.playTurkey.toFixed(3)} ms/frame (x${(r.playTurkey / r.play).toFixed(2)}); `
    + `pilot light band ${r.pilot.toFixed(3)} -> ${r.pilotTurkey.toFixed(3)} ms/frame (x${(r.pilotTurkey / r.pilot).toFixed(2)})`);
  if (r.cpu) {
    // WebGL drawn on the CPU (the headless browser's SwiftShader): every branch of the big shader is paid for, so this is
    // the worst case, not a launcher path (drawing without the GPU, the launcher has no WebGL fire: app_info.noWebgl).
    // It only guards against the bird growing much dearer still (2026-11-26: about x6 on both, Play 33 -> 195 ms).
    expect(r.playTurkey / r.play).toBeLessThan(14);
    expect(r.pilotTurkey / r.pilot).toBeLessThan(14);
  } else {
    // on a GPU (RTX 3080 Ti, 2026-11-26: Play 0.08-0.10 -> 0.14-0.19 ms, the band 0.012-0.040 -> 0.017-0.045 ms) the bird about doubles the Play
    // frame, which stays far inside the slow path's budget (FLAT_PLAY_MS, one frame per 66 ms; 33 ms elsewhere) even on a
    // GPU some 80 times slower; behind a sheet (the pilot light's band) it costs next to nothing
    expect(r.playTurkey / r.play).toBeLessThan(3);
    expect(r.pilotTurkey).toBeLessThan(r.pilot * 2 + 0.05);
  }
});
