// Mock fallback inside Tauri. While kiln-manage still answers `not_implemented` for a read-only
// command, the desktop app can show the browser mock's answer instead, so pages stay usable during development.
// On in dev builds; off in release builds unless localStorage "kiln.mockFallback" = "1" ("0" turns it off).
// Never for commands that change anything. The sidebar shows a MOCK flag while any answer came from here.
import { create } from "zustand";

const READ_ONLY = new Set([
  "app_settings_get", "task_list", "game_list", "game_status", "runtime_list", "runtime_status", "runtime_classify",
  "profile_list", "profile_get", "profile_resolve", "profile_active", "library_list", "library_detect",
  "config_list", "config_read", "config_native_state", "config_kiln_read", "launch_state", "logs_list", "logs_read",
  "logs_crash_reports", "saves_list", "saves_backups", "editor_list_saves", "editor_game_running", "editor_list_backups",
  "server_list", "server_get", "server_status", "server_settings_get", "server_mods", "server_backups", "server_query",
  "server_modpack_status", "modpack_pending", "admin_sessions",
]);

export function fallbackEnabled(): boolean {
  const v = typeof localStorage !== "undefined" ? localStorage.getItem("kiln.mockFallback") : null;
  if (v === "0") return false;
  return v === "1" || import.meta.env.DEV;
}

export function canFallBack(cmd: string): boolean {
  return READ_ONLY.has(cmd) && fallbackEnabled();
}

const useFallbackStore = create<{ cmds: string[] }>(() => ({ cmds: [] }));

export function noteFallback(cmd: string) {
  const s = useFallbackStore.getState();
  if (!s.cmds.includes(cmd)) useFallbackStore.setState({ cmds: [...s.cmds, cmd].sort() });
}

/** Commands answered by the mock so far (Tauri only). */
export const useMockFallback = () => useFallbackStore((s) => s.cmds);
export const mockFallbackActive = (cmds: string[]) => cmds.length > 0;
