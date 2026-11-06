// Routes. Server pages and admin pages live in their own folders; route lines change here.
// Optional server slots (until a page exists, the shell's defaults render):
//   src/pages/servers/ServersPage.tsx   default export: the /servers index (else: redirect to the first server)
//   src/pages/servers/ServerLayout.tsx  default export: a layout with <Outlet/> around every /servers/:id/* page
//   src/pages/servers/ModpackOffer.tsx  default export: the modpack consent modal Play opens (see PlayPage)
import { lazy, type ComponentType } from "react";
import { Navigate, Outlet, Route, Routes } from "react-router-dom";
import { AppShell } from "./shell/AppShell";
import { NotFound } from "./shell/NotFound";
import { ServersIndex } from "./shell/ServersIndex";
import PlayPage from "./pages/PlayPage";
import ProfilesPage from "./pages/profiles/ProfilesPage";
import ProfileLayout from "./pages/profiles/ProfileLayout";
import ProfileModsPage from "./pages/profiles/ProfileModsPage";
import ProfileNativePage from "./pages/profiles/ProfileNativePage";
import ProfileEmfPage from "./pages/profiles/ProfileEmfPage";
import ProfileConfigsPage from "./pages/profiles/ProfileConfigsPage";
import ProfileSavesPage from "./pages/profiles/ProfileSavesPage";
import LibraryPage from "./pages/LibraryPage";
import RuntimePage from "./pages/RuntimePage";
import LogsPage from "./pages/LogsPage";
import SavesPage from "./pages/SavesPage";
import SettingsPage from "./pages/SettingsPage";
import AboutPage from "./pages/AboutPage";

// Server and admin pages load lazily: a page that is mid-edit (or broken) fails alone, inside the shell's error
// boundary, instead of blanking the launcher.
const ServerOverviewPage = lazy(() => import("./pages/servers/ServerOverviewPage"));
const ServerSettingsPage = lazy(() => import("./pages/servers/ServerSettingsPage"));
const ServerModsPage = lazy(() => import("./pages/servers/ServerModsPage"));
const ServerConfigsPage = lazy(() => import("./pages/servers/ServerConfigsPage"));
const ServerBackupsPage = lazy(() => import("./pages/servers/ServerBackupsPage"));
const ServerLogsPage = lazy(() => import("./pages/servers/ServerLogsPage"));
const ServerModpackPage = lazy(() => import("./pages/servers/ServerModpackPage"));
const ServerTicksPage = lazy(() => import("./pages/servers/ServerTicksPage"));
const AdminRoutes = lazy(() => import("./admin").then((m) => ({ default: m.AdminRoutes })));

// The save editor is big (its game data): loaded on first visit.
const EditorPage = lazy(() => import("./pages/EditorPage"));

const slots = import.meta.glob<{ default: ComponentType }>(["./pages/servers/ServersPage.tsx", "./pages/servers/ServerLayout.tsx"]);
const slot = (name: string, fallback: ComponentType): ComponentType => {
  const load = slots[`./pages/servers/${name}.tsx`];
  return load ? lazy(load) : fallback;
};
const ServersPage = slot("ServersPage", ServersIndex);
const ServerLayout = slot("ServerLayout", Outlet);

export default function App() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<PlayPage />} />
        <Route path="profiles" element={<ProfilesPage />} />
        <Route path="profiles/:id" element={<ProfileLayout />}>
          <Route index element={<Navigate to="mods" replace />} />
          <Route path="mods" element={<ProfileModsPage />} />
          <Route path="native" element={<ProfileNativePage />} />
          <Route path="emf" element={<ProfileEmfPage />} />
          <Route path="configs" element={<ProfileConfigsPage />} />
          <Route path="saves" element={<ProfileSavesPage />} />
        </Route>
        <Route path="library" element={<LibraryPage />} />
        <Route path="runtime" element={<RuntimePage />} />
        <Route path="logs" element={<LogsPage />} />
        <Route path="saves" element={<SavesPage />} />
        <Route path="editor" element={<EditorPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="about" element={<AboutPage />} />
        <Route path="servers" element={<ServersPage />} />
        <Route path="servers/:id" element={<ServerLayout />}>
          <Route index element={<Navigate to="overview" replace />} />
          <Route path="overview" element={<ServerOverviewPage />} />
          <Route path="settings" element={<ServerSettingsPage />} />
          <Route path="mods" element={<ServerModsPage />} />
          <Route path="configs" element={<ServerConfigsPage />} />
          <Route path="backups" element={<ServerBackupsPage />} />
          <Route path="logs" element={<ServerLogsPage />} />
          <Route path="ticks" element={<ServerTicksPage />} />
          <Route path="modpack" element={<ServerModpackPage />} />
          <Route path="admin/*" element={<AdminRoutes />} />
        </Route>
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}
