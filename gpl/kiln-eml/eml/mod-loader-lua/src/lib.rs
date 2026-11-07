use std::path::PathBuf;

use kfc::reflection::LookupKey;
use mod_loader::ModEnvironment;

use crate::{alias::Path, cache::{CacheDiff, FileStateCache}, env::{AppFeatures, AppState}, log::info, runner::LuaModRunner};

mod runner;
mod definition;
mod env;
mod util;
mod log;
mod cache;
mod alias;
mod load;
mod transaction;
mod lua;

#[derive(Debug, Clone, Default)]
pub struct RunOptions {
    /// If true, it will ignore the cache and re-apply all mods.
    pub skip_cache: bool,
    /// If None, it will auto-detect based on the files within the game directory.
    pub is_server: Option<bool>,
    /// If true, it will apply patches to the game files.
    pub patch: bool,
    /// If true, it will allow mods to use `io.export` to export files to the export directory.
    pub export: bool,
    /// If true, it will allow mods to use `loader.runtime.register_dll` to register DLLs to be loaded at runtime.
    pub runtime: bool,
    /// If None, it will use the default export directory (`<game_dir>/export`).
    pub export_dir: Option<PathBuf>,
}

#[derive(Debug, Clone)]
pub struct RunArgs {
    pub file_name: String,
    /// The features to enable.
    pub options: RunOptions,
}

#[derive(Debug, Clone, Default)]
pub struct RunResult {
    pub dlls: Vec<PathBuf>,
    // kiln-eml: what the run did, for Kiln's state file and log.
    /// No mod or game file changed since the last run: nothing was patched (the game files keep the last patch).
    pub cached: bool,
    /// Resources written by this run (0 when cached or when no mod changed anything).
    pub patched: usize,
}

