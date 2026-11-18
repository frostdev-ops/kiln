// Settings, Library: where the mods' files are kept, what they take on disk, and Clean up (every version no profile and
// no game's profile file uses; library_unused, then one library_remove_many). The main list is where mods are seen,
// switched and removed; a mod's other versions are in its row's menu. Its last line is the data folder (the library
// and everything else the launcher keeps, moved as one): ./DataFolderSettings.tsx. Below the numbers: the earlier versions
// an update kept for a roll back (shell/rollback.ts), each with its size and a Remove; Clean up leaves those.
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import * as api from "../api/commands";
import type { LibraryRef } from "../api/commands";
import { useLibrary } from "../api/hooks";
import { useT } from "../i18n";
import { keptSize, keptVersions, liveVersions, type KeptRow } from "../shell/rollback";
import { Button, Card, ConfirmDanger, EmptyState, KeyValue, Modal, Skeleton, errorText, fmtAgo, fmtBytes, toast } from "../ui";
import { DataFolderValue } from "./DataFolderSettings";

export function LibraryCard() {
  const t = useT();
  const qc = useQueryClient();
  const lib = useLibrary(null);
  const [gc, setGc] = useState<LibraryRef[] | null>(null);
  const [drop, setDrop] = useState<KeptRow | null>(null);
  const all = lib.data ?? [];
  // a kept version is no second copy of a mod: counted on its own line
  const versions = all.reduce((n, e) => n + liveVersions(e), 0);
  const bytes = all.reduce((n, e) => n + e.versions.reduce((m, v) => m + (v.size ?? 0), 0), 0);
  const several = all.filter((e) => liveVersions(e) > 1).length;
  const kept = keptVersions(all);
  const keptBytes = kept.reduce((n, k) => n + keptSize(k.version), 0);
  const refresh = () => qc.invalidateQueries({ queryKey: ["library_list"] });
  const cleanUp = async () => {
    try { setGc(await api.libraryUnused()); }
    catch (e) { toast.error(e); }
  };
  return (
    <Card id="library" aria-label={t("librarySettings.title")} title={t("librarySettings.title")} sub={t("librarySettings.sub")}
      actions={<Button size="s" disabled={!all.length} onClick={() => void cleanUp()}>{t("librarySettings.cleanUp")}</Button>}>
      {lib.isLoading ? <Skeleton lines={2} /> : lib.error && <EmptyState title={t("librarySettings.unreadable")} hint={errorText(lib.error)} />}
      {/* the data folder shows whatever the library does: it is where to look when the library cannot be read */}
      <KeyValue rows={[
        lib.data && [t("librarySettings.mods"), versions > all.length
          ? several ? t("librarySettings.modsSeveral", { count: all.length, versions, several }) : t("librarySettings.modsVersions", { count: all.length, versions })
          : `${all.length}`],
        lib.data && [t("librarySettings.onDisk"), fmtBytes(bytes)],
        kept.length > 0 && [t("librarySettings.kept"), t("librarySettings.keptCount", { count: kept.length, size: fmtBytes(keptBytes) })],
        [t("librarySettings.dataFolder"), <DataFolderValue />],
      ]} />
      {kept.length > 0 && (
        <ul className="k-kept-list" aria-label={t("librarySettings.keptVersions")}>
          {kept.map((k) => (
            <li key={`${k.kind}/${k.id}/${k.version.hash}`} className="k-kept-row">
              <span className="k-kept-name">{k.name} <span className="k-mono">{k.version.version}</span></span>
              <span className="k-muted">{[
                fmtBytes(keptSize(k.version)),
                k.version.kept!.settings.length ? t("librarySettings.keptSettings", { count: k.version.kept!.settings.length }) : null,
                k.version.kept!.replacedBy ? t("librarySettings.replacedBy", { name: k.version.kept!.replacedBy }) : null,
              ].filter(Boolean).join(", ")} {fmtAgo(k.version.kept!.kept)}</span>
              <Button size="s" variant="ghost" aria-label={t("librarySettings.removeVersion", { name: k.name, version: k.version.version })} onClick={() => setDrop(k)}>{t("common.remove")}</Button>
            </li>
          ))}
        </ul>
      )}
      <span className="k-hint">{t("librarySettings.hint")}</span>
      <ConfirmDanger open={!!drop} onClose={() => setDrop(null)} relaxable word={drop?.id ?? ""} action={t("common.remove")}
        title={drop ? t("librarySettings.removeVersionAsk", { name: drop.name, version: drop.version.version }) : ""}
        message={t("librarySettings.removeVersionMessage")}
        onConfirm={async () => {
          const k = drop!;
          try {
            await api.libraryRemove({ kind: k.kind, id: k.id, version: k.version.version, hash: k.version.hash });
            toast.ok(t("librarySettings.removedVersion", { name: k.name, version: k.version.version }));
          } catch (e) { toast.error(e); throw e; } finally { refresh(); }
        }} />
      <ConfirmDanger open={!!gc && gc.length > 0} onClose={() => setGc(null)} title={t("librarySettings.removeUnusedAsk", { count: gc?.length ?? 0 })} word={t("common.confirmWord.clean")} action={t("common.remove")}
        message={t("librarySettings.removeUnusedMessage")}
        onConfirm={async () => {
          // one call: the backend looks at the running processes and the profiles once for the whole batch
          try {
            const r = await api.libraryRemoveMany(gc ?? []);
            for (const f of r.failed) toast.error(f.error, `${f.entry.id} ${f.entry.version}`);
            const n = r.removed.length;
            if (n) toast.ok(t("librarySettings.removedCount", { count: n }), t("librarySettings.removedDetail", { count: n }));
          } catch (e) {
            // a running game stops the whole clean up before anything goes ("Close Enshrouded first: ...")
            toast.error(e);
          }
          refresh();
        }} />
      {gc && gc.length === 0 && <Modal open onClose={() => setGc(null)} title={t("librarySettings.nothingToClean")} width={480} footer={<Button onClick={() => setGc(null)}>{t("common.ok")}</Button>}>{t("librarySettings.nothingToCleanText")}</Modal>}
    </Card>
  );
}
