// refresh_active reconciles current files too; Some(Activation) does not imply another profile.json write.
import { expect, test } from "@playwright/test";

test("a notes-only save reports retained copies without advancing the game's generation", async ({ page }) => {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  const generations = await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    const before = await api.profileActive("client-steam");
    const mock = (window as unknown as { __kilnMock: { planActivation: (report: unknown) => void } }).__kilnMock;
    mock.planActivation({ retained: [{ path: "shrouded_engine_tweaks/mods/edited", reason: "init.lua changed; Kiln left it as it is", mayRun: true }], keptFiles: [], warnings: [] });
    const p = await api.profileGet("ember-run");
    await api.profileSave({ ...p, notes: "Only the profile notes changed" });
    return [before!.generation, (await api.profileActive("client-steam"))!.generation];
  });
  await expect(page.locator(".k-toast", { hasText: "Some mod folders were kept" })).toContainText("shrouded_engine_tweaks/mods/edited");
  expect(generations[1]).toBe(generations[0]);
});
