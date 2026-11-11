// Retained player files need recovery instructions until the player dismisses them, not just a success toast.
import { expect, test } from "@playwright/test";

test("deactivating a profile keeps retained-folder warnings through later notifications", async ({ page }) => {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.evaluate(() => {
    const mock = (window as unknown as { __kilnMock: { planCommand: (cmd: string, plan: { result: unknown }) => void } }).__kilnMock;
    mock.planCommand("profile_deactivate", { result: {
      removed: ["mods/unchanged-addon"],
      retained: [{ path: "mods/edited-addon", reason: "player-settings.json was added by the player", mayRun: true }],
    } });
  });
  await page.getByRole("button", { name: /^Active profile: .* Change profile\.$/ }).click();
  await page.getByRole("option", { name: "No profile drop-ins" }).click();
  const warning = page.locator(".k-toast", { hasText: "Some mod folders were kept" });
  await expect(warning).toContainText("mods/edited-addon");
  await expect(warning).toContainText("player-settings.json was added by the player");
  await expect(warning).toContainText("may still run in the standalone loader");
  await page.clock.install();
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    for (let i = 0; i < 6; i++) toast.ok(`Another operation ${i}`);
  });
  await page.clock.fastForward(10_000);
  await expect(warning).toBeVisible();
  await warning.getByRole("button", { name: "Dismiss", exact: true }).click();
  await expect(warning).toHaveCount(0);
});

test("persistent warnings remain scrollable on a small screen while timed notices stay bounded", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 600 });
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.clock.install();
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    for (let i = 0; i < 8; i++) toast.info(`Recovery ${i}`, "Review the retained mod folder before restarting the standalone loader.", { ms: 0 });
    for (let i = 0; i < 6; i++) toast.ok(`Timed ${i}`);
  });
  await expect(page.locator(".k-toast", { hasText: /^Timed/ })).toHaveCount(4);
  await page.clock.fastForward(10_000);
  await expect(page.locator(".k-toast")).toHaveCount(8);
  const bounds = await page.locator(".k-toasts").boundingBox();
  expect(bounds?.y).toBeGreaterThanOrEqual(0);
  expect((bounds?.y ?? 0) + (bounds?.height ?? 0)).toBeLessThanOrEqual(600);
  const last = page.locator(".k-toast", { hasText: "Recovery 7" });
  await last.getByRole("button", { name: "Dismiss", exact: true }).click();
  await expect(last).toHaveCount(0);
  await expect(page.locator(".k-toast", { hasText: "Recovery 0" })).toHaveCount(1);
});

test("an empty cleanup result does not show a retained-folder warning", async ({ page }) => {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.getByRole("button", { name: /^Active profile: .* Change profile\.$/ }).click();
  await page.getByRole("option", { name: "No profile drop-ins" }).click();
  await expect(page.locator(".k-toast", { hasText: "No profile" })).toBeVisible();
  await expect(page.locator(".k-toast", { hasText: "Some mod folders were kept" })).toHaveCount(0);
});
