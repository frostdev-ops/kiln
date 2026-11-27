// Dev script: stills and a loop of the Thanksgiving turkey (shell/thanksgiving.ts, shell/Stage.tsx FRAG_TURKEY), forced
// on with ?turkey=1 whatever the date: the Play page with the turkey on the spit, a 2x close-up of the mouth, a
// frame-stepped loop (page.clock drives the fire's time, so the video is smooth whatever the GPU; needs ffmpeg) and the
// pilot light on another page. Needs the dev server running:
//   bun run dev --port 1477 &   bun scripts/turkey-shots.ts <out dir> [port] [only: play|close|loop|pilot]
import { chromium, type Page } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";

const OUT = process.argv[2] ?? "out";
const PORT = Number(process.argv[3] ?? 1477);
const ONLY = process.argv[4] ?? "";
const want = (k: string) => !ONLY || ONLY.split(",").includes(k);
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({
  executablePath: process.env.KILN_CHROMIUM,
  args: ["--use-angle=gl", "--enable-gpu", "--ignore-gpu-blocklist"],
});

// SHOT_MS: the stills are taken at this time of the fire's clock (the spit rocks ±22° every 7 s), default 8 s
const SHOT_MS = Number(process.env.SHOT_MS ?? 8000);

async function open(scale: number, hash = "/", clock = true): Promise<Page> {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: scale });
  const page = await ctx.newPage();
  page.on("console", (m) => { if (m.type() === "warning" || m.type() === "error") console.log("[page]", m.text()); });
  if (clock) await page.clock.install({ time: 0 });
  await page.goto(`http://127.0.0.1:${PORT}/?mock=1&turkey=1#${hash}`);
  if (clock) await page.clock.runFor(2500);
  else await page.waitForTimeout(2600);
  await page.evaluate(() => document.fonts.ready);
  return page;
}

if (want("play")) {
  const page = await open(1);
  await page.clock.runFor(SHOT_MS - 2500);
  await page.screenshot({ path: join(OUT, "play-turkey.png") });
  await page.context().close();
}

if (want("close")) {
  const page = await open(2);
  await page.clock.runFor(SHOT_MS - 2500);
  const b = (await page.locator(".k-door").boundingBox())!;
  const g = (await page.locator(".k-thanks").boundingBox())!;
  const pad = 70;
  const x = Math.max(0, b.x - pad), y = Math.max(0, g.y - 18);
  await page.screenshot({ path: join(OUT, "play-turkey-closeup.png"), clip: { x, y, width: Math.min(1280 - x, b.width + pad * 2), height: Math.min(800 - y, b.y + b.height + 20 - y) } });
  await page.context().close();
}

if (ONLY.includes("angles")) {
  // four moments of the rocking spit, the mouth only
  const page = await open(1);
  const b = (await page.locator(".k-door").boundingBox())!;
  let at = 2500;
  for (const [i, ms] of [7000, 8750, 10500, 12250].entries()) {
    await page.clock.runFor(ms - at);
    at = ms;
    await page.screenshot({ path: join(OUT, `angle-${i * 90}.png`), clip: { x: b.x, y: b.y - 20, width: b.width, height: b.height + 20 } });
  }
  await page.context().close();
}

if (want("loop")) {
  // 7 s at 25 fps, the fire's own time stepped by the page clock; frames cropped to the mouth and its arch
  const page = await open(1, "/", true);
  const b = (await page.locator(".k-door").boundingBox())!;
  const g = (await page.locator(".k-thanks").boundingBox())!;
  const pad = 60, top = Math.max(0, Math.round(g.y - 24));
  const clip = { x: Math.round(b.x - pad), y: top, width: Math.round(b.width + pad * 2) & ~1, height: Math.round(b.y + b.height + 16 - top) & ~1 };
  const dir = join(OUT, ".frames");
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  const N = 175;
  for (let f = 0; f < N; f += 1) {
    await page.clock.runFor(40);
    await page.screenshot({ path: join(dir, `f${String(f).padStart(4, "0")}.png`), clip });
  }
  execFileSync("ffmpeg", ["-y", "-loglevel", "error", "-framerate", "25", "-i", join(dir, "f%04d.png"), "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "30", "-pix_fmt", "yuv420p", join(OUT, "turkey-loop.webm")]);
  execFileSync("ffmpeg", ["-y", "-loglevel", "error", "-framerate", "25", "-i", join(dir, "f%04d.png"),
    "-vf", "fps=12,scale=420:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle", join(OUT, "turkey-loop.gif")]);
  rmSync(dir, { recursive: true, force: true });
  await page.context().close();
}

if (want("pilot")) {
  const page = await open(1, "/runtime");
  await page.clock.runFor(3000);
  await page.screenshot({ path: join(OUT, "other-page-pilot.png") });
  await page.context().close();
  // the corner at 3x, to judge the pilot light's detail
  const big = await open(3, "/runtime");
  await big.clock.runFor(3000);
  await big.screenshot({ path: join(OUT, "other-page-pilot-corner.png"), clip: { x: 1280 - 200, y: 0, width: 200, height: 56 } });
  await big.context().close();
}

await browser.close();
