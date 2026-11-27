// The launcher's admin transport (src/api/adminTauri.ts): the same Tauri commands as before the seam (admin_connect,
// admin_call, admin_subscribe, ..., the `kiln://admin` event), seen at Tauri's invoke. The KAP op `server_restart` is
// an admin_call; the launcher's own `server_restart` command (a server it manages) is only the host's
// restartManagedServer.
import { afterAll, beforeAll, beforeEach, describe, expect, test } from "bun:test";
// imported before the stand-in window below exists (modules that see a window at load expect a whole browser)
import { acquireAdmin, callAdmin, configureAdmin, kapOps, releaseAdmin } from "../src/api/admin";
import { launcherAdminHost, tauriAdminTransport } from "../src/api/adminTauri";

type Invoked = [cmd: string, args: Record<string, unknown>];
const invoked: Invoked[] = [];
const callbacks = new Map<number, (e: unknown) => void>();
let nextCb = 1;
const answers: Record<string, (args: Record<string, unknown>) => unknown> = {
  admin_connect: (a) => ({ session: `adm-${String(a.serverId)}-1`, serverId: a.serverId, transport: "tcp", ops: ["players", "server_restart"], topics: ["state", "log"], kiln: "0.2.0-dev.30.9", server: { id: "srv", name: "Lab" } }),
  admin_call: (a) => ({ op: a.op }),
  "plugin:event|listen": () => 7,
  server_restart: () => "task-1",
  logs_crash_reports: () => [{ name: "crash-1.txt", created: "2026-11-23T10:00:00Z", size: 12 }],
};
const g = globalThis as unknown as { window?: unknown };
const hadWindow = "window" in g;
beforeAll(() => {
  g.window = Object.assign(Object.create(globalThis), {
    __TAURI_INTERNALS__: {
      invoke: async (cmd: string, args: Record<string, unknown>) => {
        invoked.push([cmd, args]);
        const a = answers[cmd];
        return a ? a(args) : null;
      },
      transformCallback: (cb: (e: unknown) => void) => { const id = nextCb++; callbacks.set(id, cb); return id; },
      convertFileSrc: (path: string, protocol: string) => `${protocol}://localhost/${path}`,
    },
  });
});
afterAll(() => {
  if (hadWindow) return;
  delete g.window;
});
beforeEach(() => { invoked.length = 0; });

const cmds = () => invoked.map((i) => i[0]);

describe("the Tauri admin transport", () => {
  test("speaks the admin_* commands of the launcher's backend", async () => {
    const t = tauriAdminTransport;
    const s = await t.connect("lab", "tcp");
    expect(invoked[0]).toEqual(["admin_connect", { serverId: "lab", transport: "tcp" }]);
    expect(s).toMatchObject({ session: "adm-lab-1", transport: "tcp", ops: ["players", "server_restart"] });
    expect(await t.call(s.session, "players", { q: 1 })).toEqual({ op: "players" });
    await t.subscribe(s.session, "state");
    await t.unsubscribe(s.session, "state");
    await t.disconnect(s.session);
    expect(invoked.slice(1)).toEqual([
      ["admin_call", { session: "adm-lab-1", op: "players", args: { q: 1 } }],
      ["admin_subscribe", { session: "adm-lab-1", topic: "state" }],
      ["admin_unsubscribe", { session: "adm-lab-1", topic: "state" }],
      ["admin_disconnect", { session: "adm-lab-1" }],
    ]);
    expect(t.kinds).toEqual(["auto", "tcp", "file", "ssh"]);
  });

  test("the kiln://admin event reaches the listener as {session, serverId, event}", async () => {
    const t = tauriAdminTransport;
    const got: unknown[] = [];
    const un = await t.listen((e) => got.push(e));
    const listen = invoked.find((i) => i[0] === "plugin:event|listen")!;
    expect(listen[1].event).toBe("kiln://admin");
    const payload = { session: "adm-lab-1", serverId: "lab", event: { kind: "push", topic: "state", seq: 3, data: { clock: 1 } } };
    callbacks.get(listen[1].handler as number)!({ event: "kiln://admin", id: 7, payload });
    expect(got).toEqual([payload]);
    void un;
  });

  test("the KAP op server_restart goes out as admin_call, never as the launcher's server_restart command", async () => {
    const t = tauriAdminTransport;
    await t.call("adm-lab-1", "server_restart", { seconds: 60 });
    expect(invoked).toEqual([["admin_call", { session: "adm-lab-1", op: "server_restart", args: { seconds: 60 } }]]);
    expect(cmds()).not.toContain("server_restart");
  });

  test("through the hub too: kapOps.serverRestart is an admin_call on the server's session", async () => {
    configureAdmin({ transport: tauriAdminTransport, host: launcherAdminHost });
    acquireAdmin("lab");
    await new Promise((r) => setTimeout(r, 20));
    invoked.length = 0;
    await kapOps((op, args) => callAdmin("lab", op, args)).serverRestart({ seconds: 10 });
    expect(invoked).toEqual([["admin_call", { session: "adm-lab-1", op: "server_restart", args: { seconds: 10 } }]]);
    releaseAdmin("lab");
  });

  test("the launcher host: its own restart is the server_restart command, crash reports come from the folder", async () => {
    const h = launcherAdminHost;
    await h.restartManagedServer!("lab");
    expect(invoked).toEqual([["server_restart", { serverId: "lab" }]]);
    expect(cmds()).not.toContain("admin_call");
    expect(await h.crashReportsFromDisk!("lab")).toEqual([{ name: "crash-1.txt", created: "2026-11-23T10:00:00Z", size: 12 }]);
    expect(invoked.at(-1)).toEqual(["logs_crash_reports", { target: { server: "lab" } }]);
    // the launcher's own words for a map without art (admin.map.*)
    expect(h.useMapNoArt({ canExtract: false })).toBeUndefined();
  });
});
