// Admin pages dev server: the launcher on port 1421 with a dev-only KAP/1 bridge, so the admin
// pages run in a plain browser against a real Kiln.Admin (a lab) or tools/launcher/mock-admin.py before/without the
// Tauri backend. Never part of a production build (this config only adds a dev-server middleware).
//
//   KILN_ADMIN_SERVER=~/srv/enshrouded/labs/mylab bun x vite --config vite.admin.config.ts
//   KILN_ADMIN_SERVERS="lab=~/srv/enshrouded/labs/mylab,mock=/tmp/kap-mock" ...   (per server id)
//
// A server entry is a server folder (its kiln/data/kiln-admin/{endpoint.json,admin-token} are read at every connect)
// or HOST:PORT@TOKENFILE. The browser side is src/admin/mock/index.ts (bridge mode, VITE_ADMIN_BRIDGE=1): admin_*
// commands become POST /__kap/<command>, events come from GET /__kap/events (SSE) as `kiln://admin` payloads. The bridge
// behaves like the Rust AdminHub (one session per server, reconnect with backoff, `closed {retry}` then `ready`).
import { createHmac, randomBytes, timingSafeEqual } from "node:crypto";
import { readFileSync } from "node:fs";
import { connect, type Socket } from "node:net";
import { homedir } from "node:os";
import { join } from "node:path";
import type { IncomingMessage, ServerResponse } from "node:http";
import { defineConfig, mergeConfig, type Plugin } from "vite";
import base from "./vite.config.ts";

type Json = null | boolean | number | string | Json[] | { [k: string]: Json };
type Frame = { t: string; [k: string]: Json | undefined };

const expand = (p: string) => (p.startsWith("~/") ? join(homedir(), p.slice(2)) : p);

function serverMap(): Map<string, string> {
  const m = new Map<string, string>();
  for (const part of (process.env.KILN_ADMIN_SERVERS ?? "").split(",").map((s) => s.trim()).filter(Boolean)) {
    const [id, ...rest] = part.split("=");
    m.set(id, rest.join("="));
  }
  return m;
}

function endpointFor(serverId: string): { host: string; port: number; token: Buffer; name: string } {
  const spec = serverMap().get(serverId) ?? process.env.KILN_ADMIN_SERVER;
  if (!spec) throw { code: "admin_unavailable", message: `No KAP endpoint for server '${serverId}' (set KILN_ADMIN_SERVER or KILN_ADMIN_SERVERS).` };
  const direct = /^([\w.-]+):(\d+)@(.+)$/.exec(spec);
  if (direct) return { host: direct[1], port: Number(direct[2]), token: Buffer.from(readFileSync(expand(direct[3]), "utf8").trim(), "hex"), name: serverId };
  const dir = join(expand(spec), "kiln", "data", "kiln-admin");
  let ep: { host: string; port: number; server?: { name?: string } };
  try {
    ep = JSON.parse(readFileSync(join(dir, "endpoint.json"), "utf8"));
  } catch {
    throw { code: "admin_unavailable", message: `The server isn't running (no ${join(dir, "endpoint.json")}).` };
  }
  return { host: ep.host, port: ep.port, token: Buffer.from(readFileSync(join(dir, "admin-token"), "utf8").trim(), "hex"), name: ep.server?.name ?? serverId };
}

const mac = (token: Buffer, label: string, nonce: string, cnonce: string) => createHmac("sha256", token).update(`${label}\n${nonce}\n${cnonce}`).digest("hex");

/** One KAP/1 connection: handshake, req/res by id, pushes, ping/pong. */
class Kap {
  sock!: Socket;
  buf = Buffer.alloc(0);
  next = 1;
  waiting = new Map<number, (f: Frame) => void>();
  hello: Frame | null = null;
  ready: Frame | null = null;
  maxFrame = 16 << 20;
  lastSend = Date.now();
  ping: ReturnType<typeof setInterval> | null = null;
  onPush: (f: Frame) => void = () => {};
  onClose: (why: string) => void = () => {};
  closed = false;

