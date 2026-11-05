// SPDX-License-Identifier: MIT
//! `kiln debug`: attach a .NET debugger (netcoredbg, not bundled) to a local server's mods. Plain: says how
//! (`--json`: pid, wine, prefix, netcoredbg, env, command). `--dap`: a Debug Adapter Protocol proxy on stdin/stdout
//! around netcoredbg for editors (docs/kiln/modding/debugging.md).
//!
//! The rules that keep this from hurting a server:
//! - **Only the named server.** Windows pids are per wineserver, so the debugger runs with the server's own WINEPREFIX
//!   and the wine of the running process; the proxy attaches to the pid resolved there and refuses any other. A folder
//!   whose prefix is unknown is refused, and a server of a Steam library (where the user's live server lives) is always
//!   refused. Refusals happen before anything runs in a prefix.
//! - **The debuggee never ends with the session.** Every `disconnect` goes out with `terminateDebuggee: false`;
//!   `launch`, `terminate` and `restart` are refused and not advertised.
//! - **Stale modules go first.** Before attaching, the proxy asks the server to collect unloaded mod contexts; breakpoints
//!   otherwise can bind to the old copy of a reloaded mod (and sessions in that state have ended the server).
//! - **The debugger is never killed.** A debugger dying while attached kills the server (measured), so
//!   when the client goes away (stdin EOF, SIGINT, SIGTERM, SIGHUP) the proxy disconnects cleanly and waits; if the
//!   adapter does not answer, the proxy closes its stdin, which also makes it detach, and leaves it alone.

use crate::out::{self, CliError};
use crate::project::Project;
use crate::server::{self, Server};
use anyhow::Result;
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::mpsc;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const SERVER_EXE: &str = "enshrouded_server.exe";
/// A proxy's own requests and errors, far from what a client or netcoredbg counts to.
const OWN_SEQ: i64 = 1_000_000_000;
const MAX_FRAME: usize = 256 << 20;
/// How long a client-less proxy waits for netcoredbg's disconnect response (a detach walks every module).
const DISCONNECT_WAIT: Duration = Duration::from_secs(30);
const EXIT_WAIT: Duration = Duration::from_secs(15);
/// Seconds to wait for the control command `gc` before attaching.
const GC_WAIT: u64 = 15;

#[derive(clap::Args)]
pub struct DebugArgs {
    /// Server folder, lab name or server instance id (default: $KILN_SERVER, else the current project's KilnServer).
    #[arg(long)]
    pub server: Option<String>,
    /// Speak the Debug Adapter Protocol on stdin/stdout (for editors); forces terminateDebuggee=false.
    #[arg(long)]
    pub dap: bool,
}

/// What the debugger attaches to, and how to run it there.
#[derive(Debug)]
struct Target {
    server: PathBuf,
    via: &'static str,
    lab: Option<String>,
    /// The pid the debugger attaches to (Wine's own numbering under Wine).
    pid: u32,
    /// The Linux pid of the wine process; on Windows the same as `pid`.
    host_pid: u32,
    wine: Option<PathBuf>,
    prefix: Option<PathBuf>,
    netcoredbg: PathBuf,
}

impl Target {
    fn env(&self) -> BTreeMap<&'static str, String> {
        let mut e = BTreeMap::new();
        // netcoredbg's bundled runtime looks for ICU and aborts: "Cannot get symbol u_charsToUChars from libicuuc"
        e.insert("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1".to_string());
        if let Some(p) = &self.prefix {
            e.insert("WINEPREFIX", p.display().to_string());
            e.insert("WINEDEBUG", "-all".to_string());
            e.insert("WINEDLLOVERRIDES", kiln_manage::wine::DLL_OVERRIDES.to_string());
        }
        e
    }

    fn argv(&self) -> Vec<String> {
        let mut v = Vec::new();
        if let Some(w) = &self.wine {
            v.push(w.display().to_string());
        }
        v.push(self.netcoredbg.display().to_string());
        v.push("--interpreter=vscode".to_string());
        v
    }

    fn command(&self) -> Command {
        let argv = self.argv();
        let mut c = Command::new(&argv[0]);
        c.args(&argv[1..]);
        scrub(&mut c);
        c.envs(self.env());
        if let Some(d) = self.netcoredbg.parent() {
            c.current_dir(d);
        }
        c
    }
}

/// Inherited variables that would point a Wine call at another runner, prefix or display.
fn scrub(c: &mut Command) {
    for k in ["WINEPREFIX", "WINESERVER", "WINELOADER", "WINEDLLPATH", "WINEARCH", "WINEDLLOVERRIDES", "WINEDEBUG", "STEAM_COMPAT_DATA_PATH", "STEAM_COMPAT_CLIENT_INSTALL_PATH"] {
        c.env_remove(k);
    }
    kiln_manage::server::run::headless(c);
}

