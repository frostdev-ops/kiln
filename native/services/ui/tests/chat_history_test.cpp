// SPDX-License-Identifier: MIT
#include "../chat_history.h"
#include "../chat_focus.h"
#include <cassert>
#include <string>
#include <vector>
using kui::chat_history::History;

int main() {
    History h(3);
    h.load({"/first", "/second", "/second", "/register abc", "hello"});
    assert(h.count() == 2);
    assert(h.older("unsent") == "/second");
    assert(h.older("/second") == "/first");
    assert(h.older("/first") == "/first");
    assert(h.newer("/first") == "/second");
    assert(h.newer("/second") == "unsent");
    assert(h.newer("unsent") == "unsent");
    assert(!h.submit("/PASSWORD topsecret"));
    assert(!h.submit("/foo --token=abc"));
    assert(!h.submit("/login abc"));
    assert(!h.submit("/bad\nline"));
    assert(!h.submit(std::string(1025, 'a')));
    assert(h.count() == 2);
    assert(!h.submit("normal chat"));
    assert(h.count() == 3);
    assert(h.persisted() == std::vector<std::string>({"/first", "/second"}));
    assert(h.older("draft") == "normal chat");
    h.edited();
    assert(h.older("modified") == "normal chat");
    assert(h.newer("normal chat") == "modified");
    assert(h.submit("/third"));
    assert(h.persisted() == std::vector<std::string>({"/second", "/third"}));
    assert(!h.submit("/third"));  // repeated adjacent command
    assert(h.submit("plain"));  // eviction of a persisted entry changes the disk snapshot
    assert(h.submit("/fourth"));
    assert(h.persisted() == std::vector<std::string>({"/third", "/fourth"}));
    assert(History::valid("/say café"));
    assert(History::valid("/say 你好"));
    assert(!History::valid("\xc0\x80"));
    assert(!History::valid("\xed\xa0\x80"));
    assert(!History::valid("\xf4\x90\x80\x80"));
    assert(History::sensitive("/key ABC"));
    assert(!History::sensitive("/keyboard"));
    assert(History::sensitive("/set api_key sk-1"));     // '_' and digits split words
    assert(History::sensitive("/admin password123"));
    assert(History::sensitive("/connect --passwd=x"));
    assert(History::sensitive("/auth ABC"));
    assert(History::sensitive("/srv apiKey x"));          // the whole word "apikey", any case
    assert(!History::sensitive("/srv myToken x"));        // camelCase is one word: not caught (documented)
    assert(!History::sensitive("/give monkey"));
    assert(!History::sensitive("/passage"));
    // A normal five-byte slash command must be accepted. In-game issue #44
    // logging showed bytes=5/command=1 but count stayed zero.
    History direct;
    assert(History::valid("/help"));
    assert(!History::sensitive("/help"));
    assert(direct.submit("/help"));
    assert(direct.count() == 1);
    assert(direct.older("") == "/help");

    // Exact shape of the 17 Nov in-game failure: the private edit view
    // contained five bytes with a slash prefix and three control bytes.
    // The caller-owned widget exposed the valid five-byte /help.
    const std::string invalid_private{'/', '\x01', '\x02', '\x03', 'p'};
    assert(invalid_private.size() == 5);
    assert(!History::valid(invalid_private));
    History from_widget;
    assert(!from_widget.submit(invalid_private));
    assert(from_widget.count() == 0);
    assert(from_widget.submit("/help"));
    assert(from_widget.count() == 1);
    assert(from_widget.older("draft") == "/help");
    assert(from_widget.newer("/help") == "draft");

    // Regression (in-game 11:47): Up successfully recalled a line, but a
    // transient widget mismatch / one missing UI focus frame reset browse
    // state, so Down returned changed=0 instead of restoring the draft.
    History navigation;
    assert(navigation.submit("/one"));
    assert(navigation.submit("/two"));
    kui::chat_focus::Grace focus;
    assert(!focus.frame(true));
    assert(navigation.older("draft") == "/two");
    // Native UI's widget representation briefly diverged and one focus
    // frame was absent. Neither observation is a user edit.
    const std::string transient_widget = "stale-widget-buffer";
    assert(transient_widget != "/two");
    assert(!focus.frame(false));
    assert(!focus.frame(true));
    assert(navigation.browsing());
    assert(navigation.newer(transient_widget) == "draft");
    assert(!navigation.browsing());

    // Explicit typing must still cancel the browse cursor.
    assert(navigation.older("second draft") == "/two");
    navigation.edited(); // WndProc observed WM_CHAR, delete or paste
    assert(navigation.newer("manually edited") == "manually edited");

    // Prolonged loss of focus expires the cursor exactly once.
    assert(navigation.older("third draft") == "/two");
    for (unsigned i = 1; i < kui::chat_focus::Grace::kMissingFramesBeforeReset; ++i)
        assert(!focus.frame(false));
    assert(focus.frame(false));
    navigation.edited();
    assert(!focus.frame(false));
    assert(!navigation.browsing());
    assert(navigation.newer("closed chat") == "closed chat");

    // 12:01 in-game reproduction: widget_text() still returns the old
    // unsent draft on the next pre-TextInputProcess call, even though
    // EditSetText("/help") was verified against the widget AFTER that call.
    // Down must issue SetText("draft") despite next == pre-call widget.
    History stale_snapshot;
    assert(stale_snapshot.submit("/help"));
    const std::string old_widget = "draft";
    std::string applied;
    const bool before_first_up = stale_snapshot.browsing();
    const std::string recalled = stale_snapshot.older(old_widget);
    assert(recalled == "/help");
    assert(kui::chat_history::needs_replacement(recalled, old_widget,
                                               applied, before_first_up));
    applied = recalled; // engine accepted the replacement

    // Another Up at the oldest entry is a no-op, even when widget is stale.
    const bool before_repeat_up = stale_snapshot.browsing();
    const std::string repeated = stale_snapshot.older(old_widget);
    assert(before_repeat_up && repeated == "/help");
    assert(!kui::chat_history::needs_replacement(repeated, old_widget,
                                                applied, before_repeat_up));

    // The original bug compared restored == old_widget and skipped SetText.
    const bool before_down = stale_snapshot.browsing();
    const std::string restored = stale_snapshot.newer(old_widget);
    assert(before_down && !stale_snapshot.browsing());
    assert(restored == old_widget);
    assert(kui::chat_history::needs_replacement(restored, old_widget,
                                               applied, before_down));

    // Once restored, further Down is a no-op, using the live widget again.
    applied = restored;
    assert(!kui::chat_history::needs_replacement(restored, old_widget,
                                                applied, stale_snapshot.browsing()));

    // Empty draft is also a real replacement, not skipped by string emptiness.
    stale_snapshot.edited();
    const std::string from_empty = stale_snapshot.older("");
    assert(from_empty == "/help");
    applied = from_empty;
    const bool browsing_to_empty = stale_snapshot.browsing();
    const std::string empty_draft = stale_snapshot.newer("");
    assert(empty_draft.empty());
    assert(kui::chat_history::needs_replacement(empty_draft, "",
                                               applied, browsing_to_empty));

    // 12:06 observed defect: pre-call widget is empty, so old code saved
    // an empty draft even though the UI had shown "draft" before Up.
    // The previous frame's post-TextInputProcess snapshot is authoritative.
    kui::chat_history::DraftCapture hud_capture;
    assert(!hud_capture.valid());
    hud_capture.observe("draft");
    assert(hud_capture.valid());
    History draft_from_widget_after;
    assert(draft_from_widget_after.submit("/help"));
    const std::string stale_before_input; // empty although text was visible
    assert(stale_before_input.empty());
    const auto snapshot = hud_capture.source(stale_before_input);
    assert(snapshot == "draft");
    assert(draft_from_widget_after.older(snapshot) == "/help");
    assert(draft_from_widget_after.newer(stale_before_input) == "draft");
    assert(!draft_from_widget_after.browsing());

    // Blank drafts remain valid and must clear recalled text on Down.
    hud_capture.observe("");
    assert(hud_capture.valid());
    assert(hud_capture.source("stale") == "");
    hud_capture.clear();
    assert(!hud_capture.valid());
    assert(hud_capture.source("fallback") == "fallback");

    // HUD and Social editor snapshots must never contaminate one another.
    kui::chat_history::DraftCapture social_capture;
    hud_capture.observe("HUD draft");
    social_capture.observe("Social draft");
    assert(hud_capture.source("") == "HUD draft");
    assert(social_capture.source("") == "Social draft");
    hud_capture.clear();
    assert(social_capture.source("") == "Social draft");




}
