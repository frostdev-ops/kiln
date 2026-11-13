// The desktop save editor (/editor) in mock mode with the mock's synthetic saves (src/api/mockEditor.ts): the session
// kept when the page is left, another character asking before it drops changes, the backups' words, a read-only save,
// and a save the game wrote after it was read.
import { expect, test, type Page } from "@playwright/test";

const SHOTS = process.env.KILN_DECK_SHOTS;
const mock = <T,>(page: Page, f: (m: Record<string, (...a: unknown[]) => unknown>) => T) => page.evaluate(f as never, null as never) as Promise<T>;

async function open(page: Page) {
  await page.goto("/#/editor");
  await expect(page.locator(".k-page-title")).toContainText("Aldric", { timeout: 15000 });
}
const say = (page: Page) => page.locator(".ke .k-page-sub");

test("changes survive leaving the page; another character asks first; Discard drops them", async ({ page }) => {
  await open(page);
  await page.locator(".ke-grid .ke-slot.full").first().click();
  await page.locator('.ke-insp [aria-label="+1"]').first().click();
  await expect(say(page)).toContainText("Changes not saved");
  // to Settings and back: still changed
  await page.goto("/#/settings");
  await page.goto("/#/editor");
  await expect(say(page)).toContainText("Changes not saved");
  await expect(page.locator(".ke-grid .ke-slot.full").first()).toContainText("121");
  // another character: asked first; Cancel keeps Aldric
  await page.locator(".ke-who-btn").click();
  await page.getByRole("option", { name: /Brienne/ }).click();
  const ask = page.getByRole("dialog", { name: "Discard the changes?" });
  await expect(ask).toContainText("The changes to Aldric are not saved yet.");
  await ask.getByRole("button", { name: "Cancel" }).click();
  await expect(page.locator(".k-page-title")).toContainText("Aldric");
  // Discard: back as read
  await page.getByRole("button", { name: "Discard" }).click();
  await expect(say(page)).toContainText("Level 25");
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/desktop-editor.png` });
});

test("backups name their kind; a restore keeps the save it replaced", async ({ page }) => {
  await open(page);
  await page.getByRole("button", { name: "Backups" }).click();
  const dlg = page.getByRole("dialog", { name: "Backups for Aldric" });
  await expect(dlg.locator(".ke-bk-row")).toHaveCount(2);
  await expect(dlg).toContainText("Game's copy");
  await expect(dlg.locator(".ke-bk-row > .k-sc")).toHaveText(["Game's copy", "Game's copy"]);
  await dlg.locator(".ke-bk-row").first().getByRole("button", { name: "Restore" }).click();
  await dlg.locator(".ke-bk-row").first().getByRole("button", { name: "Restore" }).click();
  await expect(page.locator(".k-toast, [role=status]").filter({ hasText: "Restored" }).first()).toBeVisible();
  await page.getByRole("button", { name: "Backups" }).click();
  await expect(dlg.locator(".ke-bk-row")).toHaveCount(3);
  await expect(dlg).toContainText("Kiln backup");
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/desktop-backups.png` });
});

test("a save with an item no table knows is read-only: Save stays off and the page says so", async ({ page }) => {
  await open(page);
  await page.locator(".ke-who-btn").click();
  await page.getByRole("option", { name: /Mordred/ }).click();
  await expect(page.locator(".k-page-title")).toContainText("Mordred");
  await expect(say(page)).toContainText("This save can only be read: Save is off.");
  await expect(page.getByText("Unknown items in this save")).toBeVisible();
  await page.locator(".ke-grid .ke-slot.full").first().click();
  await page.locator('.ke-insp [aria-label="+1"]').first().click();
  await expect(page.getByRole("button", { name: "Save" })).toBeDisabled();
  // Cedric cannot be read at all: the reason is in English
  await page.locator(".ke-who-btn").click();
  await expect(page.getByRole("option", { name: /Cedric/ })).toContainText("The editor cannot find the equipment and inventory in this save");
});

test("a save the game wrote after the editor read it is not overwritten", async ({ page }) => {
  await open(page);
  await page.locator(".ke-grid .ke-slot.full").first().click();
  await page.locator('.ke-insp [aria-label="+1"]').first().click();
  // the game wrote the file meanwhile: its time changed
  await mock(page, () => (window as unknown as { __kilnMock: { planCommand: (c: string, p: unknown) => void } }).__kilnMock.planCommand("editor_file_info", { result: { modified: Date.now() + 5000 } }));
  await page.getByRole("button", { name: "Save" }).click();
  await expect(page.getByText("The game wrote this save after the editor read it.")).toBeVisible();
  await expect(say(page)).toContainText("Changes not saved");
  await page.getByRole("button", { name: "Read again" }).click();
  await expect(say(page)).toContainText("Level 25");
  await expect(page.getByText("The game wrote this save after the editor read it.")).toHaveCount(0);
});
