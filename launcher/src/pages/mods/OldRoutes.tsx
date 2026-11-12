// The places before dev.24, so a bookmark, a link in a mod's page or an old note does not strand anyone: /profiles goes
// to Settings, Profiles; /profiles/<id>/<tab> to that tab's new place when <id> is the loaded profile (Saves to the
// Saves page in any case), else to Settings, Profiles, where it can be loaded. /library is the main list (App.tsx).
import { Navigate, useParams } from "react-router-dom";
import { useActiveProfile, useGame } from "../../api/hooks";

const NEW_PLACE: Record<string, string> = { native: "/mods/native", engines: "/mods/engines", configs: "/mods/configs", emf: "/", mods: "/" };

export function OldProfileRoute() {
  const rest = useParams()["*"] ?? "";
  const [id, tab = "mods"] = rest.split("/").filter(Boolean);
  const { game, loading } = useGame();
  const active = useActiveProfile(game?.id);
  if (!id) return <Navigate to="/settings#profiles" replace />;
  if (tab === "saves") return <Navigate to="/saves" replace />;
  if (loading || (game && active.isLoading)) return null;
  if (active.data?.profileId !== id) return <Navigate to="/settings#profiles" replace />;
  return <Navigate to={NEW_PLACE[tab] ?? "/"} replace />;
}
