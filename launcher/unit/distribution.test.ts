import { beforeEach, expect, mock, test } from "bun:test";

let distribution: "direct" | "nexus" | undefined = "nexus";
let infoFails = false;
const invoked: string[] = [];
mock.module("../src/api/mock", () => ({
  mockInvoke: async (command: string) => {
    if (command === "app_info") {
      if (infoFails) throw { code: "io", message: "IPC unavailable" };
      return { distribution };
    }
    invoked.push(command);
    return {};
  },
  mockListen: async () => () => {},
}));
const api = await import("../src/api/commands");
const operations = [api.updateStatus, api.updateCheck, api.updateInstall, api.updateRestart, () => api.updateSkip(null), api.updateAck];
beforeEach(() => { distribution = "nexus"; infoFails = false; invoked.length = 0; });

test("a Nexus distribution cannot invoke any updater command, including installation or restart", async () => {
  for (const run of operations) await expect(run()).rejects.toMatchObject({ code: "unsupported" });
  expect(invoked).toEqual([]);
});

test("an unreadable distribution fails closed before updater IPC", async () => {
  infoFails = true;
  for (const run of operations) await expect(run()).rejects.toMatchObject({ code: "io" });
  expect(invoked).toEqual([]);
});

test("direct builds and older backends without the field keep the updater contract", async () => {
  for (const d of ["direct", undefined] as const) {
    distribution = d;
    invoked.length = 0;
    for (const run of operations) await run();
    expect(invoked).toEqual(["update_status", "update_check", "update_install", "update_restart", "update_skip", "update_ack"]);
  }
});