/// A path inside a Steam library: `.../steamapps/common/<app>`, `.../steamapps/compatdata/...`.
pub fn in_steam_library(p: &Path) -> bool {
    p.components().any(|c| matches!(c, Component::Normal(n) if n.to_str().is_some_and(|s| s.eq_ignore_ascii_case("steamapps"))))
}

fn canon(p: &Path) -> PathBuf {
    p.canonicalize().unwrap_or_else(|_| p.to_path_buf())
}

fn refuse_steam(what: &Path) -> anyhow::Error {
    CliError::new(
        "refused",
        format!("{} is inside a Steam library; kiln debug pauses the server for everyone on it, so it refuses", what.display()),
    )
    .into()
}

fn not_running(dir: &Path) -> anyhow::Error {
    CliError::new("not_running", format!("{} is not running; start it first (kiln run)", dir.display())).into()
}

/// $KILN_NETCOREDBG, else <data>/tools/netcoredbg/netcoredbg.exe. Not bundled (docs/kiln/modding/debugging.md).
fn netcoredbg() -> Result<PathBuf> {
    let (p, how) = match std::env::var_os("KILN_NETCOREDBG").filter(|v| !v.is_empty()) {
        Some(v) => (PathBuf::from(v), "KILN_NETCOREDBG"),
        None => (kiln_manage::paths::DataDirs::locate()?.root.join("tools/netcoredbg/netcoredbg.exe"), "the default place"),
    };
    if !p.is_file() {
        return Err(CliError::new(
            "tool_missing",
            format!("no netcoredbg at {} ({how}); get the win64 build (docs/kiln/modding/debugging.md) and put it there or set KILN_NETCOREDBG", p.display()),
        )
        .into());
    }
    Ok(canon(&p))
}

/// The running server's own wine: `/proc/<pid>/exe` is its preloader (`<GE>/files/lib/wine/i386-unix/wine64-preloader`,
/// `/usr/lib/wine/wine64-preloader`), below the `bin/wine` of that build. A different wine build could not talk to the
/// prefix's wineserver at all.
#[cfg(target_os = "linux")]
fn wine_of(pid: u32) -> Option<PathBuf> {
    let exe = std::fs::read_link(format!("/proc/{pid}/exe")).ok()?;
    wine_above(&exe)
}

#[cfg(target_os = "linux")]
fn wine_above(exe: &Path) -> Option<PathBuf> {
    let usable = |w: &PathBuf| w.is_file() && kiln_manage::util::is_executable(w);
    // `bin/wine` first: GE-Proton also has a `lib/wine/i386-unix/wine` next to the preloader, which is not what started it
    exe.ancestors().skip(1).map(|a| a.join("bin/wine")).find(usable).or_else(|| exe.parent().map(|d| d.join("wine")).filter(usable))
}

#[cfg(not(target_os = "linux"))]
fn wine_of(_pid: u32) -> Option<PathBuf> {
    None
}

