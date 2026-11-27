// A dialog opened over the admin map takes every click (mock mode). The map's Leaflet layers carry z-indexes of 400 to
// 1200 (its panes, its canvas, its controls); map.css makes .ka-map-root a stacking context of its own, so they stay
// below what the design system puts on the body (dialogs at z-index 50, toasts at 60). In the launcher the sheet
// (.k-sheet, z-index 3) already held them; the harness hosts the pages as Kilnwarden does, with nothing around them,
// where Kilnwarden 0.1.3's dialogs over the map took no clicks (Leaflet's canvas took them). The clicks are spread over
// the whole dialog (its buttons can happen to sit beside the map), then the dialog is used. The map's own overlays (the
// search results, the side toggle, its menu, the zoom buttons) stack inside .ka-map-root and must still take their
// clicks, as must the toast its menu raises.
import { expect, test, type Locator, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { kapServer } from "./fixtures/kapServer";

const SHOTS = process.env.SHOTS_DIR || join(import.meta.dirname, "..", "..", ".out", "launcher", "shots");

const HARNESS = "/src/admin/harness/index.html?kap=ws://kap.test/kap";
const LOAD = { timeout: 30_000 };
// a waypoint of the admin pages' own (kept in the browser, per server), away from the players and the zones
const WAYPOINT = { name: "Dialog test cairn", pos: { x: 7000, y: 60, z: 7000 }, at: 1764000000 };

/** Clicks spread over the box (a 5 x 5 grid, 10 px in from its edges): each must reach the box or what it holds. The
 * ones that do not, with what took them. */
function misses(box: Locator) {
  return box.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const name = (n: Element) => n.tagName.toLowerCase() + [...n.classList].slice(0, 2).map((c) => "." + c).join("");
    const out: string[] = [];
    for (let i = 0; i < 5; i++) {
      for (let j = 0; j < 5; j++) {
        const x = r.left + 10 + ((r.width - 20) * i) / 4, y = r.top + 10 + ((r.height - 20) * j) / 4;
        const hit = document.elementFromPoint(x, y);
        if (!hit || !el.contains(hit)) out.push(`${Math.round(x)},${Math.round(y)}: ${hit ? name(hit) + (hit.parentElement ? " in " + name(hit.parentElement) : "") : "nothing"}`);
      }
    }
    return out;
  });
}

/** How much of the box lies over the map (its share of the box's area), so the check over the map means something. */
async function overMap(page: Page, box: Locator) {
  const [a, m] = [(await box.boundingBox())!, (await page.locator(".ka-map-area").boundingBox())!];
  const w = Math.min(a.x + a.width, m.x + m.width) - Math.max(a.x, m.x), h = Math.min(a.y + a.height, m.y + m.height) - Math.max(a.y, m.y);
  return w > 0 && h > 0 ? (w * h) / (a.width * a.height) : 0;
}

