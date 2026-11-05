// Kiln Launcher.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

/// Release builds are GUI programs on Windows (no console): `--selftest` attaches to the console it was started from
/// (cmd, PowerShell), so its report is visible there. Output redirected to a file or pipe is left alone.
#[cfg(windows)]
fn attach_parent_console() {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn AttachConsole(process_id: u32) -> i32;
        fn GetStdHandle(std_handle: u32) -> *mut core::ffi::c_void;
    }
    const ATTACH_PARENT_PROCESS: u32 = u32::MAX;
    const STD_OUTPUT_HANDLE: u32 = -11i32 as u32;
    // SAFETY: plain Win32 calls without pointers; failure (no parent console) is harmless.
    unsafe {
        let out = GetStdHandle(STD_OUTPUT_HANDLE);
        if out.is_null() || out as isize == -1 {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
    }
}

fn main() {
    if std::env::args().any(|a| a == "--selftest") {
        #[cfg(windows)]
        attach_parent_console();
        std::process::exit(kiln_launcher_lib::selftest());
    }
    // WebKitGTK's DMA-BUF renderer fails on some Wayland/NVIDIA setups ("Gdk Error 71 (Protocol error)" at start,
    // seen on GNOME + NVIDIA). Off unless the user set it; costs nothing visible for this UI.
    #[cfg(target_os = "linux")]
    if std::env::var_os("WEBKIT_DISABLE_DMABUF_RENDERER").is_none() {
        // SAFETY: first thing in main, before any thread exists.
        unsafe { std::env::set_var("WEBKIT_DISABLE_DMABUF_RENDERER", "1") };
    }
    kiln_launcher_lib::run()
}