/// Wine's pid of the server: `tasklist` in the prefix only ever lists that prefix's processes.
fn wine_pid(wine: &Path, prefix: &Path) -> Result<u32> {
    let mut c = Command::new(wine);
    c.args(["tasklist.exe", "/fo", "csv", "/nh"]);
    scrub(&mut c);
    c.env("WINEPREFIX", prefix).env("WINEDEBUG", "-all").current_dir(prefix);
    c.stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::null());
    let mut child = c.spawn().map_err(|e| CliError::new("tool_missing", format!("cannot run {}: {e}", wine.display())))?;
    let mut stdout = child.stdout.take().expect("piped");
    let reader = std::thread::spawn(move || {
        let mut s = String::new();
        let _ = stdout.read_to_string(&mut s);
        s
    });
    let t0 = Instant::now();
    loop {
        if child.try_wait()?.is_some() {
            break;
        }
        if t0.elapsed() > Duration::from_secs(60) {
            let _ = child.kill(); // tasklist, not the debugger
            let _ = child.wait();
            return Err(CliError::new("timeout", "wine tasklist.exe did not answer in 60 s").into());
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let list = reader.join().unwrap_or_default();
    let pids = parse_tasklist(&list, SERVER_EXE);
    match pids.as_slice() {
        [one] => Ok(*one),
        [] => Err(CliError::new("not_running", format!("wine tasklist.exe in {} shows no {SERVER_EXE}", prefix.display())).into()),
        more => Err(CliError::new("refused", format!("{} runs {} {SERVER_EXE} processes ({more:?}); stop the extra ones", prefix.display(), more.len())).into()),
    }
}

/// `"name.exe","32","Console","1","1,234 K"` per line.
fn parse_tasklist(list: &str, exe: &str) -> Vec<u32> {
    list.lines()
        .filter_map(|l| {
            let mut f = l.trim().trim_matches('"').split("\",\"");
            let name = f.next()?;
            let pid = f.next()?.parse().ok()?;
            name.eq_ignore_ascii_case(exe).then_some(pid)
        })
        .collect()
}

/// The server's process in its own prefix, nothing else. No wine runs until this has found one, so a stopped server
/// gets no stray wineserver and another server's prefix is never entered.
fn host_pid_in_prefix(s: &Server, prefix: &Path) -> Result<u32> {
    let pids = kiln_manage::server::procs::find_servers(prefix);
    let dir = canon(&s.dir);
    let in_dir: Vec<u32> = pids
        .iter()
        .copied()
        .filter(|p| std::fs::read_link(format!("/proc/{p}/cwd")).is_ok_and(|c| canon(&c) == dir))
        .collect();
    match in_dir.as_slice() {
        [one] => Ok(*one),
        [] if pids.is_empty() => Err(not_running(&s.dir)),
        [] => Err(CliError::new("refused", format!("the server running in {} (pid {pids:?}) was not started from {}", prefix.display(), s.dir.display())).into()),
        more => Err(CliError::new("refused", format!("{} server processes run in {} ({more:?}); stop the extra ones", more.len(), prefix.display())).into()),
    }
}

/// The server's Wine prefix: labs `<labs>/<name>.pfx`, instances their record. None on Windows.
fn prefix_of(s: &Server) -> Result<Option<PathBuf>> {
    if cfg!(windows) {
        return Ok(None);
    }
    if s.lab.is_some() {
        return Ok(Some(with_pfx(&s.dir)));
    }
    if let Some(i) = &s.instance {
        return match &i.prefix {
            Some(p) => Ok(Some(p.clone())),
            None => Err(CliError::new("not_found", format!("server instance {} has no Wine prefix on record", i.id)).into()),
        };
    }
    Err(CliError::new(
        "unsupported",
        format!("{} is neither a lab nor a known server instance, so its Wine prefix is unknown (kiln debug attaches only inside a server's own prefix)", s.dir.display()),
    )
    .into())
}

fn with_pfx(dir: &Path) -> PathBuf {
    let mut p = dir.as_os_str().to_owned();
    p.push(".pfx");
    PathBuf::from(p)
}

fn resolve(a: &DebugArgs) -> Result<Target> {
    // before anything looks at the folder or lists instances
    if let Some(raw) = a.server.as_deref().map(str::to_string).or_else(|| std::env::var("KILN_SERVER").ok()).filter(|v| !v.is_empty()) {
        let p = kiln_manage::util::expand(&raw, &kiln_manage::util::home());
        if in_steam_library(&p) || in_steam_library(&canon(&p)) {
            return Err(refuse_steam(&p));
        }
    }
    let project = if a.server.is_none() { Project::find(None).ok() } else { None };
    let s = server::resolve(a.server.as_deref(), project.as_ref())?;
    // an instance id, a project's KilnServer or a symlink can still end up there
    let steam_runner = s.instance.as_ref().is_some_and(|i| matches!(i.runner, kiln_manage::server::instance::ServerRunner::Steam));
    if steam_runner || in_steam_library(&canon(&s.dir)) {
        return Err(refuse_steam(&s.dir));
    }
    if s.instance.as_ref().is_some_and(|i| i.dir.is_none()) {
        return Err(CliError::new("unsupported", "that server is remote; kiln debug attaches to local servers").into());
    }
    let prefix = prefix_of(&s)?;
    if let Some(p) = &prefix {
        if in_steam_library(&canon(p)) {
            return Err(refuse_steam(p));
        }
        if !p.is_dir() {
            return Err(CliError::new("not_found", format!("the server's Wine prefix {} does not exist", p.display())).into());
        }
    }
    let netcoredbg = netcoredbg()?;
    let (pid, host_pid, wine) = match &prefix {
        Some(p) => {
            let host = host_pid_in_prefix(&s, p)?;
            let wine = wine_of(host).ok_or_else(|| CliError::new("not_found", format!("cannot tell which wine runs pid {host}")))?;
            (wine_pid(&wine, p)?, host, Some(wine))
        }
        None => {
            let p = kiln_manage::proc::find_exe(&s.dir, SERVER_EXE)?.ok_or_else(|| not_running(&s.dir))?;
            (p.pid, p.pid, None)
        }
    };
    Ok(Target { server: s.dir.clone(), via: s.via, lab: s.lab.clone(), pid, host_pid, wine, prefix, netcoredbg })
}

pub fn run(a: DebugArgs, json: bool) -> Result<i32> {
    if a.dap {
        return match resolve(&a) {
            Ok(t) => proxy(&t),
            Err(e) => refuse_all(&e),
        };
    }
    let t = resolve(&a)?;
    let argv = t.argv();
    let env = t.env();
    if json {
        out::print_json(&json!({
            "ok": true, "server": t.server, "via": t.via, "lab": t.lab, "pid": t.pid, "hostPid": t.host_pid,
            "wine": t.wine, "prefix": t.prefix, "netcoredbg": t.netcoredbg, "env": env, "command": argv,
        }));
        return Ok(0);
    }
    println!("server    {}", t.server.display());
    println!("pid       {} (host pid {})", t.pid, t.host_pid);
    if let Some(p) = &t.prefix {
        println!("prefix    {}", p.display());
    }
    println!("debugger  {}", argv.join(" "));
    println!("\nEditors: run `kiln debug --server <server> --dap` as the debug adapter. Use only the DAP interpreter:");
    println!("a console session that ends while the server is paused can take the server down.");
    Ok(0)
}

// ---- Debug Adapter Protocol ----

/// One frame: `Content-Length: n\r\n\r\n<n bytes>`. None at end of input.
fn read_frame<R: BufRead>(r: &mut R) -> std::io::Result<Option<Vec<u8>>> {
    let mut len = None;
    let mut line = String::new();
    loop {
        line.clear();
        if r.read_line(&mut line)? == 0 {
            return Ok(None);
        }
        let l = line.trim_end();
        if l.is_empty() {
            if len.is_some() {
                break;
            }
            continue;
        }
        if let Some((k, v)) = l.split_once(':') {
            if k.trim().eq_ignore_ascii_case("content-length") {
                len = v.trim().parse::<usize>().ok();
            }
        }
    }
    let n = len.unwrap_or(0);
    if n > MAX_FRAME {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, format!("a {n} byte DAP frame")));
    }
    let mut b = vec![0; n];
    r.read_exact(&mut b)?;
    Ok(Some(b))
}

