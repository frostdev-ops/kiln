// "Always open the web panel on this computer" (0.2.0-dev.31.20), per server, beside its Open web panel button: while it
// is on, Open web panel opens the panel on this computer and never checks or uses the panel's own address on the internet
// (Kilnwarden's launcher-endpoint.json `public`); off, the hosted address when it works, as before. The setting is kept
// in the launcher's data folder (web-panel-settings.json), so it is still on after a restart.
// Browser mock: src/pages/servers/mockServers.ts (`__kilnMock.webPanel`; the mock keeps the setting in local storage).
//   bun x playwright test tests/web-panel-local-setting.spec.ts
import { expect, test, type Page } from "@playwright/test";

const HOSTED = "https://alpha.kiln.nexus/";
const ALWAYS = "Always open the web panel on this computer";

async function open(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
  if (await page.evaluate((r) => location.hash !== `#${r}`, route)) await page.evaluate((r) => { location.hash = `#${r}`; }, route);
}
/** Runs `body` with `h` = the mock's test hooks (`__kilnMock`). */
const withHooks = (page: Page, body: string) => page.evaluate(`(() => { const h = window.__kilnMock; ${body} })()`);
type Mock = { __kilnMock: { webPanel: { opens: () => { serverId: string; route: string; via: string }[]; checks: () => string[] };
  planCommand: (cmd: string, plan: unknown) => void; openGate: (name: string) => void } };
const opens = (page: Page) => page.evaluate(() => (window as unknown as Mock).__kilnMock.webPanel.opens());
/** The servers whose public address the launcher checked, one entry per check. */
const checks = (page: Page) => page.evaluate(() => (window as unknown as Mock).__kilnMock.webPanel.checks());

/** The lab server's Overview with Kilnwarden's old name loaded and the server running; its file names a working hosted
 *  address. */
async function overview(page: Page) {
  await open(page, "/");
  await withHooks(page, `h.webPanel.install(true); h.webPanel.setPublic(${JSON.stringify(HOSTED)}, "ok"); h.setServer({ running: true });`);
  await page.evaluate(() => { location.hash = "#/servers/lab/overview"; });
  await expect(page.getByRole("button", { name: "Open web panel" })).toBeVisible();
}

const LOCAL = /^Web panel open at https:\/\/127\.\d+\.\d+\.\d+:\d+$/;
/** The switch's state, and a click on it (on its label, as people do: the checkbox itself is drawn as the track). */
const alwaysSwitch = (page: Page) => page.getByRole("switch", { name: ALWAYS });
async function flip(page: Page, on: boolean) {
  await expect(alwaysSwitch(page)).toBeEnabled();
  await page.locator(".k-sov-webpanel label.k-toggle").click();
  await (on ? expect(alwaysSwitch(page)).toBeChecked() : expect(alwaysSwitch(page)).not.toBeChecked());
}

test("the setting sits with Open web panel, off by default, and says in one line what each route means", async ({ page }) => {
  await overview(page);
  const always = alwaysSwitch(page);
  await expect(always).toBeVisible();
  await expect(always).not.toBeChecked();
  const hint = page.locator(".k-sov-webpanel .k-toggle .k-hint");
  await expect(hint).toHaveText("Hosted addresses go through frostdev.io's relay (encrypted end to end); this computer's address only works from this computer.");
});

test("on: Open web panel opens on this computer and never checks the hosted address", async ({ page }) => {
  await overview(page);
  await flip(page, true);
  await page.getByRole("button", { name: "Open web panel" }).click();
  const status = page.getByRole("list", { name: "Open web panel" });
  await expect(status.getByRole("listitem").first()).toHaveText(LOCAL);
  await expect(status).not.toContainText("own address");
  await expect(page.getByRole("button", { name: "Open on this computer instead" })).toHaveCount(0);
  expect(await opens(page)).toEqual([{ serverId: "lab", route: "auto", via: "local" }]);
  expect(await checks(page)).toEqual([]);
  // again: still here, still no check
  await page.getByRole("button", { name: "Open web panel" }).click();
  await expect.poll(async () => (await opens(page)).map((o) => o.via)).toEqual(["local", "local"]);
  expect(await checks(page)).toEqual([]);
});

