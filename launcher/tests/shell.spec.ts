// Smoke: the shell renders in mock mode with its navigation and top bar.
import { expect, test } from "@playwright/test";

test("shell renders: sidebar, top bar, mock flag", async ({ page }) => {
  await page.goto("/");
  for (const item of ["Play", "Profiles", "Library", "Runtime", "Logs", "Saves", "Editor", "Servers", "Settings", "About"])
    await expect(page.locator(".k-nav-item", { hasText: item })).toBeVisible();
  await expect(page.locator(".k-topbar .k-pill")).toBeVisible();
  await expect(page.getByLabel("Tasks")).toBeVisible();
  await expect(page.locator(".k-mock-flag")).toHaveText(/mock/i);
});

test("keyboard focus shows the ember ring", async ({ page }) => {
  await page.goto("/#/settings");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.keyboard.press("Tab");
  const outline = await page.evaluate(() => getComputedStyle(document.activeElement as Element).outlineColor);
  expect(outline).toBe("rgb(255, 106, 26)");
});