fn write_frame<W: Write>(w: &mut W, body: &[u8]) -> std::io::Result<()> {
    write!(w, "Content-Length: {}\r\n\r\n", body.len())?;
    w.write_all(body)?;
    w.flush()
}

fn write_json<W: Write>(w: &mut W, v: &Value) -> std::io::Result<()> {
    write_frame(w, v.to_string().as_bytes())
}

fn error_response(req: &Value, seq: i64, message: &str) -> Value {
    json!({
        "seq": seq, "type": "response", "request_seq": req["seq"], "command": req["command"], "success": false,
        "message": message, "body": { "error": { "id": 1, "format": message, "showUser": true } },
    })
}

/// What happens to a message from the client.
#[derive(Debug, PartialEq)]
enum Policy {
    Forward,
    /// a disconnect, made safe
    Disconnect,
    Refuse(String),
}

/// The safety rules, applied to every client message before the adapter sees it.
fn apply_policy(msg: &mut Value, pid: u32) -> Policy {
    if msg["type"] != "request" {
        return Policy::Forward;
    }
    match msg["command"].as_str().unwrap_or_default() {
        "disconnect" => {
            make_safe_disconnect(msg);
            Policy::Disconnect
        }
        "launch" => Policy::Refuse("kiln debug only attaches to a running server; use an attach configuration".into()),
        "terminate" | "restart" | "terminateThreads" => Policy::Refuse("kiln debug never ends or restarts the server (use kiln run --restart); disconnect instead".into()),
        "attach" => {
            let asked = &msg["arguments"]["processId"];
            let n = match asked {
                Value::Null => Some(0),
                Value::Number(n) => n.as_u64(),
                Value::String(s) if s.trim().is_empty() => Some(0),
                Value::String(s) => s.trim().parse::<u64>().ok(),
                _ => None,
            };
            match n {
                Some(0) => {}
                Some(n) if n == u64::from(pid) => {}
                _ => return Policy::Refuse(format!("kiln debug attaches only to this server's process (pid {pid}), not to {asked}; omit processId")),
            }
            if !msg["arguments"].is_object() {
                msg["arguments"] = json!({});
            }
            msg["arguments"]["processId"] = json!(pid);
            Policy::Forward
        }
        _ => Policy::Forward,
    }
}

fn make_safe_disconnect(msg: &mut Value) {
    if !msg["arguments"].is_object() {
        msg["arguments"] = json!({});
    }
    let a = msg["arguments"].as_object_mut().expect("just made an object");
    a.insert("terminateDebuggee".into(), json!(false));
    a.insert("suspendDebuggee".into(), json!(false));
    a.remove("restart");
}

/// Don't offer the client what is refused anyway: VS Code's Stop then goes straight to disconnect.
fn patch_adapter_message(msg: &mut Value) {
    if msg["type"] == "response" && msg["command"] == "initialize" && msg["success"] == true {
        if !msg["body"].is_object() {
            msg["body"] = json!({});
        }
        for k in ["supportTerminateDebuggee", "supportSuspendDebuggee", "supportsTerminateRequest", "supportsRestartRequest", "supportsTerminateThreadsRequest"] {
            msg["body"][k] = json!(false);
        }
    }
}

