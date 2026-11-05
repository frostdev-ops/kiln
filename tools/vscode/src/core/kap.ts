// SPDX-License-Identifier: MIT
// KAP/1 client (docs/kiln/launcher/admin-protocol.md): u32 LE length + one JSON object per frame, HMAC-SHA256
// handshake keyed by kiln/data/kiln-admin/admin-token, req/res by id, sub/push topics, ping/pong, bye.
import * as crypto from "node:crypto";
import { EventEmitter } from "node:events";
import * as fs from "node:fs";
import * as net from "node:net";
import * as path from "node:path";

export const PROTO = "kap/1";

export interface Endpoint {
  format: number;
  protocol: string;
  host: string;
  port: number;
  pid?: number;
  started?: string;
  kiln?: string;
  admin?: string;
  server?: { id: string; name: string };
  token?: string;
  bridge?: string;
}

export interface Hello {
  t: "hello";
  proto: string;
  nonce: string;
  kiln?: string;
  admin?: string;
  server?: { id: string; name: string };
  auth?: string[];
}

export interface Ready {
  t: "ready";
  session: string;
  mac: string;
  ops: string[];
  topics: string[];
  limits?: { maxFrame?: number; maxClientFrame?: number; inflight?: number; pingS?: number; idleS?: number };
}

export interface Res {
  t: "res";
  id: number;
  ok: boolean;
  op: string;
  ms?: number;
  data?: unknown;
  error?: string;
  code?: string;
}

export class KapError extends Error {
  constructor(
    message: string,
    readonly code: string,
  ) {
    super(message);
  }
}

/** `<server>/kiln/data/kiln-admin/{endpoint.json, admin-token}`. */
export function adminDir(serverDir: string): string {
  return path.join(serverDir, "kiln", "data", "kiln-admin");
}

export function readToken(file: string): Buffer {
  const hex = fs.readFileSync(file, "utf8").trim();
  if (!/^[0-9a-f]{64}$/.test(hex)) throw new KapError(`${file}: not 64 lower-case hex characters`, "bad_token");
  return Buffer.from(hex, "hex");
}

export function readEndpoint(serverDir: string): { endpoint: Endpoint; token: Buffer } {
  const dir = adminDir(serverDir);
  const epFile = path.join(dir, "endpoint.json");
  if (!fs.existsSync(epFile)) {
    throw new KapError(
      `${epFile} does not exist: the server is not running, or Kiln.Admin has KAP off ("tcp": {"enabled": false})`,
      "no_endpoint",
    );
  }
  const endpoint = JSON.parse(fs.readFileSync(epFile, "utf8")) as Endpoint;
  if (endpoint.protocol && endpoint.protocol !== PROTO) {
    throw new KapError(`endpoint speaks ${endpoint.protocol}, this client ${PROTO}`, "version");
  }
  const token = readToken(path.join(dir, endpoint.token || "admin-token"));
  return { endpoint, token };
}

export function mac(token: Buffer, label: "kap1-auth" | "kap1-ready", nonce: string, cnonce: string): string {
  return crypto.createHmac("sha256", token).update(`${label}\n${nonce}\n${cnonce}`, "utf8").digest("hex");
}

export function encodeFrame(obj: unknown): Buffer {
  const payload = Buffer.from(JSON.stringify(obj), "utf8");
  const head = Buffer.alloc(4);
  head.writeUInt32LE(payload.length, 0);
  return Buffer.concat([head, payload]);
}

/** Splits a byte stream into frames. */
export class FrameReader {
  private buf: Buffer = Buffer.alloc(0);
  constructor(public maxFrame = 16 << 20) {}

