// Settings, Library: where the mods' files are kept, what they take on disk, and Clean up (every version no profile and
// no game's profile file uses; library_unused, then one library_remove_many). The main list is where mods are seen,
// switched and removed; a mod's other versions are in its row's menu. Its last line is the data folder (the library
// and everything else the launcher keeps, moved as one): ./DataFolderSettings.tsx.
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import * as api from "../api/commands";
import type { LibraryRef } from "../api/commands";
import { useLibrary } from "../api/hooks";
import { Button, Card, ConfirmDanger, EmptyState, KeyValue, Modal, Skeleton, errorText, fmtBytes, toast } from "../ui";
import { DataFolderValue } from "./DataFolderSettings";

export function LibraryCard() {
  const qc = useQueryClient();
  const lib = useLibrary(null);
  const [gc, setGc] = useState<LibraryRef[] | null>(null);
  const all = lib.data ?? [];
  const versions = all.reduce((n, e) => n + e.versions.length, 0);
  const bytes = all.reduce((n, e) => n + e.versions.reduce((m, v) => m + (v.size ?? 0), 0), 0);
  const several = all.filter((e) => e.versions.length > 1).length;
  const refresh = () => qc.invalidateQueries({ queryKey: ["library_list"] });
  const cleanUp = async () => {
    try { setGc(await api.libraryUnused()); }
    catch (e) { toast.error(e); }
  };
  return (
    <Card id="library" aria-label="Library" title="Library" sub="Where Kiln keeps every mod's files, by version."
      actions={<Button size="s" disabled={!all.length} onClick={() => void cleanUp()}>Clean up</Button>}>
      {lib.isLoading ? <Skeleton lines={2} /> : lib.error && <EmptyState title="The library is not readable" hint={errorText(lib.error)} />}
      {/* the data folder shows whatever the library does: it is where to look when the library cannot be read */}
      <KeyValue rows={[
        lib.data && ["Mods", `${all.length}${versions > all.length ? ` (${versions} versions${several ? `; ${several} with more than one: their menu on Play picks one` : ""})` : ""}`],
        lib.data && ["On disk", fmtBytes(bytes)],
        ["Data folder", <DataFolderValue />],
      ]} />
      <span className="k-hint">Clean up removes the versions no profile and no game uses. Removing one mod: its menu on Play.</span>
      <ConfirmDanger open={!!gc && gc.length > 0} onClose={() => setGc(null)} title={`Remove ${gc?.length ?? 0} unused?`} word="clean" action="Remove"
        message="No profile and no game uses them. Profiles and saves stay as they are."
        onConfirm={async () => {
          // one call: the backend looks at the running processes and the profiles once for the whole batch
          try {
            const r = await api.libraryRemoveMany(gc ?? []);
            for (const f of r.failed) toast.error(f.error, `${f.entry.id} ${f.entry.version}`);
            const n = r.removed.length;
            if (n) toast.ok(`Removed ${n}`, `${n} version${n === 1 ? "" : "s"} left the library.`);
          } catch (e) {
            // a running game stops the whole clean up before anything goes ("Close Enshrouded first: ...")
            toast.error(e);
          }
          refresh();
        }} />
      {gc && gc.length === 0 && <Modal open onClose={() => setGc(null)} title="Nothing to clean" width={480} footer={<Button onClick={() => setGc(null)}>OK</Button>}>Every version is in a profile, or in the profile file of a game.</Modal>}
    </Card>
  );
}
