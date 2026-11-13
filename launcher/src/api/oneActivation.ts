// One profile activation per game at a time (profile_activate, profile_deactivate). The backend has no lock over a
// whole activation, so two overlapping ones finish in any order and the slower wins: a Load in Settings still running
// when a toast's "Load it" is pressed, or two windows of the same view. Every caller goes through commands.ts, so the
// guard sits there: a second activation of the same game while one runs is refused at once with a sentence.
import type { KilnError } from "./commands";

const running = new Map<string, Promise<unknown>>();

export const ACTIVATION_BUSY = "Kiln is still loading a profile in this game. Try again when that is done.";

/** Runs `run` unless an activation of `gameId` is in flight; then rejects with a `busy` KilnError. */
export function oneActivation<T>(gameId: string, run: () => Promise<T>): Promise<T> {
  if (running.has(gameId)) return Promise.reject({ code: "busy", message: ACTIVATION_BUSY } satisfies KilnError);
  const p = run();
  running.set(gameId, p);
  const done = () => { if (running.get(gameId) === p) running.delete(gameId); };
  p.then(done, done);
  return p;
}
