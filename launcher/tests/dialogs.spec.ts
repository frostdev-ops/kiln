// Dialogs over the page: while one is open the page behind it shows no scrollbars. WebKitGTK paints thin (overlay)
// scrollbars after everything else, so the mods list's scrollbar was drawn across a Remove confirmation (the owner's
// report of 13 Nov 2026, test build 6). ui/Modal.tsx useCoverPage sets <html data-covered>; theme/base.css hides the
// scrollbars of .k-app and of the Deck's screen under it. Chromium runs these tests: they check the attribute and the
// computed style, not WebKitGTK's painting.
import { expect, test, type Page } from "@playwright/test";

const covered = (page: Page) => page.evaluate(() => document.documentElement.hasAttribute("data-covered"));
const listScrollbar = (page: Page) =>
  page.evaluate(() => {
    const el = document.querySelector<HTMLElement>(".k-hero .k-mods-list");
    return el ? getComputedStyle(el).scrollbarWidth : null;
  });

test("a dialog hides the page's scrollbars while it is open, and only then", async ({ page }) => {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  const list = page.locator(".k-hero").getByRole("list", { name: "Mods" });
  await expect(list).toBeVisible({ timeout: 10_000 });
  await expect.poll(() => covered(page)).toBe(false);
  await expect.poll(() => listScrollbar(page)).toBe("thin");
  const row = list.getByRole("listitem").filter({ has: page.getByText("Night Sky", { exact: true }) });
  await row.getByRole("button", { name: "More for Night Sky" }).click({ timeout: 10_000 });
  await page.getByRole("menuitem", { name: "Remove", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Remove Night Sky?" });
  await expect(dialog).toBeVisible();
  await expect.poll(() => covered(page)).toBe(true);
  await expect.poll(() => listScrollbar(page)).toBe("none");
  // the dialog's own content keeps its scrollbars (it is outside the page's root)
  expect(await dialog.evaluate((el) => getComputedStyle(el).scrollbarWidth)).toBe("thin");
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toBeHidden();
  await expect.poll(() => covered(page)).toBe(false);
  await expect.poll(() => listScrollbar(page)).toBe("thin");
});

test("on the Deck a dialog covers the screen the same way", async ({ page }) => {
  await page.goto("/?deck=steamdeck#/deck/mods");
  const row = page.locator('[data-deck-id="mod-kiln-new-items"]');
  await expect(row).toBeVisible({ timeout: 10_000 });
  await row.focus();
  expect(await covered(page)).toBe(false);
  await page.evaluate(() => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
    .emit("kiln://gamepad", { kind: "button", button: "x", pressed: true, repeat: false, device: "event9" }));
  await page.evaluate(() => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
    .emit("kiln://gamepad", { kind: "button", button: "x", pressed: false, repeat: false, device: "event9" }));
  const dialog = page.locator('[data-deck-dialog="remove"]');
  await expect(dialog).toBeVisible();
  await expect.poll(() => covered(page)).toBe(true);
  expect(await page.evaluate(() => getComputedStyle(document.querySelector("[data-deck-screen] *")!).scrollbarWidth)).toBe("none");
  await page.evaluate(() => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
    .emit("kiln://gamepad", { kind: "button", button: "b", pressed: true, repeat: false, device: "event9" }));
  await page.evaluate(() => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
    .emit("kiln://gamepad", { kind: "button", button: "b", pressed: false, repeat: false, device: "event9" }));
  await expect(dialog).toHaveCount(0);
  await expect.poll(() => covered(page)).toBe(false);
});
