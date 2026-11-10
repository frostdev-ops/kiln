//! Kiln Launcher backend.
//! Every command is registered here; `commands::NAMES` and launcher/src/api/commands.ts list the same names (tested).
//! Also: the background watcher (`kiln://game`, `kiln://last-server`, `kiln://log`), the `kiln-asset` protocol that
//! serves the save editor's local game assets, the self-update (`kiln://update`, selfupdate/; only in builds with the
//! `self-update` feature, the direct builds: the Nexus Mods build has none, distribution.rs) and the headless
//! `--selftest` mode.

pub mod assets;
pub mod commands;
pub mod deck;
pub mod distribution;
pub mod events;
pub mod selftest;
/// The self-update: only in direct builds (feature `self-update`); the Nexus Mods build compiles none of it.
#[cfg(feature = "self-update")]
pub mod selfupdate;
pub mod state;
pub mod watch;

/// `--selftest [--out <file>] [--keep]`: runs the Rust side headless and returns the exit code (see selftest.rs).
pub fn selftest() -> i32 {
    let args: Vec<String> = std::env::args().collect();
    selftest::run(&args)
}

/// The launcher was started with an nxm:// link: queue it (or hand another game's link on). False: exit, the link went
/// to the running launcher or to another mod manager (commands/nexus.rs).
pub fn nxm_entry(url: &str) -> bool {
    commands::nexus::nxm_entry(url)
}