  open(host: string, port: number, token: Buffer): Promise<Frame> {
    return new Promise((resolve, reject) => {
      let stage = 0;
      const fail = (e: unknown) => { if (stage < 2) { stage = 3; reject(e); } };
      this.sock = connect({ host, port }, () => undefined);
      this.sock.setTimeout(8000, () => { fail({ code: "timeout", message: "KAP handshake timed out" }); this.sock.destroy(); });
      this.sock.on("error", (e) => fail({ code: "network", message: `KAP ${host}:${port}: ${e.message}` }));
      this.sock.on("close", () => { this.closed = true; if (this.ping) clearInterval(this.ping); if (stage === 2) this.onClose("connection lost"); else fail({ code: "network", message: "KAP connection closed during the handshake" }); });
      this.sock.on("data", (d) => {
        this.buf = Buffer.concat([this.buf, d]);
        for (;;) {
          if (this.buf.length < 4) return;
          const n = this.buf.readUInt32LE(0);
          if (n > this.maxFrame) { this.sock.destroy(); return; }
          if (this.buf.length < 4 + n) return;
          const f = JSON.parse(this.buf.subarray(4, 4 + n).toString("utf8")) as Frame;
          this.buf = this.buf.subarray(4 + n);
          if (stage === 0 && f.t === "hello") {
            this.hello = f;
            const cnonce = randomBytes(32).toString("hex");
            (this as unknown as { cnonce: string }).cnonce = cnonce;
            this.send({ t: "auth", proto: "kap/1", cnonce, mac: mac(token, "kap1-auth", String(f.nonce), cnonce), client: "kiln-launcher-devbridge/0.2.0-dev" });
            stage = 1;
          } else if (stage === 1) {
            if (f.t !== "ready") return fail({ code: f.code === "auth_failed" ? "auth_failed" : "admin_unavailable", message: `KAP handshake refused: ${String(f.code)} ${String(f.message ?? "")}` });
            const want = mac(token, "kap1-ready", String(this.hello!.nonce), (this as unknown as { cnonce: string }).cnonce);
            if (!timingSafeEqual(Buffer.from(want), Buffer.from(String(f.mac)))) { this.sock.destroy(); return fail({ code: "auth_failed", message: "the server's ready mac is wrong" }); }
            this.ready = f;
            const lim = f.limits as { maxFrame?: number; pingS?: number } | undefined;
            this.maxFrame = lim?.maxFrame ?? this.maxFrame;
            this.sock.setTimeout(0);
            stage = 2;
            const pingS = (lim?.pingS ?? 15) * 1000;
            this.ping = setInterval(() => { if (Date.now() - this.lastSend > pingS) this.send({ t: "ping", id: this.next++, ts: Date.now() }); }, 2000);
            resolve(f);
          } else this.frame(f);
        }
      });
    });
  }
  frame(f: Frame) {
    if ((f.t === "res" || f.t === "error") && typeof f.id === "number" && this.waiting.has(f.id)) {
      const w = this.waiting.get(f.id)!;
      this.waiting.delete(f.id);
      w(f);
    } else if (f.t === "push") this.onPush(f);
    else if (f.t === "ping") this.send({ t: "pong", id: f.id ?? 0, ...(f.ts !== undefined ? { ts: f.ts } : {}) });
    else if (f.t === "bye") this.onClose(`bye: ${String(f.reason)}`);
  }
  send(f: Frame) {
    const data = Buffer.from(JSON.stringify(f), "utf8");
    const head = Buffer.alloc(4);
    head.writeUInt32LE(data.length, 0);
    this.sock.write(Buffer.concat([head, data]));
    this.lastSend = Date.now();
  }
  request(f: Frame, timeoutMs: number): Promise<Frame> {
    const id = this.next++;
    return new Promise((resolve, reject) => {
      const t = setTimeout(() => { this.waiting.delete(id); reject({ code: "timeout", message: `no answer to ${String(f.op ?? f.t)} within ${timeoutMs / 1000} s` }); }, timeoutMs);
      this.waiting.set(id, (r) => { clearTimeout(t); resolve(r); });
      this.send({ ...f, id });
    });
  }
  close() {
    this.closed = true;
    if (this.ping) clearInterval(this.ping);
    try { this.send({ t: "bye", reason: "client" }); } catch { /* gone */ }
    this.sock?.destroy();
  }
}

interface Session {
  id: string;
  serverId: string;
  kap: Kap | null;
  topics: Set<string>;
  info: { session: string; serverId: string; transport: "tcp"; ops: string[]; topics: string[]; kiln: string | null; server: Json };
  retry: ReturnType<typeof setTimeout> | null;
  attempt: number;
  gone: boolean;
}

const BUDGET: Record<string, number> = { mod_test: 180_000, backup_create: 90_000, save_now: 60_000 };
const budget = (op: string) => BUDGET[op] ?? (op.startsWith("mod_") ? 45_000 : 30_000);

