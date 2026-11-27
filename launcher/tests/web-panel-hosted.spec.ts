// "Open web panel" at the panel's own address (0.2.0-dev.31.19): when Kilnwarden's launcher-endpoint.json names its
// hosted address (`public`, Kilnwarden 0.1.4) and the launcher's check of it passes, the panel opens there, and "Open on
// this computer instead" keeps the gateway one click away; when the check fails, it opens on this computer and says why;
// without the address nothing changes. Browser mock: src/pages/servers/mockServers.ts (`__kilnMock.webPanel`).
//   bun x playwright test tests/web-panel-hosted.spec.ts
import { expect, test, type Page } from "@playwright/test";

const HOSTED = "https://alpha.kiln.nexus/";

async function open(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
  if (await page.evaluate((r) => location.hash !== `#${r}`, route)) await page.evaluate((r) => { location.hash = `#${r}`; }, route);
}
/** Runs `body` with `h` = the mock's test hooks (`__kilnMock`). */
const withHooks = (page: Page, body: string) => page.evaluate(`(() => { const h = window.__kilnMock; ${body} })()`);
type Opens = { serverId: string; route: string; via: string }[];
const opens = (page: Page) => page.evaluate(() => (window as unknown as { __kilnMock: { webPanel: { opens: () => Opens } } }).__kilnMock.webPanel.opens());

/** The lab server's Overview with Kilnwarden's old name loaded and the server running; its file names `publicUrl`. */
async function overview(page: Page, publicUrl: string | null, check = "ok") {
  await open(page, "/");
  await withHooks(page, `h.webPanel.install(true); h.webPanel.setPublic(${JSON.stringify(publicUrl)}, ${JSON.stringify(check)}); h.setServer({ running: true });`);
  await page.evaluate(() => { location.hash = "#/servers/lab/overview"; });
  await expect(page.getByRole("button", { name: "Open web panel" })).toBeVisible();
}

test("with a working hosted address it opens there, and Open on this computer instead opens the gateway", async ({ page }) => {
  await overview(page, HOSTED);
  await expect(page.getByRole("button", { name: "Open on this computer instead" })).toHaveCount(0);
  await page.getByRole("button", { name: "Open web panel" }).click();
  const status = page.getByRole("list", { name: "Open web panel" });
  await expect(status.getByRole("listitem").first()).toHaveText("Web panel open at https://alpha.kiln.nexus, your panel's own address");
  await expect(status).toContainText("signed in with a one-time login");
  await expect(status).not.toContainText("Certificate");
  // no gateway was opened, so there is nothing to close
  await expect(page.getByRole("button", { name: "Close web panel" })).toHaveCount(0);
  expect(await opens(page)).toEqual([{ serverId: "lab", route: "auto", via: "hosted" }]);

  const here = page.getByRole("button", { name: "Open on this computer instead" });
  await expect(here).toBeVisible();
  await here.click();
  await expect(status.getByRole("listitem").first()).toHaveText(/^Web panel open at https:\/\/127\.\d+\.\d+\.\d+:\d+$/);
  await expect(status).not.toContainText("own address");
  await expect(page.getByRole("button", { name: "Close web panel" })).toBeVisible();
  await expect(here).toHaveCount(0);
  expect((await opens(page)).at(-1)).toEqual({ serverId: "lab", route: "local", via: "local" });

  // Open web panel again: the hosted address again
  await page.getByRole("button", { name: "Open web panel" }).click();
  await expect(status.getByRole("listitem").first()).toHaveText("Web panel open at https://alpha.kiln.nexus, your panel's own address");
  expect((await opens(page)).at(-1)).toEqual({ serverId: "lab", route: "auto", via: "hosted" });
});

test("a hosted address that doesn't answer falls back to this computer and says so", async ({ page }) => {
  await overview(page, HOSTED, "unreachable");
  await page.getByRole("button", { name: "Open web panel" }).click();
  const status = page.getByRole("list", { name: "Open web panel" });
  await expect(status.getByRole("listitem").first()).toHaveText(/^Web panel open at https:\/\/127\.\d+\.\d+\.\d+:\d+$/);
  await expect(status).toContainText("Your panel's own address didn't answer in time, so it opened on this computer.");
  await expect(page.getByRole("button", { name: "Open on this computer instead" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Close web panel" })).toBeVisible();
  expect(await opens(page)).toEqual([{ serverId: "lab", route: "auto", via: "local" }]);
});

test("a hosted address that refuses (another certificate, no owner yet) falls back to this computer and says so", async ({ page }) => {
  await overview(page, HOSTED, "refused");
  await page.getByRole("button", { name: "Open web panel" }).click();
  const status = page.getByRole("list", { name: "Open web panel" });
  await expect(status).toContainText("Your panel's own address isn't ready for the launcher's sign-in, so it opened on this computer.");
  await expect(page.getByRole("button", { name: "Open on this computer instead" })).toHaveCount(0);
});

test("without a hosted address nothing changes: this computer, no choice, no note", async ({ page }) => {
  await overview(page, null);
  await page.getByRole("button", { name: "Open web panel" }).click();
  const status = page.getByRole("list", { name: "Open web panel" });
  await expect(status.getByRole("listitem").first()).toHaveText(/^Web panel open at https:\/\/127\.\d+\.\d+\.\d+:\d+$/);
  await expect(status).not.toContainText("own address");
  await expect(page.getByRole("button", { name: "Open on this computer instead" })).toHaveCount(0);
  expect(await opens(page)).toEqual([{ serverId: "lab", route: "auto", via: "local" }]);
});

test("after Close, the fallback note goes with the gateway", async ({ page }) => {
  await overview(page, HOSTED, "unreachable");
  await page.getByRole("button", { name: "Open web panel" }).click();
  const status = page.getByRole("list", { name: "Open web panel" });
  await expect(status).toContainText("didn't answer in time");
  await page.getByRole("button", { name: "Close web panel" }).click();
  await expect(status).toHaveCount(0);
});
