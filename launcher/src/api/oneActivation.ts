// One profile activation per game at a time. The backend has no lock over a whole activation, so two overlapping ones
// finish in any order and the slower wins: a Load in Settings still running when a toast's "Load it" or Play is
// pressed. The guard sits in commands.ts around every command that writes the game's profile.json and returns only
// when it is done: profile_activate, profile_deactivate, profile_ensure_default, library_add_to_profile and
// launch_play (its refresh_active before the start). A second one for the same game while one runs is refused at once
// with a sentence.
//
// Not guarded: commands that return a task id at once and activate later inside the task (library_add's resume or
// first profile, modpack_install, runtime_install's resume, found_add): the guard would only cover the spawn; and
// library_take_over ("Use the changed files"), which names no game and re-applies wherever the profile is active.
// Two launcher windows each have their own map: accepted for 0.2.0-dev.27 (one window is the normal case); a lock in
// the backend over a whole activation is the follow-up.
import { t } from "../i18n";
import type { KilnError } from "./commands";

const running = new Map<string, Promise<unknown>>();

/** The refusal's sentence in the current language. */
export const activationBusy = (): string => t("api.oneActivation.busy");
/** The English sentence (en.json's api.oneActivation.busy), kept for unit tests; nothing compares it. */
export const ACTIVATION_BUSY = "Kiln is still loading a profile in this game. Try again when that is done.";

/** Runs `run` unless an activation of `gameId` is in flight; then rejects with a `busy` KilnError. */
export function oneActivation<T>(gameId: string, run: () => Promise<T>): Promise<T> {
  if (running.has(gameId)) return Promise.reject({ code: "busy", message: activationBusy() } satisfies KilnError);
  const p = run();
  running.set(gameId, p);
  const done = () => { if (running.get(gameId) === p) running.delete(gameId); };
  p.then(done, done);
  return p;
}

/** Told when the visible active identity changes, and after profile_activate/profile_deactivate succeeds: what
 *  belongs to an earlier profile ends there. The target identity lets a late completion preserve
 *  state already made in the new profile. Omitted identity explicitly resets all profile state. */
const switched = new Set<(gameId: string, profileId?: string | null) => void>();
export function onProfileSwitch(f: (gameId: string, profileId?: string | null) => void): () => void {
  switched.add(f);
  return () => { switched.delete(f); };
}
export function profileSwitched(gameId: string, profileId?: string | null): void {
  switched.forEach((f) => f(gameId, profileId));
}