/// Notices a mod reloaded under an attached session: its module comes a second time.
#[derive(Default)]
struct ModuleWatch {
    seen: std::collections::HashSet<String>,
}

impl ModuleWatch {
    fn reloaded(&mut self, m: &Value) -> Option<String> {
        if m["type"] != "event" || m["event"] != "module" || m["body"]["reason"] != "new" {
            return None;
        }
        let name = m["body"]["module"]["name"].as_str()?.to_string();
        (!self.seen.insert(name.clone())).then_some(name)
    }
}

/// netcoredbg keeps breakpoints on the first copy of a module, so they stop nowhere after a reload.
fn reload_notice(module: &str) -> Value {
    let text = format!("{module} was reloaded: breakpoints in it no longer stop. Disconnect and attach again.\n");
    json!({ "seq": OWN_SEQ, "type": "event", "event": "output", "body": { "category": "important", "output": text } })
}

enum Ev {
    /// the client is gone (stdin EOF or a signal)
    ClientGone(&'static str),
    /// the client asked to disconnect (request seq, as forwarded)
    ClientDisconnect,
    /// the adapter answered this request
    AdapterResponse(i64),
    AdapterEof,
}

/// Blocks the signals that mean "the client is going away" and turns them into events. The mask is inherited, so this
/// runs after netcoredbg has been spawned.
#[cfg(unix)]
fn watch_signals(tx: mpsc::Sender<Ev>) {
    // SAFETY: sigset calls on a local set; sigwait only blocks this thread.
    unsafe {
        let mut set: libc::sigset_t = std::mem::zeroed();
        libc::sigemptyset(&mut set);
        for s in [libc::SIGINT, libc::SIGTERM, libc::SIGHUP] {
            libc::sigaddset(&mut set, s);
        }
        libc::pthread_sigmask(libc::SIG_BLOCK, &set, std::ptr::null_mut());
        std::thread::spawn(move || {
            loop {
                let mut sig = 0;
                if libc::sigwait(&set, &mut sig) == 0 {
                    let _ = tx.send(Ev::ClientGone(match sig {
                        libc::SIGINT => "SIGINT",
                        libc::SIGTERM => "SIGTERM",
                        _ => "SIGHUP",
                    }));
                }
            }
        });
    }
}

#[cfg(not(unix))]
fn watch_signals(_tx: mpsc::Sender<Ev>) {}

/// netcoredbg in its own process group: a Ctrl-C at the editor's terminal must not reach it while it is attached.
#[cfg(unix)]
fn own_group(c: &mut Command) {
    use std::os::unix::process::CommandExt;
    c.process_group(0);
}

#[cfg(not(unix))]
fn own_group(_c: &mut Command) {}

/// `gc` answers "<n> unloaded mod context(s) still alive": a mod that leaks its old copy keeps a stale module loaded, the
/// state in which breakpoints miss and disconnects have taken the server down (docs/kiln/modding/debugging.md).
fn stale_contexts_warning(gc: &Value) -> Option<String> {
    let message = gc["message"].as_str()?;
    let n: u64 = message.split_whitespace().next()?.parse().ok()?;
    (n > 0).then(|| format!("{message}: old copies of a reloaded mod are still loaded. Breakpoints may not stop, and a session in this state can take the server down. Restart the server before debugging."))
}

fn proxy(t: &Target) -> Result<i32> {
    // A reload leaves the old mod's module loaded until the next collection, and netcoredbg can bind a breakpoint to that
    // dead copy. Attaching after a reload stopped in 1 of 5 rounds without this, and the server died in both test loops;
    // with it, 6 of 6 twice and no deaths.
    let mut warning = None;
    match crate::control::send(&t.server, "gc", GC_WAIT) {
        Err(e) => eprintln!("kiln debug: no collection before attaching ({e}); after a reload, breakpoints may not stop"),
        Ok(r) => warning = stale_contexts_warning(&r),
    }
    let mut cmd = t.command();
    cmd.stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::inherit());
    own_group(&mut cmd);
    let mut child = cmd.spawn().map_err(|e| CliError::new("tool_missing", format!("cannot run {}: {e}", t.argv().join(" "))))?;
    let adapter_in = Arc::new(Mutex::new(Some(child.stdin.take().expect("piped"))));
    let adapter_out = child.stdout.take().expect("piped");
    let client_out = Arc::new(Mutex::new(std::io::stdout()));
    if let Some(w) = warning {
        eprintln!("kiln debug: {w}");
        let _ = write_json(&mut *client_out.lock().unwrap(), &json!({ "seq": OWN_SEQ, "type": "event", "event": "output", "body": { "category": "important", "output": format!("{w}\n") } }));
    }
    let (tx, rx) = mpsc::channel::<Ev>();
    watch_signals(tx.clone());