pub fn run(
    env: &ModEnvironment,
    mut args: RunArgs,
) -> anyhow::Result<RunResult> {
    info!("Running lua with options: {:?}", args);

    let cache_key = format!("{}|{:?}", args.file_name, args.options);
    let file_name = args.file_name.clone();
    let mut mods_before = FileStateCache::default();
    track_mods(&mut mods_before, env);

    // check cache if files have changed

    let cache_diff = if !args.options.skip_cache {
        let current_cache = FileStateCache::read(env.cache_dir());
        let mut new_cache = FileStateCache::default();
        new_cache.configuration(&cache_key);

        new_cache.track_game_files(env.game_dir());
        track_mods(&mut new_cache, env);

        let cache_diff = new_cache.diff(&current_cache);

        if cache_diff.is_none() && args.options.patch && !args.options.export {
            args.options.patch = false;

            info!("No changes detected, skipping patching");

            if !args.options.runtime {
                return Ok(RunResult { cached: true, ..RunResult::default() });
            }
        }

        cache_diff
    } else {
        info!("Skipping cache check");
        CacheDiff::new_dirty()
    };

    // create a new app state

    let app_state = match AppState::new(
        env.clone(),
        args,
        &cache_diff,
    ) {
        Ok(context) => context,
        Err(_) => anyhow::bail!("Failed to create AppState"),
    };
    let vanilla_containers = app_state.kfc_file().containers().len();
    let mut inputs_before = FileStateCache::default();
    inputs_before.track_game_inputs(env.game_dir().as_std_path(), &file_name, vanilla_containers);

    // setup and run all mods

    let runner = LuaModRunner::new(app_state)?;

    match runner.setup(env.mod_registry()) {
        Ok(_) => {},
        Err(err) => {
            anyhow::bail!("Failed to setup LuaModRunner: {}", err);
        }
    }

    info!("Running mods...");

    // TODO: move this somewhere else, maybe to the GameContext?

    runner.run()?;

    let app_state = runner.lua.app_data_ref::<AppState>().unwrap();

    let mut patched = 0;

    if app_state.has_feature(AppFeatures::PATCH) {
        let mut inputs_now = FileStateCache::default();
        inputs_now.track_game_inputs(env.game_dir().as_std_path(), &file_name, vanilla_containers);
        if inputs_before.diff(&inputs_now).is_some() {
            anyhow::bail!("Game inputs changed or became unreadable during patch preparation");
        }
        info!("Applying patches...");

        let mut buf = Vec::new();
        let mut writer = app_state.take_writer();
        let resources = app_state.get_cached_resources();
        let mut applied_resources = 0;

        for resource in resources.iter() {
            let resource_id = &resource.resource_id;
            let value = resource.apply(&runner.lua)
                .map_err(|e| anyhow::anyhow!(
                    "Failed to apply resource {resource_id}: {e}"
                ))?;

            if let Some(value) = value {
                let Some(r#type) = app_state.type_registry()
                    .get_by_hash(LookupKey::Qualified(resource_id.type_hash())) else {
                    anyhow::bail!(
                        "Failed to find type with hash {:#010x} for resource {resource_id}",
                        resource_id.type_hash(),
                    );
                };

                buf.clear();

                if let Err(e) = value.write_into(
                    app_state.type_registry(),
                    r#type,
                    &mut buf,
                ) {
                    anyhow::bail!(
                        "Failed to serialize resource {resource_id} of type {}: {e}",
                        r#type.qualified_name,
                    );
                }

                writer.write_resource(resource_id, &buf)?;
                applied_resources += 1;
            }
        }

        // Wine refuses to replace the live resource file while our vanilla reader still has it open.
        // All Lua/resource reads are complete; close it before publishing any game files.
        app_state.close_reader();
        writer.finalize()?;

        info!("Applied {} patches", applied_resources);
        patched = applied_resources;
    }

    // create a new cache file

    if app_state.has_feature(AppFeatures::PATCH) {
        let mut mods_after = FileStateCache::default();
        track_mods(&mut mods_after, env);
        let mut new_cache = mods_after.clone();
        new_cache.configuration(&cache_key);

        new_cache.track_game_files(env.game_dir());
        // Reuse the exact fingerprints being saved; a second independent after-snapshot can hide an ABA edit.
        let inputs_after = new_cache.clone().only_game_inputs(env.game_dir().as_std_path(), &file_name, vanilla_containers);
        if mods_before.diff(&mods_after).is_none() && inputs_before.diff(&inputs_after).is_none() { new_cache.write(env.cache_dir()); }
        else { let _ = std::fs::remove_file(env.cache_dir().join("files.json")); }

    }

    let cached = !app_state.has_feature(AppFeatures::PATCH) && cache_diff.is_none();

    Ok(RunResult {
        cached,
        patched,
        dlls: app_state.registered_dlls()
            .into_iter()
            .map(|path| path.into_std_path_buf())
            .collect::<Vec<_>>(),
    })
}

// kiln-eml: an environment from explicit mod paths (ModEnvironment::load_from) tracks exactly those, so switching a
// mod off in a Kiln profile re-patches too; otherwise EML's `<game>/mods`.
fn track_mods(cache: &mut FileStateCache, env: &ModEnvironment) {
    if env.mod_paths().is_empty() {
        cache.track_mod_files(env.mods_dir());
    } else {
        for path in env.mod_paths() {
            cache.track_directory(path);
        }
    }
}

pub fn export_lua_definitions(
    game_dir: impl AsRef<Path>,
    file_name: &str,
    force: bool,
) -> bool {
    let game_dir = game_dir.as_ref();
    let cache_dir = game_dir.join(".cache");
    let (type_registry, is_dirty) = match crate::load::load_type_registry(
        game_dir,
        &cache_dir,
        file_name,
    ) {
        Ok(type_registry) => type_registry,
        Err(_) => return false,
    };

    crate::load::export_lua_definitions(
        &cache_dir,
        &type_registry,
        is_dirty || force,
    );

    true
}

pub fn restore(
    game_dir: impl AsRef<Path>,
    file_name: &str,
) -> bool {
    let kfc_path = game_dir.as_ref()
        .join(format!("{file_name}.kfc"));

    load::restore_backup(&kfc_path).is_ok()
}
