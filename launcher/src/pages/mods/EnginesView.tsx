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

export default function EnginesView() {
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const found = useFound(game?.id, active.data?.profileId);
  if (!game) return null;
  const r = found.data;
  const notes = r ? [...loaderLine(r), ...r.unreadable.map((u) => `Could not read ${u.path}: ${u.reason}.`), ...(r.truncated ? ["A folder in the game folder has more entries than Kiln lists."] : [])] : [];
  return (
    <div className="k-scroll k-stack" style={{ gap: 18, paddingBottom: 20 }}>
      <RestoreVanillaOffer gameId={game.id} />
      <Card title="Engine plugins" sub="Each one's state file from the last start.">
        <EngineStatusList gameId={game.id} />
      </Card>
      {notes.length > 0 && (
        <Card title="In the game folder" sub="Other loaders next to the game.">
          {notes.map((l) => <p key={l} className="k-hint">{l}</p>)}
        </Card>
      )}
      <p className="k-hint">The DLLs chain.ini loads, and which load after Kiln and ShroudForge: <Link className="k-link" to="/mods/native">Native and DLLs</Link>.</p>
      <ProfileFacts hint="Kiln runs other loaders' mods with its engine plugins; XHL companions run only through XHL-Core, installed separately; Ember mods are kept but do not run yet." />
    </div>
  );
}