    // adapter -> client
    {
        let tx = tx.clone();
        let client_out = client_out.clone();
        std::thread::spawn(move || {
            let mut r = BufReader::new(adapter_out);
            let mut modules = ModuleWatch::default();
            while let Ok(Some(body)) = read_frame(&mut r) {
                let mut out = client_out.lock().unwrap();
                match serde_json::from_slice::<Value>(&body) {
                    Ok(mut m) => {
                        patch_adapter_message(&mut m);
                        if let Some(name) = modules.reloaded(&m) {
                            let _ = write_json(&mut *out, &reload_notice(&name));
                        }
                        let request_seq = m["request_seq"].as_i64();
                        // the response to the proxy's own disconnect stays here
                        if request_seq.is_none_or(|s| s < OWN_SEQ) {
                            let _ = write_json(&mut *out, &m);
                        }
                        drop(out);
                        if m["type"] == "response" {
                            let _ = tx.send(Ev::AdapterResponse(request_seq.unwrap_or(-1)));
                        }
                    }
                    Err(_) => {
                        let _ = write_frame(&mut *out, &body);
                    }
                }
            }
            let _ = tx.send(Ev::AdapterEof);
        });
    }

    // client -> adapter
    let client_disconnect_seq = Arc::new(Mutex::new(None::<i64>));
    {
        let tx = tx.clone();
        let adapter_in = adapter_in.clone();
        let client_out = client_out.clone();
        let client_disconnect_seq = client_disconnect_seq.clone();
        let pid = t.pid;
        std::thread::spawn(move || {
            let mut r = BufReader::new(std::io::stdin());
            let mut own_seq = OWN_SEQ;
            loop {
                let body = match read_frame(&mut r) {
                    Ok(Some(b)) => b,
                    _ => break,
                };
                let Ok(mut m) = serde_json::from_slice::<Value>(&body) else {
                    eprintln!("kiln debug: dropped a client message that is not JSON");
                    continue;
                };
                match apply_policy(&mut m, pid) {
                    Policy::Refuse(why) => {
                        own_seq += 1;
                        let _ = write_json(&mut *client_out.lock().unwrap(), &error_response(&m, own_seq, &why));
                    }
                    Policy::Forward => {
                        if let Some(w) = adapter_in.lock().unwrap().as_mut() {
                            let _ = write_json(w, &m);
                        }
                    }
                    Policy::Disconnect => {
                        *client_disconnect_seq.lock().unwrap() = m["seq"].as_i64();
                        if let Some(w) = adapter_in.lock().unwrap().as_mut() {
                            let _ = write_json(w, &m);
                        }
                        let _ = tx.send(Ev::ClientDisconnect);
                    }
                }
            }
            let _ = tx.send(Ev::ClientGone("stdin closed"));
        });
    }

    // Until the session ends: a disconnect is sent exactly once, and its response awaited.
    let mut disconnect_sent = false;
    let mut awaiting: Option<i64> = None;
    let mut deadline = None;
    let mut code = 0;
    loop {
        let ev = match deadline {
            Some(d) => match rx.recv_timeout(d - Instant::now().min(d)) {
                Ok(e) => e,
                Err(_) => {
                    eprintln!("kiln debug: netcoredbg has not finished after a disconnect; leaving it to detach (its stdin is closed)");
                    break;
                }
            },
            None => match rx.recv() {
                Ok(e) => e,
                Err(_) => break,
            },
        };
        match ev {
            Ev::ClientGone(why) => {
                if !disconnect_sent {
                    eprintln!("kiln debug: {why}; detaching from the server");
                    disconnect_sent = true;
                    awaiting = Some(OWN_SEQ);
                    let m = json!({ "seq": OWN_SEQ, "type": "request", "command": "disconnect", "arguments": { "terminateDebuggee": false, "suspendDebuggee": false } });
                    if let Some(w) = adapter_in.lock().unwrap().as_mut() {
                        let _ = write_json(w, &m);
                    }
                    deadline = Some(Instant::now() + DISCONNECT_WAIT);
                }
            }
            Ev::ClientDisconnect => {
                disconnect_sent = true;
                awaiting = *client_disconnect_seq.lock().unwrap();
                deadline = Some(Instant::now() + DISCONNECT_WAIT);
            }
            Ev::AdapterResponse(seq) => {
                if awaiting == Some(seq) {
                    awaiting = None;
                    // nothing more to say to it, and netcoredbg can sit on a read until its stdin closes
                    adapter_in.lock().unwrap().take();
                    deadline = Some(Instant::now() + EXIT_WAIT);
                }
            }
            Ev::AdapterEof => {
                if !disconnect_sent {
                    code = 1;
                    tell_client_adapter_died(t, &client_out);
                }
                break;
            }
        }
    }
    // never kill: closing stdin is what a clean netcoredbg exits on, and the child outlives us if it hangs
    adapter_in.lock().unwrap().take();
    let t0 = Instant::now();
    while t0.elapsed() < Duration::from_secs(5) {
        match child.try_wait() {
            Ok(Some(_)) | Err(_) => break,
            Ok(None) => std::thread::sleep(Duration::from_millis(50)),
        }
    }
    Ok(code)
}

