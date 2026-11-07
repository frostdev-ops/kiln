//! Forwarding for the `dinput8.dll` proxy: the real export is loaded from the
//! system directory on first use.

use std::ffi::c_void;
use std::sync::OnceLock;

use windows_sys::Win32::Foundation::{E_FAIL, HMODULE};
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows_sys::Win32::System::SystemInformation::GetSystemDirectoryW;

type DirectInput8CreateFn =
    unsafe extern "system" fn(HMODULE, u32, *const c_void, *mut *mut c_void, *mut c_void) -> i32;

fn real_create() -> Option<DirectInput8CreateFn> {
    static REAL: OnceLock<Option<usize>> = OnceLock::new();
    let addr = *REAL.get_or_init(|| unsafe {
        let mut buf = [0u16; 260];
        let len = GetSystemDirectoryW(buf.as_mut_ptr(), buf.len() as u32) as usize;
        let mut path: Vec<u16> = buf[..len].to_vec();
        path.extend("\\dinput8.dll\0".encode_utf16());
        let module = LoadLibraryW(path.as_ptr());
        if module.is_null() {
            return None;
        }
        GetProcAddress(module, b"DirectInput8Create\0".as_ptr()).map(|f| f as usize)
    });
    addr.map(|a| unsafe { std::mem::transmute::<usize, DirectInput8CreateFn>(a) })
}

/// # Safety
/// Called by the host exactly like the real `DirectInput8Create`.
pub unsafe fn direct_input8_create(
    hinst: HMODULE,
    version: u32,
    riid: *const c_void,
    out: *mut *mut c_void,
    outer: *mut c_void,
) -> i32 {
    match real_create() {
        Some(f) => f(hinst, version, riid, out, outer),
        None => E_FAIL,
    }
}
