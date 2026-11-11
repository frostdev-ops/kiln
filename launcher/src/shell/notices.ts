// What an activation or a Kiln install leaves for the player to read, shown the same way at every call site (Play's
// profile switch, Profiles, a profile's page, the Deck's profile dialog, the re-apply after a profile edit, Play, Kiln's
// install and a server deploy). These notices name folders and recovery paths: they stay until the player closes them.
import type { Activation, InstallResult, UninstallResult } from "../api/commands";
import { notifyPlacementCleanup, toast } from "../ui";

/** Copies left in the game folder (the retained-folder warning), mod settings kept or put back, and profile notes. */
export function notifyActivation(a: Activation | null | undefined) {
  if (!a) return;
  notifyPlacementCleanup({ removed: [], retained: a.retained ?? [] });
  if (a.keptFiles?.length) toast.info("Mod settings kept", a.keptFiles.join(" "), { ms: 0 });
  if (a.warnings?.length) toast.info("Profile notes", a.warnings.join(" "), { ms: 0 });
}

/** A runtime install, update, rollback, uninstall or server deploy: folders and files it kept, and the activation of the
 *  profile it made active again. */
export function notifyInstall(r: InstallResult | UninstallResult | null | undefined) {
  if (!r) return;
  if ("placements" in r) notifyPlacementCleanup(r.placements);
  if (r.warnings?.length) toast.info("Some files were kept", r.warnings.join(" "), { ms: 0 });
  if ("activation" in r) notifyActivation(r.activation);
}
