// The support report block on Logs (docs/kiln/launcher/support-report.md): "Save report..." and "Copy summary" in the
// browser mock (no file dialog there: the mock answers report_save with the default name).
import { expect, test, type Page } from "@playwright/test";

async function desktop(page: Page, route: string) {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

test("Logs offers the support report: save it, copy the summary", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await desktop(page, "/logs");
  const block = page.getByTestId("support-report");
  await expect(block).toContainText("Support report");
  await expect(block).toContainText("paths and server addresses are replaced");
  await block.getByRole("button", { name: "Save report..." }).click();
  await expect(page.locator(".k-toasts")).toContainText("Report saved");
  await expect(page.locator(".k-toasts")).toContainText(/kiln-report-\d{4}-\d{2}-\d{2}\.zip/);
  await block.getByRole("button", { name: "Copy summary" }).click();
  await expect(page.locator(".k-toasts")).toContainText("Summary copied");
  const copied = await page.evaluate(() => navigator.clipboard.readText());
  expect(copied).toContain("Kiln support report");
  expect(copied).toContain("Findings:");
});

test("a server's Logs page has the block too", async ({ page }) => {
  await desktop(page, "/servers/lab/logs");
  await expect(page.getByTestId("support-report")).toBeVisible();
});
