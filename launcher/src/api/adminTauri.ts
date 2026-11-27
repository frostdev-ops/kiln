// The launcher's admin transport and host: the Tauri commands admin_connect, admin_call, admin_subscribe, ... and the
// `kiln://admin` event (src-tauri/src/commands/admin.rs, the Rust AdminHub: handshake, pings, reconnects), exactly the
// calls the admin hub made before the transport seam; and what the pages need from the launcher around them (the map
// art, a stopped server's crash reports, restarting a server the launcher manages). Launcher only: the admin pages'
// package (@kiln/admin-ui) never imports this module. src/main.tsx imports it first, so the hub has its transport
// before anything renders.
//
// Two things named "restart" stay apart: Kiln.Admin's op `server_restart` (the server restarts itself) is an op like
// any other, an `admin_call` through this transport (kapOps.serverRestart); the launcher's own Tauri command
// `server_restart` (it stops and starts a server it manages) is only launcherAdminHost.restartManagedServer.
import { convertFileSrc } from "@tauri-apps/api/core";
import { useAssetsStatus } from "../components/GameArt";
import { configureAdmin, type AdminHost, type AdminTransport } from "./admin";
import {
  adminCall, adminConnect, adminDisconnect, adminSubscribe, adminUnsubscribe, isTauri, logsCrashReports, onKilnEvent, serverRestart,
} from "./commands";

export const tauriAdminTransport: AdminTransport = {
  name: "tauri",
  kinds: ["auto", "tcp", "file", "ssh"],
  connect: (serverId, want) => adminConnect(serverId, want),
  disconnect: (session) => adminDisconnect(session).then(() => undefined),
  call: (session, op, args) => adminCall(session, op, args),
  subscribe: (session, topic) => adminSubscribe(session, topic).then(() => undefined),
  unsubscribe: (session, topic) => adminUnsubscribe(session, topic).then(() => undefined),
  listen: (handler) => onKilnEvent("kiln://admin", handler),
};

export const launcherAdminHost: AdminHost = {
  /** a release build: the `kiln-asset` protocol (src-tauri/src/assets.rs), prefix admin/; dev and a plain browser: the
   *  default (/assets-local/, vite serves launcher/assets-local) */
  assetsRoot: () => {
    if (!isTauri() || import.meta.env.DEV) return undefined;
    const base = convertFileSrc("", "kiln-asset");
    return `${base.endsWith("/") ? base : `${base}/`}admin/`;
  },
  useArtStatus: () => useAssetsStatus().data?.admin,
  /** the launcher's own words for a map without art (its Game art prepares it) */
  useMapNoArt: () => undefined,
  crashReportsFromDisk: (serverId) => logsCrashReports({ server: serverId }),
  restartManagedServer: (serverId) => serverRestart(serverId),
};

configureAdmin({ transport: tauriAdminTransport, host: launcherAdminHost });
