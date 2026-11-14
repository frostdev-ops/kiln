// Play's boot game-data step (kiln_manage::boot_snapshot, docs/kiln/modding/boot-snapshot.md): after the cook, the
// launcher makes the snapshot of the game's data that Mod.OnBoot reads, once per game build (about a minute and about
// 200 MB the first time, well under a second after that). It never stops a start: what went wrong is one line here.
import type { BootSnapshot } from "../api/commands";

/** What the step left to tell, in one line: nothing when the snapshot is in place and whole; else that the game starts
 *  without it (an OnBoot mod is told the game data is unavailable) or which kinds it lacks. */
export function bootSnapshotLine(s: BootSnapshot | null | undefined): string | null {
  if (!s) return null;
  if (s.status !== "made" && s.status !== "reused")
    return `The game data for OnBoot mods was not made (${s.reason ?? "no reason given"}). The game starts without it: a mod that reads the game's data in OnBoot is told it is unavailable.`;
  if (s.emptyKinds?.length)
    return `The game data for OnBoot mods has no ${s.emptyKinds.join(", ")} (the game's archive could not give them): an OnBoot read of them finds nothing.`;
  return null;
}
