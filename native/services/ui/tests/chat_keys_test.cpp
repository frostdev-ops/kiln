// SPDX-License-Identifier: MIT
// Pure host test: native chat history consumes arrows only with its own edit focused.
#include "../chat_keys.h"
#include <cassert>

int main() {
    kui::chat_keys::Router q;
    kui::chat_keys::Action action{};
    assert(!q.key(0x26, 0, true));
    assert(!q.take(action));
    q.focus(true);
    assert(!q.key(0x26, 1, true)); // Ctrl+Up not reserved yet
    assert(!q.key(0x41, 0, true)); // other keys still reach the game
    assert(q.key(0x26, 0, true));
    assert(q.key(0x28, 0, true));
    assert(!q.key(0x26, 0, false)); // release is forwarded
    assert(q.take(action) && action == kui::chat_keys::Action::Older);
    assert(q.take(action) && action == kui::chat_keys::Action::Newer);
    assert(!q.take(action));
    assert(q.key(0x26, 0, true));
    q.focus(false);
    assert(!q.take(action)); // closing discards stale input
    q.focus(true);
    assert(!q.take(action));
    assert(q.key(0x28, 0, true));
    assert(q.take(action) && action == kui::chat_keys::Action::Newer);

    // What ends a browse: typed characters and the keys that edit without one; never Enter, Escape or a selection.
    using kui::chat_keys::edits_text;
    using kui::chat_keys::inserts_text;
    assert(inserts_text('a') && inserts_text(' ') && inserts_text('\t') && inserts_text(0x4f60));
    assert(!inserts_text('\r') && !inserts_text(0x1b) && !inserts_text(0x08) && !inserts_text(0x7f));
    assert(!inserts_text(0x01) && !inserts_text(0x03) && !inserts_text(0x16));  // Ctrl+A, Ctrl+C, Ctrl+V characters
    assert(edits_text(0x08, 0) && edits_text(0x2E, 0) && edits_text(0x2D, 2));    // Backspace, Delete, Shift+Insert
    assert(edits_text('V', 1) && edits_text('X', 1) && edits_text('Z', 1) && edits_text('Y', 1));
    assert(!edits_text('A', 1) && !edits_text('C', 1) && !edits_text(0x2D, 1));  // select all, copy, Ctrl+Insert copy
    assert(!edits_text('V', 0) && !edits_text(0x26, 0) && !edits_text(0x28, 0));
}
