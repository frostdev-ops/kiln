// The admin pages in a plain web page, the way Kilnwarden hosts them: @kiln/admin-ui's entry (../web.ts, from
// source) over the WebSocket transport, no launcher around them. The launcher's dev server serves it
// (http://localhost:1420/src/admin/harness/index.html?kap=<ws url>#/servers/<id>/admin/players); a launcher build does
// not contain it. tests/admin-ui-web.spec.ts puts a fake KAP/1-over-WebSocket server behind it.
//   ?kap=  the KAP socket's base URL; the transport connects to <kap>/<server id> (default ws(s)://<this host>/kap)
//   window.__kilnHarnessHost  a test's host (configureAdmin's `host`, as Kilnwarden passes its own), set before the page
//          loads (tests/admin-map-noart.spec.ts); none: no host
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { HashRouter, Navigate, Route, Routes } from "react-router-dom";
import { AdminConfigsPage, AdminRoutes, ConfirmHost, Toaster, configureAdmin, createWebSocketTransport, type AdminHost } from "../web";

const kap = new URLSearchParams(location.search).get("kap") ?? `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/kap`;
const host = (window as unknown as { __kilnHarnessHost?: Partial<AdminHost> }).__kilnHarnessHost;
configureAdmin({ transport: createWebSocketTransport({ url: (serverId) => `${kap}/${encodeURIComponent(serverId)}` }), host });

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={new QueryClient()}>
      <HashRouter>
        <div className="k-app" style={{ height: "100%", overflow: "auto" }}>
          <Routes>
            <Route path="servers/:id/admin/*" element={<AdminRoutes />} />
            {/* as Kilnwarden mounts it: Configs is the server's own page in the launcher */}
            <Route path="servers/:id/configs" element={<AdminConfigsPage />} />
            <Route path="*" element={<Navigate to="/servers/lab/admin/players" replace />} />
          </Routes>
        </div>
        <ConfirmHost />
        <Toaster />
      </HashRouter>
    </QueryClientProvider>
  </StrictMode>,
);
