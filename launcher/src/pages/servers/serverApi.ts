// Servers pages: shared queries and the fields the backend adds to the contract types.
// The additive fields (runner, prefix, schema, kind, ...) are in kiln_manage::server; until launcher/src/api/commands.ts
// carries them, these local types widen the contract ones.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { invoke as tauriInvoke } from "@tauri-apps/api/core";
import { listen as tauriListen } from "@tauri-apps/api/event";
import { useEffect } from "react";
import { create } from "zustand";
import * as api from "../../api/commands";
import { mockInvoke, mockListen } from "../../api/mock";
import type { ServerId, ServerInstance, ServerMod, ServerSettings, ServerSpec, ServerStatus } from "../../api/commands";

export type ServerRunner = "kilnctl" | "steam" | "windows" | "remote";

export interface ServerInstanceX extends ServerInstance {
  runner?: ServerRunner;
  prefix?: string | null;
  backupDir?: string | null;
  kilnctl?: string | null;
  stopTimeoutS?: number;
}

export interface ServerSpecX extends ServerSpec {
  wine?: string | null;
  skipSteamcmd?: boolean;
}

export interface GameSettingSpec {
  key: string;
  group: string;
  label: string;
  kind: "factor" | "bool" | "enum" | "minutes" | "int";
  min?: number;
  max?: number;
  options?: string[];
  hint?: string;
}

export interface SettingsSchema {
  gameSettings: GameSettingSpec[];
  presets: string[];
  permissions: { key: string; field: string; label: string }[];
}

export interface ServerSettingsX extends ServerSettings {
  schema?: SettingsSchema;
  running?: boolean;
}

export interface ServerModX extends ServerMod {
  kind?: "kiln" | "native" | null;
}

export const sk = {
  list: ["server_list"] as const,
  get: (id: ServerId) => ["server_get", id] as const,
  status: (id: ServerId) => ["server_status", id] as const,
  settings: (id: ServerId) => ["server_settings_get", id] as const,
  mods: (id: ServerId) => ["server_mods", id] as const,
  backups: (id: ServerId) => ["server_backups", id] as const,
  query: (id: ServerId) => ["server_query", id] as const,
  modpack: (id: ServerId) => ["server_modpack_status", id] as const,
};

export const useServers = () => useQuery({ queryKey: sk.list, queryFn: () => api.serverList() as Promise<ServerInstanceX[]> });
export const useServer = (id: ServerId) =>
  useQuery({ queryKey: sk.get(id), queryFn: () => api.serverGet(id) as Promise<ServerInstanceX>, enabled: !!id });
export const useServerStatus = (id: ServerId) =>
  useQuery({ queryKey: sk.status(id), queryFn: () => api.serverStatus(id), enabled: !!id, refetchInterval: 5000 });

/** Refreshes a server's queries when the backend reports a status change (kiln://server). */
export function useServerEvents() {
  const qc = useQueryClient();
  useEffect(() => {
    let off: (() => void) | undefined;
    let gone = false;
    api.onKilnEvent("kiln://server", (e) => {
      qc.setQueryData(sk.status(e.serverId), e.status);
      qc.invalidateQueries({ queryKey: sk.list });
      qc.invalidateQueries({ queryKey: sk.settings(e.serverId) });
    }).then((f) => (gone ? f() : (off = f)), () => {});
    return () => {
      gone = true;
      off?.();
    };
  }, [qc]);
}

export function stateTone(s: ServerStatus["state"] | undefined): "ok" | "warn" | "err" | "idle" | "info" {
  switch (s) {
    case "running": return "ok";
    case "starting":
    case "stopping": return "warn";
    case "stopped": return "idle";
    default: return "info";
  }
}

/** Kiln is in the server folder (installed, or running with it). Unknown (remote, no status yet) counts as yes. */
export function hasKiln(srv: ServerInstanceX | undefined, st: ServerStatus | undefined): boolean {
  if (!srv || !st || srv.runner === "remote" || srv.kind === "remote") return true;
  return !!(st.installed || st.kiln);
}

/** The Install/Update Kiln dialog of the server head, opened from the head's menu or from Overview. */
export const useDeployDialog = create<{ open: boolean; show: (open: boolean) => void }>((set) => ({
  open: false,
  show: (open) => set({ open }),
}));

export const runnerLabel: Record<ServerRunner, string> = {
  kilnctl: "kilnctl (Wine)",
  steam: "Steam",
  windows: "Windows",
  remote: "Remote (SSH)",
};


// ---------------------------------------------------------------- console
// Contract additions: `server_console_open`, `server_console_close`, the `kiln://console` event (contracts.md section
// 9/10 diff in the server-console report). Until launcher/src/api/commands.ts carries them, these wrappers reach the
// backend the way commands.ts does (Tauri invoke, else the mock layer). Then: replace them with the commands.ts ones.

export type ConsoleLevel = "info" | "warn" | "error" | "debug" | "meta";
export interface ConsoleLine {
  text: string;
  level: ConsoleLevel;
}
/** capture: the output of a launcher (or `kilnctl start`) run; gamelog: started elsewhere, the game log; none: no output yet. */
export type ConsoleSource = "capture" | "gamelog" | "none";
export interface ConsoleSnapshot {
  source: ConsoleSource;
  path: string | null;
  lines: ConsoleLine[];
  /** older lines of the run were left out */
  truncated: boolean;
}
export interface ConsoleOpen extends ConsoleSnapshot {
  subscription: string;
  serverId: ServerId;
}
/** `kiln://console`: `reset` = `lines` replace everything (a start, a stop, a start outside the launcher). */
export interface ConsoleEvent {
  subscription: string;
  serverId: ServerId;
  reset: boolean;
  source: ConsoleSource;
  path: string | null;
  truncated: boolean;
  lines: ConsoleLine[];
}

async function callX<T>(cmd: string, args: Record<string, unknown>): Promise<T> {
  try {
    return api.isTauri() ? await tauriInvoke<T>(cmd, args) : await mockInvoke<T>(cmd, args);
  } catch (e) {
    throw api.toKilnError(e);
  }
}

/** The server's last output lines and a subscription for the next ones (`kiln://console`). */
export const serverConsoleOpen = (serverId: ServerId, maxLines: number | null = null) => callX<ConsoleOpen>("server_console_open", { serverId, maxLines });
export const serverConsoleClose = (subscription: string) => callX<null>("server_console_close", { subscription });
export async function onServerConsole(handler: (e: ConsoleEvent) => void): Promise<() => void> {
  if (api.isTauri()) return tauriListen<ConsoleEvent>("kiln://console", (e) => handler(e.payload));
  return mockListen("kiln://console", handler as (payload: unknown) => void);
}

export type { ServerSettings, ServerMod };