function kapBridge(): Plugin {
  const sessions = new Map<string, Session>();
  const clients = new Set<ServerResponse>();
  const emit = (s: Session, event: Json) => {
    const line = `data: ${JSON.stringify({ session: s.id, serverId: s.serverId, event })}\n\n`;
    for (const c of clients) c.write(line);
  };
  async function open(s: Session) {
    const ep = endpointFor(s.serverId);
    const k = new Kap();
    const ready = await k.open(ep.host, ep.port, ep.token);
    s.kap = k;
    s.info.ops = (ready.ops as string[]) ?? [];
    s.info.topics = (ready.topics as string[]) ?? [];
    s.info.kiln = (k.hello?.kiln as string) ?? null;
    s.info.server = (k.hello?.server as Json) ?? { name: ep.name };
    k.onPush = (f) => emit(s, { kind: "push", topic: f.topic as string, seq: f.seq as number, data: f.data as Json });
    k.onClose = (why) => {
      if (s.gone || s.kap !== k) return;
      s.kap = null;
      emit(s, { kind: "closed", reason: why, retry: true });
      schedule(s);
    };
    for (const t of s.topics) k.request({ t: "sub", topic: t }, 5000).catch(() => undefined);
  }
  function schedule(s: Session) {
    const wait = [1, 2, 4, 8, 15, 30][Math.min(5, s.attempt++)] * 1000;
    s.retry = setTimeout(async () => {
      if (s.gone) return;
      try {
        await open(s);
        s.attempt = 0;
        emit(s, { kind: "ready", transport: "tcp", ops: s.info.ops, topics: s.info.topics });
      } catch {
        schedule(s);
      }
    }, wait);
  }
  const json = (res: ServerResponse, status: number, body: unknown) => {
    res.statusCode = status;
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify(body ?? null));
  };
  const body = (req: IncomingMessage) => new Promise<Record<string, unknown>>((resolve) => {
    let t = "";
    req.on("data", (c) => (t += c));
    req.on("end", () => { try { resolve(JSON.parse(t || "{}")); } catch { resolve({}); } });
  });
  const session = (id: unknown) => sessions.get(String(id)) ?? (() => { throw { code: "not_found", message: `no admin session '${String(id)}'` }; })();
  return {
    name: "kiln-kap-bridge",
    apply: "serve",
    configureServer(server) {
      server.middlewares.use(async (req, res, next) => {
        if (!req.url?.startsWith("/__kap/")) return next();
        const route = req.url.slice(7).split("?")[0];
        try {
          if (route === "events") {
            res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache", Connection: "keep-alive" });
            res.write(": kiln kap bridge\n\n");
            clients.add(res);
            const keep = setInterval(() => res.write(": keep-alive\n\n"), 15000);
            req.on("close", () => { clients.delete(res); clearInterval(keep); });
            return;
          }
          if (route === "sessions") return json(res, 200, [...sessions.values()].map((s) => s.info));
          const b = await body(req);
          if (route === "connect") {
            const serverId = String(b.serverId ?? "");
            const have = [...sessions.values()].find((s) => s.serverId === serverId && !s.gone);
            if (have) return json(res, 200, have.info);
            const s: Session = { id: `dev-${serverId}-${randomBytes(4).toString("hex")}`, serverId, kap: null, topics: new Set(), retry: null, attempt: 0, gone: false,
              info: { session: "", serverId, transport: "tcp", ops: [], topics: [], kiln: null, server: null } };
            s.info.session = s.id;
            await open(s);
            sessions.set(s.id, s);
            return json(res, 200, s.info);
          }
          if (route === "disconnect") {
            const s = sessions.get(String(b.session));
            if (s) { s.gone = true; if (s.retry) clearTimeout(s.retry); s.kap?.close(); sessions.delete(s.id); emit(s, { kind: "closed", reason: "disconnected", retry: false }); }
            return json(res, 200, null);
          }
          const s = session(b.session);
          if (!s.kap) throw { code: "admin_unavailable", message: "the admin session is reconnecting" };
          if (route === "subscribe" || route === "unsubscribe") {
            const topic = String(b.topic);
            const r = await s.kap.request({ t: route === "subscribe" ? "sub" : "unsub", topic }, 5000);
            if (!r.ok) throw { code: "op_failed", message: String(r.error ?? r.message ?? "refused"), detail: { kapCode: r.code ?? null } };
            if (route === "subscribe") s.topics.add(topic); else s.topics.delete(topic);
            return json(res, 200, null);
          }
          if (route === "call") {
            const op = String(b.op);
            const r = await s.kap.request({ t: "req", op, args: (b.args ?? {}) as Json }, budget(op) + 2000);
            if (r.t === "error") throw { code: "internal", message: `${String(r.code)}: ${String(r.message)}` };
            if (!r.ok) {
              const kapCode = String(r.code ?? "op_failed");
              const code = kapCode === "busy" ? "busy" : kapCode === "timeout" ? "timeout" : kapCode === "shutting_down" ? "admin_unavailable" : "op_failed";
              throw { code, message: String(r.error ?? "the op failed"), detail: { op, transport: "tcp", kapCode } };
            }
            return json(res, 200, r.data ?? null);
          }
          return json(res, 404, { code: "not_found", message: `no bridge route ${route}` });
        } catch (e) {
          const err = e && typeof e === "object" && "code" in e ? e : { code: "internal", message: String(e) };
          return json(res, 400, err);
        }
      });
    },
  };
}

export default mergeConfig(
  base,
  defineConfig({
    plugins: [kapBridge()],
    server: { port: 1421, strictPort: true },
    define: { "import.meta.env.VITE_ADMIN_BRIDGE": JSON.stringify(process.env.KILN_ADMIN_MOCK === "1" ? "" : "1") },
  }),
);
