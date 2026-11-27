// The admin Map page's layout, in mock mode: the map fills the page's height whatever the side panel holds (Markers,
// Find & filter, Live), at desktop sizes, the Deck's 1280×800 and the window's smallest 1024×640, and the side panel
// scrolls on its own. Kilnwarden found the bug (its 0.1.3 worked around it): .ka-mapwrap-page was not flex: 1, so
// .ka-map-root { flex: 1 } took the side panel's content height and the map grew and shrank with the tab. In a host page
// of no set height (the web harness) the map keeps the height the page measures.
// Screenshots of each size and tab go to SHOTS_DIR (default .out/launcher/shots/; SHOTS=0 skips them).
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { kapServer } from "./fixtures/kapServer";

const SHOTS = process.env.SHOTS_DIR || join(import.meta.dirname, "..", "..", ".out", "launcher", "shots");
const SIZES = [
  { name: "desktop", width: 1920, height: 1080 },
  { name: "laptop", width: 1360, height: 860 },
  { name: "deck", width: 1280, height: 800 },
  { name: "narrow", width: 1024, height: 640 },
] as const;
const TABS = [["layers", "Markers"], ["find", "Find & filter"], ["live", "Live"]] as const;

interface Box { top: number; bottom: number; height: number }
interface Layout { map: Box; root: Box; side: Box; host: Box; panel: { client: number; scroll: number }; vh: number }

async function open(page: Page, route: string) {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
  if (await page.evaluate((r) => location.hash !== `#${r}`, route)) await page.evaluate((r) => { location.hash = `#${r}`; }, route);
}

/** The map, its root and side panel, and the page area around them (the launcher's .k-server-page), in viewport pixels. */
function layout(page: Page, hostSel = ".k-server-page"): Promise<Layout> {
  return page.evaluate((hostSel) => {
    const box = (s: string) => {
      const b = document.querySelector(s)!.getBoundingClientRect();
      return { top: Math.round(b.top), bottom: Math.round(b.bottom), height: Math.round(b.height) };
    };
    const panel = document.querySelector<HTMLElement>(".ka-map-panel")!;
    return {
      map: box(".ka-map"), root: box(".ka-map-root"), side: box(".ka-map-side"), host: box(hostSel),
      panel: { client: panel.clientHeight, scroll: panel.scrollHeight }, vh: window.innerHeight,
    };
  }, hostSel);
}

/** Opens a side panel tab and waits until its content and the layout have settled. */
async function showTab(page: Page, label: string, hostSel?: string) {
  await page.getByRole("tab", { name: label, exact: true }).click();
  await expect(page.getByRole("tab", { name: label, exact: true })).toHaveClass(/\bon\b/);
  let last = "";
  await expect.poll(async () => { const now = JSON.stringify(await layout(page, hostSel)); const same = now === last; last = now; return same; }, { intervals: [150] }).toBe(true);
}

for (const size of SIZES) {
  test(`the map keeps one height across Markers, Find & filter and Live: ${size.name} ${size.width}×${size.height}`, async ({ page }) => {
    await page.setViewportSize({ width: size.width, height: size.height });
    await open(page, "/servers/lab/admin/map");
    await expect(page.locator(".ka-map")).toBeVisible({ timeout: 20_000 });
    // the Live tab's players come from the state pushes: wait for them, so its content is whole
    await showTab(page, "Live");
    await expect(page.locator(".ka-map-panel .ka-map-lrow").first()).toBeVisible({ timeout: 15_000 });

    const seen: Record<string, Layout> = {};
    for (const [key, label] of TABS) {
      await showTab(page, label);
      seen[key] = await layout(page);
      if (process.env.SHOTS !== "0") {
        mkdirSync(SHOTS, { recursive: true });
        await page.screenshot({ path: join(SHOTS, `map-layout-${size.name}-${size.width}x${size.height}-${key}.png`) });
      }
    }
    const l = seen.layers, f = seen.find, v = seen.live;
    console.log(`${size.name} ${size.width}x${size.height}: map ${l.map.height}/${f.map.height}/${v.map.height} px, host ${l.host.top}-${l.host.bottom}, side ${JSON.stringify(v.panel)}`);

    // one height whatever the tab holds
    expect(f.map, "Find & filter vs Markers").toEqual(l.map);
    expect(v.map, "Live vs Markers").toEqual(l.map);
    for (const [key] of TABS) {
      const s = seen[key];
      // the map fills the page area's height (no more than a small gap under it) and stays inside the window
      expect(s.root.top, `${key}: the map starts at the page area's top`).toBeLessThanOrEqual(s.host.top + 1);
      expect(s.root.bottom, `${key}: the map ends inside the page area`).toBeLessThanOrEqual(s.host.bottom);
      expect(s.root.bottom, `${key}: the map reaches the page area's bottom`).toBeGreaterThanOrEqual(s.host.bottom - 24);
      expect(s.host.bottom, `${key}: the page area ends inside the window`).toBeLessThanOrEqual(s.vh);
      expect(s.map.height, `${key}: the map is the root's height`).toBe(s.root.height);
      // the side panel is the map's height: its content scrolls inside it, never stretching the map
      expect(s.side.height, `${key}: the side panel is the map's height`).toBe(s.root.height);
    }
  });
}

