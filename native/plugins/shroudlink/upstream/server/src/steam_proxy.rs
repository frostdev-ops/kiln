//! `steam_api64.dll` proxy for Wine/Proton hosts, where the game's own
//! `steam_api64.dll` is the only DLL reliably loaded from the server folder.
//! The original is renamed to `steam_api64_o.dll`; every function export of this
//! DLL is a jump through `TABLE`, which is filled from the original at load time.

use std::ffi::c_void;

use windows_sys::Win32::Foundation::HMODULE;
use windows_sys::Win32::System::LibraryLoader::{GetModuleFileNameW, GetProcAddress, LoadLibraryW};

include!(concat!(env!("OUT_DIR"), "/steam_stubs.rs"));

static mut TABLE: [usize; COUNT] = [0; COUNT];

pub const ORIGINAL: &str = "steam_api64_o.dll";

/// Loads the original next to this DLL and resolves every export; called from
/// `DllMain`, as the game calls into Steam right after the loader returns.
pub unsafe fn init(own_module: HMODULE) -> Result<(), String> {
    let mut buf = [0u16; 1024];
    let len = GetModuleFileNameW(own_module, buf.as_mut_ptr(), buf.len() as u32) as usize;
    let own = String::from_utf16_lossy(&buf[..len]);
    let dir = own.rsplit_once(['\\', '/']).map(|(d, _)| d).unwrap_or(".");
    let path: Vec<u16> = format!("{dir}\\{ORIGINAL}").encode_utf16().chain([0]).collect();
    let original = LoadLibraryW(path.as_ptr());
    if original.is_null() {
        return Err(format!("{ORIGINAL} not found next to {own}"));
    }
    // our own DLL renamed by mistake would make every Steam call jump to itself
    if GetProcAddress(original, b"CrashHandlerFactory\0".as_ptr()).is_some() {
        return Err(format!("{ORIGINAL} is a copy of this mod, not Steam's original DLL"));
    }
    let table = &mut *std::ptr::addr_of_mut!(TABLE);
    let mut missing = 0;
    for (slot, name) in table.iter_mut().zip(NAMES.iter()) {
        match GetProcAddress(original, name.as_ptr()) {
            Some(f) => *slot = f as usize,
            None => {
                *slot = missing_export as *const () as usize;
                missing += 1;
            }
        }
    }
    if missing > 0 {
        return Err(format!("{missing} export(s) missing in {ORIGINAL}"));
    }
    Ok(())
}

/// Target of exports the original lacks (different Steam SDK version); fails loudly.
extern "system" fn missing_export() -> *mut c_void {
    chestsort_common::log!("the game called a Steam function missing in {ORIGINAL}");
    std::process::abort();
}
