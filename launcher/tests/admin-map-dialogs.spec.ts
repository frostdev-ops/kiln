// A dialog opened over the admin map takes every click (mock mode). The map's Leaflet layers carry z-indexes of 400 to
// 1200 (its panes, its canvas, its controls); map.css makes .ka-map-root a stacking context of its own, so they stay
// below what the design system puts on the body (dialogs at z-index 50, toasts at 60). In the launcher the sheet
// (.k-sheet, z-index 3) already held them; the harness hosts the pages as Kilnwarden does, with nothing around them,
// where Kilnwarden 0.1.3's dialogs over the map took no clicks (Leaflet's canvas took them). The clicks are spread over
// the whole dialog (its buttons can happen to sit beside the map), then the dialog is used. The map's own overlays (the
// search results, the side toggle, its menu, the zoom buttons) stack inside .ka-map-root and must still take their
// clicks, as must the toast its menu raises.
import { expect, test, type Locator, type Page } from "@playwright/test";
import { kapServer } from "./fixtures/kapServer";

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
 * context menu, the zoom buttons), and the toast its menu raises (on the body, z-index 60; at this window size it sits
 * below the map, so Kilnwarden's tour-map-dialogs.spec.ts checks one over the map), each clicked across. Not the route
 * panel: in mock mode the map has no art, and the art banner (z-index 1001, same place) lies over it, as it did before
 * the map's isolation (Kilnwarden hides that banner and checks the route panel). */
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
