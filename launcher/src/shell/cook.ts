// Cooking mods from the launcher (cook_plan, cook_run; docs/kiln/launcher/app.md "Cooking mods"). A mod with a cook
// recipe has files that are made on this PC from this game: models, animations, sounds. The launcher makes them before
// the game starts when a mod needs it (it was installed, updated or switched on, the game was updated, its last cook
// failed), shows what the tools are doing as a task, and never lets a cook stand in the way: a mod that could not be
// cooked is named with the tools' own words, the other mods are cooked, and the game starts. "Cook again" in a mod's
// menu cooks that one mod whenever the player asks.
import * as api from "../api/commands";
import type { CookJob, CookRun, GameId, ModCook } from "../api/commands";
import { toKilnError } from "../api/commands";
import { toast, waitTask } from "../ui";
import { bootSnapshotLine } from "./bootSnapshot";
import { notifyActivation } from "./notices";

/** Why a mod is cooked, as the end of "… because …" (kiln_manage::cook::CookReason::text). */
export const COOK_REASON: Record<NonNullable<CookJob["reason"]>, string> = {
  notCooked: "it was not cooked for this game yet",
  modChanged: "the mod changed since its last cook",
  gameChanged: "the game was updated since its last cook",
  lastFailed: "its last cook failed",
  filesGone: "its cooked files are gone or were changed",
  asked: "you asked",
};

/** One mod that was not cooked: its name, then what the tools said last (their own lines, kept as lines). */
export const notCookedText = (m: ModCook) => `${m.name}:\n${m.error ?? "the cook failed without a word"}`;

/** What a cook run left to tell: nothing when every mod was cooked; else one notice that stays, naming each mod that
 *  was not cooked with the tools' text. The mod keeps the files it has (those of its last cook: a failed cook puts them
 *  back), or runs without them when it was never cooked here (no models of its own, its own
 *  animations refused) until a cook goes through. */
export function notifyCook(run: CookRun | null | undefined, o: { quietOk?: boolean } = {}) {
  if (!run?.mods.length) return;
  const failed = run.mods.filter((m) => !m.ok);
  const ok = run.mods.length - failed.length;
  if (!failed.length) {
    if (!o.quietOk) toast.ok(ok === 1 ? `${run.mods[0].name} cooked` : `${ok} mods cooked`, "Their models, animations and sounds are made for this game.");
    return;
  }
  const title = failed.length === 1 ? `${failed[0].name} was not cooked` : `${failed.length} mods were not cooked`;
  toast.info(title, `${failed.map(notCookedText).join("\n\n")}\n\n${ok ? `${ok} other mod${ok === 1 ? " was" : "s were"} cooked. ` : ""}The game starts all the same, and a mod that could not be cooked keeps running on the files it has: those of its last cook, or none if it was never cooked here. "Cook again" in the mod's menu tries again.`, { ms: 0 });
}

/** What a start says when its cook has mods to cook (Play, and a modpack offer's Launch). */
export const cookingNotice = (jobs: CookJob[]) =>
  toast.info(jobs.length === 1 ? `Cooking ${jobs[0].name}` : `Cooking ${jobs.length} mods`, "Making their models, animations and sounds from your game, then the game starts.");

/** Cooks what needs a cook before the game starts. Resolves when it is done, whatever happened: a cook never stops a
 *  start. The cook runs always: it applies the active profile first when it changed (kilnctl, another window), so the
 *  mods it cooks are the ones the start loads, and ends at once when nothing is due. `started` is told when the plan
 *  shows mods to cook (Play says "Cooking"). */
export async function cookBeforePlay(gameId: GameId, started?: (jobs: CookJob[]) => void): Promise<CookRun | null> {
  try {
    const due = (await api.cookPlan(gameId).catch(() => [] as CookJob[])).filter((j) => j.reason);
    if (due.length) started?.(due);
    const run = await waitTask<CookRun>(await api.cookRun(gameId, null));
    void notifyActivation(run?.activation);
    notifyCook(run, { quietOk: true });
    const line = bootSnapshotLine(run?.bootSnapshot);
    if (line) toast.info("Game data for OnBoot mods", line, { ms: 0 });
    return run;
  } catch (e) {
    const k = toKilnError(e);
    // the game is running (it started outside the launcher), or the cook could not even be asked for: say it once, go on
    if (k.code !== "canceled" && k.code !== "game_running") toast.info("Mods were not cooked", `${k.message} The game starts all the same.`, { ms: 0 });
    return null;
  }
}

/** "Cook again": one mod, whatever its record says. */
export async function cookAgain(gameId: GameId, id: string, name: string): Promise<CookRun | null> {
  try {
    const run = await waitTask<CookRun>(await api.cookRun(gameId, id));
    void notifyActivation(run?.activation);
    notifyCook(run);
    return run;
  } catch (e) {
    if (toKilnError(e).code !== "canceled") toast.error(e, `${name} was not cooked`);
    return null;
  }
}
