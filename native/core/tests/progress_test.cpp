// SPDX-License-Identifier: MIT
// kiln.progress's model (progress_model.h): the tasks, the one the window shows, its bar and when the window is due.
// Host-only: the Win32 window (services/progress.cpp) is checked under Wine (docs/kiln/eml.md, "Progress window").
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>
#include <vector>

#include "../src/progress_model.h"

using kiln::progress::Model;

static int failures = 0;
#define CHECK(cond)                                                                    \
    do {                                                                               \
        if (!(cond)) {                                                                 \
            std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);       \
            ++failures;                                                                \
        }                                                                              \
    } while (0)

int main() {
    {
        Model m;
        CHECK(!m.active() && !m.view().active && !m.due(10'000, 0));
        const uint64_t a = m.begin("Preparing the game data", 1000);
        CHECK(a != 0 && m.active());
        // not due before the delay, due at it
        CHECK(!m.due(1000, 2000) && !m.due(2999, 2000) && m.due(3000, 2000) && m.due(9000, 2000));
        auto v = m.view();
        CHECK(v.active && v.title == "Preparing the game data" && v.text.empty() && v.indeterminate && v.percent == 0);

        const uint64_t rev = v.revision;
        m.update(a, "Checking the game data: 3.0 GiB of 12.0 GiB (25%)", 3, 12);
        v = m.view();
        CHECK(v.revision != rev && !v.indeterminate && v.percent == 25 && v.done == 3 && v.total == 12);
        m.update(a, "x", 30, 12);  // more done than total: the bar stops at the end
        CHECK(m.view().percent == 100);
        m.update(a, "stage without a total", 5, 0);
        CHECK(m.view().indeterminate);
        m.update(9999, "nobody's", 1, 2);  // an unknown task changes nothing
        CHECK(m.view().text == "stage without a total");

        // two tasks: the one that began last is shown; when it ends the first is shown again; the delay runs from the
        // first of the tasks that run now
        const uint64_t b = m.begin("Second", 1500);
        CHECK(m.view().title == "Second" && m.due(3000, 2000) && !m.due(2999, 2000));
        m.end(b);
        CHECK(m.view().title == "Preparing the game data" && m.active());
        m.end(a);
        CHECK(!m.active() && !m.view().active && !m.due(99'999, 0));
        m.end(a);  // a second end changes nothing
        // a new burst of work starts its own delay
        const uint64_t c = m.begin("Later", 50'000);
        CHECK(c != a && !m.due(50'500, 2000) && m.due(52'000, 2000));
        m.end_all();
        CHECK(!m.active());
    }
    {
        // text is cut at 255 bytes and never inside a UTF-8 sequence (a three-byte character straddling the limit)
        std::string text(254, 'a');
        text += "\xE2\x82\xAC";  // the euro sign: bytes 254..256
        text += "tail";
        Model m;
        const uint64_t t = m.begin(text, 0);
        m.update(t, text, 0, 0);
        const auto v = m.view();
        CHECK(v.text.size() == 254 && v.title.size() == 254);
        CHECK(kiln::progress::clip(std::string(300, 'b')).size() == 255);
        CHECK(kiln::progress::clip("short") == "short");
    }
    {
        // the calls come from several threads while the window's thread reads
        Model m;
        const uint64_t id = m.begin("t", 0);
        std::vector<std::thread> threads;
        for (int i = 0; i < 4; ++i)
            threads.emplace_back([&m, id, i] {
                for (int n = 0; n < 2000; ++n) {
                    m.update(id, "line " + std::to_string(i), static_cast<uint64_t>(n), 2000);
                    (void)m.view();
                    (void)m.due(static_cast<uint64_t>(n), 100);
                }
            });
        for (auto& t : threads) t.join();
        m.end(id);
        CHECK(!m.active());
    }
    if (failures) return 1;
    std::puts("progress_test: ok");
    return 0;
}
