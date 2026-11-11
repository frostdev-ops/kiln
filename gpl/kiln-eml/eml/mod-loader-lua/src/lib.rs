use std::path::PathBuf;

use kfc::reflection::LookupKey;
use mod_loader::ModEnvironment;

use crate::{alias::Path, cache::{CacheDiff, FileStateCache, Fingerprints}, env::{AppFeatures, AppState}, log::info, runner::LuaModRunner};

// kiln-eml: what a run spent fingerprinting the game's files (bytes read, files reused), for the log and Kiln's state.
pub use cache::FingerprintStats;
// kiln-eml: what a run is doing, for the people waiting for it (progress.rs).
pub use progress::{Event, Progress, ProgressGuard, Sink, Stage, Timing};

mod runner;
mod definition;
mod env;
mod util;
mod log;
mod cache;
mod progress;
mod alias;
mod load;
mod transaction;
mod ownership;
// kiln-eml: restoration needs exact writer ownership, not an input/performance cache.
pub use ownership::{owns_output, adopt, prune, Adoption};
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
    /// What the change cache and the input barriers read of the game's files.
    pub fingerprint: FingerprintStats,
    /// kiln-eml: ShroudForge asset mods (`<id> <version>`) whose prepared output this run's publication replaced.
    pub sf_displaced: Vec<String>,
}

pub fn run(
    env: &ModEnvironment,
    args: RunArgs,
) -> anyhow::Result<RunResult> {
    // kiln-eml: progress lines in the log, no sink
    let progress = Progress::start(None);
    run_with(env, args, &progress)
}

/// The run's fingerprint counters, with what the ownership checks of this run read.
fn stats(prints: &Fingerprints) -> FingerprintStats {
    FingerprintStats { ownership_bytes: ownership::read(), ..prints.stats() }
}

/// `run`, reporting what it does into `progress` (a stage, and while it reads game files the file and the bytes done and
/// to do): see progress.rs.
pub fn run_with(
    env: &ModEnvironment,
    args: RunArgs,
    progress: &Progress,
) -> anyhow::Result<RunResult> {
    info!("Running lua with options: {:?}", args);

    // kiln-eml: one memo of file fingerprints for the whole run (cache/prints.rs): every barrier below re-stats its files
    // and reads only those that changed since they were hashed, so the game data is read once, not four times.
    let mut prints = Fingerprints::new(env.game_dir().as_std_path()).with_progress(progress.clone());
    // what an interrupted run had read, and a journal for this one (removed when it ends well)
    prints.resume_from(env.cache_dir().as_std_path());
    ownership::reset_read();
    let result = run_steps(env, args, progress, &mut prints);
    if result.is_ok() {
        prints.finish();
    }
    result
}

