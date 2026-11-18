// Cooking mods from the launcher (cook_plan, cook_run; docs/kiln/launcher/app.md "Cooking mods"). A mod with a cook
// recipe has files that are made on this PC from this game: models, animations, sounds. The launcher makes them before
// the game starts when a mod needs it (it was installed, updated or switched on, the game was updated, its last cook
// failed), shows what the tools are doing as a task, and never lets a cook stand in the way: a mod that could not be
// cooked is named with the tools' own words, the other mods are cooked, and the game starts. "Cook again" in a mod's
// menu cooks that one mod whenever the player asks.
import * as api from "../api/commands";
import type { BootSnapshot, CookJob, CookRun, GameId, ModCook, ProfileId } from "../api/commands";
import { toKilnError } from "../api/commands";
import { toast, waitTask } from "../ui";
import { bootSnapshotLine } from "./bootSnapshot";
import { notifyActivation } from "./notices";
import { t } from "../i18n";

/** Why a mod is cooked, as the end of "… because …" (kiln_manage::cook::CookReason::text). */
export function cookReason(reason: NonNullable<CookJob["reason"]>): string {
  switch (reason) {
    case "notCooked": return t("shell.cook.reasonNotCooked");
    case "modChanged": return t("shell.cook.reasonModChanged");
    case "gameChanged": return t("shell.cook.reasonGameChanged");
    case "lastFailed": return t("shell.cook.reasonLastFailed");
    case "filesGone": return t("shell.cook.reasonFilesGone");
    case "asked": return t("shell.cook.reasonAsked");
  }
}

/** One mod that was not cooked: its name, then what the tools said last (their own lines, kept as lines). */
export const notCookedText = (m: ModCook) => t("shell.cook.notCookedText", { name: m.name, error: m.error ?? t("shell.cook.noWord") });

/** What a cook run left to tell: nothing when every mod was cooked; else one notice that stays, naming each mod that
 *  was not cooked with the tools' text. The mod keeps the files it has (those of its last cook: a failed cook puts them
 *  back), or runs without them when it was never cooked here (no models of its own, its own
 *  animations refused) until a cook goes through. */
export function notifyCook(run: CookRun | null | undefined, o: { quietOk?: boolean } = {}) {
  if (!run?.mods.length) return;
  const failed = run.mods.filter((m) => !m.ok);
  const ok = run.mods.length - failed.length;
  if (!failed.length) {
    if (!o.quietOk) toast.ok(ok === 1 ? t("shell.cook.oneCooked", { name: run.mods[0].name }) : t("shell.cook.manyCooked", { count: ok }), t("shell.cook.cookedDetail"));
    return;
  }
  const title = failed.length === 1 ? t("shell.cook.oneNotCooked", { name: failed[0].name }) : t("shell.cook.manyNotCooked", { count: failed.length });
  const rest = ok ? t("shell.cook.failedRestOthers", { count: ok }) : t("shell.cook.failedRest");
  toast.info(title, `${failed.map(notCookedText).join("\n\n")}\n\n${rest}`, { ms: 0 });
}

/** What a start says when its cook has mods to cook (Play, and a modpack offer's Launch). */
export const cookingNotice = (jobs: CookJob[]) =>
  toast.info(jobs.length === 1 ? t("shell.cook.cookingOne", { name: jobs[0].name }) : t("shell.cook.cookingMany", { count: jobs.length }), t("shell.cook.cookingDetail"));

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
    if (line) toast.info(t("shell.cook.bootData"), line, { ms: 0 });
    return run;
  } catch (e) {
    const k = toKilnError(e);
    // the game is running (it started outside the launcher), or the cook could not even be asked for: say it once, go on
    if (k.code !== "canceled" && k.code !== "game_running") toast.info(t("shell.cook.notCookedTitle"), t("shell.cook.startsAnyway", { error: k.message }), { ms: 0 });
    return null;
  }
}

/** Play once with another profile: the boot game-data step Play runs after its cook, for the mods of that single start
 *  (boot_snapshot_run; a single start cooks nothing). Resolves when it is done, whatever happened: like the cook it
 *  never stops a start, and what went wrong is one line. */
export async function bootSnapshotBeforeOnce(gameId: GameId, profileId: ProfileId): Promise<BootSnapshot | null> {
  try {
    const s = await waitTask<BootSnapshot>(await api.bootSnapshotRun(gameId, profileId));
    const line = bootSnapshotLine(s);
    if (line) toast.info(t("shell.cook.bootData"), line, { ms: 0 });
    return s;
  } catch (e) {
    const k = toKilnError(e);
    // the game is running, or the step could not be asked for: the start goes on (and says what it refuses itself)
    if (k.code !== "canceled" && k.code !== "game_running" && k.code !== "not_found")
      toast.info(t("shell.cook.bootDataNotMade"), t("shell.cook.startsAnyway", { error: k.message }), { ms: 0 });
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
    if (toKilnError(e).code !== "canceled") toast.error(e, t("shell.cook.oneNotCooked", { name }));
    return null;
  }
}
