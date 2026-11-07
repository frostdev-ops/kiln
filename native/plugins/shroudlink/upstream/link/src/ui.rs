//! Declarative UI that server mods send to client addons.
//!
//! The server describes a window as a tree of nodes; the addon draws it every
//! frame with the game's own widgets and reports interactions back to the mod
//! that owns the window. New server mods therefore need no client update.
//!
//! server -> client  `{"mod":"ui","op":"open","owner":"chestsort","window":{...}}`
//!                   `{"mod":"ui","op":"close","owner":"chestsort","id":"search"}`
//!                   `{"mod":"ui","op":"entries","owner":"chestsort","entries":[...]}`
//! client -> server  `{"mod":"chestsort","op":"ui_event","window":"search","node":"query","event":{...}}`
//!
//! Inventory building blocks (the addon draws and stores, the mod decides):
//!
//! server -> client  `{"mod":"ui","op":"slot_keys","owner":"chestsort","keys":[{"key":"L","label":"Lock"}]}`
//!                   `{"mod":"ui","op":"marks","owner":"chestsort","marks":[{"slots":[...],"style":"rim"}]}`
//!                   `{"mod":"ui","op":"store","owner":"chestsort","data":{...}}`
//!                   `{"mod":"ui","op":"section","owner":"hello","id":"wave","nodes":[...]}`
//!                   `{"mod":"ui","op":"notify","owner":"hello","text":"..."}`
//!                   `{"mod":"ui","op":"hud","owner":"meter","id":"dps","title":"..","rows":[...]}`
//! client -> server  `{"mod":"chestsort","op":"slot_key","key":"L","slot":..,"position":[page,index],"item":..}`
//!                   `{"mod":"chestsort","op":"stored","data":{...}}`   after the welcome, if stored
//!                   `{"mod":"chestsort","op":"layout","pages":[...]}`  backpack page entities in display order

use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
pub struct Window {
    pub id: String,
    pub title: String,
    pub content: Vec<Node>,
}

/// Version of the messages and windows (above the transport's `PROTOCOL_VERSION`). The
/// server sends it in its welcome, the addon in its `hello`; both sides warn the player
/// when they differ. A new version may break the other side:
/// - 1: addon 0.5 and older (no `hello`)
/// - 2: addon and server 0.6: `tabs` nodes, `change` from text fields, tab-separated
///   list columns
pub const UI_PROTOCOL: u32 = 2;

/// An item as the game identifies it (`keen::ItemStack`).
#[derive(Serialize, Deserialize, Debug, Clone, Copy, PartialEq, Eq)]
pub struct Item {
    pub id: u32,
    pub count: u32,
}

#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Node {
    Text { text: String },
    Heading { text: String },
    Separator,
    Row { children: Vec<Node> },
    Button { id: String, label: String },
    /// Single line text field; `submit` fires on Enter, `change` on edits.
    TextInput { id: String, value: String, #[serde(default)] hint: String },
    Toggle { id: String, label: String, value: bool },
    /// Item slots drawn like inventory slots (icon, count, tooltip); `click` reports the index.
    ItemGrid { id: String, columns: u8, items: Vec<Item> },
    /// Selectable text rows; `click` reports the index. Tab-separated cells line up in
    /// columns.
    List { id: String, entries: Vec<String> },
    /// A tab bar along the top of a window; `click` reports the index of the tab. The mod
    /// sends the content of the selected tab after it.
    Tabs { id: String, tabs: Vec<String>, selected: u32 },
}

/// A line of a HUD panel: label on the left, value on the right, over a bar filled to
/// `fraction` (0..1; 0 draws no bar).
#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
pub struct HudRow {
    pub label: String,
    pub value: String,
    #[serde(default)]
    pub fraction: f32,
}

/// An entry the addon offers in its mod menu (opened with the addon hotkey).
#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
pub struct MenuEntry {
    pub id: String,
    pub label: String,
}

/// A key that acts on the backpack slot under the cursor (menu screens only).
#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
pub struct SlotKey {
    /// one letter, A-Z
    pub key: String,
    pub label: String,
}

#[derive(Serialize, Deserialize, Debug, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum MarkStyle {
    /// a gold rim around the slot
    Rim,
    /// drawn faded
    Dim,
}

/// How to draw a set of inventory slots (slot ids: `page entity | index << 32`).
#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
pub struct Mark {
    pub slots: Vec<u64>,
    pub style: MarkStyle,
}

#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum ToClient {
    Open { owner: String, window: Window },
    Close { owner: String, id: String },
    Entries { owner: String, entries: Vec<MenuEntry> },
    /// Replaces the owner's slot hotkeys.
    SlotKeys { owner: String, keys: Vec<SlotKey> },
    /// Replaces the owner's slot marks.
    Marks { owner: String, marks: Vec<Mark> },
    /// Keeps `data` on the player's PC for this server; it comes back as `stored` on every join.
    Store { owner: String, data: serde_json::Value },
    /// Rows the mod adds to the chest screen's backpack panel, below its title (where the
    /// item search sits). Events come back as `ui_event` with `window` = `id`; no nodes
    /// removes the section.
    Section { owner: String, id: String, nodes: Vec<Node> },
    /// A short message shown on the HUD for a few seconds.
    Notify { owner: String, text: String },
    /// A small panel on the HUD, also while playing (bottom right; the player can drag it
    /// while a menu shows the cursor). Replaces the owner's panel `id`; no rows removes it.
    Hud { owner: String, id: String, title: String, rows: Vec<HudRow> },
}

