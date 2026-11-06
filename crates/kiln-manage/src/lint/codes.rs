//! Every diagnostic code kiln lint emits. Codes and rule names are stable: tools (tools/vscode, CI scripts) match on
//! them. docs/kiln/modding/lint.md documents each one (tests check that it does).

use super::Severity;

pub struct Rule {
    pub code: &'static str,
    pub name: &'static str,
    pub severity: Severity,
    pub title: &'static str,
}

macro_rules! rules {
    ($($konst:ident = $code:literal, $name:literal, $sev:ident, $title:literal;)*) => {
        $(pub const $konst: &str = $code;)*
        pub const ALL: &[Rule] = &[$(Rule { code: $code, name: $name, severity: Severity::$sev, title: $title }),*];
    };
}

rules! {
    // KL0xx: reading the file
    SYNTAX = "KL001", "syntax", Error, "not valid JSON";
    COMMENT = "KL002", "comment-not-allowed", Error, "a comment in a file whose reader takes plain JSON";
    TRAILING_COMMA = "KL003", "trailing-comma", Error, "a trailing comma the file's reader refuses";
    DUPLICATE_KEY = "KL004", "duplicate-key", Warning, "a key given twice in one object";
    BOM = "KL005", "bom", Error, "a byte-order mark the file's reader refuses";
    NOT_UTF8 = "KL006", "not-utf8", Error, "the file is not UTF-8";
    UNREADABLE = "KL007", "unreadable", Error, "the file or folder cannot be read";
    CONTAINER = "KL008", "container", Error, "a binary container (.kschem) that is cut short or malformed";

    // KL1xx: the file's schema (schema/*.schema.json)
    TYPE = "KL101", "type", Error, "a value of the wrong type";
    KEY_NOT_ALLOWED = "KL102", "key-not-allowed", Error, "a key the format does not allow";
    UNKNOWN_KEY = "KL103", "unknown-key", Warning, "a key Kiln ignores (often a typo)";
    REQUIRED = "KL104", "required", Error, "a required key is missing";
    ENUM = "KL105", "enum", Error, "a value that is not one of the allowed ones";
    PATTERN = "KL106", "pattern", Error, "a string of the wrong form (ids, GUIDs, paths, names)";
    RANGE = "KL107", "range", Error, "a number out of range";
    SIZE = "KL108", "size", Error, "too long, too short, too many or too few";
    SCHEMA_RULE = "KL109", "schema-rule", Error, "a rule of the format (alternatives, combinations, exclusions)";
    UNIQUE = "KL110", "unique-items", Error, "a list with the same entry twice";
    FORMAT_VERSION = "KL111", "format-version", Error, "a format version this Kiln does not read";
    CASE = "KL112", "case", Hint, "a name Kiln matches without regard to case, written in another case";

    // KL2xx: the mod's manifest against what the mod contains
    CLIENTS_DEFINITIONS = "KL201", "clients-definitions", Error, "new content needs \"clients\": \"kiln\" or \"mod\"";
    CLIENTS_ASSETS = "KL202", "clients-assets", Warning, "client-drawn assets need \"clients\": \"mod\"";
    SIDE_ASSETS = "KL203", "side-assets", Warning, "client-only assets in a side \"server\" mod";
    SIDE_CLIENTS = "KL204", "side-clients", Info, "\"clients\": \"mod\" on a side \"server\" mod";
    ENTRY_MISSING = "KL205", "entry-missing", Error, "entryAssembly or an export is not in the mod folder";
    UNDECLARED_DEPENDENCY = "KL206", "undeclared-dependency", Warning, "uses another mod's content without depending on it";
    CLIENTS_SKILLS = "KL207", "clients-skills", Error, "game-tree nodes need \"clients\": \"mod\" (a talent tree needs it for its screen)";
    MANIFEST_REFUSED = "KL209", "manifest-refused", Error, "a manifest Kiln.Host (or the native loader) refuses";
    NATIVE_PREFIX = "KL208", "native-prefix", Warning, "a Kiln native named after another mod id";
    CLIENTS_QUESTS = "KL210", "clients-quests", Error, "a quest in the game's own journal needs \"clients\": \"mod\"";
    CLIENTS_VFX = "KL211", "clients-vfx", Error, "a new VFX or material needs \"clients\": \"mod\"";

    // KL3xx: ids, GUIDs and names
    DUPLICATE_GUID = "KL301", "duplicate-guid", Error, "a GUID defined twice in one mod";
    PROFILE_DUPLICATE_GUID = "KL302", "profile-duplicate-guid", Error, "a GUID defined by two mods of a profile";
    GAME_GUID = "KL303", "game-guid", Error, "a definition's GUID or id is the game's own";
    ID_COLLISION = "KL304", "id-collision", Error, "two GUIDs that hash to the same id";
    DUPLICATE_NAME = "KL305", "duplicate-name", Error, "a debug name (or variant id) defined twice";
    GAME_NAME = "KL306", "game-name", Error, "a definition named like a game entry";
    PROFILE_DUPLICATE_NAME = "KL307", "profile-duplicate-name", Error, "a debug name defined by two mods of a profile";
    PROFILE_DUPLICATE_MOD = "KL308", "profile-duplicate-mod", Error, "two mods with the same id";
    DUPLICATE_ASSET = "KL309", "duplicate-asset", Warning, "the same resource key served twice";
    TALENT_CELL = "KL310", "talent-cell", Error, "two nodes of a talent tree in one grid cell";
    QUEST_REF = "KL311", "quest-reference", Warning, "a quest, step, NPC, dialogue or node a quest file names does not exist";
    NATIVE_QUEST = "KL312", "native-quest", Error, "a native quest, dialogue or NPC the boot build refuses (the mod's native data is not built)";
    BLOCK_REF = "KL314", "block-reference", Warning, "a block a blocks/*.json file names does not exist, or is the wrong kind";
    SUMMON_ITEM_TEXT = "KL313", "summon-item-text", Warning, "a roster NPC's summon tool with a name or description of its own: the game's Survivors list shows the tool's for the NPC, so the build replaces them with the NPC's";
    VFX_BUDGET = "KL320", "vfx-budget", Error, "more VFX, meshes, mesh memory or sounds in VFX than the game has room for";
    VFX_PARAMETER = "KL321", "vfx-parameter", Warning, "a VFX parameter the VFX does not have, of the wrong type, or on a field that cannot set one";
    VFX_TEXTURE = "KL322", "vfx-texture", Error, "a VFX or material texture that is not a power of two, or outside the sizes it may have";
    VFX_REF = "KL323", "vfx-reference", Warning, "a game VFX the catalog does not have, or that only its GUID can find";
    VFX_FLIPBOOK = "KL324", "vfx-flipbook", Warning, "a flipbook grid its PNG cannot divide into";
    MATERIAL_BUDGET = "KL330", "material-budget", Error, "more new building materials than the game has ids for";
    MATERIAL_COUNT = "KL331", "material-count", Warning, "a mod with many building materials, which every mod shares room for";
    MATERIAL_REF = "KL332", "material-reference", Warning, "a game material the catalog does not have";
    SURFACE_TEXTURE = "KL333", "surface-texture", Error, "a building surface's PNG that kiln.surfaces refuses, or reads otherwise than it looks";

    // KL4xx: references to the game's data (needs the game-data catalog)
    UNKNOWN_REF = "KL401", "unknown-reference", Warning, "a name, id or GUID the game (and the mods) do not have";
    UNKNOWN_FIELD = "KL402", "unknown-field", Warning, "a field path the game's reflection does not have";
    GLOB_EMPTY = "KL403", "glob-matches-nothing", Warning, "a selector glob that matches nothing";
    UNKNOWN_LANGUAGE = "KL404", "unknown-language", Info, "a language key no game language uses";
    ASSET_MODE = "KL405", "asset-mode", Error, "a resource \"override\" the game lacks, or a \"new\" one it has";
    CRAFTED_ROLLED = "KL406", "crafted-rolled", Warning, "a recipe makes an item that rolls its rarity or level, unlike its tooltip";

    // KL5xx: files, assets and cook recipes
    FILE_MISSING = "KL501", "file-missing", Error, "a file the mod names is not there";
    COOK_OUTPUT_MISSING = "KL502", "cook-output-missing", Info, "a cooked file is not there yet (run the cook)";
    NOT_PNG = "KL503", "not-png", Error, "an icon or texture that is not a PNG";
    BLOB_SIZE = "KL504", "blob-size", Error, "a blob override whose file length differs from its hash's size";
    PATCH_OFFSET = "KL505", "patch-offset", Error, "a patch offset past the end of the resource";
    COOK_BASE = "KL510", "cook-base", Error, "a cook step's base is not an input of the right type";
    COOK_INPUT = "KL511", "cook-input", Error, "an @input reference to no input";
    COOK_UNLISTED = "KL512", "cook-unlisted", Warning, "a cook output assets.json does not serve";
    COOK_STEM = "KL513", "cook-stem", Error, "two cook steps write the same output";
    NATIVE_HASH = "KL514", "native-hash", Error, "a native's hash or argc that does not match its name or args";
    SCHEMATIC_SIZE = "KL515", "schematic-size", Error, "a schematic over the 4 M cell limit";
    COOK_SKELETON = "KL516", "cook-skeleton", Error, "a model step's skeleton is neither a skeleton step of the recipe nor a skeleton input";

    // KL6xx: licences and what may be shipped
    NO_LICENSE = "KL601", "no-license", Info, "the manifest names no licence";
    BAD_LICENSE = "KL602", "bad-license", Warning, "a licence that is not an SPDX expression";
    THIRD_PARTY = "KL603", "third-party-notice", Warning, "bundled third-party files without a licence notice";
    GAME_DATA = "KL604", "game-data", Error, "game files inside a mod (mods ship sources and a recipe)";
    COOKED_COMMITTED = "KL605", "cooked-committed", Warning, "cooked game-derived files not ignored by git";

    // KL9xx: the linter itself
    NO_CATALOG = "KL901", "no-catalog", Info, "no game-data catalog: references to the game are not checked";
    CATALOG_BAD = "KL902", "catalog-unreadable", Warning, "the game-data catalog cannot be read";
    NOTHING = "KL903", "nothing-to-lint", Info, "no file kiln lint knows in the path";
    NOT_BUILT = "KL904", "manifest-not-built", Info, "a Kiln.Sdk project whose generated kiln.mod.json does not exist yet";
}

pub fn rule(code: &str) -> Option<&'static Rule> {
    ALL.iter().find(|r| r.code == code || r.name == code)
}
