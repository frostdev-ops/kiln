// /saves: save slots and backups of the selected game.
import { useQuery } from "@tanstack/react-query";
import { FolderOpen } from "lucide-react";
import * as api from "../api/commands";
import type { SaveSlot } from "../api/commands";
import { qk, useGame } from "../api/hooks";
import { SaveBackups } from "../components/SaveBackups";
import { useT, type TFunction } from "../i18n";
import { Badge, Card, EmptyState, IconButton, Page, PageHeader, Table, errorText, fmtAgo, fmtBytes, toast, type Column } from "../ui";

/** A slot's kind as a word (lower case: the Badge capitalizes it). */
function kindWord(t: TFunction, kind: SaveSlot["kind"]): string {
  switch (kind) {
    case "world": return t("saves.kind.world");
    case "character": return t("saves.kind.character");
    case "other": return t("saves.kind.otherKind");
    default: return kind;
  }
}

export default function SavesPage() {
  const t = useT();
  const { game } = useGame();
  const slots = useQuery({ queryKey: qk.saves(game?.id ?? ""), queryFn: () => api.savesList(game!.id), enabled: !!game });
  if (!game) return <Page><PageHeader title={t("saves.title")} sub={t("saves.noGame")} /></Page>;
  const cols: Column<SaveSlot>[] = [
    { key: "file", header: t("saves.col.file"), width: "minmax(0, 1.4fr)", render: (s) => <span className="k-mono">{s.file}</span> },
    { key: "kind", header: t("saves.col.kind"), width: "110px", render: (s) => <Badge tone={s.kind === "character" ? "ember" : "idle"}>{kindWord(t, s.kind)}</Badge> },
    { key: "modified", header: t("saves.col.changed"), width: "minmax(0, 1fr)", sort: (a, b) => a.modified.localeCompare(b.modified), render: (s) => fmtAgo(s.modified) },
    { key: "size", header: t("saves.col.size"), width: "90px", align: "right", render: (s) => <span className="k-num">{fmtBytes(s.size)}</span> },
  ];
  const dir = slots.data?.[0]?.path.replace(/[\\/][^\\/]+$/, "");
  return (
    <Page>
      <PageHeader title={t("saves.title")} sub={t("saves.sub", { count: slots.data?.length ?? 0 })} />
      <Card flush title={t("saves.files")} sub={slots.data?.[0]?.steamId ? t("saves.steamUser", { id: slots.data[0].steamId }) : undefined}
        actions={dir && <IconButton label={t("saves.openFolder")} icon={<FolderOpen />} onClick={() => api.appOpenPath(dir).catch((e) => toast.error(e))} />}>
        {slots.error ? <EmptyState title={t("saves.unavailable")} hint={errorText(slots.error)} /> : (
          <Table aria-label={t("saves.files")} columns={cols} rows={slots.data ?? []} rowKey={(s) => s.path} rowHeight={42} empty={<EmptyState title={t("saves.none")} />} />
        )}
      </Card>
      <SaveBackups gameId={game.id} />
    </Page>
  );
}
