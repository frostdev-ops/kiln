// Server administration: the admin pages of one server. Mount `<AdminRoutes/>` on `servers/:id/admin/*` (App.tsx does);
// it provides the server's shared admin session (<AdminProvider>) and the pages. The server's grouped list
// (shell/serverNav.ts) links to them; the connection shows in the server's live sentence (pages/servers/ServerLayout.tsx).
// Configs, Mods and the console are merged into the server's own pages (live while it runs); their old admin routes
// redirect there. Pages and hooks: docs/kiln/launcher/admin-pages.md.
import { Navigate, Outlet, Route, Routes, useParams } from "react-router-dom";
import { AdminProvider } from "../api/admin";
import { t } from "../i18n";
import { startHistory } from "./history";
import { ADMIN_MERGED } from "../shell/serverNav";
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

/** The admin pages (route segment, label (call it: the current language), page). */
export const ADMIN_PAGES = [
  ["players", () => t("admin.pages.players"), AdminPlayersPage],
  ["map", () => t("admin.pages.map"), AdminMapPage],
  ["zones", () => t("admin.pages.zones"), AdminZonesPage],
  ["items", () => t("admin.pages.items"), AdminItemsPage],
  ["spawn", () => t("admin.pages.spawn"), AdminSpawnPage],
  ["world", () => t("admin.pages.world"), AdminWorldPage],
  ["combat", () => t("admin.pages.combat"), AdminCombatPage],
  ["creatures", () => t("admin.pages.creatures"), AdminCreaturesPage],
  ["gamedata", () => t("admin.pages.gamedata"), AdminGameDataPage],
  ["permissions", () => t("admin.pages.permissions"), AdminPermissionsPage],
  ["content", () => t("admin.pages.content"), AdminContentPage],
  ["saves", () => t("admin.pages.saves"), AdminSavesPage],
  ["crashes", () => t("admin.pages.crashes"), AdminCrashesPage],
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

/** Old admin routes that are merged into the server's own pages (App.tsx redirects them before this bundle loads;
 *  these routes serve an AdminRoutes mounted elsewhere). */
const MERGED = ADMIN_MERGED;

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