/// A message the addon sends to a mod about inventory slots.
#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum FromClient {
    /// A registered key over a backpack slot. `position` is (page in display order, index)
    /// and stays valid across sessions, unlike the slot id.
    SlotKey { key: String, slot: u64, position: (u32, u32), item: u32 },
    /// What the mod stored on this player's PC for this server.
    Stored { data: serde_json::Value },
    /// The backpack's page entities in display order (positions map to slot ids through it).
    Layout { pages: Vec<u32> },
}

impl FromClient {
    pub fn to_message(&self, owner: &str) -> serde_json::Value {
        let mut v = serde_json::to_value(self).expect("slot messages serialise");
        v["mod"] = owner.into();
        v
    }

    /// The slot message in a mod's incoming `message`, if it is one.
    pub fn from_message(message: &serde_json::Value) -> Option<FromClient> {
        let mut m = message.clone();
        m.as_object_mut()?.remove("mod");
        serde_json::from_value(m).ok()
    }
}

#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Event {
    Click { #[serde(default)] index: Option<u32> },
    Change { value: serde_json::Value },
    Submit { value: String },
    /// The player chose a mod menu entry.
    Menu,
    /// The player closed the window.
    Closed,
}

#[derive(Serialize, Deserialize, Debug, Clone, PartialEq)]
pub struct UiEvent {
    pub window: String,
    pub node: String,
    pub event: Event,
}

impl ToClient {
    /// The JSON message for the addon (`"mod":"ui"` routes it to the renderer).
    pub fn to_message(&self) -> serde_json::Value {
        let mut v = serde_json::to_value(self).expect("ui messages serialise");
        v["mod"] = "ui".into();
        v
    }
}

impl UiEvent {
    pub fn to_message(&self, owner: &str) -> serde_json::Value {
        serde_json::json!({ "mod": owner, "op": "ui_event", "event": self })
    }

    pub fn from_message(message: &serde_json::Value) -> Option<UiEvent> {
        (message.get("op")?.as_str()? == "ui_event").then(|| serde_json::from_value(message.get("event")?.clone()).ok())?
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn window_json_shape() {
        let open = ToClient::Open {
            owner: "chestsort".into(),
            window: Window {
                id: "search".into(),
                title: "Search chests".into(),
                content: vec![
                    Node::TextInput { id: "query".into(), value: String::new(), hint: "item name".into() },
                    Node::ItemGrid { id: "results".into(), columns: 8, items: vec![Item { id: 7, count: 3 }] },
                ],
            },
        };
        let msg = open.to_message();
        assert_eq!(msg["mod"], "ui");
        assert_eq!(msg["op"], "open");
        assert_eq!(msg["window"]["content"][1]["type"], "item_grid");
        let mut back = msg.clone();
        back.as_object_mut().unwrap().remove("mod");
        assert_eq!(serde_json::from_value::<ToClient>(back).unwrap(), open);
    }

    #[test]
    fn slot_building_blocks_json_shape() {
        let marks = ToClient::Marks { owner: "chestsort".into(), marks: vec![Mark { slots: vec![6672 | 6 << 32], style: MarkStyle::Rim }] };
        let msg = marks.to_message();
        assert_eq!(msg["op"], "marks");
        assert_eq!(msg["marks"][0]["style"], "rim");
        let key = FromClient::SlotKey { key: "L".into(), slot: 6672 | 6 << 32, position: (5, 6), item: 42 };
        let msg = key.to_message("chestsort");
        assert_eq!(msg["mod"], "chestsort");
        assert_eq!(msg["op"], "slot_key");
        assert_eq!(msg["position"], serde_json::json!([5, 6]));
        assert_eq!(FromClient::from_message(&msg), Some(key));
        assert_eq!(FromClient::from_message(&serde_json::json!({ "mod": "x", "op": "ui_event" })), None);
    }

    #[test]
    fn hud_json_shape() {
        let hud = ToClient::Hud { owner: "meter".into(), id: "dps".into(), title: "DPS".into(), rows: vec![HudRow { label: "You".into(), value: "120".into(), fraction: 1.0 }] };
        let msg = hud.to_message();
        assert_eq!(msg["op"], "hud");
        assert_eq!(msg["rows"][0]["fraction"], 1.0);
        let mut back = msg.clone();
        back.as_object_mut().unwrap().remove("mod");
        assert_eq!(serde_json::from_value::<ToClient>(back).unwrap(), hud);
    }

    #[test]
    fn event_roundtrip() {
        let e = UiEvent { window: "search".into(), node: "query".into(), event: Event::Submit { value: "iron".into() } };
        let msg = e.to_message("chestsort");
        assert_eq!(msg["mod"], "chestsort");
        assert_eq!(UiEvent::from_message(&msg), Some(e));
    }
}
