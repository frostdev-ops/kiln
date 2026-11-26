// Entry.
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createHashRouter, RouterProvider } from "react-router-dom";
import App from "./App";
import { isTauri, loadMock } from "./api/commands";
// the admin pages talk to Kiln.Admin through the launcher's backend (Tauri commands); the hub gets that transport here
import "./api/adminTauri";
import { UiModeGate } from "./deck/mode";
import { SnapshotGate } from "./shell/SnapshotGate";
import { DataFolderGate } from "./shell/DataFolderGate";
import { Started } from "./shell/started";
import { installCloseGuard } from "./shell/closeGuard";
import { setLanguage } from "./i18n";
import "./i18n/locales";
import { LanguageSync, storedLanguage } from "./i18n/LanguageSync";
import "./theme/base.css";

const queryClient = new QueryClient();

// a plain browser (development, the Playwright tests): the mock answers every command, so it is there before the first
// render, as when it was part of the bundle (the desktop app never loads it)
if (!isTauri()) await loadMock();

// the language the last start chose, before the first render (settings load later: i18n/LanguageSync.tsx)
setLanguage(storedLanguage());

// closing the window while work would be lost asks first (the desktop app only: shell/closeGuard.ts)
installCloseGuard();

// a data router (one splat route; App's own <Routes> under it) for its navigation blocker: a page with unsaved work asks
// before any way out (shell/leaveGuard.tsx)
const router = createHashRouter([{
  path: "*",
  element: (
    // a data folder that cannot be used: only its dialog, before either UI (shell/DataFolderGate.tsx)
    <DataFolderGate>
      <SnapshotGate><LanguageSync /><UiModeGate>
        <App />
      </UiModeGate></SnapshotGate>
    </DataFolderGate>
  ),
}]);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {/* tells the backend the first screen is up (shell/started.ts): the Linux launcher's render watchdog */}
    <Started />
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