pub fn run() {
    let builder = tauri::Builder::default().plugin(tauri_plugin_dialog::init());
    #[cfg(feature = "self-update")]
    let builder = builder.plugin(tauri_plugin_updater::Builder::new().build());
    builder
        .register_uri_scheme_protocol("kiln-asset", |_ctx, req| assets::serve(req.uri().path()))
        .setup(|app| {
            watch::start(app.handle().clone());
            let shipped = app.handle().clone();
            std::thread::spawn(move || commands::runtime::import_shipped(&shipped));
            // kiln://last-server comes from the modpack watcher (commands/modpack.rs)
            commands::modpack::watch_last_server(app.handle());
            // nxm:// links (Nexus Mods' Mod Manager Download): the instance lock and the link inbox (commands/nexus.rs)
            commands::nexus::start(app.handle());
            // self-update: the state for this start, then a check after 8 s and every 6 hours (crate::selfupdate)
            #[cfg(feature = "self-update")]
            selfupdate::start(app.handle().clone());
            // the Nexus Mods build never updates itself (crate::distribution); launcher.log says so once
            #[cfg(not(feature = "self-update"))]
            commands::app::launcher_log("update", &format!(
                "Kiln Launcher {} (pid {}), Nexus Mods build: no self-update, no update checks; new versions: {}",
                app.package_info().version, std::process::id(), distribution::NEXUS_PAGE));
            #[cfg(any(debug_assertions, feature = "dev-eval"))]
            if let Some(dir) = std::env::var_os("KILN_DEV_EVAL_DIR").filter(|d| !d.is_empty()) {
                dev_eval(app.handle().clone(), dir.into());
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::app::app_info,
            commands::app::app_settings_get,
            commands::app::app_settings_set,
            commands::app::app_open_path,
            commands::app::app_open_url,
            commands::app::task_list,
            commands::app::task_get,
            commands::app::task_cancel,
            commands::game::game_list,
            commands::game::game_detect,
            commands::game::game_add,
            commands::game::game_remove,
            commands::game::game_status,
            commands::runtime::runtime_list,
            commands::runtime::runtime_import,
            commands::runtime::runtime_status,
            commands::runtime::runtime_install,
            commands::runtime::runtime_uninstall,
            commands::runtime::runtime_rollback,
            commands::runtime::runtime_classify,
            commands::profile::profile_list,
            commands::profile::profile_get,
            commands::profile::profile_create,
            commands::profile::profile_save,
            commands::profile::profile_delete,
            commands::profile::profile_resolve,
            commands::profile::profile_activate,
            commands::profile::profile_deactivate,
            commands::profile::profile_active,
            commands::library::library_list,
            commands::library::library_detect,
            commands::library::library_import,
            commands::library::library_remove,
            commands::library::library_remove_many,
            commands::library::library_unused,
            commands::library::library_users,
            commands::library::library_lint,
            commands::config::config_list,
            commands::config::config_read,
            commands::config::config_set,
            commands::config::config_write,
            commands::config::config_reset,
            commands::config::config_native_state,
            commands::config::config_native_set,
            commands::launch::launch_play,
            commands::launch::launch_state,
            commands::logs::logs_list,
            commands::logs::logs_read,
            commands::logs::logs_follow,
            commands::logs::logs_unfollow,
            commands::logs::logs_crash_reports,
            commands::saves::saves_list,
            commands::saves::saves_backups,
            commands::saves::saves_backup,
            commands::saves::saves_restore,
            commands::saves::saves_delete,
            commands::editor::editor_list_saves,
            commands::editor::editor_file_info,
            commands::editor::editor_read_file,
            commands::editor::editor_zstd_decompress,
            commands::editor::editor_zstd_compress,
            commands::editor::editor_game_running,
            commands::editor::editor_backup_file,
            commands::editor::editor_write_save,
            commands::editor::editor_list_backups,
            commands::editor::editor_restore_backup,
            commands::editor::editor_open_backups,
            commands::server::server_list,
            commands::server::server_get,
            commands::server::server_create,
            commands::server::server_import,
            commands::server::server_remove,
            commands::server::server_status,
            commands::server::server_start,
            commands::server::server_stop,
            commands::server::server_restart,
            commands::server::server_update,
            commands::server::server_deploy,
            commands::server::server_settings_get,
            commands::server::server_settings_set,
            commands::server::server_mods,
            commands::server::server_mods_sync,
            commands::server::server_mod_enable,
            commands::server::server_backups,
            commands::server::server_backup_create,
            commands::server::server_backup_restore,
            commands::server::server_query,
            commands::server::server_modpack_status,
            commands::server::server_modpack_build,
            commands::server::server_modpack_publish,
            commands::admin::admin_connect,
            commands::admin::admin_disconnect,
            commands::admin::admin_sessions,
            commands::admin::admin_call,
            commands::admin::admin_subscribe,
            commands::admin::admin_unsubscribe,
            commands::modpack::modpack_pending,
            commands::modpack::modpack_inspect,
            commands::modpack::modpack_install,
            commands::modpack::modpack_dismiss,
            commands::config::config_kiln_read,
            commands::config::config_kiln_set,
            commands::config::config_kiln_reset,
            commands::app::app_assets_status,
            commands::app::app_assets_import,
            commands::server::server_console_open,
            commands::server::server_console_close,
            commands::update::update_status,
            commands::update::update_check,
            commands::update::update_install,
            commands::update::update_restart,
            commands::update::update_skip,
            commands::update::update_ack,
            commands::library::library_add,
            commands::library::library_add_to_profile,
            commands::profile::profile_revert,
            commands::profile::profile_ensure_default,
            commands::deck::deck_browse,
            commands::deck::deck_gamepad,
            commands::deck::deck_fullscreen,
            commands::deck::deck_quit,
            commands::nexus::nexus_account,
            commands::nexus::nexus_sign_in,
            commands::nexus::nexus_sign_out,
            commands::nexus::nexus_search,
            commands::nexus::nexus_mod,
            commands::nexus::nexus_installed,
            commands::nexus::nexus_download,
            commands::nexus::nexus_links,
            commands::nexus::nexus_link_install,
            commands::nexus::nexus_link_dismiss,
            commands::nexus::nexus_handler_status,
            commands::nexus::nexus_handler_set,
            commands::engines::engine_settings_get,
            commands::engines::engine_settings_set,
            commands::engines::engine_status,
            commands::engines::eml_backup,
            commands::engines::eml_restore,
            commands::compat::chain_view,
            commands::compat::chain_set_late,
            commands::found::found_scan,
            commands::found::found_add,
            commands::logs::report_save,
            commands::logs::report_summary
        ])
        .build(tauri::generate_context!())
        .expect("error while building the Kiln Launcher")
        .run(|_, event| {
            if matches!(event, tauri::RunEvent::Exit) { deck::gamepad::stop(); }
        });
}

/// Debug builds (and QA builds with the `dev-eval` feature) only: runs every `*.js` dropped into `$KILN_DEV_EVAL_DIR`
/// in the main webview (then deletes it). Lets headless checks (Xvfb) drive the real app: navigate, click, fill.
/// Never compiled into the shipped bundles (tools/launcher/build-*.sh do not enable the feature).
#[cfg(any(debug_assertions, feature = "dev-eval"))]
fn dev_eval(app: tauri::AppHandle, dir: std::path::PathBuf) {
    use tauri::Manager;
    std::thread::spawn(move || loop {
        let mut files: Vec<_> = std::fs::read_dir(&dir).into_iter().flatten().flatten().map(|e| e.path())
            .filter(|p| p.extension().is_some_and(|x| x == "js")).collect();
        files.sort();
        for f in files {
            if let Ok(js) = std::fs::read_to_string(&f) {
                if let Some(w) = app.get_webview_window("main") {
                    let _ = w.eval(&js);
                }
            }
            let _ = std::fs::remove_file(&f);
        }
        std::thread::sleep(std::time::Duration::from_millis(250));
    });
}

#[cfg(test)]
mod tests {
    #[test]
    fn embedded_updater_config_matches_the_feature() {
        let context: tauri::Context<tauri::Wry> = tauri::generate_context!();
        assert_eq!(context.config().plugins.0.contains_key("updater"), cfg!(feature = "self-update"));
        #[cfg(feature = "self-update")]
        assert!(context.config().plugins.0["updater"].get("pubkey").is_some());
    }

    #[test]
    fn selftest_passes() {
        assert_eq!(crate::selftest::run(&["kiln-launcher".into(), "--selftest".into()]), 0);
    }
}

