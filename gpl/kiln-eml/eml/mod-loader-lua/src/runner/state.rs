use std::{cell::{Ref, RefCell}, collections::{HashMap, HashSet}, rc::Rc};

use mlua::Table;
use mod_loader::{FileSystem, Mod, ModManifest, ReadFailure}; // kiln-eml: ReadFailure
use parking_lot::MutexGuard;

use crate::{log::error, lua::{LuaError, LuaValue}, util::RequirePath}; // kiln-eml: log::error

pub struct ModLuaState {
    r#mod: Mod,
    env: Table,
}

impl ModLuaState {

    #[inline]
    pub fn info(&self) -> &ModManifest {
        self.r#mod.info()
    }

    #[inline]
    pub fn fs(&self) -> MutexGuard<FileSystem> {
        self.r#mod.fs()
    }

}

#[derive(Default)]
pub struct RunnerState {
    mods: RefCell<HashMap<String, ModLuaState>>,
    loaded: RefCell<HashMap<String, LuaValue>>,
    is_loading: RefCell<HashSet<String>>,
}

impl RunnerState {

    #[inline]
    pub fn mods(&self) -> Ref<HashMap<String, ModLuaState>> {
        self.mods.borrow()
    }

    pub fn add_mod(
        &self,
        r#mod: Mod,
        env: Table,
    ) {
        let id = r#mod.info().id.clone();
        let state = ModLuaState {
            r#mod,
            env,
        };

        self.mods.borrow_mut().insert(id, state);
    }

    // kiln-eml: logs one error line for a module that could not be read and builds the Lua error naming it.
    fn module_load_error(
        r#mod: &Mod,
        mod_id: &str,
        qualified_id: &str,
        path: &str,
        fs: &FileSystem,
        why: ReadFailure,
    ) -> mlua::Error {
        let since_load_ms = r#mod.age().as_millis() as u64;
        let since = format!("{:.1}s", since_load_ms as f64 / 1000.0);

        error!(
            mod_id,
            module = qualified_id,
            path,
            root = fs.root().as_str(),
            since_load_ms,
            "module {qualified_id} could not be read {since} after the mod was loaded: {why}",
        );

        LuaError::module_load(qualified_id, path, format!("{why}; mod loaded {since} ago"))
    }

    pub fn require(
        &self,
        qualified_id: &str,
        lua: &mlua::Lua,
    ) -> mlua::Result<LuaValue> {
        if let Some(table) = self.loaded.borrow().get(qualified_id) {
            return Ok(table.clone());
        }

        // check for circular dependencies
        if self.is_loading.borrow().contains(qualified_id) {
            return Err(LuaError::circular_dependency(qualified_id));
        }

        self.is_loading.borrow_mut().insert(qualified_id.to_string());

        // load the module
        let require_path = RequirePath::parse_qualified(qualified_id)?;

        let func = {
            let mods = self.mods.borrow();
            let mod_state = mods.get(require_path.mod_id())
                .ok_or_else(|| LuaError::mod_not_found(require_path.mod_id()))?;

            let mut path = String::new();

            if !require_path.path().is_empty() {
                path.push_str("src");

                for segment in require_path.path().split('.') {
                    path.push('/');
                    path.push_str(segment);
                }

                path.push_str(".lua");
            }

            let mut fs = mod_state.fs();
            // kiln-eml: a failed read says which stage failed (root, folder, file, canonicalize, open, read), whether
            // the mod folder is still there and how long ago the mod was loaded, in eml.log and kiln.log, and names
            // the module in the error (RunnerState::module_load_error).
            let chunk = match fs.read_file(&path).and_then(std::io::read_to_string) {
                Ok(chunk) => chunk,
                Err(e) => {
                    let why = fs.diagnose(&path, &e);
                    return Err(Self::module_load_error(&mod_state.r#mod, require_path.mod_id(), qualified_id, &path, &fs, why));
                }
            };

            drop(fs);

            lua.load(chunk)
                .set_name(qualified_id)
                .set_environment(mod_state.env.clone())
                .into_function()?
        };
        let result = func.call::<LuaValue>(())?;

        // register mod as loaded
        let mut loaded_mut = self.loaded.borrow_mut();

        loaded_mut.insert(qualified_id.to_string(), result.clone());

        if require_path.path() == "mod" {
            loaded_mut.insert(format!("mods.{}", require_path.mod_id()), result.clone());
        }

        // cleanup
        self.is_loading.borrow_mut().remove(qualified_id);

        Ok(result)
    }

}

pub struct RunnerLocalState {
    mod_id: String,
    loaded: RefCell<HashMap<String, LuaValue>>,
    global_state: Rc<RunnerState>,
}

impl RunnerLocalState {

    #[inline]
    pub fn new<S: AsRef<str>>(
        mod_id: S,
        global_state: Rc<RunnerState>
    ) -> Self {
        Self {
            mod_id: mod_id.as_ref().to_string(),
            loaded: RefCell::new(HashMap::new()),
            global_state,
        }
    }

    pub fn require(
        &self,
        id: &str,
        lua: &mlua::Lua,
    ) -> mlua::Result<LuaValue> {
        // lookup local cache first
        if let Some(table) = self.loaded.borrow().get(id) {
            return Ok(table.clone());
        }

        // lookup global cache (mods.id.path)
        if let Some(table) = self.global_state.loaded.borrow().get(id) {
            return Ok(table.clone());
        }

        // load the module
        let require_path = RequirePath::parse(id, &self.mod_id)?;
        let qualified_id = require_path.qualified_id();

        let result = self.global_state.require(&qualified_id, lua)?;

        // register mod as loaded in local state
        if require_path.is_local() {
            self.loaded.borrow_mut().insert(require_path.path().to_string(), result.clone());
        }

        Ok(result)
    }

}

