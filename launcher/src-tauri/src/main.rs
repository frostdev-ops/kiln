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
    // `<launcher> --kiln-ctrl-c <pid>`: the short-lived helper that sends a Windows server its Ctrl-C (Stop), so the
    // launcher itself never attaches to the server's console (kiln_manage::server::run::set_ctrl_c_helper)
    let args: Vec<String> = std::env::args().collect();
    if let Some(i) = args.iter().position(|a| a == kiln_manage::server::run::CTRL_C_HELPER_ARG) {
        let pid = args.get(i + 1).and_then(|p| p.parse::<u32>().ok()).unwrap_or(0);
        std::process::exit(if pid == 0 { 1 } else { kiln_manage::server::run::ctrl_c_helper_main(pid) });
    }
    if let Ok(exe) = std::env::current_exe() {
        kiln_manage::server::run::set_ctrl_c_helper(exe);
    }
    // `--nxm <link>` (what Kiln registers as the nxm:// handler) or a bare `nxm://` argument: the link goes to the
    // running launcher (or to another game's mod manager) and this process ends; else it starts with the link queued
    let nxm = args.iter().position(|a| a == "--nxm").and_then(|i| args.get(i + 1).cloned())
        .or_else(|| args.iter().skip(1).find(|a| a.to_ascii_lowercase().starts_with("nxm://")).cloned());
    if let Some(url) = nxm
        && !kiln_launcher_lib::nxm_entry(&url)
    {
        std::process::exit(0);
    }
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