fn tell_client_adapter_died(t: &Target, client_out: &Mutex<std::io::Stdout>) {
    let alive = kiln_manage::proc::alive(t.host_pid);
    let text = if alive {
        "netcoredbg exited while attached; the server is still running. Check that it still responds before trusting it.\n".to_string()
    } else {
        "netcoredbg exited while attached and the server is gone with it; see kiln/logs/kiln.log and restart it.\n".to_string()
    };
    eprintln!("kiln debug: {}", text.trim_end());
    let mut out = client_out.lock().unwrap();
    let _ = write_json(&mut *out, &json!({ "seq": OWN_SEQ, "type": "event", "event": "output", "body": { "category": "important", "output": text } }));
    let _ = write_json(&mut *out, &json!({ "seq": OWN_SEQ + 1, "type": "event", "event": "terminated", "body": {} }));
}

/// `--dap` when there is nothing to attach to: an editor shows a response's message, but only "the adapter exited" for
/// a process that just quits. Every request fails with the reason until the client disconnects; that one succeeds and
/// exits 0, because nothing is attached and a failure there would only stack a second error on the refusal.
fn refuse_all(e: &anyhow::Error) -> Result<i32> {
    let why = e.downcast_ref::<CliError>().map_or_else(|| format!("{e:#}"), |c| c.message.clone());
    eprintln!("kiln debug: {why}");
    let mut r = BufReader::new(std::io::stdin());
    let mut w = std::io::stdout();
    let mut seq = OWN_SEQ;
    while let Ok(Some(body)) = read_frame(&mut r) {
        let Ok(m) = serde_json::from_slice::<Value>(&body) else { continue };
        if m["type"] != "request" {
            continue;
        }
        seq += 1;
        if m["command"] == "disconnect" {
            let _ = write_json(&mut w, &json!({ "seq": seq, "type": "response", "request_seq": m["seq"], "command": "disconnect", "success": true }));
            return Ok(0);
        }
        let _ = write_json(&mut w, &error_response(&m, seq, &why));
    }
    Ok(1)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn req(command: &str, arguments: Value) -> Value {
        json!({ "seq": 7, "type": "request", "command": command, "arguments": arguments })
    }

    #[test]
    fn every_disconnect_keeps_the_debuggee() {
        for args in [json!({ "terminateDebuggee": true }), json!({ "terminateDebuggee": false }), json!({ "restart": true, "terminateDebuggee": true }), json!({}), Value::Null] {
            let mut m = req("disconnect", args);
            assert_eq!(apply_policy(&mut m, 32), Policy::Disconnect);
            assert_eq!(m["arguments"]["terminateDebuggee"], json!(false));
            assert_eq!(m["arguments"]["suspendDebuggee"], json!(false));
            assert!(m["arguments"].get("restart").is_none());
        }
        let mut m = json!({ "seq": 1, "type": "request", "command": "disconnect" });
        assert_eq!(apply_policy(&mut m, 32), Policy::Disconnect);
        assert_eq!(m["arguments"]["terminateDebuggee"], json!(false));
    }

    #[test]
    fn what_could_end_or_start_a_process_is_refused() {
        for c in ["launch", "terminate", "restart", "terminateThreads"] {
            assert!(matches!(apply_policy(&mut req(c, json!({})), 32), Policy::Refuse(_)), "{c}");
        }
    }

    #[test]
    fn attach_only_to_the_resolved_pid() {
        for ok in [json!({}), json!({ "processId": 0 }), json!({ "processId": 32 }), json!({ "processId": "32" }), json!({ "processId": "" }), json!({ "processId": null }), Value::Null] {
            let mut m = req("attach", ok.clone());
            assert_eq!(apply_policy(&mut m, 32), Policy::Forward, "{ok}");
            assert_eq!(m["arguments"]["processId"], json!(32), "{ok}");
        }
        for bad in [json!({ "processId": 33 }), json!({ "processId": "33" }), json!({ "processId": -1 }), json!({ "processId": "${command:pickProcess}" }), json!({ "processId": [32] })] {
            assert!(matches!(apply_policy(&mut req("attach", bad.clone()), 32), Policy::Refuse(_)), "{bad}");
        }
    }

    #[test]
    fn others_pass_untouched() {
        let mut m = req("setBreakpoints", json!({ "source": { "path": "/a.cs" } }));
        let before = m.clone();
        assert_eq!(apply_policy(&mut m, 32), Policy::Forward);
        assert_eq!(m, before);
        // a client's response to a reverse request is not a request
        let mut r = json!({ "seq": 2, "type": "response", "command": "disconnect", "request_seq": 1, "success": true });
        let before = r.clone();
        assert_eq!(apply_policy(&mut r, 32), Policy::Forward);
        assert_eq!(r, before);
    }

    #[test]
    fn capabilities_are_not_offered() {
        let mut m = json!({ "seq": 1, "type": "response", "command": "initialize", "request_seq": 1, "success": true,
                            "body": { "supportsTerminateRequest": true, "supportTerminateDebuggee": true, "supportsConfigurationDoneRequest": true } });
        patch_adapter_message(&mut m);
        assert_eq!(m["body"]["supportsTerminateRequest"], json!(false));
        assert_eq!(m["body"]["supportTerminateDebuggee"], json!(false));
        assert_eq!(m["body"]["supportsRestartRequest"], json!(false));
        assert_eq!(m["body"]["supportsConfigurationDoneRequest"], json!(true));
    }

    #[test]
    fn leaked_contexts_warn() {
        assert!(stale_contexts_warning(&json!({ "message": "0 unloaded mod context(s) still alive" })).is_none());
        assert!(stale_contexts_warning(&json!({ "message": "2 unloaded mod context(s) still alive" })).unwrap().contains("Restart the server"));
        assert!(stale_contexts_warning(&json!({ "ok": true })).is_none());
    }

    #[test]
    fn a_module_that_comes_twice_is_a_reload() {
        let new = |n: &str| json!({ "seq": 1, "type": "event", "event": "module", "body": { "reason": "new", "module": { "name": n } } });
        let mut w = ModuleWatch::default();
        assert_eq!(w.reloaded(&new("TplCode")), None);
        assert_eq!(w.reloaded(&new("Other")), None);
        assert_eq!(w.reloaded(&new("TplCode")), Some("TplCode".to_string()));
        assert_eq!(w.reloaded(&json!({ "type": "event", "event": "output", "body": {} })), None);
        let gone = json!({ "type": "event", "event": "module", "body": { "reason": "removed", "module": { "name": "Other" } } });
        assert_eq!(w.reloaded(&gone), None);
    }

    #[test]
    fn steam_paths() {
        assert!(in_steam_library(Path::new("/home/u/.local/share/Steam/steamapps/common/EnshroudedServer")));
        assert!(in_steam_library(Path::new("/mnt/games/SteamLibrary/steamapps/compatdata/2278520/pfx")));
        assert!(!in_steam_library(Path::new("/home/u/srv/enshrouded/labs/mylab")));
        assert!(!in_steam_library(Path::new("/home/u/steamapps-backup/server")));
    }

    #[test]
    fn tasklist_lines() {
        let l = "\"System Idle Process\",\"0\",\"Services\",\"0\",\"8 K\"\r\n\"Enshrouded_Server.exe\",\"32\",\"Console\",\"1\",\"1,234 K\"\r\n\"tasklist.exe\",\"64\",\"Console\",\"1\",\"9 K\"\r\n";
        assert_eq!(parse_tasklist(l, SERVER_EXE), vec![32]);
        assert!(parse_tasklist("INFO: No tasks are running\n", SERVER_EXE).is_empty());
    }

    #[test]
    fn frames_round_trip() {
        let mut b = Vec::new();
        write_frame(&mut b, b"{\"a\":1}").unwrap();
        write_frame(&mut b, b"{}").unwrap();
        let mut r = std::io::Cursor::new(b);
        assert_eq!(read_frame(&mut r).unwrap().unwrap(), b"{\"a\":1}");
        assert_eq!(read_frame(&mut r).unwrap().unwrap(), b"{}");
        assert!(read_frame(&mut r).unwrap().is_none());
        let mut r = std::io::Cursor::new(b"content-length:2\r\nContent-Type: x\r\n\r\n{}".to_vec());
        assert_eq!(read_frame(&mut r).unwrap().unwrap(), b"{}");
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn wine_is_found_above_the_preloader() {
        let t = std::env::temp_dir().join(format!("kiln-debug-wine-{}", std::process::id()));
        let lib = t.join("files/lib/wine/i386-unix");
        std::fs::create_dir_all(&lib).unwrap();
        std::fs::create_dir_all(t.join("files/bin")).unwrap();
        let wine = t.join("files/bin/wine");
        let decoy = lib.join("wine");
        for w in [&wine, &decoy] {
            std::fs::write(w, "").unwrap();
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(w, std::fs::Permissions::from_mode(0o755)).unwrap();
        }
        assert_eq!(wine_above(&lib.join("wine64-preloader")), Some(wine));
        std::fs::remove_dir_all(&t).unwrap();
    }

    #[test]
    fn lab_prefix_is_a_sibling() {
        assert_eq!(with_pfx(Path::new("/srv/labs/mylab")), PathBuf::from("/srv/labs/mylab.pfx"));
    }
}