  push(chunk: Buffer): Record<string, unknown>[] {
    this.buf = this.buf.length ? Buffer.concat([this.buf, chunk]) : chunk;
    const out: Record<string, unknown>[] = [];
    while (this.buf.length >= 4) {
      const n = this.buf.readUInt32LE(0);
      if (n === 0) throw new KapError("zero-length frame", "bad_frame");
      if (n > this.maxFrame) throw new KapError(`frame of ${n} bytes (limit ${this.maxFrame})`, "too_large");
      if (this.buf.length < 4 + n) break;
      const payload = this.buf.subarray(4, 4 + n).toString("utf8");
      this.buf = this.buf.subarray(4 + n);
      const obj = JSON.parse(payload);
      if (typeof obj !== "object" || obj === null || typeof obj.t !== "string") {
        throw new KapError("frame without a type", "bad_frame");
      }
      out.push(obj);
    }
    return out;
  }
}

interface Pending {
  resolve: (r: Res) => void;
  reject: (e: Error) => void;
  timer: NodeJS.Timeout;
}

export interface ConnectOptions {
  client?: string;
  handshakeTimeoutMs?: number;
  /** test hook: a fixed client nonce */
  cnonce?: string;
}

/**
 * Events: "push" (topic, data, seq), "bye" (reason), "close" (reason), "error" (Error).
 */
export class KapClient extends EventEmitter {
  hello!: Hello;
  ready!: Ready;
  private nextId = 1;
  private pending = new Map<number, Pending>();
  private reader = new FrameReader();
  private lastSend = Date.now();
  private pinger?: NodeJS.Timeout;
  private closed = false;

  private constructor(private readonly sock: net.Socket) {
    super();
  }

  static connect(host: string, port: number, token: Buffer, o: ConnectOptions = {}): Promise<KapClient> {
    return new Promise((resolve, reject) => {
      const sock = net.createConnection({ host, port });
      const c = new KapClient(sock);
      let stage: "hello" | "ready" | "open" = "hello";
      let cnonce = "";
      const fail = (e: Error) => {
        clearTimeout(timer);
        sock.destroy();
        reject(e);
      };
      const timer = setTimeout(
        () => fail(new KapError("KAP/1 handshake timed out", "timeout")),
        o.handshakeTimeoutMs ?? 5000,
      );
      sock.on("error", (e: NodeJS.ErrnoException) => {
        if (stage !== "open") {
          fail(
            e.code === "ECONNREFUSED"
              ? new KapError(`nothing listens on ${host}:${port} (a stale endpoint.json: the server is not running)`, "refused")
              : e,
          );
        } else c.emit("error", e);
      });
      sock.on("close", () => {
        if (stage !== "open") fail(new KapError("connection closed during the handshake", "closed"));
        else c.onClose("closed");
      });
      sock.on("data", (chunk: Buffer) => {
        let frames;
        try {
          frames = c.reader.push(chunk);
        } catch (e) {
          if (stage !== "open") fail(e as Error);
          else {
            c.emit("error", e);
            c.close();
          }
          return;
        }
        for (const f of frames) {
          if (stage === "open") {
            c.onFrame(f);
            continue;
          }
          if (f.t === "error") {
            fail(new KapError(String(f.message ?? f.code), String(f.code)));
            return;
          }
          if (stage === "hello" && f.t === "hello") {
            const h = f as unknown as Hello;
            if (h.proto !== PROTO) {
              fail(new KapError(`server speaks ${h.proto}`, "version"));
              return;
            }
            c.hello = h;
            cnonce = o.cnonce ?? crypto.randomBytes(32).toString("hex");
            c.send({ t: "auth", proto: PROTO, cnonce, mac: mac(token, "kap1-auth", h.nonce, cnonce), client: o.client ?? "kiln-vscode" });
            stage = "ready";
          } else if (stage === "ready" && f.t === "ready") {
            const r = f as unknown as Ready;
            const want = Buffer.from(mac(token, "kap1-ready", c.hello.nonce, cnonce), "hex");
            const got = Buffer.from(String(r.mac ?? ""), "hex");
            if (got.length !== want.length || !crypto.timingSafeEqual(got, want)) {
              fail(new KapError("the server's ready mac does not match the token", "auth_failed"));
              return;
            }
            c.ready = r;
            if (r.limits?.maxFrame) c.reader.maxFrame = Math.max(r.limits.maxFrame, 1 << 20);
            stage = "open";
            clearTimeout(timer);
            c.startPinger((r.limits?.pingS ?? 15) * 1000);
            resolve(c);
          } else {
            fail(new KapError(`unexpected ${String(f.t)} frame during the handshake`, "bad_frame"));
            return;
          }
        }
      });
    });
  }

