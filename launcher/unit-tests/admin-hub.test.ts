// The admin hub (src/api/admin.ts) over the AdminTransport seam: it connects, subscribes, follows the transport's
// events and runs ops only through the transport it was given (configureAdmin), so the same pages work over Tauri
// (src/api/adminTauri.ts) and over a WebSocket (src/api/adminWs.ts).
import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import {
  acquireAdmin, adminHost, callAdmin, configureAdmin, kapOps, reconnectAdmin, releaseAdmin, useAdminHub,
  type AdminHubEvent, type AdminSession, type AdminTransport, type AdminTransportKind,
} from "../src/api/admin";
import type { Json } from "../src/api/shared";

type Call = [string, ...unknown[]];

/** A transport that records what the hub asks and lets the test send events. */
function fakeTransport(opts: { ops?: string[]; fail?: { code: string; message: string } } = {}) {
  const calls: Call[] = [];
  const listeners = new Set<(e: AdminHubEvent) => void>();
  let n = 0;
  const answers = new Map<string, (args: Record<string, Json>) => unknown>();
  const t: AdminTransport & { calls: Call[]; emit: (e: AdminHubEvent) => void; answers: typeof answers; last?: AdminSession } = {
    name: "fake",
    kinds: ["auto", "tcp"],
    calls,
    answers,
    emit: (e) => { for (const l of listeners) l(e); },
    async connect(serverId: string, want: AdminTransportKind) {
      calls.push(["connect", serverId, want]);
      if (opts.fail) throw opts.fail;
      const s: AdminSession = { session: `fake-${serverId}-${++n}`, serverId, transport: "tcp", ops: opts.ops ?? ["players", "server_restart", "update_list"], topics: ["state", "log"], kiln: "0.2.0-dev.30.9", server: { id: serverId, name: "Lab" } };
      t.last = s;
      return s;
    },
    async disconnect(session: string) { calls.push(["disconnect", session]); },
    async call<T>(session: string, op: string, args: Record<string, Json>) {
      calls.push(["call", session, op, args]);
      const a = answers.get(op);
      return (a ? a(args) : { op }) as T;
    },
    async subscribe(session: string, topic: string) { calls.push(["subscribe", session, topic]); },
    async unsubscribe(session: string, topic: string) { calls.push(["unsubscribe", session, topic]); },
    async listen(handler: (e: AdminHubEvent) => void) {
      calls.push(["listen"]);
      listeners.add(handler);
      return () => { listeners.delete(handler); };
    },
  };
  return t;
}

const settle = () => new Promise((r) => setTimeout(r, 10));
const conn = (id = "lab") => useAdminHub.getState().conns[id];

let fake: ReturnType<typeof fakeTransport>;
beforeEach(() => {
  fake = fakeTransport();
  configureAdmin({ transport: fake });
});
afterEach(() => {
  releaseAdmin("lab");
});

describe("the admin hub over a transport", () => {
  test("connects through the configured transport with the chosen kind and subscribes to state", async () => {
    acquireAdmin("lab");
    await settle();
    expect(fake.calls.filter((c) => c[0] === "connect")).toEqual([["connect", "lab", "auto"]]);
    expect(conn().status).toBe("ready");
    expect(conn().session?.session).toBe(fake.last!.session);
    expect(fake.calls).toContainEqual(["subscribe", fake.last!.session, "state"]);
    expect(fake.calls).toContainEqual(["listen"]);
  });

  test("state pushes, a closed session and its ready again reach the server's state", async () => {
    acquireAdmin("lab");
    await settle();
    const session = fake.last!.session;
    fake.emit({ session, serverId: "lab", event: { kind: "push", topic: "state", seq: 1, data: { clock: 5, kiln: "x", ready: true, players: {} } } });
    expect(conn().state?.clock).toBe(5);
    expect(conn().state?.players).toEqual([]);  // a Lua `{}` becomes a list
    fake.emit({ session, serverId: "lab", event: { kind: "closed", reason: "bye: unload", retry: true } });
    expect(conn().status).toBe("reconnecting");
    fake.emit({ session, serverId: "lab", event: { kind: "ready", transport: "tcp", ops: ["players"], topics: ["state"] } });
    expect(conn().status).toBe("ready");
    expect(conn().session?.ops).toEqual(["players"]);
    fake.emit({ session, serverId: "lab", event: { kind: "closed", reason: "token", retry: false } });
    expect(conn().status).toBe("error");
    expect(conn().error?.code).toBe("auth_failed");
    // another session's events are not this one's
    fake.emit({ session: "other", serverId: "lab", event: { kind: "ready", transport: "tcp", ops: [], topics: [] } });
    expect(conn().status).toBe("error");
  });

  test("ops run on the transport's session; an op the server lacks is refused before the transport", async () => {
    acquireAdmin("lab");
    await settle();
    fake.answers.set("players", () => [{ entity: 1, name: "Mira" }]);
    expect(await callAdmin("lab", "players")).toEqual([{ entity: 1, name: "Mira" }]);
    expect(fake.calls).toContainEqual(["call", fake.last!.session, "players", {}]);
    const before = fake.calls.length;
    await expect(callAdmin("lab", "teleport", { entity: 1 })).rejects.toMatchObject({ code: "unsupported" });
    expect(fake.calls.length).toBe(before);
  });

  test("a forgotten session (not_found) makes the hub connect again", async () => {
    acquireAdmin("lab");
    await settle();
    fake.answers.set("players", () => { throw { code: "not_found", message: "no admin session" }; });
    await expect(callAdmin("lab", "players")).rejects.toMatchObject({ code: "not_found" });
    expect(conn().status).toBe("error");
    expect(conn().retryAt).not.toBeNull();
    expect(fake.calls).toContainEqual(["disconnect", fake.last!.session]);
  });

  test("Reconnect over another kind closes the session first and asks the transport for that kind", async () => {
    acquireAdmin("lab");
    await settle();
    const first = fake.last!.session;
    reconnectAdmin("lab", "tcp");
    await settle();
    const connects = fake.calls.filter((c) => c[0] === "connect");
    expect(connects.at(-1)).toEqual(["connect", "lab", "tcp"]);
    const iDisc = fake.calls.findIndex((c) => c[0] === "disconnect" && c[1] === first);
    const iConn = fake.calls.lastIndexOf(connects.at(-1)!);
    expect(iDisc).toBeGreaterThanOrEqual(0);
    expect(iDisc).toBeLessThan(iConn);
    reconnectAdmin("lab", "auto");
    await settle();
  });

  test("Reconnect while connected survives the transport's closed {retry: false} for the session it ends", async () => {
    // the launcher's Rust hub and the WebSocket transport both say `closed` when a session is disconnected; the hub
    // ended that session itself, so it must not take it for the server going away (and drop the new session)
    for (const sync of [true, false]) {
      const t = fakeTransport();
      const disconnect = t.disconnect.bind(t);
      t.disconnect = async (session: string) => {
        await disconnect(session);
        const ev: AdminHubEvent = { session, serverId: "lab", event: { kind: "closed", reason: "disconnected", retry: false } };
        if (sync) t.emit(ev);
        else setTimeout(() => t.emit(ev), 0);
      };
      configureAdmin({ transport: t });
      acquireAdmin("lab");
      await settle();
      const first = t.last!.session;
      reconnectAdmin("lab");
      await settle();
      await settle();
      expect(conn().status).toBe("ready");
      expect(conn().session?.session).not.toBe(first);
      expect(conn().session?.session).toBe(t.last!.session);
      releaseAdmin("lab");
    }
  });

  test("a transport that cannot connect: the error, and no transport at all says so", async () => {
    configureAdmin({ transport: fakeTransport({ fail: { code: "admin_unavailable", message: "server stopped" } }) });
    acquireAdmin("lab");
    await settle();
    expect(conn().status).toBe("error");
    expect(conn().error).toMatchObject({ code: "admin_unavailable", message: "server stopped" });
    releaseAdmin("lab");
    configureAdmin({ transport: null });
    acquireAdmin("lab2");
    await settle();
    expect(conn("lab2").error?.code).toBe("admin_unavailable");
    releaseAdmin("lab2");
  });

  test("configuring another transport drops the old transport's sessions", async () => {
    acquireAdmin("lab");
    await settle();
    expect(conn().status).toBe("ready");
    const next = fakeTransport();
    configureAdmin({ transport: next });
    expect(conn()).toBeUndefined();
  });
});

