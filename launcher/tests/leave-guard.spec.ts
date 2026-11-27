// Unsaved work on a server's page asks before the page is left (netjunk, #bug-reports 2026-11-26): every way out
// (the header, the server's tabs, Back) stops at one dialog: Stay keeps the work, Discard and Save go on. With nothing
// unsaved nothing asks (tests/server-nav.spec.ts walks every header entry from every server tab).
import { expect, test, type Page } from "@playwright/test";

const LOAD = { timeout: 30_000 };
type Mock = { __kilnMock: { calls: () => string[]; setServer: (o: unknown) => void; planCommand: (cmd: string, plan: unknown) => void } };

async function open(page: Page, route: string) {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
  if (await page.evaluate((r) => location.hash !== `#${r}`, route)) await page.evaluate((r) => { location.hash = `#${r}`; }, route);
}
const calls = (page: Page) => page.evaluate(() => (window as unknown as Mock).__kilnMock.calls());
const dialog = (page: Page) => page.getByRole("dialog", { name: "Leave without saving?" });
const nameInput = (page: Page) => page.locator(".k-field", { hasText: "Server name" }).locator("input");
const header = (page: Page) => page.getByRole("navigation", { name: "Main" });
const tabs = (page: Page) => page.getByRole("navigation", { name: "Server", exact: true });