fn run_steps(
    env: &ModEnvironment,
    mut args: RunArgs,
    progress: &Progress,
    prints: &mut Fingerprints,
) -> anyhow::Result<RunResult> {
    let cache_key = format!("{}|{:?}", args.file_name, args.options);
    let file_name = args.file_name.clone();
    let current_cache = (!args.options.skip_cache).then(|| FileStateCache::read(env.cache_dir()));
    if let Some(saved) = &current_cache {
        prints.seed(saved);
    }
    progress.stage(Stage::Checking);
    let mut mods_before = FileStateCache::default();
    track_mods(&mut mods_before, env, prints);

    // check cache if files have changed

    let cache_diff = if let Some(current_cache) = &current_cache {
        let mut new_cache = FileStateCache::default();
        new_cache.configuration(&cache_key);
        // kiln-eml: another build of the cache (or none): this start patches whatever the files say, so no game file is
        // read to be compared with the saved fingerprints (prints.rs)
        if new_cache.diff(current_cache).build_id_changed() {
            prints.patch_certain();
        }

        new_cache.track_game_files(env.game_dir(), prints);
        track_mods(&mut new_cache, env, prints);

        let cache_diff = new_cache.diff(current_cache);

        // kiln-eml: an unchanged start skips the patch only over output Kiln can prove it published. This cache vouches
        // for it when it recorded that receipt's output and every game file is as it recorded them (nothing is read);
        // otherwise it is checked in full once (SHA-256 of the receipt's files and backups), and this cache records it.
        let checked = cache_diff.is_none() && args.options.patch && !args.options.export;
        let owned = if checked {
            let root = env.game_dir().as_std_path();
            match ownership::vouched(root, &file_name, current_cache.owner()) {
                Ok(Some(marker)) => Some(marker),
                _ => {
                    info!("Nothing changed; checking the game data against its ownership receipt (once, until the next patch)");
                    match owns_output(root, &file_name) {
                        Ok(true) => ownership::marker(root, &file_name).ok().flatten(),
                        _ => None,
                    }
                }
            }
        } else { None };
        new_cache.set_owner(if checked { owned.clone() } else { current_cache.owner().map(str::to_owned) });

        // kiln-eml: nothing changed, but this start learned stamps the saved cache lacks (it predates them, or its
        // files were still too fresh to stamp), or its ownership: save them, so the next start reads nothing either.
        if cache_diff.is_none() && (new_cache.stamps_differ(current_cache) || new_cache.owner() != current_cache.owner()) {
            new_cache.write(env.cache_dir());
        }

        if owned.is_some() {
            args.options.patch = false;

            info!("No changes detected, skipping patching");

            if !args.options.runtime {
                return Ok(RunResult { cached: true, fingerprint: stats(prints), ..RunResult::default() });
            }
        }

        cache_diff
    } else {
        info!("Skipping cache check");
        prints.patch_certain();
        CacheDiff::new_dirty()
    };

    // create a new app state

    progress.stage(Stage::Prepare);
    let app_state = match AppState::new(
        env.clone(),
        args,
        &cache_diff,
    ) {
        Ok(context) => context,
        Err(_) => anyhow::bail!("Failed to create AppState"),
    };
    let vanilla_containers = app_state.kfc_file().containers().len();
    progress.stage(Stage::Checking);
    let mut inputs_before = FileStateCache::default();
    inputs_before.track_game_inputs(env.game_dir().as_std_path(), &file_name, vanilla_containers, prints);

    // setup and run all mods

    let runner = LuaModRunner::new(app_state)?;

    match runner.setup(env.mod_registry()) {
        Ok(_) => {},
        Err(err) => {
            anyhow::bail!("Failed to setup LuaModRunner: {}", err);
        }
    }

    info!("Running mods...");
    progress.stage(Stage::Mods);

    // TODO: move this somewhere else, maybe to the GameContext?

    runner.run()?;

    let app_state = runner.lua.app_data_ref::<AppState>().unwrap();

    let mut patched = 0;

    if app_state.has_feature(AppFeatures::PATCH) {
        progress.stage(Stage::Checking);
        let mut inputs_now = FileStateCache::default();
        inputs_now.track_game_inputs(env.game_dir().as_std_path(), &file_name, vanilla_containers, prints);
        if inputs_before.diff(&inputs_now).is_some() {
            anyhow::bail!("Game inputs changed or became unreadable during patch preparation");
        }
        info!("Applying patches...");
        progress.stage(Stage::Writing);

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
        progress.stage(Stage::Saving);
        let mut mods_after = FileStateCache::default();
        track_mods(&mut mods_after, env, prints);
        let mut new_cache = mods_after.clone();
        new_cache.configuration(&cache_key);

        new_cache.track_game_files(env.game_dir(), prints);
        // kiln-eml: these are the fingerprints of the output just published, which its receipt lists exactly
        new_cache.set_owner(ownership::marker(env.game_dir().as_std_path(), &file_name).ok().flatten());
        // Reuse the exact fingerprints being saved; a second independent after-snapshot can hide an ABA edit.
        let inputs_after = new_cache.clone().only_game_inputs(env.game_dir().as_std_path(), &file_name, vanilla_containers);
        if mods_before.diff(&mods_after).is_none() && inputs_before.diff(&inputs_after).is_none() { new_cache.write(env.cache_dir()); }
        else { let _ = std::fs::remove_file(env.cache_dir().join("files.json")); }

    }

    let cached = !app_state.has_feature(AppFeatures::PATCH) && cache_diff.is_none();

    Ok(RunResult {
        cached,
        patched,
        fingerprint: stats(prints),
        sf_displaced: ownership::displaced(),
        dlls: app_state.registered_dlls()
            .into_iter()
            .map(|path| path.into_std_path_buf())
            .collect::<Vec<_>>(),
    })
}

// kiln-eml: an environment from explicit mod paths (ModEnvironment::load_from) tracks exactly those, so switching a
// mod off in a Kiln profile re-patches too; otherwise EML's `<game>/mods`.
fn track_mods(cache: &mut FileStateCache, env: &ModEnvironment, prints: &mut Fingerprints) {
    if env.mod_paths().is_empty() {
        cache.track_mod_files(env.mods_dir(), prints);
    } else {
        for path in env.mod_paths() {
            cache.track_directory(path, prints);
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

    // kiln-eml: the containers the owned live output added (its receipt lists them), read while its header is live
    let root = game_dir.as_ref().as_std_path();
    let added = ownership::live_containers(root, file_name).unwrap_or_else(|error| {
        tracing::warn!(%error, "The receipt's containers cannot be read; the restore leaves them in place");
        None
    });
    let restored = load::restore_backup(&kfc_path).is_ok();
    // ... and are no longer referenced once the vanilla header is back: remove exactly those
    if restored && let Some(added) = added && let Err(error) = ownership::remove_unreferenced(root, file_name, &added) {
        tracing::warn!(%error, "The vanilla data is back; container files EML had added were left in place");
    }
    restored
}
