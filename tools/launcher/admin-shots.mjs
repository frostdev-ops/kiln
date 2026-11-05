#!/usr/bin/env node
// Screenshots of the launcher's admin pages, for review: every page of
// /servers/<id>/admin/* in Chromium (Playwright), saved to .out/launcher/admin-shots/<prefix><page>.png. Console errors
// and page errors are printed (exit 1 when there are any). Needs the admin dev server:
//   KILN_ADMIN_MOCK=1 bun x vite --config vite.admin.config.ts      (browser mock)   or with KILN_ADMIN_SERVER=<lab>
//   node tools/launcher/admin-shots.mjs [--base http://localhost:1421] [--server lab] [--prefix mock-] [--only players,map]
import { mkdirSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..", "..");
const require = createRequire(join(repo, "launcher", "package.json"));
const { chromium } = require("@playwright/test");

const arg = (k, d) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : d; };
const base = arg("--base", "http://localhost:1421");
const server = arg("--server", "lab");
const prefix = arg("--prefix", "");
const only = arg("--only", "");
const wait = Number(arg("--wait", "2500"));
const out = join(repo, ".out", "launcher", "admin-shots");
mkdirSync(out, { recursive: true });
const PAGES = ["players", "map", "zones", "items", "spawn", "world", "combat", "permissions", "content", "configs", "mods", "console", "saves", "crashes"];
const pages = only ? only.split(",") : PAGES;

// a Chromium Playwright can drive: its own (when installed for this version), else KILN_CHROMIUM, the 1243 build in the
// cache, or the system's
import { existsSync, readdirSync } from "node:fs";
const cache = join(process.env.HOME ?? "", ".cache", "ms-playwright");
const cached = existsSync(cache) ? readdirSync(cache).filter((d) => d.startsWith("chromium_headless_shell-")).map((d) => join(cache, d, "chrome-headless-shell-linux64", "chrome-headless-shell")).filter(existsSync).sort().reverse() : [];
let browser;
try {
  browser = await chromium.launch();
} catch {
  const exe = [process.env.KILN_CHROMIUM, ...cached, "/usr/bin/chromium"].find((p) => p && existsSync(p));
  browser = await chromium.launch({ executablePath: exe });
}
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
let errors = 0;
for (const p of pages) {
  const page = await ctx.newPage();
  page.on("console", (m) => { if (m.type() === "error") { errors++; console.log(`[${p}] console: ${m.text()}`); } });
  page.on("pageerror", (e) => { errors++; console.log(`[${p}] pageerror: ${e.message}`); });
  await page.goto(`${base}/#/servers/${server}/admin/${p}`);
  await page.waitForTimeout(wait);
  const file = join(out, `${prefix}${p}.png`);
  await page.screenshot({ path: file, fullPage: p !== "map" });
  console.log(`shot ${file}`);
  await page.close();
}
await browser.close();
process.exit(errors ? 1 : 0);
