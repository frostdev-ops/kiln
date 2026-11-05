// admin_* commands in a plain browser. src/api/mock.ts registers these handlers, so the admin
// pages run without the Tauri backend:
//   - default: an in-memory Kiln.Admin (./world.ts) per server id, from results captured on a real Kiln.Admin;
//     the server id "offline" refuses to connect (to see the offline state);
//   - `vite --config vite.admin.config.ts` (port 1421) sets VITE_ADMIN_BRIDGE: the commands go to its dev-only KAP/1
//     bridge (/__kap/*), which speaks KAP/1 to a real Kiln.Admin or to tools/launcher/mock-admin.py.
// Only types come from ../../api (mock.ts imports this module).
import type { AdminSession, Json, KilnError } from "../../api/commands";
import { MockWorld, OpError, type Fixture } from "./world";

type Handler = (args: Record<string, unknown>) => unknown;
type Emit = (name: string, payload: unknown) => void;

const fail = (code: KilnError["code"], message: string, detail?: unknown): never => {
  throw { code, message, ...(detail !== undefined ? { detail } : {}) } satisfies KilnError;
};

// The committed synthetic fixture (real shapes, invented game content: tools/launcher/mock-admin.py synthesize); a local
// capture of a real Kiln.Admin (game names, gitignored: launcher/assets-local/fixtures/kiln-admin.capture.json, made by
// `mock-admin.py capture`) is used instead when the dev server has one.
let fixture: Promise<Fixture> | null = null;
const loadFixture = () =>
  (fixture ??= (async () => {
    try {
      const r = await fetch("/assets-local/fixtures/kiln-admin.capture.json");
      if (r.ok && (r.headers.get("content-type") ?? "").includes("json")) return (await r.json()) as Fixture;
    } catch {
      /* no capture: synthetic */
    }
    const m = await import("./fixtures/synthetic.json");
    return (m.default ?? m) as unknown as Fixture;
  })());

interface MockSession {
  id: string;
  serverId: string;
  world: MockWorld;
  topics: Set<string>;
  seq: Record<string, number>;
  logSent: number;
}

/** The handlers for src/api/mock.ts. */
export function adminMockHandlers(emit: Emit): Record<string, Handler> {
  if (import.meta.env.VITE_ADMIN_BRIDGE) return bridgeHandlers(emit);
  return cannedHandlers(emit);
}

// ---------------------------------------------------------------- canned (in-memory) Kiln.Admin

function cannedHandlers(emit: Emit): Record<string, Handler> {
  const worlds = new Map<string, MockWorld>();
  const sessions = new Map<string, MockSession>();
  let n = 0;
  let timer: ReturnType<typeof setInterval> | null = null;

  const tick = () => {
    for (const w of worlds.values()) w.tick();
    for (const s of sessions.values()) {
      if (s.topics.has("state")) {
        s.seq.state = (s.seq.state ?? 0) + 1;
        emit("kiln://admin", { session: s.id, serverId: s.serverId, event: { kind: "push", topic: "state", seq: s.seq.state, data: s.world.state() } });
      }
      if (s.topics.has("log") && s.world.log.length > s.logSent) {
        const lines = s.world.log.slice(s.logSent);
        s.logSent = s.world.log.length;
        s.seq.log = (s.seq.log ?? 0) + 1;
        emit("kiln://admin", { session: s.id, serverId: s.serverId, event: { kind: "push", topic: "log", seq: s.seq.log, data: { lines } } });
      }
    }
  };
  const session = (a: Record<string, unknown>) => sessions.get(String(a.session)) ?? fail("not_found", `no admin session '${String(a.session)}'`);
  const info = (s: MockSession): AdminSession => ({
    session: s.id, serverId: s.serverId, transport: "tcp", ops: [...s.world.ops], topics: ["state", "log"], kiln: s.world.kiln,
    server: { id: s.world.fx.server?.id ?? s.serverId, name: s.world.fx.server?.name ?? s.serverId },
  });

  return {
    admin_connect: async (a) => {
      const serverId = String(a.serverId ?? "");
      if (!serverId) fail("invalid_argument", "serverId required");
      if (serverId === "offline") fail("admin_unavailable", "The server isn't running (no endpoint.json, no file bridge answer).");
      const fx = await loadFixture();
      let w = worlds.get(serverId);
      if (!w) worlds.set(serverId, (w = new MockWorld(fx)));
      const s: MockSession = { id: `mock-${serverId}-${++n}`, serverId, world: w, topics: new Set(), seq: {}, logSent: w.log.length };
      sessions.set(s.id, s);
      timer ??= setInterval(tick, 1000);
      return info(s);
    },
    admin_disconnect: (a) => {
      sessions.delete(String(a.session));
      return null;
    },
    admin_sessions: () => [...sessions.values()].map(info),
    admin_call: async (a) => {
      const s = session(a);
      const op = String(a.op);
      const args = (a.args ?? {}) as Record<string, unknown>;
      if (!s.world.ops.includes(op)) fail("op_failed", `unknown op '${op}'`, { code: "unknown_op" });
      await new Promise((r) => setTimeout(r, s.world.slow(op)));
      try {
        return s.world.run(op, { ...args, op }) as Json;
      } catch (e) {
        if (e instanceof OpError) fail("op_failed", e.message, { code: "op_failed" });
        fail("internal", e instanceof Error ? e.message : String(e));
      }
    },
    admin_subscribe: (a) => {
      const s = session(a);
      const topic = String(a.topic);
      if (topic !== "state" && topic !== "log") fail("op_failed", `no topic '${topic}' (topics: state, log)`, { code: "unknown_topic" });
      s.topics.add(topic);
      if (topic === "state") setTimeout(tick, 50);  // a first push right away, like a fresh subscription on the next tick
      return null;
    },
    admin_unsubscribe: (a) => {
      session(a).topics.delete(String(a.topic));
      return null;
    },
  };
}

// ---------------------------------------------------------------- dev bridge (vite.admin.config.ts)

function bridgeHandlers(emit: Emit): Record<string, Handler> {
  let events: EventSource | null = null;
  const post = async (path: string, body: unknown) => {
    const r = await fetch(`/__kap/${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const d = await r.json().catch(() => ({ code: "internal", message: `bridge answered ${r.status}` }));
    if (!r.ok) throw d as KilnError;
    return d;
  };
  const listen = () => {
    if (events) return;
    events = new EventSource("/__kap/events");
    events.onmessage = (m) => {
      try {
        emit("kiln://admin", JSON.parse(m.data));
      } catch {
        /* keep-alive */
      }
    };
  };
  return {
    admin_connect: async (a) => {
      listen();
      return post("connect", { serverId: a.serverId, transport: a.transport });
    },
    admin_disconnect: (a) => post("disconnect", { session: a.session }),
    admin_sessions: async () => (await fetch("/__kap/sessions")).json(),
    admin_call: (a) => post("call", { session: a.session, op: a.op, args: a.args ?? {} }),
    admin_subscribe: (a) => post("subscribe", { session: a.session, topic: a.topic }),
    admin_unsubscribe: (a) => post("unsubscribe", { session: a.session, topic: a.topic }),
  };
}
