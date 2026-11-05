// SPDX-License-Identifier: MIT
// EMF commands (API v6 register_command, v15 command_sender, v17 register_command_ex) and players' chat lines (v19
// on_chat). Two native sources, as EMF has on a dedicated server:
//  - players' chat (kiln.chat, Pre): "/<command> args" naming a registered EMF command is consumed (nobody else
//    sees it) and run after the step; replies come back as chat lines only that player sees ("[EMF] ..."). Who may:
//    [chat] admins / everyone / deny in <configDir>/emf/emf_core.ini (else <game>/emf/emf_core.ini, EMF's file), and
//    players with the game's admin bit (kiln.players); EMF_COMMAND_PLAYERS commands are for everyone. Lines that are
//    no command go to on_chat after the step.
//  - <game>/emf/commands.txt: one command per line, run as the console (admin); replies go to kiln.log.
// Kiln's own /help stays Kiln.Chat's: EMF's built-ins answer to "emf.help", "emf.mods", "emf.settings", "emf.set" in
// the chat (and also without the prefix in commands.txt).
#include <intrin.h>

#include <algorithm>
#include <deque>
#include <map>

#include "emf_internal.h"

namespace kemf {

namespace {

struct Command {
    std::string name, lname, help;
    EmfCommandCallback cb;
    void* user;
    uint32_t flags;
    Mod* mod;
    std::atomic<bool> dead{false};
};
std::mutex g_mutex;
std::vector<std::unique_ptr<Command>> g_cmds;
std::deque<std::shared_ptr<CommandRun>> g_queue;  // waiting for the next server step

struct ChatLine {
    uint64_t account;
    uint32_t entity;
    std::string text;
};
std::deque<ChatLine> g_lines;
struct ChatReg {
    Mod* mod;
    EmfChatCallback cb;
    void* user;
    std::atomic<bool> dead{false};
};
std::vector<std::unique_ptr<ChatReg>> g_chat_regs;
bool g_chat_on = false;
KilnHandle g_chat_sub = 0;

// [chat] configuration
struct ChatConfig {
    std::vector<uint64_t> admins;
    std::vector<std::string> deny;
    bool everyone = false;
    std::filesystem::path file;
    std::filesystem::file_time_type mtime{};
} g_cfg;
std::map<uint64_t, std::deque<uint64_t>> g_rate;  // account -> times of its last commands (non-admins)
uint64_t g_last_file_check = 0;

bool valid_name(std::string_view n) {
    if (n.empty() || n.size() > 32) return false;
    for (char c : n)
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
            return false;
    return true;
}

Command* find_cmd(std::string_view name) {
    for (const auto& c : g_cmds)
        if (!c->dead && iequals(c->name, name)) return c.get();
    return nullptr;
}

bool add(Mod* m, const char* name, const char* help, EmfCommandCallback cb, void* user, uint32_t flags, const char* fn) {
    if (!name || !valid_name(name) || !cb) return refuse(m, fn, format("bad command name \"%s\" (1-32 of A-Z a-z 0-9 _ . -)", name ? name : "").c_str()), false;
    if (flags & ~static_cast<uint32_t>(EMF_COMMAND_PLAYERS)) return refuse(m, fn, "unknown flags"), false;
    std::lock_guard lock(g_mutex);
    if (Command* c = find_cmd(name)) {
        if (c->cb == cb && c->user == user) {
            c->help = help ? help : "";
            c->flags = flags;
            return true;
        }
        return refuse(m, fn, format("the command %s exists already", name).c_str()), false;
    }
    static const char* kBuiltins[] = {"help", "mods", "settings", "set", "emf.help", "emf.mods", "emf.settings", "emf.set", "chat.players"};
    for (const char* b : kBuiltins)
        if (iequals(b, name)) return refuse(m, fn, format("%s is a built-in command", name).c_str()), false;
    auto c = std::make_unique<Command>();
    c->name = name;
    c->lname = lower(name);
    c->help = help ? help : "";
    c->cb = cb;
    c->user = user;
    c->flags = flags;
    c->mod = m;
    g_cmds.push_back(std::move(c));
    return true;
}

bool api_register_command(const char* name, const char* help, EmfCommandCallback cb, void* user) {
    Mod* m = mod_of_address(reinterpret_cast<const void*>(cb));
    if (!m) m = caller_mod(_ReturnAddress());
    return add(m, name, help, cb, user, 0, "register_command");
}

bool api_register_command_ex(const EmfCommandDef* d) {
    Mod* m = d ? mod_of_address(reinterpret_cast<const void*>(d->cb)) : nullptr;
    if (!m) m = caller_mod(_ReturnAddress());
    if (!d || d->size < sizeof(EmfCommandDef)) return refuse(m, "register_command_ex", "bad definition"), false;
    return add(m, d->name, d->help, d->cb, d->user, d->flags, "register_command_ex");
}

void api_command_reply(const char* fmt, ...) {
    if (!t_ctx.cmd) return;  // EMF: outside a command it does nothing
    va_list ap;
    va_start(ap, fmt);
    const std::string text = vformat(fmt ? fmt : "", ap);
    va_end(ap);
    command_reply_text(t_ctx.cmd, text);
}

bool api_command_sender(EmfCommandSender* out) {
    if (!out || out->size < sizeof(EmfCommandSender) || !t_ctx.cmd) return false;
    const uint32_t size = out->size;
    *out = t_ctx.cmd->sender;
    out->size = size;
    return true;
}

bool api_on_chat(EmfChatCallback cb, void* user) {
    Mod* m = mod_of_address(reinterpret_cast<const void*>(cb));
    if (!m) m = caller_mod(_ReturnAddress());
    if (!cb) return refuse(m, "on_chat", "no callback"), false;
    if (!g_chat_on) return refuse(m, "on_chat", "players' chat lines do not reach mods here (kiln.chat cannot receive)"), false;
    auto r = std::make_unique<ChatReg>();
    r->mod = m;
    r->cb = cb;
    r->user = user;
    std::lock_guard lock(g_mutex);
    g_chat_regs.push_back(std::move(r));
    return true;
}

// ---------------------------------------------------------------------------------------------- config --------
void read_config() {
    std::filesystem::path f = P.config / L"emf" / L"emf_core.ini";
    std::error_code ec;
    if (!std::filesystem::exists(f, ec)) f = P.emf / L"emf_core.ini";
    const auto mt = std::filesystem::last_write_time(f, ec);
    if (f == g_cfg.file && !ec && mt == g_cfg.mtime) return;
    g_cfg = ChatConfig{};
    g_cfg.file = f;
    g_cfg.mtime = ec ? std::filesystem::file_time_type{} : mt;
    std::string text;
    if (!read_file(f, &text)) return;
    bool in_chat = false;
    size_t i = 0;
    while (i < text.size()) {
        size_t j = text.find('\n', i);
        if (j == std::string::npos) j = text.size();
        std::string line = text.substr(i, j - i);
        i = j + 1;
        while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
        if (line.empty() || line[0] == ';' || line[0] == '#') continue;
        if (line[0] == '[') {
            in_chat = iequals(line, "[chat]");
            continue;
        }
        if (!in_chat) continue;
        const size_t eq = line.find('=');
        if (eq == std::string::npos) continue;
        const std::string k = lower(line.substr(0, line.find_last_not_of(' ', eq - 1) + 1));
        std::string v = line.substr(eq + 1);
        const size_t sc = v.find(';');
        if (sc != std::string::npos) v.resize(sc);
        std::replace(v.begin(), v.end(), ',', ' ');
        std::vector<std::string> words;
        size_t a = 0;
        while (a < v.size()) {
            while (a < v.size() && v[a] == ' ') ++a;
            size_t b = a;
            while (b < v.size() && v[b] != ' ') ++b;
            if (b > a) words.push_back(v.substr(a, b - a));
            a = b;
        }
        if (k == "admins") {
            for (const auto& w : words) {
                if (w.size() == 17 && w.find_first_not_of("0123456789") == std::string::npos) {
                    logf(KILN_LOG_WARN, "[chat] admins: %s is a Steam64 id, not an account hash: skipped", w.c_str());
                    continue;
                }
                g_cfg.admins.push_back(std::strtoull(w.c_str(), nullptr, 0));
            }
        } else if (k == "everyone") {
            g_cfg.everyone = !words.empty() && words[0] == "1";
        } else if (k == "deny") {
            for (const auto& w : words) g_cfg.deny.push_back(lower(w));
        }
    }
    logf(KILN_LOG_INFO, "chat commands: %zu admin account(s), everyone=%d, %zu denied (%s)", g_cfg.admins.size(),
         g_cfg.everyone ? 1 : 0, g_cfg.deny.size(), path_utf8(f).c_str());
}

bool game_admin(uint32_t entity) {
    if (!S.players || !entity) return false;
    KilnPlayer p{};
    p.size = sizeof p;
    return S.players->get(entity, &p) == KILN_OK && (p.flags & KILN_PLAYER_HAS_ADMIN) && p.is_admin;
}

// ---------------------------------------------------------------------------------------------- parsing -------
std::vector<std::string> split_args(std::string_view s) {
    std::vector<std::string> out;
    size_t i = 0;
    while (i < s.size()) {
        while (i < s.size() && (s[i] == ' ' || s[i] == '\t')) ++i;
        if (i >= s.size()) break;
        std::string a;
        if (s[i] == '"') {
            ++i;
            while (i < s.size() && s[i] != '"') a += s[i++];
            ++i;
        } else {
            while (i < s.size() && s[i] != ' ' && s[i] != '\t') a += s[i++];
        }
        out.push_back(a);
    }
    return out;
}

void reply(const std::shared_ptr<CommandRun>& run, const std::string& t) { command_reply_text(run, t); }

bool may_run(const CommandRun& run, const Command* c) {
    if (run.sender.admin) return true;
    if (!c || !(c->flags & EMF_COMMAND_PLAYERS)) return false;
    return std::find(g_cfg.deny.begin(), g_cfg.deny.end(), c->lname) == g_cfg.deny.end();
}

void builtin_help(const std::shared_ptr<CommandRun>& run, const std::vector<std::string>& a) {
    std::vector<std::string> lines;
    {
        std::lock_guard lock(g_mutex);
        for (const auto& c : g_cmds)
            if (!c->dead && may_run(*run, c.get())) lines.push_back(c->name + " " + c->help);
    }
    std::sort(lines.begin(), lines.end());
    const size_t per = run->to_chat ? 8 : lines.size() + 1;
    const size_t page = a.size() > 1 ? std::max<size_t>(1, std::strtoul(a[1].c_str(), nullptr, 10)) : 1;
    const size_t pages = std::max<size_t>(1, (lines.size() + per - 1) / per);
    reply(run, format("EMF commands (kiln-emf), page %zu of %zu:", std::min(page, pages), pages));
    for (size_t i = (std::min(page, pages) - 1) * per; i < lines.size() && i < std::min(page, pages) * per; ++i) reply(run, lines[i]);
}

void builtin_mods(const std::shared_ptr<CommandRun>& run) {
    static const char* kStatus[] = {"loaded", "FAILED", "off", "not loaded", "script", "pending"};
    for (const auto& m : g_mods)
        reply(run, format("%s %s: %s%s%s", m->id.c_str(), m->version.c_str(), m->status < 6 ? kStatus[m->status] : "?",
                          m->reason.empty() ? "" : " - ", m->reason.c_str()));
}

}  // namespace

void command_reply_text(const std::shared_ptr<CommandRun>& run, std::string_view text) {
    if (!run) return;
    size_t i = 0;
    while (i <= text.size()) {
        size_t j = text.find('\n', i);
        if (j == std::string_view::npos) j = text.size();
        const std::string line(text.substr(i, j - i));
        logf(KILN_LOG_INFO, "[cmd] %s", line.c_str());
        if (run->to_chat && S.chat && run->sender.entity && run->replies <= 10) {
            const std::string out = run->replies == 10 ? std::string("[EMF] ...") : "[EMF] " + line;
            ++run->replies;
            const KilnEntity to = run->sender.entity;
            if (g_api->on_tick_thread()) S.chat->send(to, kstr(out), nullptr);
        }
        if (j == text.size()) break;
        i = j + 1;
    }
}

bool run_builtin_or_mod_command(EmfWorld* w, const std::shared_ptr<CommandRun>& run) {
    std::vector<std::string> args = split_args(run->line);
    if (args.empty()) return false;
    if (!args[0].empty() && args[0][0] == '/') args[0].erase(0, 1);
    std::string name = args[0];
    std::string base = lower(name);
    if (base.rfind("emf.", 0) == 0 && (base == "emf.help" || base == "emf.mods" || base == "emf.settings" || base == "emf.set"))
        base = base.substr(4);
    if (base == "help") return builtin_help(run, args), true;
    if (base == "mods" || base == "settings" || base == "set" || base == "chat.players") {
        if (!run->sender.admin) return reply(run, "you may not run " + name), true;
        if (base == "mods") builtin_mods(run);
        else if (base == "settings") {
            Mod* m = args.size() > 1 ? find_mod(args[1]) : nullptr;
            reply(run, m ? m->id + ": " + settings_summary(m) : std::string("usage: settings <mod>"));
        } else if (base == "set") {
            if (args.size() < 4) return reply(run, "usage: set <mod> <key> <value>"), true;
            std::string why;
            std::string value = args[3];
            for (size_t i = 4; i < args.size(); ++i) value += " " + args[i];
            if (settings_set_text(args[1].c_str(), args[2].c_str(), value.c_str(), &why))
                reply(run, format("%s.%s = %s (saved%s)", args[1].c_str(), args[2].c_str(), value.c_str(),
                                  args[2] == "enabled" ? "; applies at the next start" : ""));
            else
                reply(run, format("not changed: %s", why.c_str()));
        } else if (S.chat) {
            KilnChatPlayer ps[64];
            uint32_t n = 0;
            S.chat->players(ps, 64, &n);
            for (uint32_t i = 0; i < n && i < 64; ++i)
                reply(run, format("player %u %s account 0x%016llx", ps[i].entity, ps[i].name, static_cast<unsigned long long>(ps[i].account)));
            if (!n) reply(run, "no players are connected");
        }
        return true;
    }
    Command* c;
    {
        std::lock_guard lock(g_mutex);
        c = find_cmd(name);
    }
    if (!c) return false;
    if (!may_run(*run, c)) {
        reply(run, "you may not run " + c->name);
        logf(KILN_LOG_INFO, "chat command %s refused from account 0x%016llx", c->name.c_str(),
             static_cast<unsigned long long>(run->sender.account));
        return true;
    }
    args[0] = c->name;
    std::vector<const char*> argv;
    for (const auto& a : args) argv.push_back(a.c_str());
    argv.push_back(nullptr);
    struct Call {
        EmfCommandCallback cb;
        EmfWorld* w;
        int argc;
        const char* const* argv;
        void* user;
    } call{c->cb, w, static_cast<int>(args.size()), argv.data(), c->user};
    ContextScope scope(c->mod, w, true, run);
    if (!guarded(c->mod, ("the command " + c->name).c_str(),
                 [](void* p) {
                     auto* k = static_cast<Call*>(p);
                     k->cb(k->w, k->argc, k->argv, k->user);
                 },
                 &call)) {
        c->dead = true;
        reply(run, c->name + " faulted and was switched off");
    }
    return true;
}

namespace {

bool KILN_CALL on_chat_line(const KilnChatMessage* msg, void*) {
    const std::string_view text = sv(msg->text);
    if (!text.empty() && text[0] == '/') {
        const auto args = split_args(text.substr(1));
        if (!args.empty()) {
            const std::string l = lower(args[0]);
            bool ours = l == "emf.help" || l == "emf.mods" || l == "emf.settings" || l == "emf.set";
            {
                std::lock_guard lock(g_mutex);
                ours = ours || find_cmd(args[0]) != nullptr;
            }
            if (ours) {
                read_config();
                auto run = std::make_shared<CommandRun>();
                run->line = std::string(text.substr(1));
                run->sender.size = sizeof(EmfCommandSender);
                run->sender.entity = msg->sender.entity;
                run->sender.account = msg->sender.account;
                run->sender.from_chat = 1;
                const bool listed = std::find(g_cfg.admins.begin(), g_cfg.admins.end(), msg->sender.account) != g_cfg.admins.end();
                run->sender.admin = (g_cfg.everyone || listed || game_admin(msg->sender.entity)) ? 1 : 0;
                run->to_chat = true;
                if (!run->sender.admin) {  // at most 5 commands in 10 s for players who are no admins
                    auto& q = g_rate[msg->sender.account];
                    const uint64_t now = now_ms();
                    while (!q.empty() && now - q.front() > 10000) q.pop_front();
                    if (q.size() >= 5) {
                        if (q.size() == 5) {
                            q.push_back(now);
                            if (S.chat && msg->sender.entity) S.chat->send(msg->sender.entity, kstr("[EMF] too many commands"), nullptr);
                        }
                        return true;
                    }
                    q.push_back(now);
                }
                std::lock_guard lock(g_mutex);
                if (g_queue.size() >= 256) {
                    if (S.chat && msg->sender.entity)
                        S.chat->send(msg->sender.entity, kstr("[EMF] too many commands are waiting"), nullptr);
                    return true;
                }
                g_queue.push_back(run);
                return true;  // consumed: nobody else sees the command line
            }
        }
    }
    std::lock_guard lock(g_mutex);
    if (!g_chat_regs.empty()) {
        if (g_lines.size() >= 256) {
            static bool told = false;
            if (!told) logf(KILN_LOG_WARN, "on_chat: more than 256 lines wait for the next step; lines are dropped");
            told = true;
        } else {
            std::string t(text);
            for (char& ch : t)
                if (static_cast<unsigned char>(ch) < 0x20) ch = '?';
            g_lines.push_back({msg->sender.account, msg->sender.entity, t});
        }
    }
    return false;
}

}  // namespace

void commands_on_ready() {
    read_config();
    if (!S.chat) {
        logf(KILN_LOG_INFO, "kiln.chat is not available: EMF commands run from emf\\commands.txt only, on_chat is off");
        return;
    }
    KilnChatStatus st{};
    st.size = sizeof st;
    if (S.chat->status(&st) == KILN_OK && !st.receive_hooked) {
        logf(KILN_LOG_INFO, "kiln.chat cannot receive players' lines on this build: chat commands and on_chat are off");
        return;
    }
    g_chat_on = S.chat->subscribe(g_owner, &on_chat_line, nullptr, &g_chat_sub) == KILN_OK;
    logf(KILN_LOG_INFO, "players' chat: %s", g_chat_on ? "EMF commands and on_chat on (kiln.chat)" : "subscribe failed");
}

void commands_server_pre() {
    const uint64_t now = now_ms();
    if (now - g_last_file_check < 500) return;
    g_last_file_check = now;
    const auto file = P.emf / L"commands.txt";
    std::error_code ec;
    if (!std::filesystem::exists(file, ec) || std::filesystem::file_size(file, ec) == 0) return;
    auto taken = file;
    taken += L".running";
    std::filesystem::remove(taken, ec);
    std::filesystem::rename(file, taken, ec);
    if (ec) return;
    std::string text;
    read_file(taken, &text);
    std::filesystem::remove(taken, ec);
    size_t i = 0;
    std::lock_guard lock(g_mutex);
    while (i < text.size()) {
        size_t j = text.find('\n', i);
        if (j == std::string::npos) j = text.size();
        std::string line = text.substr(i, j - i);
        i = j + 1;
        while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
        if (line.empty() || line[0] == '#' || line[0] == ';') continue;
        auto run = std::make_shared<CommandRun>();
        run->line = line;
        run->sender.size = sizeof(EmfCommandSender);
        run->sender.admin = 1;
        logf(KILN_LOG_INFO, "[cmd] > %s (emf\\commands.txt)", line.c_str());
        g_queue.push_back(run);
    }
}

void commands_after_step(EmfWorld* w, uint8_t kind) {
    if (kind != EMF_WORLD_SERVER) return;
    std::deque<std::shared_ptr<CommandRun>> runs;
    std::deque<ChatLine> lines;
    std::vector<ChatReg*> regs;
    {
        std::lock_guard lock(g_mutex);
        runs.swap(g_queue);
        lines.swap(g_lines);
        for (const auto& r : g_chat_regs)
            if (!r->dead) regs.push_back(r.get());
    }
    for (auto& run : runs)
        if (!run_builtin_or_mod_command(w, run)) command_reply_text(run, "unknown command: " + run->line);
    for (const ChatLine& l : lines)
        for (ChatReg* r : regs) {
            if (r->dead) continue;
            struct Call {
                EmfChatCallback cb;
                EmfWorld* w;
                uint64_t account;
                uint32_t entity;
                const char* text;
                void* user;
            } c{r->cb, w, l.account, l.entity, l.text.c_str(), r->user};
            ContextScope scope(r->mod, w, true);
            if (!guarded(r->mod, "an on_chat callback",
                         [](void* p) {
                             auto* k = static_cast<Call*>(p);
                             k->cb(k->w, k->account, k->entity, k->text, k->user);
                         },
                         &c))
                r->dead = true;
        }
}

void fill_commands(EmfApi* t) {
    t->register_command = &api_register_command;
    t->command_reply = &api_command_reply;
    t->command_sender = &api_command_sender;
    t->register_command_ex = &api_register_command_ex;
    t->on_chat = &api_on_chat;
}

std::string commands_json();
std::string commands_json() {
    std::lock_guard lock(g_mutex);
    std::string s = "[";
    bool first = true;
    for (const auto& c : g_cmds) {
        s += (first ? "" : ",") + std::string("{\"name\":\"") + c->name + "\",\"mod\":\"" + mod_name(c->mod) +
             "\",\"players\":" + ((c->flags & EMF_COMMAND_PLAYERS) ? "true" : "false") + ",\"active\":" + (c->dead ? "false" : "true") + "}";
        first = false;
    }
    return s + "]";
}

}  // namespace kemf
