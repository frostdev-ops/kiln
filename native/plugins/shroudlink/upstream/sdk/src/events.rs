//! Game events: everything the server's ECS reports (an enemy died, a player picked something
//! up, levelled up, crafted, built …). A mod names the event types it wants in
//! [`crate::Mod::game_events`] and gets each one in [`crate::Mod::on_game_event`], once per
//! occurrence, before the game's own systems handle it.
//!
//! [`GameEvent::known`] decodes the common types; any other type can be read from the raw
//! payload with the layout from the game's reflection data (`research/scripts/reflect.py`).

pub use crate::event_types::EVENT_TYPES;

/// The queue id of an event type ("EntityDiedEvent" -> its hash), if the type exists.
pub fn hash_of(name: &str) -> Option<u32> {
    EVENT_TYPES.iter().find(|e| e.1 == name).map(|e| e.0)
}

/// The type name of a queue id.
pub fn name_of(hash: u32) -> Option<&'static str> {
    EVENT_TYPES.binary_search_by_key(&hash, |e| e.0).ok().map(|i| EVENT_TYPES[i].1)
}

/// One event from the game's queue. `data` is the payload as the game laid it out.
pub struct GameEvent<'a> {
    pub hash: u32,
    pub name: &'static str,
    pub data: &'a [u8],
}

/// The common event types, decoded. Entity ids are `keen::ecs::EntityId`s (players, enemies,
/// chests …); slot ids are `page entity | index << 32` as everywhere in the SDK.
#[derive(Clone, Debug, PartialEq)]
pub enum Known {
    /// A player entered the world.
    Login { player: u32 },
    EntityDied { entity: u32 },
    /// Items went into an inventory (looting, gathering, picking up).
    Pickup { item: u32, count: u32, by: u32, from: u32, slot: u64 },
    LevelUp { player: u32, level: i32 },
    XpGained { player: u32, amount: u32 },
    ConsumeItem { player: u32, slot: u64 },
    CraftRecipeSuccess { workshop: u32, recipe: u32 },
    BuildingPlace { owner: u32, item: u32, position: [f32; 3] },
    BuildingTearDown { owner: u32, position: [f32; 3] },
    TeleportPlayer { player: u32, target: u32, position: [f32; 3] },
    RevivedPlayer { player: u32, by: u32 },
    UnlockSkill { player: u32, skill: u32 },
    TrashItem { player: u32, slot: u64 },
}

impl GameEvent<'_> {
    pub fn u32_at(&self, offset: usize) -> Option<u32> {
        Some(u32::from_le_bytes(self.data.get(offset..offset + 4)?.try_into().ok()?))
    }

    pub fn i32_at(&self, offset: usize) -> Option<i32> {
        self.u32_at(offset).map(|v| v as i32)
    }

    pub fn f32_at(&self, offset: usize) -> Option<f32> {
        self.u32_at(offset).map(f32::from_bits)
    }

    /// `keen::InventorySlotId` (entity id, slot index) at `offset`, as a slot id.
    pub fn slot_at(&self, offset: usize) -> Option<u64> {
        let entity = self.u32_at(offset)?;
        let index = *self.data.get(offset + 4)?;
        Some(entity as u64 | (index as u64) << 32)
    }

    fn position_at(&self, offset: usize) -> Option<[f32; 3]> {
        Some([self.f32_at(offset)?, self.f32_at(offset + 4)?, self.f32_at(offset + 8)?])
    }

    /// The event decoded, for the types in [`Known`] (offsets from the game's reflection data).
    pub fn known(&self) -> Option<Known> {
        Some(match self.name {
            "LoginEvent" => Known::Login { player: self.u32_at(0)? },
            "EntityDiedEvent" => Known::EntityDied { entity: self.u32_at(0x8)? },
            "PickupEvent" => Known::Pickup { item: self.u32_at(0x14)?, count: self.u32_at(0x18)?, by: self.u32_at(0x1c)?, from: self.u32_at(0x20)?, slot: self.slot_at(0x24)? },
            "LevelUpEvent" => Known::LevelUp { player: self.u32_at(0x20)?, level: self.i32_at(0x30)? },
            "XPGainedEvent" => Known::XpGained { player: self.u32_at(0x8)?, amount: self.u32_at(0xc)? },
            "ConsumeItemEvent" => Known::ConsumeItem { player: self.u32_at(0x8)?, slot: self.slot_at(0xc)? },
            "CraftRecipeSuccessEvent" => Known::CraftRecipeSuccess { workshop: self.u32_at(0x8)?, recipe: self.u32_at(0xc)? },
            "BuildingPlaceEvent" => Known::BuildingPlace { owner: self.u32_at(0x44)?, item: self.u32_at(0x40)?, position: self.position_at(0x8)? },
            "BuildingTearDownEvent" => Known::BuildingTearDown { owner: self.u32_at(0x40)?, position: self.position_at(0x8)? },
            "TeleportPlayerEvent" => Known::TeleportPlayer { player: self.u32_at(0x8)?, target: self.u32_at(0xc)?, position: self.position_at(0x14)? },
            "RevivedPlayerEvent" => Known::RevivedPlayer { player: self.u32_at(0)?, by: self.u32_at(4)? },
            "UnlockSkillEvent" => Known::UnlockSkill { player: self.u32_at(0x8)?, skill: self.u32_at(0xc)? },
            "TrashItemEvent" => Known::TrashItem { player: self.u32_at(0x8)?, slot: self.slot_at(0xc)? },
            _ => return None,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_and_hashes_round_trip() {
        let hash = hash_of("EntityDiedEvent").expect("known type");
        assert_eq!(name_of(hash), Some("EntityDiedEvent"));
        assert!(EVENT_TYPES.windows(2).all(|w| w[0].0 < w[1].0), "sorted for binary search");
    }

    #[test]
    fn level_up_decodes() {
        let mut data = vec![0u8; 56];
        data[0x20..0x24].copy_from_slice(&7u32.to_le_bytes());
        data[0x30..0x34].copy_from_slice(&12i32.to_le_bytes());
        let event = GameEvent { hash: hash_of("LevelUpEvent").unwrap(), name: "LevelUpEvent", data: &data };
        assert_eq!(event.known(), Some(Known::LevelUp { player: 7, level: 12 }));
    }
}
