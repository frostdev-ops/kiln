// Steam's selected runner loads Kiln's dinput8.dll by itself (GE-Proton 11-11 and newer): no routine "One Steam
// setting" advice, but the launch option comes back as the fallback when Kiln really did not load (issue #77).
import { expect, test } from "@playwright/test";

const option = 'WINEDLLOVERRIDES="dinput8=n,b" %command%';
type M = { __kilnMock: { stopGame: () => void; setNoProxy: (on: boolean) => void } };

test("desktop: no Steam setting advice while nothing is wrong", async ({ page }) => {
  await page.goto("/?mock=native-runner#/");
  await expect(page.locator(".k-head-state")).toHaveText("Stopped");
  await expect(page.getByText("One Steam setting", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Kiln is not loading", { exact: true })).toHaveCount(0);
  await expect(page.getByText(option, { exact: true })).toHaveCount(0);
});

test("desktop: the launch option is offered when the game ran without Kiln", async ({ page }) => {
  await page.goto("/?mock=native-runner#/");
  await expect(page.locator(".k-head-state")).toHaveText("Stopped");
  await page.evaluate(() => (window as unknown as M).__kilnMock.setNoProxy(true));
  await page.getByRole("button", { name: "Without mods", exact: true }).click();
  await expect(page.locator(".k-head-state")).toHaveText(/^No Kiln/, { timeout: 8000 });
  await expect(page.getByText("Kiln is not loading")).toBeVisible();
  await expect(page.getByText(option, { exact: true })).toBeVisible();
  await page.evaluate(() => (window as unknown as M).__kilnMock.stopGame());
  await page.evaluate(() => (window as unknown as M).__kilnMock.setNoProxy(false));
});

test("Deck Help: not needed with this Proton, the option still shown as the fallback", async ({ page }) => {
  await page.goto("/?mock=native-runner&deck=steamdeck#/deck/help");
  await expect(page.getByText("Not needed with your Proton", { exact: true })).toBeVisible();
  await expect(page.getByText("Needed for this game", { exact: true })).toHaveCount(0);
  await expect(page.getByTestId("deck-launch-option")).toHaveText(option);
});
