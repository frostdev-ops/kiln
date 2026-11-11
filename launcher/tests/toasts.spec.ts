// Notices that stay until closed (errors, and recovery notices pushed with `ms: 0`): pushing the same one again does
// not stack a copy, many of them stay a bounded stack without losing any notice's text, and the newest is in view
// however many came before it (ui/Toast.tsx).
import { expect, test, type Page } from "@playwright/test";

async function shell(page: Page) {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

test("the same persistent notice pushed again shows once, as the newest", async ({ page }) => {
  await shell(page);
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    for (let i = 0; i < 3; i++) {
      toast.info("Some mod folders were kept", "mods/edited-addon: player-settings.json was added by the player. These mods may still run.", { ms: 0 });
      toast.error({ code: "io", message: "permission denied" }, "Could not remove Edited Addon");
    }
    toast.info("Another notice", "with its own text", { ms: 0 });
    toast.error({ code: "io", message: "permission denied" }, "Could not remove Edited Addon");
  });
  const kept = page.locator(".k-toast", { hasText: "Some mod folders were kept" });
  await expect(kept).toHaveCount(1);
  await expect(kept).toContainText("mods/edited-addon: player-settings.json was added by the player");
  await expect(kept).toContainText("(3 times)");
  const failed = page.locator(".k-toast", { hasText: "Could not remove Edited Addon" });
  await expect(failed).toHaveCount(1);
  await expect(page.locator(".k-toast").last()).toContainText("Could not remove Edited Addon");
});

test("many persistent notices stay a bounded stack and every one's text stays readable", async ({ page }) => {
  await shell(page);
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    toast.info("Some mod folders were kept", "mods/edited-addon: player-settings.json was added by the player.", { ms: 0 });
    for (let i = 0; i < 20; i++) toast.error({ code: "io", message: `permission denied on mods/mod-${i}` }, `Could not remove Mod ${i}`);
  });
  const stack = page.locator(".k-toasts");
  expect(await stack.locator(".k-toast").count()).toBeLessThanOrEqual(8);
  for (let i = 0; i < 20; i++) {
    await expect(stack).toContainText(`Could not remove Mod ${i}`, { useInnerText: true });
    await expect(stack).toContainText(`permission denied on mods/mod-${i}`, { useInnerText: true });
  }
  await expect(stack).toContainText("mods/edited-addon: player-settings.json was added by the player.", { useInnerText: true });
});

test("a new error is in view however many notices came before it", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 600 });
  await shell(page);
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    for (let i = 0; i < 8; i++) toast.info(`Recovery ${i}`, "Review the retained mod folder mods/edited-addon before restarting the standalone loader.", { ms: 0 });
  });
  await expect(page.locator(".k-toast", { hasText: "Recovery 0" })).toBeVisible();
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    toast.error({ code: "io", message: "permission denied" }, "Clean up failed for Edited Addon");
  });
  await expect(page.locator(".k-toast", { hasText: "Clean up failed for Edited Addon" })).toBeInViewport();
});
