// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln-eml: the ShroudForge-dialect startup pass's own Lua-facing state.
//!
//! - `session`: the reference/type session. One registry, the selected target index's facts and the
//!   worker-local Type cache, shared by every package's `game` table in one startup VM.
//! - `game`: `game.version` and `game.types`, `game.assets` over the VM's one `AssetSession` (catalog
//!   and content; the asset session: `Resource.data`, `create_resource` and the dirty-resource serialization into the one stage).
//!   Later builders extend the same `game` table (guid and the runtime registrar composition); they never replace it.
//! - `buffer_resource`: the Buffer resource port the pass attaches to its VM before any Lua access.
//! - `plan`, `common`, `runner`: the startup pass itself. `plan` holds one attempt's frozen host selections
//!   (discovered membership, the admitted load order, entry selection, features, export root, build catalog, locale);
//!   `common` is one package's registrar (the GPL `game` builders, then sfi-core's one composition with the runtime registrar on the
//!   same table); `runner` runs one attempt end to end on a `StartupHost` generation and hands back the prepared
//!   archive for the sole publisher.
//! - `resource_values`: the typed value codec: validation, conversion, resource bytes; the value wrappers plug in.
//! - `format_adapter`: the pinned parser's name/type lookup helpers on EML's existing kfc APIs, without a format change.
//! - `attributes`: the archive's attribute catalog and ShroudForge's attribute program model, behind the pregame
//!   `runtime.ecs` attribute members; it belongs to the VM's one asset session.
//! - `admission`, `cache`, `retry`: the startup controller's (crate::startup) admission set, the startup cache
//!   of the SF dialect, and the SF pass with its one retry on the start's witness.
//!
//! Nothing here is shipped behaviour yet: the pass composition composes it into the SF-dialect pass (`runner`) and kiln mode wires that pass
//! into the plugin. All Lua and every cache here stay on the one serial interpreter worker that owns the VM (`Rc`, no
//! `Send`).
//!
//! Adapted from ShroudForge 1.7.9, Kiln's fork pin 2df1de68 (these files are the same at d8a394bc):
//! - modules/mod-runtime/lua/bindings/game/{mod,types,resolve_type}.rs;
//! - modules/mod-runtime/reflection/traverse_reflected_types.rs (inheritance, effective fields);
//! - modules/mod-runtime/session/mod.rs (`AppState::get_type`, the per-session Type cache);
//! - modules/mod-runtime/lua/readonly_collections.rs (ReadOnlyMap, ReadOnlyArray);
//! - modules/mod-runtime/lua/bindings/register_functions.rs (`add_function`);
//! - modules/mod-runtime/lua/bindings/game/assets/{mod,access_file_resources,access_file_content,content,resource}.rs,
//!   modules/mod-runtime/lua/bindings/game/content_id_casts.rs and modules/mod-runtime/session/asset_resources.rs
//!   (game/{assets,casts,content,resource}.rs, the asset session);
//! - modules/mod-runtime/startup/commit_asset_changes.rs (the dirty-resource serialization in game/assets.rs, the asset session) and
//!   modules/mod-runtime/lua/bindings/buffer/mod.rs:345-364,457-480 (buffer_resource.rs, the asset session);
//! - modules/mod-runtime/lua/resource_values/{mod,validate_resource_values,convert_resource_values,value_path}.rs, the
//!   container walks of lua/resource_values/owned_values.rs, the Guid cast of lua/bindings/game/content_id_casts.rs and
//!   lua/values/encode_owned_value.rs (resource_values, the codec).
//! - modules/mod-runtime/lua/resource_values/{owned_values,mapped_values}.rs and the wrapper half of
//!   resource_values/mod.rs (`is_dirty_lua_value`, `mapped_source_bytes`, `push`/`insert`/`pop`/`remove`/`clear`,
//!   `type_of`) (resource_values owned, mapped and objects, the value wrappers).
//! - the same resource_values sources and the Guid cast once more, as the typed graph's nodes and its reader-driven
//!   validator (crate::archive_values::{node,input}, the typed graph).
//! - modules/mod-runtime/startup/run_startup_mods.rs:18-97, modules/mod-runtime/lua/mod_runner.rs:49-74,99-135,
//!   modules/mod-runtime/lua/require_mod.rs:70-175, modules/mod-runtime/lua/bindings/mod.rs:66-114,
//!   modules/mod-runtime/lua/eml/v1/create_loader_api.rs:7-47 and modules/modloader/mod_manifest_types.rs
//!   `requires_pregame` (plan, common and runner, the pass composition).
//! - modules/mod-runtime/lua/ingame/{mod,check_ingame_access,set_profile_patch_enabled,impact/mod,access_named_service}.rs,
//!   lua/ingame/{ecs,world,player_input,functions,overrides}/*, lua/values/*.rs and modules/transport/lua/network.rs
//!   (pregame_runtime, the pregame runtime; the overrides' two Lua files verbatim).
//! - modules/mod-runtime/reflection/{build_attribute_catalog,evaluate_attribute_program}.rs (attributes, the attribute catalog),
//!   lua/ingame/ecs/attributes/*.rs, lua/convert_json.rs and lua/ingame/resources/*.rs (pregame_runtime, the attribute catalog).
//!
//! `format_adapter` adapts a file of ShroudForge's kfc-parser submodule instead: GPL-3.0-or-later, see its header.
//!
//! Those parts are:
//!   MIT License
//!
//!   Copyright (c) 2026 bonsaibauer
//!
//!   Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
//!   documentation files (the "Software"), to deal in the Software without restriction, including without limitation
//!   the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
//!   permit persons to whom the Software is furnished to do so, subject to the following conditions:
//!
//!   The above copyright notice and this permission notice shall be included in all copies or substantial portions of
//!   the Software.
//!
//!   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
//!   THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//!   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
//!   TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//!   SOFTWARE.

// The archive's attribute catalog and the attribute program model.
pub mod attributes;
// The startup controller's admission set, startup cache and the SF pass with its retry (startup.rs runs them).
pub mod admission;
// Buffer:read_resource / Buffer:write_resource on the core Buffer.
pub mod buffer_resource;
pub mod cache;
// One package's registrar (GPL game builders + sfi-core's composition with the runtime registrar).
pub mod common;
pub mod format_adapter;
pub mod game;
// The startup change journal (what each package's entrypoint changed), recorded by the asset session.
pub mod journal;
// The member paths each package changed in a resource (format 2 of the ShroudForge dialect's journal).
pub mod journal_paths;
// One attempt's frozen host selections.
pub mod plan;
// The runtime families of the pregame VM (ecs, impact, services, overrides, world, network, patch, functions,
// values) with ShroudForge's pregame behaviour.
pub mod pregame_runtime;
pub mod readonly;
pub mod retry;
// The typed value codec (validator, converter, path) behind the typed-object seam.
pub mod resource_values;
// One startup attempt end to end on a StartupHost generation.
pub mod runner;
pub mod session;

pub use session::{SelectedTarget, TypeRecognizer, TypeSession};
