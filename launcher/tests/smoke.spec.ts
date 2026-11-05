// Smoke tests in browser-mock mode: every route renders without console errors, the key
// flows work (import → enable → resolver preview; Play button state; typed confirmation), and screenshots of the
// main pages go to .out/launcher/shots/ (SHOTS=0 skips them).
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = join(import.meta.dirname, "..", "..", ".out", "launcher", "shots");

/** The launcher's own routes: must render without console errors. */
const CLIENT_ROUTES = [
  "/", "/profiles", "/profiles/ember-run/mods", "/profiles/ember-run/native", "/profiles/ember-run/emf", "/profiles/ember-run/configs",
  "/profiles/ember-run/saves", "/library", "/runtime", "/logs", "/saves", "/editor", "/settings", "/about", "/servers",
];
/** Server and admin routes: rendered inside the shell, checked softly. */
const SERVER_ROUTES = [
  "/servers/lab/overview", "/servers/lab/settings", "/servers/lab/mods", "/servers/lab/configs", "/servers/lab/backups",
  "/servers/lab/logs", "/servers/lab/modpack",
  ...["players", "map", "zones", "items", "spawn", "world", "combat", "permissions", "content", "configs", "mods", "console", "saves", "crashes"]
    .map((p) => `/servers/lab/admin/${p}`),
];

/** Console errors and page errors while `fn` runs (missing optional editor assets are not errors). */
async function collectErrors(page: Page, fn: () => Promise<void>): Promise<string[]> {
  const errors: string[] = [];
  const onConsole = (m: { type(): string; text(): string; location(): { url: string } }) => {
    if (m.type() !== "error") return;
    const url = m.location().url ?? "";
    if (url.includes("/assets-local/") || /Failed to load resource.*assets-local/.test(m.text())) return;
    errors.push(m.text());
  };
  const onError = (e: Error) => errors.push(String(e));
  page.on("console", onConsole);
  page.on("pageerror", onError);
  try { await fn(); } finally { page.off("console", onConsole); page.off("pageerror", onError); }
  return errors;
}

async function open(page: Page, route: string) {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

async function routeErrors(page: Page, routes: string[]): Promise<Record<string, string[]>> {
  const out: Record<string, string[]> = {};
  await open(page, "/");
  for (const r of routes) {
    const errs = await collectErrors(page, async () => {
      await page.evaluate((h) => { location.hash = h; }, `#${r}`);
      await expect(page.locator(".k-main")).not.toBeEmpty();
      await page.waitForTimeout(300);
    });
    if (await page.getByText("This page failed").count()) errs.push("error boundary: This page failed");
    if (errs.length) out[r] = errs;
  }
  return out;
}

test("every client route renders without console errors", async ({ page }) => {
  expect(await routeErrors(page, CLIENT_ROUTES)).toEqual({});
});

test("server and admin routes render inside the shell", async ({ page }) => {
  const errs = await routeErrors(page, SERVER_ROUTES);
  // the shell survives every one of them (a broken page stays inside the route error boundary)
  await expect(page.locator(".k-sidebar")).toBeVisible();
  for (const r of SERVER_ROUTES) expect.soft(errs[r] ?? [], r).toEqual([]);
});

test("import → add to profile → enable → resolver preview", async ({ page }) => {
  const errors = await collectErrors(page, async () => {
    await open(page, "/library");
    // drop a mod zip (mock mode: the file name stands in for the path)
    const dt = await page.evaluateHandle(() => { const d = new DataTransfer(); d.items.add(new File(["x"], "night-owl-1.2.0.zip")); return d; });
    await page.locator(".k-drop").dispatchEvent("drop", { dataTransfer: dt });
    const dialog = page.getByRole("dialog", { name: "Import" });
    await expect(dialog.getByText("night-owl")).toBeVisible();
    await dialog.getByRole("button", { name: "Import 1" }).click();
    await expect(page.getByText("Imported 1")).toBeVisible();
    await expect(page.getByRole("table", { name: "Library" }).getByText("Night Owl")).toBeVisible();

    await open(page, "/profiles/ember-run/mods");
    await page.getByRole("button", { name: "Add", exact: true }).first().click();
    const add = page.getByRole("dialog", { name: "Add from library" });
    await add.getByText("Night Owl").click();
    await add.getByRole("button", { name: "Add 1" }).click();
    const order = page.locator(".k-order");
    await expect(order.getByText("night-owl")).toBeVisible();
    // switch it off: it leaves the load order; on again: back
    await page.getByLabel("Enable night-owl").uncheck({ force: true });
    await expect(order.getByText("night-owl")).toHaveCount(0);
    await page.getByLabel("Enable night-owl").check({ force: true });
    await expect(order.getByText("night-owl")).toBeVisible();
    // rejections carry the resolver's reasons
    await expect(page.getByText("missing dependency ui-lib ^1.0")).toBeVisible();
    // server side: client-only mods are skipped with a reason
    await page.getByRole("radio", { name: "Server" }).click();
    await expect(page.getByText("side 'client': not loaded on a server").first()).toBeVisible();
  });
  expect(errors).toEqual([]);
});

test("Play button follows the game state", async ({ page }) => {
  await open(page, "/");
  const play = page.locator(".k-hero").getByRole("button", { name: "Play", exact: true });
  await expect(play).toBeEnabled();
  await play.click();
  await expect(page.getByText("Starting")).toBeVisible();
  await expect(page.locator(".k-topbar .k-pill")).toHaveText(/Starting|Running/);
  await expect(page.locator(".k-hero").getByRole("button", { name: /Running|Starting/ })).toBeDisabled();
  await expect(page.locator(".k-topbar .k-pill")).toHaveText("Running", { timeout: 8000 });
  await page.evaluate(() => (window as unknown as { __kilnMock: { stopGame: () => void } }).__kilnMock.stopGame());
  await expect(page.locator(".k-topbar .k-pill")).toHaveText("Stopped");
  await expect(play).toBeEnabled();
  // the other modes sit in the menu
  await page.getByLabel("More ways to play").click();
  await expect(page.getByRole("menuitem", { name: "Play vanilla once" })).toBeVisible();
  await page.getByRole("menuitem", { name: "Play vanilla once" }).click();
  await expect(page.getByRole("dialog").getByText("A character carrying items from a Kiln mod can crash")).toBeVisible();
  await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByRole("button", { name: "More ways to play" }).click();
  // every item is really on screen: a parent with overflow: hidden once cut them off
  for (const name of ["Play vanilla once", "Play without mods once", "Play a profile once"]) {
    const box = await page.getByRole("menuitem", { name }).boundingBox();
    const hit = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.textContent ?? "", [box!.x + box!.width / 2, box!.y + box!.height / 2]);
    expect(hit).toContain(name);
  }
  await page.getByRole("menuitem", { name: "Play a profile once…" }).click();
  await expect(page.getByRole("dialog", { name: "Play a profile once" })).toBeVisible();
});

