import { expect, test } from "@playwright/test";

test("drops show every new kind's skip reason without claiming it was added", async ({ page }) => {
  // Feed real-shaped backend reports through the existing task and toast flow.
  await page.route("**/src/api/mock.ts", async (route) => {
    const response = await route.fetch();
    const text = (await response.text()).replace("library_add: (a) => {", `library_add: (a) => {
      if (a.paths?.[0]?.includes("-engine-review.zip")) {
        const path = a.paths[0], kind = path.split("-engine-review")[0];
        return startTask("library_add", "Detecting", ["Reading"], () => ({
          import: { imported: [], existing: [], skipped: [{ path, reason: kind + ": no importer is registered" }] },
          added: [], already: [], modpacks: [], profileId: null, profileName: null, changes: [], missing: [], running: false
        }), 10);
      }
    `);
    await route.fulfill({ response, body: text });
  });
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  for (const kind of ["eml", "shroudforge", "set", "enshwick", "greeneye", "shroudtopia", "flame", "shroudlink", "ember",
    "proxy", "data", "save", "reshade", "cheat-table", "asi"]) {
    const dt = await page.evaluateHandle((name) => { const d = new DataTransfer(); d.items.add(new File(["x"], name)); return d; }, `${kind}-engine-review.zip`);
    await page.locator(".k-hero").dispatchEvent("drop", { dataTransfer: dt });
    const toast = page.locator(".k-toast", { hasText: `${kind}: no importer is registered` });
    await expect(toast).toContainText("Nothing to add");
    await expect(toast).not.toContainText("Added");
  }
});
