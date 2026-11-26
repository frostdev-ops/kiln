// The header's navigation leaves a server's page from every one of its tabs (netjunk, #bug-reports 2026-11-26: on the
// World tab, Play opened the server's Players tab; on Players, Play did nothing). The page that is left stays on the
// sheet while the sheet drops (AppShell's useLinger); it must not act on the new address meanwhile.
import { expect, test, type Page } from "@playwright/test";

/** The first page of each of the server's seven section tabs (shell/serverNav.ts). */
const SERVER_TABS = ["overview", "console", "admin/players", "admin/map", "mods", "backups", "settings"];
// a lazily loaded server page on a loaded PC (see smoke.spec.ts)
const LOAD = { timeout: 30_000 };

async function openTab(page: Page, tab: string) {
  const route = `/servers/lab/${tab}`;
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
  if (await page.evaluate((r) => location.hash !== `#${r}`, route)) await page.evaluate((r) => { location.hash = `#${r}`; }, route);
  await expect(page.getByRole("navigation", { name: "Server", exact: true })).toBeVisible(LOAD);
  await expect(page).toHaveURL(new RegExp(`#${route.replace(/\//g, "\\/")}$`));
}

for (const tab of SERVER_TABS) {
  test(`every header entry leaves the server's ${tab} tab for its own page`, async ({ page }) => {
    await openTab(page, tab);
    const main = page.getByRole("navigation", { name: "Main" });
    const entries = await main.getByRole("link").evaluateAll((els) => els.map((e) => ({ name: e.textContent?.trim() ?? "", href: e.getAttribute("href") ?? "" })));
    // Play first: the entry of the report
    expect(entries[0]).toEqual({ name: "Play", href: "#/" });
    // and the header's right side (Settings, About)
    const side = await page.locator(".k-head-link").evaluateAll((els) => els.map((e) => ({ name: e.textContent?.trim() ?? "", href: e.getAttribute("href") ?? "" })));
    expect(entries.length).toBeGreaterThan(5);
    expect(side.map((e) => e.name)).toEqual(["Settings", "About"]);
    for (const [i, { name, href }] of [...entries, ...side].entries()) {
      if (i > 0) await openTab(page, tab);
      await (i < entries.length ? main.locator(`a[href="${href}"]`) : page.locator(`a.k-head-link[href="${href}"]`)).click();
      const want = new RegExp(`${href.replace(/[/#]/g, "\\$&")}$`);
      await expect(page, `${tab} -> ${name}`).toHaveURL(want);
      // past the sheet's drop (480 ms): the page that was left has gone and sent nobody back
      await page.waitForTimeout(900);
      await expect(page, `${tab} -> ${name}, after the sheet's drop`).toHaveURL(want);
      await expect(page.getByRole("navigation", { name: "Server", exact: true }), `${tab} -> ${name}`).toHaveCount(0);
    }
  });
}
