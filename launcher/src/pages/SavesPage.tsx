// /saves: save slots and backups of the selected game.
import { useQuery } from "@tanstack/react-query";
import { FolderOpen } from "lucide-react";
import * as api from "../api/commands";
import type { SaveSlot } from "../api/commands";
import { qk, useGame } from "../api/hooks";
import { SaveBackups } from "../components/SaveBackups";
import { Badge, Card, EmptyState, IconButton, Page, PageHeader, Table, errorText, fmtAgo, fmtBytes, toast, type Column } from "../ui";

export default function SavesPage() {
  const { game } = useGame();
  const slots = useQuery({ queryKey: qk.saves(game?.id ?? ""), queryFn: () => api.savesList(game!.id), enabled: !!game });
  if (!game) return <Page><PageHeader title="Saves" sub="No game yet. Find Enshrouded on Play first." /></Page>;
  const cols: Column<SaveSlot>[] = [
    { key: "file", header: "File", width: "minmax(0, 1.4fr)", render: (s) => <span className="k-mono">{s.file}</span> },
    { key: "kind", header: "Kind", width: "110px", render: (s) => <Badge tone={s.kind === "character" ? "ember" : "idle"}>{s.kind}</Badge> },
    { key: "modified", header: "Changed", width: "minmax(0, 1fr)", sort: (a, b) => a.modified.localeCompare(b.modified), render: (s) => fmtAgo(s.modified) },
    { key: "size", header: "Size", width: "90px", align: "right", render: (s) => <span className="k-num">{fmtBytes(s.size)}</span> },
  ];
  const dir = slots.data?.[0]?.path.replace(/[\\/][^\\/]+$/, "");
  return (
    <Page>
      <PageHeader title="Saves" sub={`${slots.data?.length ?? 0} save file${slots.data?.length === 1 ? "" : "s"}. Kiln backs them up before a start with different mods and keeps the last ones.`} />
      <Card flush title="Save files" sub={slots.data?.[0]?.steamId ? `Steam user ${slots.data[0].steamId}` : undefined}
        actions={dir && <IconButton label="Open folder" icon={<FolderOpen />} onClick={() => api.appOpenPath(dir).catch((e) => toast.error(e))} />}>
        {slots.error ? <EmptyState title="Saves unavailable" hint={errorText(slots.error)} /> : (
          <Table aria-label="Save files" columns={cols} rows={slots.data ?? []} rowKey={(s) => s.path} rowHeight={42} empty={<EmptyState title="No saves found" />} />
        )}
      </Card>
      <SaveBackups gameId={game.id} />
    </Page>
  );
}