  get isOpen(): boolean {
    return !this.closed;
  }

  hasOp(op: string): boolean {
    return this.ready?.ops?.includes(op) ?? false;
  }

  /** One request. Resolves with the res frame (ok or not); rejects only on transport problems or a local timeout. */
  request(op: string, args: Record<string, unknown> = {}, timeoutMs = 60_000): Promise<Res> {
    return this.call({ t: "req", op, args }, timeoutMs);
  }

  async subscribe(topic: string): Promise<Res> {
    return this.call({ t: "sub", topic }, 10_000);
  }

  async unsubscribe(topic: string): Promise<Res> {
    return this.call({ t: "unsub", topic }, 10_000);
  }

  close(reason = "client"): void {
    if (this.closed) return;
    try {
      this.send({ t: "bye", reason });
    } catch {
      /* closing anyway */
    }
    this.sock.end();
    this.onClose(reason);
  }

  private call(frame: Record<string, unknown>, timeoutMs: number): Promise<Res> {
    if (this.closed) return Promise.reject(new KapError("connection closed", "closed"));
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new KapError(`no answer to ${String(frame.op ?? frame.t)} within ${Math.round(timeoutMs / 1000)} s`, "timeout"));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.send({ ...frame, id });
    });
  }

  private send(obj: unknown) {
    this.lastSend = Date.now();
    this.sock.write(encodeFrame(obj));
  }

  private startPinger(everyMs: number) {
    this.pinger = setInterval(() => {
      if (Date.now() - this.lastSend >= everyMs) this.send({ t: "ping", id: this.nextId++, ts: Date.now() });
    }, Math.max(1000, Math.floor(everyMs / 3)));
    this.pinger.unref?.();
  }

  private onFrame(f: Record<string, unknown>) {
    switch (f.t) {
      case "res": {
        const p = this.pending.get(f.id as number);
        if (!p) return;
        this.pending.delete(f.id as number);
        clearTimeout(p.timer);
        p.resolve(f as unknown as Res);
        return;
      }
      case "push":
        this.emit("push", f.topic, f.data, f.seq);
        return;
      case "ping":
        this.send({ t: "pong", id: f.id, ts: f.ts });
        return;
      case "pong":
        return;
      case "bye":
        this.emit("bye", f.reason);
        this.sock.end();
        this.onClose(String(f.reason ?? "bye"));
        return;
      case "error": {
        const e = new KapError(String(f.message ?? f.code), String(f.code));
        const p = f.id != null ? this.pending.get(f.id as number) : undefined;
        if (p) {
          this.pending.delete(f.id as number);
          clearTimeout(p.timer);
          p.reject(e);
        } else this.emit("error", e);
        return;
      }
      default:
        return; // unknown frame types are ignored (kap/1 additions)
    }
  }

  private onClose(reason: string) {
    if (this.closed) return;
    this.closed = true;
    if (this.pinger) clearInterval(this.pinger);
    for (const p of this.pending.values()) {
      clearTimeout(p.timer);
      p.reject(new KapError(`connection closed (${reason})`, "closed"));
    }
    this.pending.clear();
    this.emit("close", reason);
  }
}

/** Connects to the server in `serverDir` through its endpoint.json and admin-token. */
export async function connectServer(serverDir: string, o: ConnectOptions = {}): Promise<KapClient> {
  const { endpoint, token } = readEndpoint(serverDir);
  return KapClient.connect(endpoint.host || "127.0.0.1", endpoint.port, token, o);
}
