// SPDX-License-Identifier: MIT
// KAP/1 client: the handshake test vector (schema/fixtures/kap/handshake.json) and a session against the launcher's
// mock Kiln.Admin (tools/launcher/mock-admin.py serve), which answers every op with the real shapes.
import * as assert from "node:assert/strict";
import { ChildProcess, spawn } from "node:child_process";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { connectServer, encodeFrame, FrameReader, KapClient, KapError, mac, readEndpoint } from "../../src/core/kap";
import { colorLogLine, parseOpArgs } from "../../src/core/consoleFormat";

const EXT = path.resolve(__dirname, "../../..");
const REPO = path.resolve(EXT, "../..");
const VECTOR = path.join(REPO, "schema", "fixtures", "kap", "handshake.json");
const MOCK = path.join(REPO, "tools", "launcher", "mock-admin.py");

suite("KAP/1 framing and MACs", () => {
  test("the test vector", function () {
    if (!fs.existsSync(VECTOR)) this.skip();
    const v = JSON.parse(fs.readFileSync(VECTOR, "utf8"));
    const token = Buffer.from(v.token, "hex");
    assert.equal(mac(token, "kap1-auth", v.nonce, v.cnonce), v.mac);
    assert.equal(mac(token, "kap1-ready", v.nonce, v.cnonce), v.serverMac);
    assert.equal(encodeFrame(JSON.parse(v.helloPayload)).toString("hex"), v.helloFrameHex);
  });
  test("frames split across chunks, several per chunk", () => {
    const a = encodeFrame({ t: "res", id: 1, ok: true, op: "ping" });
    const b = encodeFrame({ t: "push", topic: "log", seq: 1, data: { lines: ["é🔥"] } });
    const all = Buffer.concat([a, b]);
    const r = new FrameReader();
    assert.deepEqual(r.push(all.subarray(0, 3)), []);
    const got = r.push(all.subarray(3, a.length + 5));
    assert.equal(got.length, 1);
    const rest = r.push(all.subarray(a.length + 5));
    assert.deepEqual((rest[0].data as { lines: string[] }).lines, ["é🔥"]);
  });
  test("zero and oversize frames are errors", () => {
    assert.throws(() => new FrameReader().push(Buffer.from([0, 0, 0, 0])), (e: KapError) => e.code === "bad_frame");
    const big = Buffer.alloc(4);
    big.writeUInt32LE(32 << 20);
    assert.throws(() => new FrameReader().push(big), (e: KapError) => e.code === "too_large");
  });
});

suite("console helpers", () => {
  test("op arguments", () => {
    assert.deepEqual(parseOpArgs('entity=5 x=100 safe=true name="a b" pos={"x":1}'), { entity: 5, x: 100, safe: true, name: "a b", pos: { x: 1 } });
    assert.deepEqual(parseOpArgs('{"q":"sword"}'), { q: "sword" });
    assert.deepEqual(parseOpArgs(""), {});
  });
  test("log colors", () => {
    assert.match(colorLogLine("[E 00:00:00.000] [host] boom"), /^\x1b\[31m/);
    assert.match(colorLogLine("[I 00:00:00.000] [mod:x] PASS A.B"), /\x1b\[32mPASS/);
  });
});

suite("KAP/1 against mock-admin.py", function () {
  this.timeout(20000);
  let proc: ChildProcess | undefined;
  let dir = "";

  suiteSetup(async function () {
    if (!fs.existsSync(MOCK)) this.skip();
    dir = fs.mkdtempSync(path.join(os.tmpdir(), "kiln-kap-"));
    proc = spawn("python3", [MOCK, "serve", "--port", "0", "--dir", dir], { stdio: ["ignore", "pipe", "pipe"] });
    const ep = path.join(dir, "kiln", "data", "kiln-admin", "endpoint.json");
    const t0 = Date.now();
    while (!fs.existsSync(ep)) {
      if (Date.now() - t0 > 10000) throw new Error("mock-admin did not write endpoint.json");
      await new Promise((r) => setTimeout(r, 100));
    }
  });

  suiteTeardown(() => {
    proc?.kill("SIGTERM");
  });

  test("handshake, ops, console line, completion, unknown op, subscription", async () => {
    const c = await connectServer(dir, { client: "kiln-vscode-test" });
    try {
      assert.equal(c.hello.proto, "kap/1");
      assert.ok(c.ready.ops.includes("command_run"));
      assert.ok(c.ready.topics.includes("log"));
      const ping = await c.request("ping");
      assert.equal(ping.ok, true);
      const run = await c.request("command_run", { line: "help" });
      assert.equal(run.ok, true);
      assert.ok(Array.isArray((run.data as { output: string[] }).output));
      const comp = await c.request("command_complete", { text: "/he" });
      assert.ok(Array.isArray((comp.data as { suggestions: string[] }).suggestions));
      const bad = await c.request("no_such_op_here");
      assert.equal(bad.ok, false);
      assert.equal(bad.code, "unknown_op");
      const pushes: unknown[] = [];
      c.on("push", (topic: string, data: unknown) => topic === "state" && pushes.push(data));
      const sub = await c.subscribe("state");
      assert.equal(sub.ok, true);
      const t0 = Date.now();
      while (!pushes.length && Date.now() - t0 < 5000) await new Promise((r) => setTimeout(r, 50));
      assert.ok(pushes.length >= 1, "a state push right after sub");
      const unknown = await c.subscribe("nope");
      assert.equal(unknown.code, "unknown_topic");
    } finally {
      c.close();
    }
    assert.equal(c.isOpen, false);
  });

  test("a wrong token is refused (auth_failed)", async () => {
    const { endpoint } = readEndpoint(dir);
    await assert.rejects(
      KapClient.connect(endpoint.host, endpoint.port, Buffer.alloc(32, 7), { handshakeTimeoutMs: 4000 }),
      (e: KapError) => e.code === "auth_failed",
    );
  });

  test("a stale endpoint (nothing listening) says so", async () => {
    const d = fs.mkdtempSync(path.join(os.tmpdir(), "kiln-kap-stale-"));
    const a = path.join(d, "kiln", "data", "kiln-admin");
    fs.mkdirSync(a, { recursive: true });
    fs.writeFileSync(path.join(a, "admin-token"), "00".repeat(32) + "\n");
    fs.writeFileSync(path.join(a, "endpoint.json"), JSON.stringify({ format: 1, protocol: "kap/1", host: "127.0.0.1", port: 1, token: "admin-token" }));
    await assert.rejects(connectServer(d), (e: KapError) => e.code === "refused");
  });

  test("no endpoint.json", async () => {
    assert.throws(() => readEndpoint(fs.mkdtempSync(path.join(os.tmpdir(), "kiln-kap-none-"))), (e: KapError) => e.code === "no_endpoint");
  });
});