test("off: the hosted address, after its check", async ({ page }) => {
  await overview(page);
  await page.getByRole("button", { name: "Open web panel" }).click();
  const status = page.getByRole("list", { name: "Open web panel" });
  await expect(status.getByRole("listitem").first()).toHaveText("Web panel open at https://alpha.kiln.nexus, your panel's own address");
  expect(await opens(page)).toEqual([{ serverId: "lab", route: "auto", via: "hosted" }]);
  expect(await checks(page)).toEqual(["lab"]);
  // switched on and off again: the hosted address again
  await flip(page, true);
  await flip(page, false);
  await page.getByRole("button", { name: "Open web panel" }).click();
  await expect.poll(() => opens(page)).toHaveLength(2);
  expect((await opens(page)).at(-1)).toEqual({ serverId: "lab", route: "auto", via: "hosted" });
  expect(await checks(page)).toEqual(["lab", "lab"]);
});

test("after a hosted open, always on this computer is one click away; then the choice button goes", async ({ page }) => {
  await overview(page);
  await page.getByRole("button", { name: "Open web panel" }).click();
  const here = page.getByRole("button", { name: "Open on this computer instead" });
  await expect(here).toBeVisible();
  await flip(page, true);
  await expect(here).toHaveCount(0);
  await page.getByRole("button", { name: "Open web panel" }).click();
  const status = page.getByRole("list", { name: "Open web panel" });
  await expect(status.getByRole("listitem").first()).toHaveText(LOCAL);
  expect((await opens(page)).at(-1)).toEqual({ serverId: "lab", route: "auto", via: "local" });
  expect(await checks(page)).toEqual(["lab"]);
});

test("the setting is kept: still on after the launcher starts again", async ({ page }) => {
  await overview(page);
  await flip(page, true);
  // a new start: the page loads again, at Home (the mock's server state is set up again; the setting comes from storage)
  await page.evaluate(() => { location.hash = "#/"; });
  await page.reload();
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await withHooks(page, `h.webPanel.install(true); h.webPanel.setPublic(${JSON.stringify(HOSTED)}, "ok"); h.setServer({ running: true });`);
  await page.evaluate(() => { location.hash = "#/servers/lab/overview"; });
  await expect(alwaysSwitch(page)).toBeChecked();
  await page.getByRole("button", { name: "Open web panel" }).click();
  await expect(page.getByRole("list", { name: "Open web panel" }).getByRole("listitem").first()).toHaveText(LOCAL);
  expect(await opens(page)).toEqual([{ serverId: "lab", route: "auto", via: "local" }]);
  expect(await checks(page)).toEqual([]);
});

// (review) The switch is saved by the launcher's backend, which reads it again at every open: while the save is on its
// way, Open web panel waits for it, so a click right after the switch never opens at the hosted address after all.
test("while the switch is being saved, Open web panel waits for it", async ({ page }) => {
  await overview(page);
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.planCommand("admin_web_panel_settings_set", { gate: "save" }));
  const openButton = page.getByRole("button", { name: "Open web panel" });
  await expect(alwaysSwitch(page)).toBeEnabled();
  await page.locator(".k-sov-webpanel label.k-toggle").click();
  await expect(openButton).toBeDisabled();
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.openGate("save"));
  await expect(alwaysSwitch(page)).toBeChecked();
  await expect(openButton).toBeEnabled();
  await openButton.click();
  await expect(page.getByRole("list", { name: "Open web panel" }).getByRole("listitem").first()).toHaveText(LOCAL);
  expect(await opens(page)).toEqual([{ serverId: "lab", route: "auto", via: "local" }]);
  expect(await checks(page)).toEqual([]);
});
