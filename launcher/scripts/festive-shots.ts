// The festive season's look (shell/season.ts, shell/Festive.tsx) in pictures: the Play page at 1280x800 and 1920x1080, a
// 2x close-up of the mouth and its mantel, a sheet page, the still frame of reduced motion, the Settings row, and a
// frame-stepped loop (the page's clock is paused and stepped, so the video is smooth whatever the GPU). The browser
// mock, with ?theme=festive forcing the look. Needs the dev server running:
//   vite --port 1489 &   bun scripts/festive-shots.ts <out dir> [port] [only: play,big,close,sheet,reduced,settings,normal,loop,loopfull]
// KILN_CHROMIUM: a chromium with WebGL (the full one, not the headless shell). The loops: ffmpeg on PATH or FFMPEG.
import { chromium, type Page } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";

const OUT = process.argv[2] ?? "out";
const PORT = Number(process.argv[3] ?? 1489);
const ONLY = (process.argv[4] ?? "play,big,close,sheet,reduced,settings").split(",");
const want = (k: string) => ONLY.includes(k);
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({
  executablePath: process.env.KILN_CHROMIUM,
  args: ["--use-angle=gl", "--enable-gpu", "--ignore-gpu-blocklist"],
});

// the stills are taken at this time of the fire's clock
const SHOT_MS = Number(process.env.SHOT_MS ?? 4500);

async function open(scale: number, hash = "/", theme = "festive", w = 1280, h = 800, reduce = false): Promise<Page> {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: scale, reducedMotion: reduce ? "reduce" : "no-preference" });
  const page = await ctx.newPage();
  page.on("console", (m) => { if (m.type() === "error" && !m.text().includes("404")) console.log("[page]", m.text()); });
  page.on("pageerror", (e) => console.log("[pageerror]", e.message));
  await page.clock.install({ time: 0 });
  // paused: only runFor moves it (else it also runs while each screenshot is taken)
  await page.clock.pauseAt(1000);
  await page.goto(`http://127.0.0.1:${PORT}/?mock=1&theme=${theme}#${hash}`);
  await page.clock.runFor(2500);
  await page.evaluate(() => document.fonts.ready);
  return page;
}

async function still(name: string, page: Page, clip?: { x: number; y: number; width: number; height: number }) {
  await page.clock.runFor(SHOT_MS - 2500);
  await page.screenshot({ path: join(OUT, name), clip });
  await page.context().close();
}

if (want("play")) await still("play-festive-1280.png", await open(1));
if (want("big")) await still("play-festive-1920.png", await open(1, "/", "festive", 1920, 1080));
if (want("normal")) await still("play-normal-1280.png", await open(1, "/", "normal"));
if (want("sheet")) await still("sheet-servers-festive.png", await open(1, "/servers"));
if (want("reduced")) await still("play-festive-reduced-motion.png", await open(1, "/", "festive", 1280, 800, true));
if (want("close")) {
  const page = await open(2);
  const b = (await page.locator(".k-door").boundingBox())!;
  const x = Math.max(0, b.x - 70);
  await still("play-festive-closeup.png", page, { x, y: 78, width: 1280 - x, height: 722 });
}
if (want("settings")) {
  const page = await open(1, "/settings");
  const row = page.locator('[data-setting="festive"]');
  await row.scrollIntoViewIfNeeded();
  const g = (await page.locator('[data-setting="fire"]').boundingBox())!, r = (await row.boundingBox())!;
  await still("settings-festive-row.png", page, { x: Math.max(0, g.x - 30), y: Math.max(0, g.y - 60), width: Math.min(1280, r.width + 60), height: r.y + r.height - g.y + 80 });
}

// 7 s at 25 fps, frame by frame on the page's clock: the flames, the lanterns' sway, the snow. "loop": the mouth and its
// mantel at 1.5x; "loopfull": the whole window at 1x.
for (const [kind, scale, name] of [["loop", 1.5, "festive-loop.webm"], ["loopfull", 1, "festive-loop-full.webm"]] as const) {
  if (!want(kind)) continue;
  const FPS = 25, SECS = Number(process.env.LOOP_S ?? 7);
  const page = await open(scale);
  await page.clock.runFor(1500);
  const b = await page.locator(".k-door").boundingBox();
  const clip = kind === "loop" && b ? { x: Math.round(b.x - 112), y: 76, width: Math.round(1280 - b.x + 112), height: 724 } : undefined;
  const frames = join(OUT, ".frames");
  rmSync(frames, { recursive: true, force: true });
  mkdirSync(frames, { recursive: true });
  for (let i = 0; i < FPS * SECS; i += 1) {
    await page.clock.runFor(1000 / FPS);
    await page.screenshot({ path: join(frames, `${String(i).padStart(4, "0")}.png`), clip });
  }
  await page.context().close();
  const ff = process.env.FFMPEG ?? "ffmpeg";
  execFileSync(ff, ["-y", "-loglevel", "error", "-framerate", String(FPS), "-i", join(frames, "%04d.png"),
    "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "31", "-pix_fmt", "yuv420p", "-row-mt", "1", join(OUT, name)]);
  if (existsSync(join(OUT, name))) rmSync(frames, { recursive: true, force: true });
}

await browser.close();
