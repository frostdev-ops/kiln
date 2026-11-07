//! The launcher's Tauri commands, one file per area (contracts.md section 9). The command list here, the handler
//! list in lib.rs and `COMMANDS` in launcher/src/api/commands.ts are the same (tested).

pub mod app;
pub mod game;
pub mod runtime;
pub mod profile;
pub mod library;
pub mod config;
pub mod launch;
pub mod logs;
pub mod saves;
pub mod editor;
pub mod server;
pub mod admin;
pub mod modpack;
pub mod update;
pub mod deck;

/// A background task id; the task reports through `kiln://task`.
pub type TaskId = String;

/// Every command name, in contracts.md order.
pub const NAMES: &[&str] = &[
    "app_info",
    "app_settings_get",
    "app_settings_set",
    "app_open_path",
    "app_open_url",
    "task_list",
    "task_get",
    "task_cancel",
    "game_list",
    "game_detect",
    "game_add",
    "game_remove",
    "game_status",
    "runtime_list",
    "runtime_import",
    "runtime_status",
    "runtime_install",
    "runtime_uninstall",
    "runtime_rollback",
    "runtime_classify",
    "profile_list",
    "profile_get",
    "profile_create",
    "profile_save",
    "profile_delete",
    "profile_resolve",
    "profile_activate",
    "profile_deactivate",
    "profile_active",
    "library_list",
    "library_detect",
    "library_import",
    "library_remove",
    "library_lint",
    "config_list",
    "config_read",
    "config_set",
    "config_write",
    "config_reset",
    "config_native_state",
    "config_native_set",
    "launch_play",
    "launch_state",
    "logs_list",
    "logs_read",
    "logs_follow",
    "logs_unfollow",
    "logs_crash_reports",
    "saves_list",
    "saves_backups",
    "saves_backup",
    "saves_restore",
    "saves_delete",
    "editor_list_saves",
    "editor_file_info",
    "editor_read_file",
    "editor_zstd_decompress",
    "editor_zstd_compress",
    "editor_game_running",
    "editor_backup_file",
    "editor_write_save",
    "editor_list_backups",
    "editor_restore_backup",
    "editor_open_backups",
    "server_list",
    "server_get",
    "server_create",
    "server_import",
    "server_remove",
    "server_status",
    "server_start",
    "server_stop",
    "server_restart",
    "server_update",
    "server_deploy",
    "server_settings_get",
    "server_settings_set",
    "server_mods",
    "server_mods_sync",
    "server_mod_enable",
    "server_backups",
    "server_backup_create",
    "server_backup_restore",
    "server_query",
    "server_modpack_status",
    "server_modpack_build",
    "server_modpack_publish",
    "admin_connect",
    "admin_disconnect",
    "admin_sessions",
    "admin_call",
    "admin_subscribe",
    "admin_unsubscribe",
    "modpack_pending",
    "modpack_inspect",
    "modpack_install",
    "modpack_dismiss",
    "config_kiln_read",
    "config_kiln_set",
    "config_kiln_reset",
    "app_assets_status",
    "app_assets_import",
    "server_console_open",
    "server_console_close",
    "update_status",
    "update_check",
    "update_install",
    "update_restart",
    "update_skip",
    "update_ack",
    "library_add",
    "profile_revert",
    "profile_ensure_default",
    "deck_browse",
    "deck_gamepad",
    "deck_fullscreen",
    "deck_quit",
];

#[cfg(test)]
mod tests {
    #[test]
    fn names_match_commands_ts() {
        let ts = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/../src/api/commands.ts")).unwrap();
        let start = ts.find("export const COMMANDS = [").expect("COMMANDS in commands.ts");
        let end = start + ts[start..].find("] as const").unwrap();
        let listed: Vec<&str> = ts[start..end].split('"').skip(1).step_by(2).collect();
        assert_eq!(listed, super::NAMES);
    }

    #[test]
    fn handler_list_matches_names() {
        let lib = include_str!("../lib.rs");
        let start = lib.find("generate_handler![").expect("generate_handler! in lib.rs");
        let block = &lib[start..start + lib[start..].find(']').unwrap()];
        let registered: Vec<&str> = block.split("commands::").skip(1).filter_map(|s| s.split("::").nth(1))
            .map(|s| s.split(|c: char| !c.is_alphanumeric() && c != '_').next().unwrap()).collect();
        assert_eq!(registered, super::NAMES);
    }
}
