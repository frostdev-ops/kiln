// SPDX-License-Identifier: MIT
//! /proc: finding an instance's server process, UDP ports, signals. Shims over kiln_manage::server::procs.
#[allow(unused_imports)]
pub use kiln_manage::server::procs::*;

// Unix: kilnctl's Wine instances (Unix paths, tar, /proc); on Windows kilnctl only bundles, installs and administers.
#[cfg(all(test, unix))]
mod tests {
    use super::*;

    #[test]
    fn environ() {
        let env = b"HOME=/home/u\0WINEPREFIX=/srv/x.pfx\0WINEPREFIXX=no\0";
        assert_eq!(parse_environ(env, "WINEPREFIX"), Some(&b"/srv/x.pfx"[..]));
        assert_eq!(parse_environ(env, "HOME"), Some(&b"/home/u"[..]));
        assert_eq!(parse_environ(env, "WINE"), None);
        assert_eq!(parse_environ(b"", "X"), None);
    }

    #[test]
    fn udp_table() {
        let t = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops\n\
  1234: 00000000:6460 00000000:0000 07 00000000:00000000 00:00000000 00000000  1000        0 9876543 2 0000000000000000 0\n\
  1235: 0100007F:3D15 00000000:0000 07 00000000:00000000 00:00000000 00000000  1000        0 111 2 0000000000000000 0\n";
        assert_eq!(parse_udp_table(t, 25696), vec![9_876_543]);
        assert_eq!(parse_udp_table(t, 15637), vec![111]);
        assert!(parse_udp_table(t, 25760).is_empty());
        let t6 = "  sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops\n\
  10: 00000000000000000000000000000000:64A0 00000000000000000000000000000000:0000 07 00000000:00000000 00:00000000 00000000  1000        0 42 2 0000000000000000 0\n";
        assert_eq!(parse_udp_table(t6, 25760), vec![42]);
    }

    #[test]
    fn stat_state() {
        assert_eq!(parse_stat_state("123 (enshrouded_serv) S 1 2 3"), Some('S'));
        assert_eq!(parse_stat_state("9 (a) b) c) Z 1"), Some('Z'));
        assert_eq!(parse_stat_state("garbage"), None);
    }

    #[test]
    fn self_is_alive() {
        assert!(alive(std::process::id()));
        assert!(uptime(std::process::id()).is_some());
    }
}