test("server settings: nothing unsaved leaves at once; unsaved asks, and Stay keeps the work", async ({ page }) => {
  await open(page, "/servers/lab/settings");
  await expect(nameInput(page)).toBeVisible(LOAD);
  await header(page).getByRole("link", { name: "Logs", exact: true }).click();
  await expect(page).toHaveURL(/#\/logs$/);
  await expect(dialog(page)).toHaveCount(0);

  await open(page, "/servers/lab/settings");
  await expect(nameInput(page)).toBeVisible(LOAD);
  const before = await nameInput(page).inputValue();
  await nameInput(page).fill("Renamed lab");
  // the header's Play (the report's way out)
  await header(page).getByRole("link", { name: "Play", exact: true }).click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialog(page)).toContainText("Not saved yet: the server's settings.");
  await expect(page).toHaveURL(/#\/servers\/lab\/settings$/);
  await dialog(page).getByRole("button", { name: "Stay" }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(page).toHaveURL(/#\/servers\/lab\/settings$/);
  await expect(nameInput(page)).toHaveValue("Renamed lab");
  // Esc is Stay too; then the server's own tabs ask as well
  await tabs(page).getByRole("link", { name: /^Overview/ }).click();
  await expect(dialog(page)).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(dialog(page)).toHaveCount(0);
  await expect(page).toHaveURL(/#\/servers\/lab\/settings$/);
  // Discard: the page goes, and the edits with it
  await tabs(page).getByRole("link", { name: /^Overview/ }).click();
  await dialog(page).getByRole("button", { name: "Discard changes" }).click();
  await expect(page).toHaveURL(/#\/servers\/lab\/overview$/);
  await expect(dialog(page)).toHaveCount(0);
  expect(await calls(page)).not.toContain("server_settings_set");
  await tabs(page).getByRole("link", { name: /^Settings/ }).click();
  await expect(nameInput(page)).toHaveValue(before, LOAD);
});

test("server settings: Save and leave writes the file, then goes where the click went", async ({ page }) => {
  await open(page, "/servers/lab/settings");
  await expect(nameInput(page)).toBeVisible(LOAD);
  await nameInput(page).fill("Renamed lab");
  await page.locator(".k-head-link", { hasText: "About" }).click();
  await expect(dialog(page)).toBeVisible();
  await dialog(page).getByRole("button", { name: "Save and leave" }).click();
  await expect(page).toHaveURL(/#\/about$/);
  await expect(dialog(page)).toHaveCount(0);
  expect(await calls(page)).toContain("server_settings_set");
  // past the sheet's drop: nothing sends it back, and nothing asks again
  await page.waitForTimeout(900);
  await expect(page).toHaveURL(/#\/about$/);
});

test("server settings: Back asks too", async ({ page }) => {
  await open(page, "/servers/lab/overview");
  await expect(tabs(page)).toBeVisible(LOAD);
  await tabs(page).getByRole("link", { name: /^Settings/ }).click();
  await expect(nameInput(page)).toBeVisible(LOAD);
  await nameInput(page).fill("Renamed lab");
  await page.goBack();
  await expect(dialog(page)).toBeVisible();
  await expect(page).toHaveURL(/#\/servers\/lab\/settings$/);
  await dialog(page).getByRole("button", { name: "Discard changes" }).click();
  await expect(page).toHaveURL(/#\/servers\/lab\/overview$/);
});

test("a stopped server's config file changed by hand asks before Play, and Save and leave writes it", async ({ page }) => {
  await open(page, "/servers/lab/overview");
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.setServer({ running: false }));
  await tabs(page).getByRole("link", { name: /^Mods/ }).click();
  await page.getByRole("navigation", { name: "Mods" }).getByRole("link", { name: "Configs" }).click();
  await expect(page).toHaveURL(/#\/servers\/lab\/configs$/);
  await page.getByRole("radiogroup", { name: "View" }).getByRole("radio", { name: "File" }).click(LOAD);
  const text = page.getByRole("textbox", { name: "File" });
  await text.fill('{ "enabled": false }');
  await header(page).getByRole("link", { name: "Play", exact: true }).click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialog(page)).toContainText("Not saved yet: new-items.json.");
  await dialog(page).getByRole("button", { name: "Save and leave" }).click();
  await expect(page).toHaveURL(/#\/$/);
  expect(await calls(page)).toContain("config_write");
  await page.waitForTimeout(900);
  await expect(page).toHaveURL(/#\/$/);
});

test("server settings: Save and leave that fails stays, with the work and the reason", async ({ page }) => {
  await open(page, "/servers/lab/settings");
  await expect(nameInput(page)).toBeVisible(LOAD);
  await nameInput(page).fill("Renamed lab");
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.planCommand("server_settings_set",
    { error: { code: "conflict", message: "The server is running and rewrites this file: stop it first." } }));
  await header(page).getByRole("link", { name: "Play", exact: true }).click();
  await dialog(page).getByRole("button", { name: "Save and leave" }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(page.getByText("The server is running and rewrites this file: stop it first.")).toBeVisible();
  await expect(page).toHaveURL(/#\/servers\/lab\/settings$/);
  await expect(nameInput(page)).toHaveValue("Renamed lab");
  // past the sheet's drop: still here; and the next way out asks again
  await page.waitForTimeout(900);
  await expect(page).toHaveURL(/#\/servers\/lab\/settings$/);
  await header(page).getByRole("link", { name: "Play", exact: true }).click();
  await expect(dialog(page)).toBeVisible();
  await dialog(page).getByRole("button", { name: "Save and leave" }).click();
  await expect(page).toHaveURL(/#\/$/);
});

test("server settings: the server starting while the dialog asks does not strand the player", async ({ page }) => {
  await open(page, "/servers/lab/settings");
  await expect(nameInput(page)).toBeVisible(LOAD);
  await nameInput(page).fill("Renamed lab");
  await header(page).getByRole("link", { name: "Logs", exact: true }).click();
  await expect(dialog(page)).toBeVisible();
  // the server starts: its settings are read again and say so (the game rewrites the file while it runs, so the page drops
  // the edits it can no longer save); nothing is left to ask about, and the way out the player chose goes on
  await page.evaluate(`(async () => {
    const { serverMockHandlers } = await import("/src/pages/servers/mockServers.ts");
    const mock = window.__kilnMock;
    mock.planCommand("server_settings_get", { result: { ...serverMockHandlers.server_settings_get({ serverId: "lab" }), running: true } });
    mock.setServer({ running: true });
  })()`);
  await expect(dialog(page)).toHaveCount(0);
  await expect(page).toHaveURL(/#\/logs$/);
  expect(await calls(page)).not.toContain("server_settings_set");
});

test("back and forward with nothing unsaved go as before, without a word", async ({ page }) => {
  await open(page, "/servers/lab/overview");
  await expect(tabs(page)).toBeVisible(LOAD);
  await tabs(page).getByRole("link", { name: /^Settings/ }).click();
  await expect(nameInput(page)).toBeVisible(LOAD);
  await header(page).getByRole("link", { name: "Play", exact: true }).click();
  await expect(page).toHaveURL(/#\/$/);
  await page.goBack();
  await expect(page).toHaveURL(/#\/servers\/lab\/settings$/);
  await expect(nameInput(page)).toBeVisible(LOAD);
  await page.goBack();
  await expect(page).toHaveURL(/#\/servers\/lab\/overview$/);
  await page.goForward();
  await expect(page).toHaveURL(/#\/servers\/lab\/settings$/);
  await page.goForward();
  await expect(page).toHaveURL(/#\/$/);
  await page.waitForTimeout(900);
  await expect(page).toHaveURL(/#\/$/);
  await expect(dialog(page)).toHaveCount(0);
});
