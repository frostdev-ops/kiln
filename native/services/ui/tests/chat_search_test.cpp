// SPDX-License-Identifier: MIT
// Issue #44 Phase 1c: portable reverse-search matching/selection tests.
#include "../chat_search.h"
#include <cassert>
#include <string>
#include <vector>
using kui::chat_history::History;
using kui::chat_search::Search;

int main() {
    History h;
    assert(h.submit("/help"));
    assert(h.submit("hello world") == false); // session-only; still searchable
    assert(h.submit("/heal"));
    assert(h.submit("/help")); // nonadjacent repeat: show newest only in search
    assert(!h.submit("/password confidential")); // never enters history
    assert(h.submit("/say café"));
    assert(h.submit("/say 你好"));
    assert(h.submit("/say 😀"));
    assert(h.submit("/HELP"));

    Search s;
    s.begin(h.entries());
    assert(s.active());
    assert(s.query().empty());
    assert(s.matches().size() == 7); // one distinct /help, excludes sensitive
    assert(*s.chosen() == "/HELP");
    s.older();
    assert(*s.chosen() == "/say 😀");
    s.newer();
    assert(*s.chosen() == "/HELP");
    s.newer(); // wraps to oldest distinct submitted line
    assert(*s.chosen() == "hello world");

    s.begin(h.entries());
    assert(s.append_utf16('H', h.entries()));
    assert(s.append_utf16('E', h.entries()));
    assert(s.query() == "HE");
    // ASCII folding is independent of original letter case.
    assert(s.matches().size() == 4); // /HELP, /help, /heal, hello world
    assert(*s.chosen() == "/HELP");
    s.older();
    assert(s.selected() == 1);
    assert(s.select(1));
    assert(*s.chosen() == "/help");
    assert(!s.select(1000));

    // Substring matching: non-ASCII bytes are exact, no Unicode case fold.
    s.begin(h.entries());
    assert(s.append_utf16(0x00e9, h.entries()));
    assert(s.query() == "é");
    assert(s.matches().size() == 1 && *s.chosen() == "/say café");
    assert(s.backspace(h.entries()));
    assert(s.query().empty());
    assert(!s.backspace(h.entries()));

    s.begin(h.entries());
    assert(!s.append_utf16(0xd83d, h.entries())); // high surrogate alone is not a character
    assert(s.query().empty());
    assert(s.append_utf16(0xde00, h.entries())); // emoji forms one UTF-8 scalar
    assert(s.query() == "😀");
    assert(s.matches().size() == 1 && *s.chosen() == "/say 😀");
    assert(s.backspace(h.entries()));
    assert(s.query().empty());
    assert(!s.append_utf16(0xdc00, h.entries())); // stray low surrogate dropped
    assert(s.query().empty());

    s.begin(h.entries());
    assert(s.append_utf16('z', h.entries()));
    assert(s.matches().empty());
    assert(s.chosen() == nullptr);
    s.older(); s.newer(); // no-match cycling is safe
    assert(s.selected() == 0);
    assert(s.backspace(h.entries()));
    assert(!s.matches().empty());

    s.begin(h.entries());
    for (int i = 0; i < 256; ++i) assert(s.append_utf16('x', h.entries()));
    assert(s.query().size() == 256);
    assert(!s.append_utf16('x', h.entries())); // bounded to game's completion input cap
    assert(s.query().size() == 256);
    assert(s.backspace(h.entries()));
    assert(s.query().size() == 255);
    s.cancel();
    assert(!s.active() && s.query().empty() && s.matches().empty());

    // A long history line (native chat's 1024-byte cap) remains searchable.
    History long_lines;
    const std::string long_line = "/long " + std::string(990, 'a');
    assert(long_lines.submit(long_line));
    s.begin(long_lines.entries());
    assert(*s.chosen() == long_line);
    assert(s.append_utf16('L', long_lines.entries()));
    assert(s.matches().size() == 1);

    // Review follow-up: render-thread clicked indices are only valid for the
    // exact results generation that was displayed. A change of query (even
    // when the editor revision is unchanged) invalidates a stale click.
    Search click;
    click.begin(h.entries());
    const uint32_t shown = click.generation();
    assert(click.select_at_generation(1, shown));
    assert(click.append_utf16('H', h.entries()));
    assert(click.generation() != shown);
    assert(!click.select_at_generation(1, shown));
    assert(click.select_at_generation(1, click.generation())); // current view
    const uint32_t filtered = click.generation();
    assert(click.backspace(h.entries()));
    assert(!click.select_at_generation(1, filtered));
    const uint32_t before_close = click.generation();
    click.cancel();
    assert(!click.select_at_generation(0, before_close));
    click.begin(h.entries());
    assert(!click.select_at_generation(0, before_close)); // new session
}
