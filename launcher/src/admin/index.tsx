// Server administration: the admin pages of one server. Mount `<AdminRoutes/>` on `servers/:id/admin/*` (App.tsx does);
// it provides the server's shared admin session (<AdminProvider>) and the pages. The server's grouped list
// (shell/serverNav.ts) links to them; the connection shows in the server's live sentence (pages/servers/ServerLayout.tsx).
// Configs, Mods and the console are merged into the server's own pages (live while it runs); their old admin routes
// redirect there. Pages and hooks: docs/kiln/launcher/admin-pages.md.
import { Navigate, Outlet, Route, Routes, useParams } from "react-router-dom";
import { AdminProvider } from "../api/admin";
import { startHistory } from "./history";
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
import AdminSavesPage from "./pages/AdminSavesPage";
import AdminCrashesPage from "./pages/AdminCrashesPage";
import "./admin.css";

export { AdminProvider } from "../api/admin";
export { ConfirmHost } from "./ui";

/** The admin pages (route segment, label, page). */
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
  ["saves", "Saves", AdminSavesPage],
  ["crashes", "Crashes", AdminCrashesPage],
] as const;

startHistory();

/** `/servers/<id>/admin` for absolute links (relative ones break inside the splat route). */
export function adminBase(serverId: string) {
  return `/servers/${encodeURIComponent(serverId)}/admin`;
}

/** The admin pages render inside the server's layout, which also hosts their confirm() dialogs (ConfirmHost). */
function AdminLayout() {
  return <Outlet />;
}

/** Old admin routes that are merged into the server's own pages. */
const MERGED: [string, string][] = [["configs", "configs"], ["mods", "mods"], ["console", "overview"]];

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
          {MERGED.map(([from, to]) => (
            <Route key={from} path={from} element={<Navigate to={`/servers/${encodeURIComponent(id)}/${to}`} replace />} />
          ))}
          <Route path="*" element={<Navigate to={`${adminBase(id)}/players`} replace />} />
        </Route>
      </Routes>
    </AdminProvider>
  );
}
