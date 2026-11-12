// Entry.
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { HashRouter } from "react-router-dom";
import App from "./App";
import { isTauri, loadMock } from "./api/commands";
import { UiModeGate } from "./deck/mode";
import { DataFolderGate } from "./shell/DataFolderGate";
import { Started } from "./shell/started";
import "./theme/base.css";

const queryClient = new QueryClient();

// a plain browser (development, the Playwright tests): the mock answers every command, so it is there before the first
// render, as when it was part of the bundle (the desktop app never loads it)
if (!isTauri()) await loadMock();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {/* tells the backend the first screen is up (shell/started.ts): the Linux launcher's render watchdog */}
    <Started />
    <QueryClientProvider client={queryClient}>
      <HashRouter>
        {/* a data folder that cannot be used: only its dialog, before either UI (shell/DataFolderGate.tsx) */}
        <DataFolderGate>
          <UiModeGate>
            <App />
          </UiModeGate>
        </DataFolderGate>
      </HashRouter>
    </QueryClientProvider>
  </StrictMode>,
);