describe("KAP ops and launcher actions stay apart", () => {
  test("kapOps.serverRestart is a KAP request on the session; the host's managed restart is not called", async () => {
    const managed: string[] = [];
    configureAdmin({ transport: fake, host: { restartManagedServer: async (id) => { managed.push(id); } } });
    acquireAdmin("lab");
    await settle();
    const kap = kapOps((op, args) => callAdmin("lab", op, args));
    await kap.serverRestart({ seconds: 30, message: "update" });
    expect(fake.calls).toContainEqual(["call", fake.last!.session, "server_restart", { seconds: 30, message: "update" }]);
    expect(managed).toEqual([]);
    // and the launcher action is the host's, by its own name, never an op
    await adminHost().restartManagedServer!("lab");
    expect(managed).toEqual(["lab"]);
    expect(fake.calls.filter((c) => c[0] === "call").map((c) => c[2])).toEqual(["server_restart"]);
  });

  test("the typed ops call the op names of the protocol", async () => {
    const seen: [string, Record<string, Json> | undefined][] = [];
    const kap = kapOps(async (op, args) => { seen.push([op, args]); return null as never; });
    await kap.serverRestartCancel();
    await kap.updateList();
    await kap.updateStage({ kind: "mod", source: "kiln-equipment-1.2.0" });
    await kap.updateCancel({ unit: "mod.kiln-equipment" });
    await kap.updateRollback({ unit: "kiln" });
    await kap.loginMint({ consumer: "kiln-webadmin" });
    await kap.loginRedeem({ consumer: "kiln-webadmin", secret: "ab".repeat(32) });
    await kap.adminInfo();
    expect(seen.map((s) => s[0])).toEqual(["server_restart_cancel", "update_list", "update_stage", "update_cancel", "update_rollback", "login_mint", "login_redeem", "admin_info"]);
    expect(Object.keys(kap)).not.toContain("restartManagedServer");
  });

  test("without a host the launcher-only actions are absent", () => {
    configureAdmin({ transport: fake });
    expect(adminHost().restartManagedServer).toBeUndefined();
    expect(adminHost().crashReportsFromDisk).toBeUndefined();
    expect(adminHost().assetsRoot).toBeUndefined();
    expect(adminHost().useArtStatus()).toBeUndefined();
    // the Map page without art keeps the launcher's words
    expect(adminHost().useMapNoArt({ canExtract: false })).toBeUndefined();
  });

  test("a host's words for a map without art reach the pages; a later host without them gets the launcher's back", () => {
    const words = { title: "This server has no map art yet", markers: "Copy it from the Kiln Launcher." };
    configureAdmin({ transport: fake, host: { useMapNoArt: (art) => (art?.canExtract === false ? words : undefined) } });
    expect(adminHost().useMapNoArt({ canExtract: false })).toEqual(words);
    expect(adminHost().useMapNoArt(undefined)).toBeUndefined();
    configureAdmin({ transport: fake, host: {} });
    expect(adminHost().useMapNoArt({ canExtract: false })).toBeUndefined();
  });
});
