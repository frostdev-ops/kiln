// "In the game folder": mods a player extracted into the game folder (as mod pages say) that Kiln did not put there,
// whether each loads with this profile and why, and the one action that helps (kiln_manage::found). On the profile's
// Mods tab (every kind) and Engines tab (other engines' kinds); Play links here when some do not load.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useLocation } from "react-router-dom";
import * as api from "../../api/commands";
import type { AddReport, FoundMod, FoundReport, GameId, ModKind, ProfileId } from "../../api/commands";
import { qk, useGame } from "../../api/hooks";
import { KIND_SHORT, KIND_TONE } from "../../api/kinds";
import { Badge, Button, Skeleton, Table, errorText, runTask, toast, type Column } from "../../ui";
import { notifyActivation } from "../../shell/notices";
import { withDropin } from "./dropins";
import { useProfileEdit } from "./useProfileEdit";

/** The query key; profile saves and adds refresh every found_scan. */
export const foundKey = (gameId: GameId, profileId: ProfileId) => ["found_scan", gameId, profileId] as const;

export function useFound(gameId: GameId | null | undefined, profileId: ProfileId | null | undefined) {
  return useQuery({ queryKey: foundKey(gameId ?? "", profileId ?? ""), queryFn: () => api.foundScan(gameId!, profileId!), enabled: !!gameId && !!profileId });
}

const STATE: Record<FoundMod["state"], { word: string; tone: "ok" | "warn" | "info" | "idle" }> = {
  loads: { word: "Loads", tone: "ok" },
  off: { word: "Not loaded", tone: "warn" },
  inProfile: { word: "In this profile", tone: "info" },
  cannotRun: { word: "Can't run", tone: "idle" },
};

/** The loaders line: "EML's own loader (dbghelp.dll) is in the game folder and loads these itself." */
function loaderLine(r: FoundReport, kinds?: ModKind[]) {
  return r.loaders.filter((l) => !kinds || l.kinds.some((k) => kinds.includes(k)))
    .map((l) => `${l.engine === "eml" ? "EML" : l.name}'s own loader (${l.file}) is in the game folder and loads its mods itself.`);
}

export function GameFolderSection({ profileId, kinds }: { profileId: ProfileId; kinds?: ModKind[] }) {
  const qc = useQueryClient();
  const { game } = useGame();
  const { update } = useProfileEdit(profileId);
  const found = useFound(game?.id, profileId);
  const [busy, setBusy] = useState<string | null>(null);
  // Play's link lands here: /profiles/<id>/mods#game-folder
  const ref = useRef<HTMLElement>(null);
  const { hash } = useLocation();
  const ready = !!found.data;
  useEffect(() => { if (ready && hash === "#game-folder") ref.current?.scrollIntoView({ block: "start" }); }, [ready, hash]);
  if (!game) return null;
  const mods = (found.data?.mods ?? []).filter((m) => !kinds || (m.kind && kinds.includes(m.kind)));
  const add = async (m: FoundMod) => {
    setBusy(m.path);
    try {
      const r = await runTask<AddReport>(() => api.foundAdd(game.id, profileId, m.path), { quiet: true });
      const skipped = r.import.skipped[0];
      notifyActivation(r.activation);
      if (!r.added.length && !r.already.length && skipped) toast.error({ code: "refused", message: skipped.reason }, `The Library did not take ${m.name}`);
      else toast.ok(`${m.name} is in this profile`, r.applied ? "Written to the game." : undefined);
    } catch (e) {
      toast.error(e, `Could not add ${m.name}`);
    } finally {
      setBusy(null);
      void qc.invalidateQueries({ queryKey: ["found_scan"] });
      void qc.invalidateQueries({ queryKey: qk.profile(profileId) });
      void qc.invalidateQueries({ queryKey: ["library_list"] });
    }
  };
  const columns: Column<FoundMod>[] = [
    { key: "name", header: "Mod", width: "minmax(0, 1fr)", sort: (a, b) => a.name.localeCompare(b.name), render: (m) => (
      <span className="k-stack" style={{ gap: 2, minWidth: 0 }}>
        <span className="k-row" style={{ gap: 8, minWidth: 0 }}>
          <span title={m.name} style={{ overflow: "hidden", textOverflow: "ellipsis" }}>{m.name}</span>
          {m.kind && <Badge tone={KIND_TONE[m.kind]}>{KIND_SHORT[m.kind]}</Badge>}
          {m.version && <span className="k-mono k-muted">{m.version}</span>}
        </span>
        <span className="k-mono k-muted" title={m.path} style={{ fontSize: 13, overflow: "hidden", textOverflow: "ellipsis" }}>{m.path}</span>
      </span>
    ) },
    { key: "state", header: "With this profile", width: "minmax(0, 1.4fr)", render: (m) => (
      <span className="k-stack" style={{ gap: 2 }}>
        <Badge tone={STATE[m.state].tone}>{STATE[m.state].word}</Badge>
        <span className="k-hint" style={{ whiteSpace: "normal" }}>{m.reason}</span>
      </span>
    ) },
    { key: "act", header: <span className="k-sr-only">Action</span>, width: "232px", align: "right", render: (m) => (
      m.fix === "dropins" && m.kind ? (
        <Button size="s" onClick={() => update((p) => withDropin(p, m.kind!, true))}>Load the game-folder {KIND_SHORT[m.kind]} mods</Button>
      ) : m.fix === "add" ? (
        <Button size="s" loading={busy === m.path} disabled={!!busy} onClick={() => void add(m)}>Add to this profile</Button>
      ) : null
    ) },
  ];
  const off = mods.filter((m) => m.state === "off").length;
  return (
    <section ref={ref} className="k-card" id="game-folder" aria-label="In the game folder">
      <header className="k-card-head">
        <div className="k-card-titles">
          <h2 className="k-card-title">In the game folder</h2>
          <span className="k-card-sub">
            {found.data ? (mods.length ? `${mods.length} mod${mods.length === 1 ? "" : "s"} Kiln did not put there${off ? `; ${off} ${off === 1 ? "does" : "do"} not load with this profile` : ""}.` : "No other mods in the game folder.") : "Mods extracted into the game folder by hand."}
          </span>
        </div>
      </header>
      {found.isLoading && <Skeleton lines={2} />}
      {found.error && <p className="k-error-text">{errorText(found.error)}</p>}
      {found.data && !found.data.kilnInstalled && <p className="k-hint">Kiln is not installed in this game: only the engines' own loaders run these.</p>}
      {found.data && loaderLine(found.data, kinds).map((l) => <p key={l} className="k-hint">{l}</p>)}
      {mods.length > 0 && <Table aria-label="Mods in the game folder" columns={columns} rows={mods} rowKey={(m) => m.path} />}
      {found.data?.unreadable.map((u) => <p key={u.path + u.reason} className="k-hint">Could not read <span className="k-mono">{u.path}</span>: {u.reason}</p>)}
      {found.data?.truncated && <p className="k-hint">A folder has more entries than Kiln lists here.</p>}
    </section>
  );
}
