// /mods/engines: the other loaders in this game, for the loaded profile (docs/kiln/engines.md): what each of Kiln's
// engine plugins wrote at the last start (EML's phase, ShroudForge hosted or its own and its mods' effects, the XHL-Core
// host, each engine's own loader and what it means for the mods that are on), the shared archive note, and the loaders
// found next to the game. The DLL chain is on Native and DLLs.
import { Link } from "react-router-dom";
import { useActiveProfile, useGame } from "../../api/hooks";
import { EngineStatusList, RestoreVanillaOffer } from "../../components/EngineStatus";
import { Card } from "../../ui";
import { loaderLine, useFound } from "../profiles/GameFolder";
import { ProfileFacts } from "../profiles/ProfileLayout";
import { rich, useT } from "../../i18n";

export default function EnginesView() {
  const t = useT();
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const found = useFound(game?.id, active.data?.profileId);
  if (!game) return null;
  const r = found.data;
  const notes = r ? [...loaderLine(r), ...r.unreadable.map((u) => t("mods.engines.unreadable", { path: u.path, reason: u.reason })), ...(r.truncated ? [t("mods.engines.truncated")] : [])] : [];
  return (
    <div className="k-scroll k-stack" style={{ gap: 18, paddingBottom: 20 }}>
      <RestoreVanillaOffer gameId={game.id} />
      <Card title={t("mods.engines.pluginsTitle")} sub={t("mods.engines.pluginsSub")}>
        <EngineStatusList gameId={game.id} />
      </Card>
      {notes.length > 0 && (
        <Card title={t("mods.engines.folderTitle")} sub={t("mods.engines.folderSub")}>
          {notes.map((l) => <p key={l} className="k-hint">{l}</p>)}
        </Card>
      )}
      <p className="k-hint">{rich("mods.engines.chainHint", { link: <Link className="k-link" to="/mods/native">{t("mods.tabs.native")}</Link> })}</p>
      <ProfileFacts hint={t("mods.engines.facts")} />
    </div>
  );
}
