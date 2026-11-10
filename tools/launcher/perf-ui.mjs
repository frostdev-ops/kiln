#!/usr/bin/env node
// The launcher frontend's timings (docs/kiln/launcher/perf.md): a production build in mock mode (a plain browser, so
// src/api/mock.ts answers), headless Chromium only, never a window. Measures:
// - start: first contentful paint, the Play button ready (splash gone), JS fetched before it;
// - idle: 20 s on Play, on a sheet page (Library), and on Play with the window not focused: main-thread busy time
//   (CDP Performance metrics: script, style, layout), React commits (a DevTools-hook counter), backend calls by command,
//   long tasks;
// - route changes: click a header word until the new page has painted without a skeleton;
// - big data: the Logs page with a 2 MB log and the Library with 600 entries (answers planned into the mock).
//   node tools/launcher/perf-ui.mjs [--dist DIR] [--out FILE] [--idle SECONDS]
// --dist: a built frontend (default: build one into $KILN_OUT/perf/dist). --out: the JSON (default
// $KILN_OUT/perf/ui.json). Browser: KILN_CHROMIUM, else the newest chromium headless shell in ~/.cache/ms-playwright.
import { createServer } from "node:http";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, extname, join, resolve } from "node:path";

const repo = resolve(dirname(new URL(import.meta.url).pathname), "..", "..");
const launcher = join(repo, "launcher");
const require = createRequire(join(launcher, "package.json"));
const { chromium } = require("@playwright/test");

const arg = (name, dflt) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : dflt; };
const out_dir = process.env.KILN_OUT || join(repo, ".out", "launcher");
const dist = resolve(arg("--dist", join(out_dir, "perf", "dist")));
const outFile = resolve(arg("--out", join(out_dir, "perf", "ui.json")));
const IDLE = Number(arg("--idle", "20")) * 1000;
delete process.env.DISPLAY;
delete process.env.WAYLAND_DISPLAY;

if (!process.argv.includes("--dist")) {
  execFileSync(join(launcher, "node_modules", ".bin", "vite"), ["build", "--outDir", dist, "--emptyOutDir"], { cwd: launcher, stdio: "inherit" });
}

function browserPath() {
  if (process.env.KILN_CHROMIUM) return process.env.KILN_CHROMIUM;
  const cache = join(homedir(), ".cache", "ms-playwright");
  if (!existsSync(cache)) return undefined;
  for (const s of readdirSync(cache).filter((d) => d.startsWith("chromium_headless_shell-")).sort().reverse()) {
    const exe = join(cache, s, "chrome-headless-shell-linux64", "chrome-headless-shell");
    if (existsSync(exe)) return exe;
  }
  return undefined;
}

