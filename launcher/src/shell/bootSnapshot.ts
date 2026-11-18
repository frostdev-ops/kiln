// Play's boot game-data step (kiln_manage::boot_snapshot, docs/kiln/modding/boot-snapshot.md): after the cook, when an
// enabled mod of the start asks for the boot phase, the launcher makes the snapshot of the game's data that Mod.OnBoot
// reads, once per game build (about a minute and about 200 MB the first time, well under a second after that). It never
// stops a start: what went wrong is one line here.
import type { BootSnapshot } from "../api/commands";
import { t } from "../i18n";

/** What the step left to tell, in one line: nothing when the snapshot is in place and whole, or when no enabled mod
 *  needs it (`skipped`: not a failure); else that the game starts without it (an OnBoot mod is told the game data is
 *  unavailable) or which kinds it lacks. */
export function bootSnapshotLine(s: BootSnapshot | null | undefined): string | null {
  if (!s || s.status === "skipped") return null;
  if (s.status !== "made" && s.status !== "reused")
    return t("shell.bootSnapshot.notMade", { reason: s.reason ?? t("shell.bootSnapshot.noReason") });
  if (s.emptyKinds?.length)
    return t("shell.bootSnapshot.empty", { kinds: s.emptyKinds.join(", ") });
  return null;
}
