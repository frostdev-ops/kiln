// /profiles/:id/saves: the game's save backups made while this profile was active.
import { useGame } from "../../api/hooks";
import { SaveBackups } from "../../components/SaveBackups";
import { EmptyState } from "../../ui";
import { useProfileId } from "./useProfileEdit";

export default function ProfileSavesPage() {
  const id = useProfileId();
  const { game } = useGame();
  if (!game) return <EmptyState title="No game" hint="Pick a game on Play." />;
  return <SaveBackups gameId={game.id} profileId={id} title="Backups for this profile" />;
}
