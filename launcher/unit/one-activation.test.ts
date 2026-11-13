// review finding 3: two activations of one game must not overlap (the slower would win); the second is refused at
// once with a sentence while the first runs, and another game is not held up (src/api/oneActivation.ts)
import { expect, test } from "bun:test";
import { ACTIVATION_BUSY, oneActivation } from "../src/api/oneActivation";

test("a second activation of the same game while one runs is refused; after it, a new one runs", async () => {
  let finish!: (v: string) => void;
  const first = oneActivation("g", () => new Promise<string>((ok) => { finish = ok; }));
  let ran = false;
  await expect(oneActivation("g", async () => { ran = true; return "q"; })).rejects.toEqual({ code: "busy", message: ACTIVATION_BUSY });
  expect(ran).toBe(false);
  expect(await oneActivation("other", async () => "x")).toBe("x");
  finish("p");
  expect(await first).toBe("p");
  expect(await oneActivation("g", async () => "next")).toBe("next");
});

test("a failed activation frees the game too", async () => {
  await expect(oneActivation("h", () => Promise.reject(new Error("no")))).rejects.toThrow("no");
  expect(await oneActivation("h", async () => 1)).toBe(1);
});
