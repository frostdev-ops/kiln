// A background status refresh or a recovery toast must not dismiss a menu the player is using.
import { expect, test } from "@playwright/test";

test("a profile menu survives status refetches and scrolling recovery notices", async ({ page }) => {
  await page.goto("/#/profiles");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.getByLabel("More for Vanilla plus").click();
  const menu = page.getByRole("menu");
  await expect(menu).toBeVisible();
  const original = await menu.elementHandle();
  await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    const p = await api.profileGet("vanilla-plus");
    await api.profileSave({ ...p, name: "Vanilla plus refreshed" });
    const mock = (window as unknown as { __kilnMock: { emit: (name: string, payload: unknown) => void } }).__kilnMock;
    for (let i = 0; i < 3; i++) mock.emit("kiln://game", { gameId: "client-steam", kind: "status" });
  });
  // The refetched name proves the list's update reached React while the same menu remained open.
  await expect(page.getByLabel("More for Vanilla plus refreshed")).toHaveAttribute("aria-expanded", "true");
  await expect(menu).toBeVisible();
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    for (let i = 0; i < 9; i++) toast.info(`Recovery ${i}`, `mods/edited-${i}: review this retained copy before starting the game. `.repeat(8), { ms: 0 });
  });
  await expect.poll(() => page.locator(".k-toasts").evaluate((e) => e.scrollTop)).toBeGreaterThan(0);
  await expect(menu).toBeVisible({ timeout: 2000 });
  expect(await original!.evaluate((e) => e.isConnected)).toBe(true);
  await menu.getByRole("menuitem", { name: "Delete", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("Delete Vanilla plus refreshed?");
  await page.getByRole("dialog").getByRole("button", { name: "Cancel", exact: true }).click();
  await page.getByLabel("More for Vanilla plus refreshed").click();
  await menu.dispatchEvent("scroll");
  await expect(menu).toBeVisible();
  await page.locator(".k-table-scroll").dispatchEvent("scroll");
  await expect(menu).toHaveCount(0); // scrolling the trigger's own container still closes the fixed menu
});
