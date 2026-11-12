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

/// URL invocations have their own argument shape. A quote in an OS-supplied link must never turn into a launcher
/// option (especially the console helper or selftest). The registered handlers pass exactly one link.
fn nxm_argument(args: &[String]) -> Result<Option<&str>, ()> {
    let is_url = |s: &str| s.get(..6).is_some_and(|p| p.eq_ignore_ascii_case("nxm://"));
    if !args.iter().skip(1).any(|a| a == "--nxm" || is_url(a)) { return Ok(None); }
    match args {
        [_, flag, url] if flag == "--nxm" && is_url(url) => Ok(Some(url)),
        [_, url] if is_url(url) => Ok(Some(url)),
        _ => Err(()),
    }
}

fn main() {
    // `<launcher> --kiln-ctrl-c <pid>`: the short-lived helper that sends a Windows server its Ctrl-C (Stop), so the
    // launcher itself never attaches to the server's console (kiln_manage::server::run::set_ctrl_c_helper)
    let args: Vec<String> = std::env::args().collect();
    let nxm = match nxm_argument(&args) {
        Ok(link) => link,
        Err(()) => std::process::exit(1),
    };
    if args.len() == 3 && args[1] == kiln_manage::server::run::CTRL_C_HELPER_ARG {
        let pid = args[2].parse::<u32>().unwrap_or(0);
        std::process::exit(if pid == 0 { 1 } else { kiln_manage::server::run::ctrl_c_helper_main(pid) });
    }
    if let Ok(exe) = std::env::current_exe() {
        kiln_manage::server::run::set_ctrl_c_helper(exe);
    }
    // `--nxm <link>` (what Kiln registers as the nxm:// handler) or a bare `nxm://` argument: the link goes to the
    // running launcher (or to another game's mod manager) and this process ends; else it starts with the link queued
    if let Some(url) = nxm
        && !kiln_launcher_lib::nxm_entry(url)
    {
        std::process::exit(0);
    }
    if nxm.is_none() && args.iter().any(|a| a == "--selftest") {
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
    // How the window is drawn: `--software-render` / `--gpu-render` (here, after the nxm check above: a link start has
    // no other argument), `KILN_LAUNCHER_RENDER`, the marker; software mode's variables, the watchdog (render.rs).
    #[cfg(target_os = "linux")]
    kiln_launcher_lib::render::prepare(&args);
    kiln_launcher_lib::run()
}

#[cfg(test)]
mod tests {
    use super::*;
    fn args(tail: &[&str]) -> Vec<String> { std::iter::once("kiln").chain(tail.iter().copied()).map(str::to_string).collect() }
    #[test]
    fn nxm_invocations_cannot_inject_launcher_options() {
        let url = "nxm://enshrouded/mods/1/files/2";
        assert_eq!(nxm_argument(&args(&["--nxm", url])), Ok(Some(url)));
        assert_eq!(nxm_argument(&args(&[url])), Ok(Some(url)));
        assert_eq!(nxm_argument(&args(&["--selftest"])), Ok(None));
        for tail in [vec!["--nxm"], vec!["--nxm", "--selftest"], vec!["--nxm", url, "--kiln-ctrl-c", "1"], vec!["--kiln-ctrl-c", "1", url], vec![url, "--selftest"],
            vec!["--nxm", url, "--software-render"], vec!["--gpu-render", url]] {
            assert!(nxm_argument(&args(&tail)).is_err());
        }
    }
}
