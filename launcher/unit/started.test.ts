// The page tells the backend once that its first screen is up (src/shell/started.ts; the backend's render watchdog
// falls back to software mode on Linux when that call never comes).
import { expect, test } from "bun:test";
import { startedReporter } from "../src/shell/started";

function harness() {
  const sent: number[] = [];
  const frames: (() => void)[] = [];
  const tell = startedReporter(async () => { sent.push(sent.length + 1); }, (cb) => { frames.push(cb); });
  const frame = () => frames.shift()?.();
  return { sent, frames, tell, frame };
}

test("the ready call goes out once, however often the first commit's effect runs", async () => {
  const h = harness();
  // React's StrictMode runs a mount effect twice in development; a remount runs it again
  h.tell(); h.tell(); h.tell();
  expect(h.frames.length).toBe(1);
  h.frame(); h.frame();
  await Promise.resolve();
  expect(h.sent).toEqual([1]);
  h.tell();
  expect(h.frames.length).toBe(0);
  await Promise.resolve();
  expect(h.sent).toEqual([1]);
});

test("the ready call waits for two frames after the commit (the first screen was painted)", async () => {
  const h = harness();
  h.tell();
  await Promise.resolve();
  expect(h.sent).toEqual([]);
  h.frame();
  await Promise.resolve();
  expect(h.sent).toEqual([]);
  h.frame();
  await Promise.resolve();
  expect(h.sent).toEqual([1]);
});

test("a page hidden at the start says it started at once, without waiting for frames", async () => {
  const sent: number[] = [];
  const frames: (() => void)[] = [];
  const tell = startedReporter(async () => { sent.push(1); }, (cb) => { frames.push(cb); }, () => true);
  tell(); tell();
  await Promise.resolve();
  expect(sent).toEqual([1]);
  expect(frames.length).toBe(0);
});

test("a page hidden before its frames came says it started then, and only once", async () => {
  const sent: number[] = [];
  const frames: (() => void)[] = [];
  let hide: (() => void) | undefined;
  const tell = startedReporter(async () => { sent.push(1); }, (cb) => { frames.push(cb); }, () => false, (cb) => { hide = cb; });
  tell();
  await Promise.resolve();
  expect(sent).toEqual([]);
  hide?.();
  await Promise.resolve();
  expect(sent).toEqual([1]);
  // the frames come when it is shown again: no second call
  frames.shift()?.(); frames.shift()?.();
  hide?.();
  await Promise.resolve();
  expect(sent).toEqual([1]);
});

test("a failed ready call is not retried and does not throw", async () => {
  let calls = 0;
  const tell = startedReporter(() => { calls += 1; return Promise.reject(new Error("no backend")); }, (cb) => cb());
  expect(() => { tell(); tell(); }).not.toThrow();
  await new Promise((r) => setTimeout(r, 0));
  expect(calls).toBe(1);
});