test("destructive actions need the typed word", async ({ page }) => {
  await open(page, "/profiles");
  await page.getByRole("button", { name: "New profile" }).first().click();
  await page.getByLabel("Name").fill("Throwaway");
  await page.getByRole("button", { name: "Create" }).click();
  await expect(page.getByRole("heading", { name: "Throwaway" })).toBeVisible();
  await open(page, "/profiles");
  const row = page.getByRole("listitem").filter({ hasText: "Throwaway" });
  await row.getByLabel("Profile actions").click();
  await page.getByRole("menuitem", { name: "Delete" }).click();
  const dialog = page.getByRole("dialog");
  const confirm = dialog.getByRole("button", { name: "Delete" });
  await expect(confirm).toBeDisabled();
  await dialog.getByRole("textbox").fill("throw");
  await expect(confirm).toBeDisabled();
  await dialog.getByRole("textbox").fill("throwaway");
  await confirm.click();
  await expect(page.getByRole("listitem").filter({ hasText: "Throwaway" })).toHaveCount(0);
});

test("admin tabs navigate (absolute links inside the admin/* splat route)", async ({ page }) => {
  await open(page, "/servers/lab/admin/players");
  const tabs = page.getByRole("navigation", { name: "Admin pages" });
  await expect(tabs.getByRole("link", { name: /Players/ })).toHaveClass(/active/);
  await tabs.getByRole("link", { name: "Map", exact: true }).click();
  await expect(page).toHaveURL(/#\/servers\/lab\/admin\/map$/);
  await expect(tabs.getByRole("link", { name: "Map", exact: true })).toHaveClass(/active/);
  // the server tabs above keep working from an admin page
  await page.getByRole("link", { name: "Backups", exact: true }).click();
  await expect(page).toHaveURL(/#\/servers\/lab\/backups$/);
});

test("schema form edits a config value", async ({ page }) => {
  await open(page, "/profiles/ember-run/configs");
  await expect(page.getByText("Rarity boost")).toBeVisible();
  await expect(page.getByText("restart").first()).toBeVisible();
  await expect(page.getByText("synced").first()).toBeVisible();
  const tier = page.locator("#sf-loot-tier");
  await tier.selectOption("high");
  await expect(page.locator(".k-sform-row.changed", { hasText: "Tier" })).toBeVisible();
  await page.getByRole("radio", { name: "File" }).click();
  await expect(page.getByRole("textbox", { name: "File" })).toHaveValue(/"tier": "high"/);
});

test("screenshots of the main pages", async ({ page }) => {
  test.skip(process.env.SHOTS === "0", "SHOTS=0");
  mkdirSync(SHOTS, { recursive: true });
  const shots: [string, string][] = [
    ["play", "/"], ["profiles", "/profiles"], ["profile-mods", "/profiles/ember-run/mods"], ["profile-native", "/profiles/ember-run/native"],
    ["profile-configs", "/profiles/ember-run/configs"], ["library", "/library"], ["runtime", "/runtime"], ["logs", "/logs"],
    ["saves", "/saves"], ["settings", "/settings"], ["about", "/about"], ["editor", "/editor"],
  ];
  for (const [name, route] of shots) {
    await open(page, route);
    await page.waitForTimeout(name === "editor" ? 2500 : 700);
    await page.screenshot({ path: join(SHOTS, `mock-${name}.png`) });
  }
  // a modal and the tick stats
  await open(page, "/logs");
  await page.getByRole("tab", { name: "Tick stats" }).click();
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(SHOTS, "mock-logs-ticks.png") });
  await open(page, "/runtime");
  await page.getByRole("button", { name: "Uninstall" }).click();
  await page.screenshot({ path: join(SHOTS, "mock-confirm-danger.png") });
});