// ---------------------------------------------------------------- a static server for the build
const TYPES = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css", ".svg": "image/svg+xml", ".woff2": "font/woff2", ".png": "image/png", ".webp": "image/webp", ".json": "application/json", ".map": "application/json" };
const server = createServer((req, res) => {
  const path = decodeURIComponent((req.url ?? "/").split("?")[0]);
  let file = join(dist, path);
  if (!file.startsWith(dist) || !existsSync(file) || statSync(file).isDirectory()) file = join(dist, "index.html");
  res.writeHead(200, { "content-type": TYPES[extname(file)] ?? "application/octet-stream" });
  res.end(readFileSync(file));
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const base = `http://127.0.0.1:${server.address().port}`;

// counters the page keeps: React commits (React reports each to a DevTools hook) and long tasks
const INIT = `
  window.__perf = { commits: 0, long: [], t0: performance.now() };
  window.__REACT_DEVTOOLS_GLOBAL_HOOK__ = { isDisabled: false, supportsFiber: true, renderers: new Map(),
    inject() { return 1; }, onCommitFiberRoot() { window.__perf.commits += 1; }, onCommitFiberUnmount() {}, onPostCommitFiberRoot() {},
    checkDCE() {}, on() {}, off() {}, sub() { return () => {}; }, emit() {} };
  try { new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__perf.long.push(Math.round(e.duration)); }).observe({ type: "longtask", buffered: true }); } catch {}
`;

// Headless Chromium draws WebGL in software (SwiftShader), which takes the main thread and drowns everything else; the
// real webview draws the fire on the GPU. So everything runs with WebGL off (the plain mouth: Settings, Appearance),
// and one idle measurement on Play with the fire on shows its cost here.
const browser = await chromium.launch({ executablePath: browserPath(), headless: true, args: ["--disable-webgl", "--disable-3d-apis"] });
const glBrowser = await chromium.launch({ executablePath: browserPath(), headless: true });
const report = { when: new Date().toISOString(), dist, start: [], idle: {}, routes: {}, big: {} };

async function newPage(b = browser) {
  const ctx = await b.newContext({ viewport: { width: 1360, height: 860 } });
  await ctx.addInitScript(INIT);
  const page = await ctx.newPage();
  const cdp = await ctx.newCDPSession(page);
  await cdp.send("Performance.enable");
  return { ctx, page, cdp };
}

async function metrics(cdp) {
  const { metrics } = await cdp.send("Performance.getMetrics");
  return Object.fromEntries(metrics.map((m) => [m.name, m.value]));
}

const calls = (page) => page.evaluate(() => window.__kilnMock?.calls() ?? []);
const ready = (page) => page.waitForFunction(() => !document.querySelector(".k-splash") && !!document.querySelector(".k-door"), null, { timeout: 30_000 });

/** Main-thread work, React commits and backend calls while the page sits for `ms`. */
async function idle(page, cdp, ms) {
  const m0 = await metrics(cdp);
  const c0 = await page.evaluate(() => window.__perf.commits);
  const k0 = (await calls(page)).length;
  const l0 = await page.evaluate(() => window.__perf.long.length);
  await page.waitForTimeout(ms);
  const m1 = await metrics(cdp);
  const c1 = await page.evaluate(() => window.__perf.commits);
  const all = await calls(page);
  const long = await page.evaluate((n) => window.__perf.long.slice(n), l0);
  const per = {};
  for (const c of all.slice(k0)) per[c] = (per[c] ?? 0) + 1;
  const s = ms / 1000;
  const d = (k) => +(((m1[k] - m0[k]) * 1000) / s).toFixed(2);
  return {
    seconds: s,
    busyMsPerS: d("TaskDuration"), scriptMsPerS: d("ScriptDuration"), styleMsPerS: d("RecalcStyleDuration"), layoutMsPerS: d("LayoutDuration"),
    styleRecalcsPerS: +((m1.RecalcStyleCount - m0.RecalcStyleCount) / s).toFixed(1),
    commitsPerS: +((c1 - c0) / s).toFixed(2),
    callsPerMin: Object.fromEntries(Object.entries(per).sort().map(([k, v]) => [k, +((v * 60) / s).toFixed(1)])),
    longTasks: long,
  };
}

// ---------------------------------------------------------------- start (3 cold loads)
for (let i = 0; i < 3; i++) {
  const { ctx, page } = await newPage();
  const t = Date.now();
  await page.goto(base + "/#/");
  await ready(page);
  const wall = Date.now() - t;
  const r = await page.evaluate(() => {
    const fcp = performance.getEntriesByName("first-contentful-paint")[0]?.startTime ?? null;
    const res = performance.getEntriesByType("resource").filter((e) => e.name.endsWith(".js"));
    const nav = performance.getEntriesByType("navigation")[0];
    return { fcpMs: fcp && Math.round(fcp), domContentLoadedMs: Math.round(nav.domContentLoadedEventEnd), jsFiles: res.length,
      jsKB: Math.round(res.reduce((a, e) => a + (e.decodedBodySize || 0), 0) / 1024), commits: window.__perf.commits, longTasks: window.__perf.long };
  });
  report.start.push({ readyMs: wall, ...r, callsBeforeReady: (await calls(page)).length });
  await ctx.close();
}

// ---------------------------------------------------------------- idle
{
  const { ctx, page, cdp } = await newPage();
  await page.goto(base + "/#/");
  await ready(page);
  await page.waitForTimeout(2000);
  report.idle.play = await idle(page, cdp, IDLE);
  // not focused (the game in front): the window's blur, as a focus change reports it
  await page.evaluate(() => { Object.defineProperty(document, "hasFocus", { value: () => false, configurable: true }); window.dispatchEvent(new Event("blur")); });
  await page.waitForTimeout(1000);
  report.idle.playUnfocused = await idle(page, cdp, IDLE);
  await page.evaluate(() => { Object.defineProperty(document, "hasFocus", { value: () => true, configurable: true }); window.dispatchEvent(new Event("focus")); });
  await page.click('a.k-navb[href="#/library"]');
  await page.waitForTimeout(2000);
  report.idle.library = await idle(page, cdp, IDLE);
  // ---------------------------------------------------------------- route changes (twice: first visit, then warm)
  const routes = ["/profiles", "/library", "/runtime", "/logs", "/saves", "/settings", "/about", "/servers", "/editor", "/"];
  for (const pass of ["first", "again"]) {
    for (const r of routes) {
      const href = `#${r}`;
      const sel = r === "/settings" || r === "/about" ? `a.k-head-link[href="${href}"]` : `a.k-navb[href="${href}"]`;
      const t0 = await page.evaluate(() => performance.now());
      await page.click(sel);
      const t1 = await page.evaluate(async (route) => {
        const want = route === "/" ? ["#/", "#", ""] : [`#${route}`];
        const ok = () => want.some((w) => location.hash === w || location.hash.startsWith(`${w}/`)) &&
          document.querySelector(".k-route") && !document.querySelector(".k-route [aria-busy]");
        while (!ok()) await new Promise((res) => requestAnimationFrame(res));
        await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
        return performance.now();
      }, r);
      (report.routes[r] ??= {})[pass] = Math.round(t1 - t0);
    }
  }
  await ctx.close();
}

// ---------------------------------------------------------------- the fire on (software WebGL: see above)
{
  const { ctx, page, cdp } = await newPage(glBrowser);
  await page.goto(base + "/#/");
  await ready(page);
  await page.waitForTimeout(2000);
  report.idle.playWebgl = await idle(page, cdp, IDLE);
  await page.click('a.k-navb[href="#/library"]');
  await page.waitForTimeout(2000);
  report.idle.libraryWebgl = await idle(page, cdp, IDLE);
  await ctx.close();
}

// ---------------------------------------------------------------- big data: 600 Library entries, a 2 MB log
{
  const { ctx, page, cdp } = await newPage();
  await page.goto(base + "/#/");
  await ready(page);
  await page.evaluate(() => {
    const lib = [];
    const kinds = ["kiln", "native", "emf", "eml", "shroudforge", "set"];
    for (let i = 0; i < 600; i++) {
      const kind = kinds[i % kinds.length];
      lib.push({ kind, id: `perf-${kind}-${i}`, name: `Perf ${kind} mod ${i}`, versions: [{ version: "1.0.0", hash: `kdh1:${String(i).padStart(64, "0")}`,
        folder: `/data/library/${kind}/perf-${i}/1.0.0+00000000`, imported: "2026-11-01T00:00:00Z", side: "both", clients: "kiln", size: 1048576, source: null, manifest: null }] });
    }
    for (let i = 0; i < 40; i++) window.__kilnMock.planCommand("library_list", { result: lib });
    const lines = [];
    for (let i = 0; lines.length < 26000; i++) lines.push(`[${i % 97 ? "I" : "E"} 02:${String((i / 1000) % 60 | 0).padStart(2, "0")}:00.000] [kiln-svc-${i % 17}] tick ${i}: something happened with entity ${i * 31} in zone ${i % 9}`);
    const text = lines.join("\n") + "\n";
    for (let i = 0; i < 40; i++) window.__kilnMock.planCommand("logs_read", { result: { file: "kiln/logs/kiln.log", offset: 0, next: text.length, size: text.length, text, truncated: false } });
  });
  for (const [name, route] of [["library600", "/library"], ["logs2MB", "/logs"]]) {
    const m0 = await metrics(cdp);
    const t0 = await page.evaluate(() => performance.now());
    await page.click(`a.k-navb[href="#${route}"]`);
    const t1 = await page.evaluate(async (route) => {
      const ok = () => location.hash.startsWith(`#${route}`) && document.querySelector(".k-route") && !document.querySelector(".k-route [aria-busy]");
      while (!ok()) await new Promise((res) => requestAnimationFrame(res));
      await new Promise((res) => setTimeout(res, 600)); // the answer (30 ms in the mock) and its render
      await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
      return performance.now();
    }, route);
    const m1 = await metrics(cdp);
    const dom = await page.evaluate(() => document.querySelectorAll(".k-route *").length);
    report.big[name] = { settleMs: Math.round(t1 - t0), busyMs: Math.round((m1.TaskDuration - m0.TaskDuration) * 1000), scriptMs: Math.round((m1.ScriptDuration - m0.ScriptDuration) * 1000), domNodes: dom };
    report.big[name].idle = await idle(page, cdp, 6000);
  }
  await ctx.close();
}

await browser.close();
await glBrowser.close();
server.close();
mkdirSync(dirname(outFile), { recursive: true });
writeFileSync(outFile, JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, (k, v) => (k === "longTasks" ? `${v.length} tasks, max ${Math.max(0, ...v)} ms` : v), 1));
console.log(`report: ${outFile}`);
