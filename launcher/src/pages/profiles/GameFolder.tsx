// The mods a player extracted into the game folder (as mod pages say) that Kiln did not put there, whether each loads
// with the loaded profile and why, and the one action that helps (kiln_manage::found). The main list shows each as a row
// (pages/mods/MainList.tsx); Engines names the other loaders found next to the game.
import { useQuery } from "@tanstack/react-query";
import * as api from "../../api/commands";
import type { FoundReport, GameId, ModKind, ProfileId } from "../../api/commands";

/** The query key; profile saves and adds refresh every found_scan. */
export const foundKey = (gameId: GameId, profileId: ProfileId) => ["found_scan", gameId, profileId] as const;

export function useFound(gameId: GameId | null | undefined, profileId: ProfileId | null | undefined) {
  return useQuery({ queryKey: foundKey(gameId ?? "", profileId ?? ""), queryFn: () => api.foundScan(gameId!, profileId!), enabled: !!gameId && !!profileId });
}

/** The loaders line: "EML's own loader (dbghelp.dll) is in the game folder and loads its mods itself." */
export function loaderLine(r: FoundReport, kinds?: ModKind[]) {
  return r.loaders.filter((l) => !kinds || l.kinds.some((k) => kinds.includes(k)))
    .map((l) => `${l.engine === "eml" ? "EML" : l.name}'s own loader (${l.file}) is in the game folder and loads its mods itself.`);
}
