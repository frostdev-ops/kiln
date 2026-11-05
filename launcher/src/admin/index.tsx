// Server administration: the admin pages of one server, self-contained. Mount
// `<AdminRoutes/>` on `servers/:id/admin/*` (App.tsx does); it provides the server's shared admin session
// (<AdminProvider>), the connection bar, the page tabs and the pages. Pages and hooks: docs/kiln/launcher/admin-pages.md.
import { Navigate, Outlet, Route, Routes, useParams } from "react-router-dom";
import { AdminProvider, useAdminState } from "../api/admin";
import { ConnectionBar } from "./ConnectionBar";
import { startHistory } from "./history";
import { ConfirmHost, NavTabs } from "./ui";
import AdminPlayersPage from "./pages/AdminPlayersPage";
import AdminMapPage from "./pages/AdminMapPage";
import AdminZonesPage from "./pages/AdminZonesPage";
import AdminItemsPage from "./pages/AdminItemsPage";
import AdminSpawnPage from "./pages/AdminSpawnPage";
import AdminWorldPage from "./pages/AdminWorldPage";
import AdminCombatPage from "./pages/AdminCombatPage";
import AdminCreaturesPage from "./pages/AdminCreaturesPage";
import AdminGameDataPage from "./pages/AdminGameDataPage";
import AdminPermissionsPage from "./pages/AdminPermissionsPage";
import AdminContentPage from "./pages/AdminContentPage";
import AdminConfigsPage from "./pages/AdminConfigsPage";
import AdminModsPage from "./pages/AdminModsPage";
import AdminConsolePage from "./pages/AdminConsolePage";
import AdminSavesPage from "./pages/AdminSavesPage";
import AdminCrashesPage from "./pages/AdminCrashesPage";
import "./admin.css";

export { AdminProvider } from "../api/admin";

/** The admin pages in tab order (route segment, tab label, page). */
export const ADMIN_PAGES = [
  ["players", "Players", AdminPlayersPage],
  ["map", "Map", AdminMapPage],
  ["zones", "Zones", AdminZonesPage],
  ["items", "Items", AdminItemsPage],
  ["spawn", "Spawn", AdminSpawnPage],
  ["world", "World", AdminWorldPage],
  ["combat", "Combat", AdminCombatPage],
  ["creatures", "Creatures", AdminCreaturesPage],
  ["gamedata", "Game data", AdminGameDataPage],
  ["permissions", "Permissions", AdminPermissionsPage],
  ["content", "Content", AdminContentPage],
  ["configs", "Configs", AdminConfigsPage],
  ["mods", "Mods", AdminModsPage],
  ["console", "Console", AdminConsolePage],
  ["saves", "Saves", AdminSavesPage],
  ["crashes", "Crashes", AdminCrashesPage],
] as const;

startHistory();

/** `/servers/<id>/admin` for absolute links (relative ones break inside the splat route). */
export function adminBase(serverId: string) {
  return `/servers/${encodeURIComponent(serverId)}/admin`;
}

function AdminLayout() {
  const st = useAdminState();
  const base = adminBase(useParams().id ?? st.serverId);
  const players = st.state?.players?.length;
  return (
    <div className="ka-admin">
      <div className="ka-admin-head">
        <ConnectionBar />
        {/* absolute links: inside the admin/* splat route, React Router 7 resolves relative ones against the splat */}
        <NavTabs aria-label="Admin pages" items={ADMIN_PAGES.map(([seg, label]) => ({ to: `${base}/${seg}`, label, count: seg === "players" && players ? players : undefined }))} />
      </div>
      <div className="ka-admin-body">
        <Outlet />
      </div>
      <ConfirmHost />
    </div>
  );
}

/** Every admin page of the server in the URL (`/servers/:id/admin/*`). */
export function AdminRoutes({ serverId }: { serverId?: string }) {
  const params = useParams();
  const id = serverId ?? params.id;
  if (!id) return null;
  return (
    <AdminProvider serverId={id}>
      <Routes>
        <Route element={<AdminLayout />}>
          <Route index element={<Navigate to={`${adminBase(id)}/players`} replace />} />
          {ADMIN_PAGES.map(([seg, , Page]) => (
            <Route key={seg} path={seg} element={<Page />} />
          ))}
          <Route path="*" element={<Navigate to={`${adminBase(id)}/players`} replace />} />
        </Route>
      </Routes>
    </AdminProvider>
  );
}