test("in a host page of no set height (the admin pages' web harness) the map keeps MapView's height across the tabs", async ({ page }) => {
  // the harness puts the pages in a plain scrolling block, no flex column: the map's height is MapView's `height`
  await kapServer(page);
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/src/admin/harness/index.html?kap=ws://kap.test/kap#/servers/lab/admin/map");
  await expect(page.locator(".ka-map")).toBeVisible({ timeout: 30_000 });
  await showTab(page, "Live", ".k-app");
  await expect(page.locator(".ka-map-panel .ka-map-lrow").first()).toBeVisible({ timeout: 15_000 });
  const seen: Layout[] = [];
  for (const [, label] of TABS) {
    await showTab(page, label, ".k-app");
    seen.push(await layout(page, ".k-app"));
  }
  console.log(`harness 1280x800: map ${seen.map((s) => s.map.height).join("/")} px, root ${seen[0].root.top}-${seen[0].root.bottom}`);
  expect(seen[1].map).toEqual(seen[0].map);
  expect(seen[2].map).toEqual(seen[0].map);
  // the page's own measure: the window's height under the page, less 18 px
  expect(seen[0].root.bottom).toBe(seen[0].vh - 18);
  for (const s of seen) expect(s.side.height).toBe(s.root.height);
});

test("the side panel scrolls on its own: the map stays put while the Live tab scrolls (1024×640)", async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 640 });
  await open(page, "/servers/lab/admin/map");
  await showTab(page, "Live");
  await expect(page.locator(".ka-map-panel .ka-map-lrow").first()).toBeVisible({ timeout: 15_000 });
  await showTab(page, "Live");
  const before = await layout(page);
  // the Live tab holds more than the window's smallest size shows
  expect(before.panel.scroll).toBeGreaterThan(before.panel.client);
  await page.locator(".ka-map-panel").evaluate((e) => { e.scrollTop = e.scrollHeight; });
  await expect.poll(() => page.locator(".ka-map-panel").evaluate((e) => e.scrollTop)).toBeGreaterThan(0);
  const after = await layout(page);
  expect(after.map).toEqual(before.map);
  expect(after.root).toEqual(before.root);
  // the page itself does not scroll for it
  expect(await page.locator(".k-server-page").evaluate((e) => e.scrollTop)).toBe(0);
});

for (const size of SIZES) {
  test(`the search results stay inside the map, their last hit in reach: ${size.name} ${size.width}×${size.height}`, async ({ page }) => {
    // the list was 70vh tall from under the search box whatever the map's own height, so under the launcher's server
    // header its end ran past the map's bottom edge, which clips it: the last hits could not be scrolled into view
    await page.setViewportSize({ width: size.width, height: size.height });
    await open(page, "/servers/lab/admin/map");
    await expect(page.locator(".ka-map")).toBeVisible({ timeout: 20_000 });
    await page.getByRole("searchbox").fill("Brannoc");
    const results = page.locator(".ka-map-results");
    await expect(results.locator("button").first()).toBeVisible({ timeout: 15_000 });
    // as many hits as the game's markers give (the mock has no map art, so few match): copies of the first
    await results.evaluate((r) => {
      const hit = r.querySelector("button")!;
      for (let i = 0; i < 40; i++) r.appendChild(hit.cloneNode(true));
    });
    const rect = (sel: string) => page.locator(sel).evaluate((e) => { const b = e.getBoundingClientRect(); return { top: Math.round(b.top), bottom: Math.round(b.bottom) }; });
    const root = await rect(".ka-map-root");
    const list = await rect(".ka-map-results");
    expect(list.bottom, "the list ends inside the map").toBeLessThanOrEqual(root.bottom);
    await results.evaluate((r) => { r.scrollTop = r.scrollHeight; });
    const last = await results.locator("button").last().evaluate((e) => { const b = e.getBoundingClientRect(); return { top: Math.round(b.top), bottom: Math.round(b.bottom) }; });
    expect(last.bottom, "the last hit, scrolled to, is above the map's bottom edge").toBeLessThanOrEqual(root.bottom);
    expect(last.top).toBeGreaterThanOrEqual(list.top);
  });
}
