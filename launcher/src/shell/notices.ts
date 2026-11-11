// What an activation or a Kiln install leaves for the player to read, shown the same way at every call site that gets
// one: Play's profile switch, Profiles, a profile's page, the Deck's profile dialog, the add-a-mod flows (drop, picked
// profile, found mods, the catalog, the Deck's Add mods), a modpack install, Play itself (also when the start fails),
// a re-apply after a profile edit or at start-up (taken through activation_notices), Kiln's install, update, rollback
// and uninstall, and a server deploy. These notices name folders and recovery paths: they stay until closed.
import type { Activation, AddReport, InstallResult, RetainedPlacement, UninstallResult } from "../api/commands";
import { toKilnError } from "../api/commands";
import { notifyPlacementCleanup, toast } from "../ui";

/** Folders an activation left as they are. Each reason ends with what still runs from it (engine_mods::still_runs): an
 *  enabled mod's changed copy is simply what runs, a switched-off one may still be loaded by its own loader or by
 *  Kiln's plugin for that engine (SET's runs every mod in its folder). */
function notifyKept(retained: RetainedPlacement[]) {
  if (!retained.length) return;
  const detail = retained.map((r) => `${r.path}: ${r.reason}`).join(" ");
  toast.info("Some mod folders were kept", `${detail} Kiln left these folders as they are. With the game closed, move a folder out of the game folder to stop a mod that is off; your files have not been deleted.`, { ms: 0 });
}

/** Copies left in the game folder, mod settings kept or put back, and profile notes. */
export function notifyActivation(a: Activation | null | undefined) {
  if (!a) return;
  notifyKept(a.retained ?? []);
  if (a.keptFiles?.length) toast.info("Mod settings kept", a.keptFiles.join(" "), { ms: 0 });
  if (a.warnings?.length) toast.info("Profile notes", a.warnings.join(" "), { ms: 0 });
}

/** An add's result (library_add, library_add_to_profile, found_add, a catalog install): what writing the profile again
 *  kept, and what the add did in the profile besides adding (`notices`: an entry replaced, a second copy of a proxy
 *  DLL switched off, an XHL-Core build kept). */
export function notifyAdd(r: AddReport | null | undefined) {
  if (!r) return;
  notifyActivation(r.activation);
  if (r.notices?.length) toast.info("Changes in the profile", r.notices.join(" "), { ms: 0 });
}

/** A failed command whose backend wrote the profile again first (a start after Play's re-apply): what it kept. */
export function notifyActivationOf(e: unknown) {
  const d = toKilnError(e).detail as { activation?: Activation } | null | undefined;
  notifyActivation(d?.activation);
}

/** A runtime install, update, rollback or server deploy ("Install notes"), or an uninstall ("Some files were kept"):
 *  the folders and files it kept, and the activation of the profile written again or made active after it. */
export function notifyInstall(r: InstallResult | UninstallResult | null | undefined) {
  if (!r) return;
  if ("placements" in r) notifyPlacementCleanup(r.placements);
  if (r.warnings?.length) toast.info("placements" in r ? "Some files were kept" : "Install notes", r.warnings.join(" "), { ms: 0 });
  if ("activation" in r) notifyActivation(r.activation);
}
