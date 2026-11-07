//! Component types by name. The world keeps one 0x100-byte descriptor per component type
//! in an array indexed by type index; a descriptor starts with the type's `qualifiedHash`
//! (as in the reflection data) and holds the index itself at +0x92. The component handles
//! a game context carries (e.g. ctx[3], InventorySetup) point at that index field, so the
//! array is found from any context: `handle - index * 0x100 - 0x92`.

use crate::game::{Ctx, EntityRecord, MAX_COMPONENT_TYPES};

const DESCRIPTOR_SIZE: usize = 0x100;
const INDEX_FIELD: usize = 0x92;
/// ctx word holding the InventorySetup handle (any handle works).
const HANDLE_WORD: usize = 3;

/// `qualifiedHash` of component types the core uses (from the reflection data).
pub mod hash {
    pub const CURRENT_TRANSFORM: u32 = 0x027a_c564;
    pub const LAST_TRANSFORM: u32 = 0x4b6a_bcf9;
    pub const TELEPORT_COUNT: u32 = 0xbffe_34ef;
}

unsafe fn table(ctx: *mut Ctx) -> Option<*const u8> {
    let handle = *(ctx as *const usize).add(HANDLE_WORD);
    if handle == 0 {
        return None;
    }
    let index = *(handle as *const u16) as usize;
    Some((handle - index * DESCRIPTOR_SIZE - INDEX_FIELD) as *const u8)
}

/// Type index of the component type with this `qualifiedHash`.
pub unsafe fn index_of(ctx: *mut Ctx, type_hash: u32) -> Option<u16> {
    let table = table(ctx)?;
    (0..MAX_COMPONENT_TYPES).find(|&i| {
        let descriptor = table.add(i as usize * DESCRIPTOR_SIZE);
        *(descriptor as *const u32) == type_hash && *(descriptor.add(INDEX_FIELD) as *const u16) == i
    })
}

/// Address and size of an entity's component of this type, if it has one.
pub unsafe fn of(ctx: *mut Ctx, record: &EntityRecord, type_hash: u32) -> Option<(*mut u8, u16)> {
    let index = index_of(ctx, type_hash)?;
    record.component(index).map(|(addr, size)| (addr as *mut u8, size))
}