/** On the map page: the waypoint's Delete waypoint confirmation, over the map, cancelled and then confirmed. */
async function confirmOverMap(page: Page, where: string) {
  await expect(page.getByRole("searchbox", { name: "Search the map" })).toBeVisible(LOAD);
  await expect(page.locator(".ka-map.leaflet-container")).toBeVisible();
  // the side panel off: the map takes the page's width, so a dialog in the middle lies over it
  await page.locator(".ka-map-sidetoggle").click();
  await expect(page.locator(".ka-map-side")).toBeHidden();
  const waypoint = page.locator(".ka-wp", { hasText: WAYPOINT.name });
  await expect(waypoint).toBeVisible(LOAD);
  const ask = async () => {
    await waypoint.click({ button: "right" });
    await page.locator(".ka-map-menu").getByText("Delete waypoint", { exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Delete waypoint?", exact: true });
    await expect(dialog).toBeVisible();
    await page.waitForTimeout(400); // its fade in
    return dialog;
  };
  let dialog = await ask();
  expect(await overMap(page, dialog), `${where}: the share of the dialog over the map`).toBeGreaterThan(0.25);
  expect.soft(await misses(dialog), `${where}: clicks on the dialog that went elsewhere`).toEqual([]);
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click({ timeout: 5000 });
  await expect(dialog).toHaveCount(0);
  await expect(waypoint).toBeVisible();
  dialog = await ask();
  await dialog.getByRole("button", { name: "Delete", exact: true }).click({ timeout: 5000 });
  await expect(dialog).toHaveCount(0);
  await expect(waypoint).toHaveCount(0);
}

/** On the map page: the map's own overlays, which stack inside .ka-map-root (the search results, the side toggle, its
 * context menu, the route panel, the zoom buttons), and the toast its menu raises (on the body, z-index 60; at this
 * window size it sits below the map, so Kilnwarden's tour-map-dialogs.spec.ts checks one over the map), each clicked
 * across. In mock mode the map has no art: the route panel stands below the art banner (31.20; before, the banner lay
 * over it and took its ✕), which the size checks further down look at closely. */
async function overlaysOverMap(page: Page, where: string) {
  const search = page.getByRole("searchbox", { name: "Search the map" });
  await expect(search).toBeVisible(LOAD);
  await expect(page.locator(".ka-map.leaflet-container")).toBeVisible();
  await search.fill(WAYPOINT.name);
  const results = page.locator(".ka-map-results");
  await expect(results).toBeVisible();
  expect.soft(await misses(results), `${where}: clicks on the search results that went elsewhere`).toEqual([]);
  await search.fill("");
  await page.locator(".ka-map-sidetoggle").click({ timeout: 5000 });
  await expect(page.locator(".ka-map-side")).toBeHidden();
  const waypoint = page.locator(".ka-wp", { hasText: WAYPOINT.name });
  await expect(waypoint).toBeVisible(LOAD);
  await waypoint.click({ button: "right" });
  const menu = page.locator(".ka-map-menu");
  await expect(menu).toBeVisible();
  expect.soft(await misses(menu), `${where}: clicks on the map's menu that went elsewhere`).toEqual([]);
  await menu.getByText("Measure from here", { exact: true }).click({ timeout: 5000 });
  const toast = page.locator(".k-toast", { hasText: "Start point set" });
  await expect(toast).toBeVisible();
  await page.waitForTimeout(500); // its spring in
  expect.soft(await misses(toast), `${where}: clicks on the toast that went elsewhere`).toEqual([]);
  await expect(page.locator(".ka-map-route")).toBeVisible();
  expect.soft(await misses(page.locator(".ka-map-route")), `${where}: clicks on the route panel that went elsewhere`).toEqual([]);
  expect.soft(await misses(page.locator(".ka-map-zoom")), `${where}: clicks on the zoom buttons that went elsewhere`).toEqual([]);
  await page.locator(".ka-map-sidetoggle").click({ timeout: 5000 });
  await expect(page.locator(".ka-map-side")).toBeVisible();
}

test.beforeEach(async ({ page }) => {
  await page.addInitScript((w) => localStorage.setItem("kiln.admin.history.v1.lab", JSON.stringify({ waypoints: [w] })), WAYPOINT);
});

test("in the launcher, a dialog over the admin map takes every click", async ({ page }) => {
  await page.goto("/#/servers/lab/admin/map");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await confirmOverMap(page, "launcher");
});

test("in a plain page (the pages as Kilnwarden hosts them), a dialog over the admin map takes every click", async ({ page }) => {
  await kapServer(page);
  await page.goto(`${HARNESS}#/servers/lab/admin/map`);
  await confirmOverMap(page, "harness");
});

test("in the launcher, the map's own overlays and its toast take their clicks", async ({ page }) => {
  await page.goto("/#/servers/lab/admin/map");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await overlaysOverMap(page, "launcher");
});

test("in a plain page (the pages as Kilnwarden hosts them), the map's own overlays and its toast take their clicks", async ({ page }) => {
  await kapServer(page);
  await page.goto(`${HARNESS}#/servers/lab/admin/map`);
  await overlaysOverMap(page, "harness");
});

// While the map has no art, the art banner and the route panel both want the map's top left corner (left 56, top 12):
// the banner (z-index 1001) lay over the route panel (1000) and took its ✕, so a route could not be closed. The route
// panel stands below the banner now, whatever the banner holds: the launcher's one line, or a host's words with an action
// (Kilnwarden's useMapNoArt), long enough here to be taller than the map, where the banner scrolls and gives the route
// panel its room. At the launcher window's smallest size, the Deck's and a desktop's, with the side panel open (the
// narrowest map). Kilnwarden hides this banner by its place and inline style, `.ka-map-area > div[style*="z-index:
// 1001"]` (web/src/app.css): that still finds it. A screenshot of each goes to SHOTS_DIR (SHOTS=0 skips them).
const SIZES = [
  { name: "narrow", width: 1024, height: 640 },
  { name: "deck", width: 1280, height: 800 },
  { name: "desktop", width: 1920, height: 1080 },
] as const;
const HOST_STEPS = Array.from({ length: 30 }, (_, i) => `Step ${i + 1}: copy the map and data folders from the Kiln Launcher's admin-assets into the server's folder.`).join(" ");

/** No map art wherever the test runs (a checkout with launcher/assets-local has it): the map's probe finds nothing. */
async function withoutArt(page: Page) {
  await page.route("**/map/gpu/d/0_0.webp", (r) => r.fulfill({ status: 404, body: "" }));
}

/** A host as Kilnwarden's: it makes no art, and its banner says so in many words, with an action (clicks counted). */
async function hostWords(page: Page) {
  await page.addInitScript((body) => {
    const w = window as unknown as { __kilnHarnessHost: unknown; __noArtClicks: number };
    w.__noArtClicks = 0;
    w.__kilnHarnessHost = {
      useArtStatus: () => ({ map: false, extracted: null, canExtract: false }),
      useMapNoArt: () => ({ title: "This server has no map art yet", body, action: { label: "Copy it from the Kiln Launcher", onClick: () => { w.__noArtClicks++; } } }),
    };
  }, HOST_STEPS);
}

type Rect = { left: number; top: number; right: number; bottom: number };
const rectOf = (l: Locator): Promise<Rect> => l.evaluate((e) => { const b = e.getBoundingClientRect(); return { left: b.left, top: b.top, right: b.right, bottom: b.bottom }; });
const inside = (a: Rect, b: Rect) => a.left >= b.left - 0.5 && a.top >= b.top - 0.5 && a.right <= b.right + 0.5 && a.bottom <= b.bottom + 0.5;
const apart = (a: Rect, b: Rect) => a.right <= b.left || b.right <= a.left || a.bottom <= b.top || b.bottom <= a.top;

/** A route left open on an earlier visit (the map keeps its state in the browser), so the page opens with it at every
 * size (the test's waypoint, where Measure from here would start one, lies under the banner at 1024×640). `view`: the
 * map's saved view too (else it opens on the whole world). */
async function openRoute(page: Page, view: { x: number; z: number; zoom: number } | null = null) {
  await page.addInitScript((ui) => localStorage.setItem("kiln.admin.map.v1", JSON.stringify(ui)), {
    route: { from: { kind: "point", name: WAYPOINT.name, ...WAYPOINT.pos }, to: { x: 7400, y: 95, z: 6600, name: "Dialog test spire" } },
    view,
  });
}

/** With no map art, the side panel open (the narrowest map) and a route open: the banner and the route panel, each
 * whole inside the map, apart, and clicked across; the banner's action (a host's) and the route panel's ✕ clicked for
 * real. */
async function routeBesideBanner(page: Page, where: string, action: boolean) {
  await expect(page.getByRole("searchbox", { name: "Search the map" })).toBeVisible(LOAD);
  await expect(page.locator(".ka-map.leaflet-container")).toBeVisible();
  await expect(page.locator(".ka-map-side")).toBeVisible();
  const banner = page.locator(".ka-map-area > .ka-map-noart");
  await expect(banner.locator(".k-banner")).toBeVisible(LOAD);
  // Kilnwarden's rule (web/src/app.css) still finds the banner, and only the banner
  await expect(page.locator('.ka-map-area > div[style*="z-index: 1001"]')).toHaveCount(1);
  await expect(page.locator('.ka-map-area > div[style*="z-index: 1001"] > .k-banner')).toHaveCount(1);
  const route = page.locator(".ka-map-route");
  await expect(route).toContainText("Dialog test spire");
  // the web fonts in first (font-display: swap; the screenshot waits for them, SHOTS=0 does not): a late one rewraps the
  // banner, which moves the route panel below it where the banner does not fill the column (1920×1080)
  await page.evaluate(() => document.fonts.ready);
  if (process.env.SHOTS !== "0") {
    mkdirSync(SHOTS, { recursive: true });
    await page.screenshot({ path: join(SHOTS, `map-route-banner-${where}.png`) });
  }
  const [area, b, r] = [await rectOf(page.locator(".ka-map-area")), await rectOf(banner), await rectOf(route)];
  expect.soft(inside(b, area), `${where}: the banner lies inside the map (${JSON.stringify(b)} in ${JSON.stringify(area)})`).toBe(true);
  expect.soft(inside(r, area), `${where}: the route panel lies inside the map (${JSON.stringify(r)} in ${JSON.stringify(area)})`).toBe(true);
  expect.soft(apart(b, r), `${where}: the banner and the route panel do not overlap (${JSON.stringify(b)}, ${JSON.stringify(r)})`).toBe(true);
  expect.soft(await misses(banner), `${where}: clicks on the banner that went elsewhere`).toEqual([]);
  expect.soft(await misses(route), `${where}: clicks on the route panel that went elsewhere`).toEqual([]);
  if (action) {
    const copy = banner.getByRole("button", { name: "Copy it from the Kiln Launcher" });
    await copy.scrollIntoViewIfNeeded();
    await copy.click({ timeout: 5000 });
    await expect.poll(() => page.evaluate(() => (window as unknown as { __noArtClicks: number }).__noArtClicks)).toBe(1);
    // the banner scrolled on its own: the map did not move (its root, overflow hidden, is the box scrollIntoView could
    // scroll; the area, overflow visible, never scrolls), and the route panel stayed where it was, still clickable
    expect(await page.locator(".ka-map-root").evaluate((e) => [e.scrollTop, e.scrollLeft])).toEqual([0, 0]);
    expect(await rectOf(route)).toEqual(r);
  }
  await route.locator(".ka-map-x").click({ timeout: 5000 });
  await expect(route).toHaveCount(0);
  await expect(banner.locator(".k-banner")).toBeVisible();
}

for (const size of SIZES) {
  test(`in the launcher without map art, the banner and the route panel's ✕ both take their clicks: ${size.name} ${size.width}×${size.height}`, async ({ page }) => {
    await withoutArt(page);
    await openRoute(page);
    await page.setViewportSize({ width: size.width, height: size.height });
    await page.goto("/#/servers/lab/admin/map");
    await page.locator(".k-splash").waitFor({ state: "detached" });
    if (await page.evaluate(() => location.hash !== "#/servers/lab/admin/map")) await page.evaluate(() => { location.hash = "#/servers/lab/admin/map"; });
    await routeBesideBanner(page, `launcher-${size.width}x${size.height}`, false);
  });

  test(`in a plain page, a host's long banner, its action and the route panel's ✕ all take their clicks: ${size.name} ${size.width}×${size.height}`, async ({ page }) => {
    await withoutArt(page);
    await openRoute(page);
    await hostWords(page);
    await kapServer(page);
    await page.setViewportSize({ width: size.width, height: size.height });
    await page.goto(`${HARNESS}#/servers/lab/admin/map`);
    await routeBesideBanner(page, `harness-${size.width}x${size.height}`, true);
  });
}

test("with no banner over the map (a host that drops it, as Kilnwarden does), the route panel keeps its place at the top left", async ({ page }) => {
  await withoutArt(page);
  await openRoute(page);
  await page.addInitScript(() => {
    (window as unknown as { __kilnHarnessHost: unknown }).__kilnHarnessHost = {
      useArtStatus: () => ({ map: false, extracted: null, canExtract: false }),
      useMapNoArt: () => ({ title: null, markers: "No markers without the map art: see the notice above the map." }),
    };
  });
  await kapServer(page);
  await page.setViewportSize({ width: 1024, height: 640 });
  await page.goto(`${HARNESS}#/servers/lab/admin/map`);
  const route = page.locator(".ka-map-route");
  await expect(route).toContainText("Dialog test spire", LOAD);
  await expect(page.locator(".ka-map-area > .ka-map-noart")).toHaveCount(0);
  // where it always stood: 12 px down, 56 px in (right of the side toggle)
  const [area, r] = [await rectOf(page.locator(".ka-map-area")), await rectOf(route)];
  expect(Math.round(r.top - area.top)).toBe(12);
  expect(Math.round(r.left - area.left)).toBe(56);
  expect(await misses(route)).toEqual([]);
  await route.locator(".ka-map-x").click({ timeout: 5000 });
  await expect(route).toHaveCount(0);
});

// The pointer's coordinates over the map (.ka-map-coords, bottom left) show while the pointer is on the map. A host's
// banner taller than the map fills the top column, which put the route panel at the column's foot, on the coordinates'
// corner: they covered the start of the route ("…st cairn"). The column ends above that corner, so neither the banner
// nor the route panel lies under them. The map opens zoomed in on the test's waypoint, so the gutter beside the column
// is the world, where the pointer shows its coordinates.
test("with a host's long banner and a route open, the pointer's coordinates keep their corner of the map", async ({ page }) => {
  await withoutArt(page);
  await openRoute(page, { x: WAYPOINT.pos.x, z: WAYPOINT.pos.z, zoom: 5 });
  await hostWords(page);
  await kapServer(page);
  await page.setViewportSize({ width: 1024, height: 640 });
  await page.goto(`${HARNESS}#/servers/lab/admin/map`);
  const banner = page.locator(".ka-map-area > .ka-map-noart");
  const route = page.locator(".ka-map-route");
  await expect(banner.locator(".k-banner")).toBeVisible(LOAD);
  await expect(route).toContainText("Dialog test spire");
  await page.evaluate(() => document.fonts.ready);
  // the banner takes all the room the column gives it (it scrolls), so the route panel stands at the column's foot
  expect(await banner.evaluate((e) => e.scrollHeight > e.clientHeight)).toBe(true);
  // the pointer on the map in the gutter left of the column, near the map's foot
  const area = await rectOf(page.locator(".ka-map-area"));
  await page.mouse.move(area.left + 28, area.bottom - 30);
  await page.mouse.move(area.left + 28, area.bottom - 6, { steps: 4 });
  const coords = page.locator(".ka-map-coords");
  await expect(coords).toBeVisible();
  const [c, b, r] = [await rectOf(coords), await rectOf(banner), await rectOf(route)];
  expect.soft(apart(c, r), `the coordinates and the route panel do not overlap (${JSON.stringify(c)}, ${JSON.stringify(r)})`).toBe(true);
  expect.soft(apart(c, b), `the coordinates and the banner do not overlap (${JSON.stringify(c)}, ${JSON.stringify(b)})`).toBe(true);
  expect(await misses(route)).toEqual([]);
});
