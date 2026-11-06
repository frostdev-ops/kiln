// Smoke: the shell renders in mock mode: the header's words, the game's state word, the mock flag, the gold focus ring.
import { expect, test } from "@playwright/test";

test("shell renders: header navigation, state word, mock flag", async ({ page }) => {
  await page.goto("/");
  const nav = page.getByRole("navigation", { name: "Main" });
  for (const item of ["Play", "Profiles", "Library", "Runtime", "Logs", "Saves", "Editor", "Servers"])
    await expect(nav.getByRole("link", { name: item, exact: true })).toBeVisible();
  for (const item of ["Settings", "About"]) await expect(page.locator(".k-head-link", { hasText: item })).toBeVisible();
  await expect(page.locator(".k-head-state")).toHaveText(/Stopped|Checking/);
  await expect(page.locator(".k-mock-flag")).toHaveText(/mock/i);
});

test("keyboard focus shows the gold ring", async ({ page }) => {
  await page.goto("/#/settings");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.keyboard.press("Tab");
  const outline = await page.evaluate(() => getComputedStyle(document.activeElement as Element).outlineColor);
  expect(outline).toBe("rgb(249, 199, 79)");
});

test("every button, link and field on the main pages has a name", async ({ page }) => {
  for (const r of ["/", "/profiles/ember-run/mods", "/library", "/runtime", "/servers/lab/overview", "/servers/lab/admin/items"]) {
    await page.goto(`/#${r}`);
    await page.locator(".k-splash").waitFor({ state: "detached" });
    await page.waitForTimeout(500);
    const unnamed = await page.evaluate(() => [...document.querySelectorAll("button, a[href], input, select, textarea")]
      .filter((el) => (el as HTMLElement).offsetParent !== null)
      .filter((el) => {
        const e = el as HTMLElement;
        const label = e.getAttribute("aria-label") || e.getAttribute("title") || e.textContent?.trim() || (e.id && document.querySelector(`label[for="${e.id}"]`)?.textContent?.trim()) || e.closest("label")?.textContent?.trim() || e.getAttribute("placeholder");
        return !label;
      })
      .map((el) => el.outerHTML.slice(0, 120)));
    expect(unnamed, r).toEqual([]);
  }
});
