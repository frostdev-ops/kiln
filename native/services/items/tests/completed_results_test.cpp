// SPDX-License-Identifier: MIT
#include "../completed_results.h"
#include "../../../core/abi/services/kiln_items.h"
#include <cstdio>
#include <cstring>

namespace {
int failures = 0;
void check(bool ok, const char* what) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", what); failures += !ok; }
KilnItemsResult result(uint64_t ticket, uint32_t count = 2) {
    KilnItemsResult r{};
    r.size = sizeof r; r.ticket = ticket; r.entity = 42; r.item = 24; r.requested = r.done = count;
    r.change_count = 1; r.changes[0].slot = 7; r.changes[0].amount = count;
    std::strcpy(r.message, "committed");
    return r;
}
}
int main() {
    static_assert(KILN_ITEMS_VERSION == 3 && offsetof(KilnItemsApi, peek_result) == 192 && sizeof(KilnItemsApi) == 200,
                  "peek appends one row without moving the existing v3 ABI");
    kitems::CompletedResults<KilnItemsResult> queue;
    KilnItemsResult out{};
    check(!queue.peek(1, &out), "uncommitted requests have no result");
    const auto first = result(1), second = result(2);
    queue.push_back(first); queue.push_back(second);
    check(queue.peek(2, &out) && std::memcmp(&out, &second, sizeof out) == 0,
          "save after inventory commit can copy the entire queued result before callback delivery");
    check(queue.peek(2, &out) && queue.peek(2, &out), "peeking neither consumes results nor fires callbacks");
    const auto& batch = queue.begin_delivery();
    check(batch.size() == 2 && batch[0].ticket == 1, "callbacks keep their original delivery order");
    check(queue.peek(1, &out) && queue.peek(2, &out),
          "save inside the first callback sees its own and every later committed result in the active batch");
    queue.push_back(result(3, 4));
    check(queue.peek(3, &out) && out.done == 4 && batch.size() == 2,
          "a newly committed result remains queued without invalidating the active callback pointers");
    queue.end_delivery();
    check(!queue.peek(1, &out) && !queue.peek(2, &out) && queue.peek(3, &out),
          "delivered results expire only after the whole callback batch returns");
    check(queue.begin_delivery().size() == 1 && queue.peek(3, &out), "the next batch delivers a result committed during callbacks");
    queue.end_delivery();
    check(queue.empty() && !queue.peek(3, &out), "no completed-result history grows after delivery");
    return failures ? 1 : 0;
}
