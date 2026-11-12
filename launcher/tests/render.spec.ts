// The page's ready call (shell/started.ts) and Settings' "Draw the launcher without the GPU" (render.rs), in mock mode.
// The dev server runs React in development, where StrictMode runs the first commit's effects twice.
import { expect, test, type Page } from "@playwright/test";

type Mock = { calls: () => string[] };
const calls = (page: Page, cmd: string) => page.evaluate((c) => (window as unknown as { __kilnMock: Mock }).__kilnMock.calls().filter((x) => x === c).length, cmd);

test("the page says it started exactly once, in the desktop and the Deck UI", async ({ page }) => {
  for (const at of ["/#/", "/#/deck"]) {
    await page.goto(at);
    await page.locator(".k-splash").waitFor({ state: "detached" });
    await expect.poll(() => calls(page, "app_started")).toBe(1);
    await page.waitForTimeout(300);
    expect(await calls(page, "app_started")).toBe(1);
  }
  // moving around the app does not say it again
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.getByRole("link", { name: "Settings" }).first().click();
  await page.waitForTimeout(300);
  expect(await calls(page, "app_started")).toBe(1);
});

test("Settings sets and clears the marker for the next start and says how this start draws", async ({ page }) => {
  await page.goto("/#/settings");
  const sw = page.getByRole("switch", { name: "Draw the launcher without the GPU" });
  await expect(sw).not.toBeChecked();
  const card = page.locator(".k-toggle", { has: sw });
  await expect(card).toContainText("Applies at the next start. This start draws with the GPU.");
  await card.click();
  await expect(sw).toBeChecked();
  await expect(page.locator(".k-toast", { hasText: "The next start draws without the GPU" })).toBeVisible();
  await expect(card).toContainText("Turned on in Settings");
  expect(await calls(page, "app_render_set")).toBe(1);
  await card.click();
  await expect(sw).not.toBeChecked();
});

test("after the fallback, Settings says why and the fire is the CSS one", async ({ page }) => {
  await page.goto("/?render=software#/settings");
  const sw = page.getByRole("switch", { name: "Draw the launcher without the GPU" });
  await expect(sw).toBeChecked();
  await expect(page.locator(".k-toggle", { has: sw })).toContainText("This start draws without the GPU: the page did not start with it. The launcher turned this on by itself");
  const fire = page.getByRole("switch", { name: "Animated fire" });
  await expect(fire).toBeDisabled();
  await expect(page.locator(".k-toggle", { has: fire })).toContainText("Off while the launcher draws without the GPU.");
  await page.goto("/?render=software#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-gl", /.*/);
});
