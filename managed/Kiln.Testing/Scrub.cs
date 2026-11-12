// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.RegularExpressions;

namespace Kiln.Testing;

/// <summary>Keeps private data out of the check ledger, with the classes of pattern the launcher's support report uses
/// (crates/kiln-manage/src/report.rs): home folders in Linux, Windows and Wine form become <c>&lt;home&gt;</c>, the
/// user's and the machine's names <c>&lt;user&gt;</c> and <c>&lt;host&gt;</c>, Steam ids <c>&lt;steamid&gt;</c>, IPv4 and
/// IPv6 addresses other than loopback <c>&lt;ip&gt;</c>, and values after a secret's name (<c>token=…</c>) and long
/// token-like runs <c>&lt;redacted&gt;</c>.</summary>
internal sealed partial class Scrubber
{
    /// <summary>Words that name nobody and fill Kiln's own lines: left alone as user or machine names.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "kiln", "mod", "mods", "game", "games", "steam", "user", "users", "admin", "administrator", "root", "test", "data", "log",
        "logs", "home", "server", "client", "host", "config", "state", "local", "share", "proton", "wine", "plugin", "plugins",
        "deck", "steamuser", "desktop", "windows", "linux", "redacted", "steamid", "name", "ip", "public", "default", "player",
        "localhost", "enshrouded", "keen",
    };

    private readonly List<(Regex Pattern, string Token)> _words = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _players = new(StringComparer.OrdinalIgnoreCase);
    private Regex? _playerPattern;

    /// <summary>Known platform names are private even when short or equal to a common word. Retained after disconnect.</summary>
    public void AddPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            if (!_players.Add(name.Trim())) return;
            var names = _players.OrderByDescending(n => n.Length).Select(n => n.Length <= 3
                ? $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(n)}(?![\p{{L}}\p{{N}}_])" : Regex.Escape(n));
            // Do not rewrite redaction tokens (including <player>) when a player chose e.g. "player" or "home".
            _playerPattern = new Regex(@"(?<token><(?:home|user|host|steamid|ip|redacted|player)>)|(?:" + string.Join("|", names) + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    // a home folder: /home/x, /var/home/x, /Users/x, C:\Users\x, Z:\home\x (any run of either slash, also doubled
    // inside JSON); the rest of the path stays
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:[A-Za-z]:)?[\\/]+(?:var[\\/]+)?(?:home|users)[\\/]+[^\\/\s""'<>|:*?]+", RegexOptions.IgnoreCase)]
    private static partial Regex HomePattern();

    // SteamID64 (any 17-digit run), the account number in [U:1:n] and in userdata/<n>
    [GeneratedRegex(@"(?<!\d)\d{17}(?!\d)")]
    private static partial Regex SteamId64();

    [GeneratedRegex(@"\[U:1:\d+\]", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId3();

    [GeneratedRegex(@"(userdata[\\/]+)\d+", RegexOptions.IgnoreCase)]
    private static partial Regex UserData();

    // dotted IPv4, not inside a version (v1.2.3.4) or a longer dotted number
    [GeneratedRegex(@"(?<![A-Za-z0-9.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![0-9]|\.[0-9])")]
    private static partial Regex Ipv4();

    // IPv6: eight groups, or a "::" form with at least two groups; not a C++ scope (keen::Ui) or a clock (12:34:56)
    [GeneratedRegex(@"(?<![0-9A-Za-z:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,6}(?::[0-9A-Fa-f]{1,4}){1,6}|(?:[0-9A-Fa-f]{1,4}:){1,7}:|:(?::[0-9A-Fa-f]{1,4}){1,7})(?:%[0-9A-Za-z]+)?(?![0-9A-Za-z:])")]
    private static partial Regex Ipv6();

    // a secret's name and its value: token=abc, "password": "x", Authorization: Bearer abc
    [GeneratedRegex(@"(?i)\b((?:access_?|auth_?|session_?|refresh_?|api_?)?(?:token|password|passwd|passphrase|secret|apikey|api_key|cookie|credentials?|ticket|steamid|steam_id|authorization|bearer)""?\s*[:=]\s*""?)(?:bearer\s+)?[^\s"",;&]+")]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex Bearer();

    // long token-like runs (a session ticket, a key); a sha256 (64 hex) stays readable
    [GeneratedRegex(@"[A-Za-z0-9+/_=-]{96,}")]
    private static partial Regex LongRun();

    /// <summary>The scrubber for this process: the user and machine names Windows, Wine and the environment report.</summary>
    public static Scrubber ForThisMachine()
    {
        var s = new Scrubber();
        void User(string? v) => s.AddWord(v, "<user>");
        void Host(string? v) => s.AddWord(v, "<host>");
        try { User(Environment.UserName); } catch { /* not known */ }
        try { Host(Environment.MachineName); } catch { /* not known */ }
        foreach (var k in new[] { "USER", "USERNAME", "LOGNAME" }) User(Environment.GetEnvironmentVariable(k));
        foreach (var k in new[] { "COMPUTERNAME", "HOSTNAME" }) Host(Environment.GetEnvironmentVariable(k));
        foreach (var k in new[] { "HOME", "USERPROFILE", "WINEHOMEDIR" })
            if (Environment.GetEnvironmentVariable(k) is { Length: > 0 } home)
                User(home.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault());
        return s;
    }

    /// <summary>Replaces <paramref name="word"/> (case ignored) with <paramref name="token"/>: inside other words when it
    /// has more than three characters, else only as a whole word. Common words are left alone.</summary>
    public void AddWord(string? word, string token)
    {
        var w = word?.Trim();
        if (string.IsNullOrEmpty(w) || w.Length < 2 || Common.Contains(w)) return;
        var body = Regex.Escape(w);
        var pattern = w.Length <= 3 ? $@"(?<![\p{{L}}\p{{N}}_]){body}(?![\p{{L}}\p{{N}}_])" : body;
        lock (_lock)
        {
            if (_words.Any(x => x.Pattern.ToString() == pattern)) return;
            _words.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), token));
            // longer names first, so a name that contains a shorter one is replaced whole
            _words.Sort((a, b) => b.Pattern.ToString().Length.CompareTo(a.Pattern.ToString().Length));
        }
    }

    /// <summary>The text without private data.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = HomePattern().Replace(text, "<home>");
        lock (_lock)
            foreach (var (p, token) in _words)
                s = p.Replace(s, token);
        s = SecretValue().Replace(s, m => m.Groups[1].Value + "<redacted>");
        s = Bearer().Replace(s, "Bearer <redacted>");
        s = UserData().Replace(s, "$1<steamid>");
        s = SteamId3().Replace(s, "<steamid>");
        s = SteamId64().Replace(s, "<steamid>");
        s = Ipv6().Replace(s, m => m.Value is "::1" or "::" ? m.Value : "<ip>");
        s = Ipv4().Replace(s, m =>
        {
            var parts = new int[4];
            for (var i = 0; i < 4; i++)
                if (!int.TryParse(m.Groups[i + 1].Value, out parts[i]) || parts[i] > 255) return m.Value;  // not an address
            return parts[0] == 127 || parts.All(p => p == 0) ? m.Value : "<ip>";
        });
        s = LongRun().Replace(s, "<redacted>");
        lock (_lock)
            if (_playerPattern is { } players)
                s = players.Replace(s, m => m.Groups["token"].Success ? m.Value : "<player>");
        return s;
    }

    /// <summary>One line (line breaks become spaces, control characters go), scrubbed, at most <paramref name="max"/>
    /// characters.</summary>
    public string Line(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(ch is '\r' or '\n' or '\t' ? ' ' : char.IsControl(ch) ? '\uFFFD' : ch);
        var s = Scrub(sb.ToString().Trim());
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
