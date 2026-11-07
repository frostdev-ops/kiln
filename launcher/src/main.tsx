// Entry.
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { HashRouter } from "react-router-dom";
import App from "./App";
import { UiModeGate } from "./deck/mode";
import "./theme/base.css";

const queryClient = new QueryClient();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <HashRouter>
        <UiModeGate>
          <App />
        </UiModeGate>
      </HashRouter>
    </QueryClientProvider>
  </StrictMode>,
);
