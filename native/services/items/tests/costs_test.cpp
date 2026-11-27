// SPDX-License-Identifier: MIT
// kiln.item_costs host test (no game, no Wine): costs_logic.h's request checks, wear and restore arithmetic, and the
// receipt table (one debit per request id, conflicts, closed ids, settlement, acknowledgement, what did not run, expiry).
//   g++ -std=c++20 -Wall -Wextra -I native/core/abi native/services/items/tests/costs_test.cpp -o costs_test && ./costs_test
#include <cstdio>
#include <cstring>

#include "../costs_logic.h"

namespace {
int failures = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    failures += !ok;
}

using namespace kitems::costs;

constexpr uint64_t kSlot = 2001ull | (3ull << 32);  // inventory 2001, index 3

KilnSelectedCost take(uint32_t amount = 2, uint32_t expected = 5, uint64_t lo = 1) {
    KilnSelectedCost c{};
    c.size = sizeof c;
    c.kind = KILN_COST_STACK_TAKE;
    c.world_epoch = 3;
    c.character_session = 77;
    c.holder = 1;
    c.slot = kSlot;
    c.item = 0xabcd;
    c.expected_count = expected;
    c.amount = amount;
    c.request_lo = lo;
    c.request_hi = 9;
    return c;
}

KilnSelectedCost wear(uint32_t amount = 1, uint32_t allow_break = 0, uint64_t lo = 2) {
    KilnSelectedCost c = take(1, 1, lo);
    c.kind = KILN_COST_GEAR_WEAR;
    c.expected_count = 1;
    c.amount = amount;
    c.item_entity = 4100;
    c.item_stamp = 17;
    c.allow_break = allow_break;
    return c;
}

void shapes() {
    check(check_shape(take()) == nullptr, "a stack take of 2 from a slot of 5 is well formed");
    check(check_shape(wear()) == nullptr, "a gear wear of 1 is well formed");
    KilnSelectedCost c = take();
    c.size = 40;
    check(check_shape(c) != nullptr, "a cost smaller than KilnSelectedCost refuses");
    c = take();
    c.kind = 0;
    check(check_shape(c) != nullptr, "kind 0 (no cost) refuses: a no-cost step needs no receipt");
    c.kind = 5;
    check(check_shape(c) != nullptr, "an unknown kind refuses");
    c = take(6, 5);
    check(check_shape(c) != nullptr, "a take of more than the slot's expected count refuses");
    c = take(0, 5);
    check(check_shape(c) != nullptr, "a take of 0 refuses");
    c = take(100001, 100001);
    check(check_shape(c) != nullptr, "a take over 100000 refuses");
    c = take();
    c.slot = 2001ull | (8ull << 32);
    check(check_shape(c) != nullptr, "slot index 8 refuses");
    c.slot = 2001ull | (1ull << 41);
    check(check_shape(c) != nullptr, "a slot id with bits above 39 refuses");
    c.slot = 0;
    check(check_shape(c) != nullptr, "a stack take without a slot refuses (no search)");
    c = take();
    c.item_entity = 5;
    check(check_shape(c) != nullptr, "a stack take naming an item entity refuses");
    c = take();
    c.request_lo = c.request_hi = 0;
    check(check_shape(c) != nullptr, "request id 0 refuses");
    c = take();
    c.world_epoch = 0;
    check(check_shape(c) != nullptr, "world 0 refuses");
    c = take();
    c.reserved = 1;
    check(check_shape(c) != nullptr, "reserved not 0 refuses");
    c = wear();
    c.item_stamp = 0;
    check(check_shape(c) != nullptr, "a gear wear without the item's stamp refuses");
    c = wear(32768);
    check(check_shape(c) != nullptr, "a gear wear over 32767 refuses");
    c = wear(0);
    check(check_shape(c) == nullptr, "a gear wear of 0 is well formed (a use that costs nothing)");
    c = wear(1, 2);
    check(check_shape(c) != nullptr, "allow_break other than 0/1 refuses");
    c = take();
    c.kind = KILN_COST_STACK_REFUND;
    c.expected_count = 0;
    c.slot = 0;
    check(check_shape(c) == nullptr, "a stack refund without a slot is well formed (the game's choice)");
    c.expected_count = 2;
    check(check_shape(c) != nullptr, "a stack refund with an expected count refuses");
    c = wear(5);
    c.kind = KILN_COST_GEAR_RESTORE;
    c.expected_count = 0;
    check(check_shape(c) == nullptr, "a gear restore of 5 is well formed");
    c.amount = 0;
    check(check_shape(c) != nullptr, "a gear restore of 0 refuses");
    check(character_session(0x45, 1000) == ((0x45ull << 32) ^ 1000), "a player's session is handle << 32 ^ joined step (Kiln.Pets' formula)");
}

void wear_math() {
    WearPlan p = plan_wear(180, 180, 7, false, true);
    check(p.refusal == KILN_COST_REFUSAL_NONE && p.delta == -7 && p.expect == 7 && !p.crosses_zero, "wear 7 of 180: delta -7, 7 expected");
    p = plan_wear(5, 180, 5, false, true);
    check(p.refusal == KILN_COST_WOULD_BREAK, "wear 5 of 5 without allow_break refuses (it would leave 0)");
    p = plan_wear(5, 180, 4, false, true);
    check(p.refusal == KILN_COST_REFUSAL_NONE && p.expect == 4, "wear 4 of 5 without allow_break leaves 1");
    p = plan_wear(5, 180, 9, true, true);
    check(p.refusal == KILN_COST_REFUSAL_NONE && p.crosses_zero && p.delta == -9 && p.expect == 5,
          "wear 9 of 5 with allow_break: the tool breaks, the game clamps at 0 (5 expected)");
    p = plan_wear(0, 180, 1, true, true);
    check(p.refusal == KILN_COST_BROKEN, "a broken tool refuses any wear");
    p = plan_wear(0, 180, 0, true, true);
    check(p.refusal == KILN_COST_BROKEN, "a broken tool refuses even a wear of 0 (it is not usable)");
    p = plan_wear(10, 180, 0, false, true);
    check(p.refusal == KILN_COST_REFUSAL_NONE && p.delta == 0 && p.expect == 0, "wear 0: committed, nothing applied");
    p = plan_wear(10, 180, 5, false, false);
    check(p.waived && p.delta == 0 && gear_disposition(p, 0) == KILN_COST_WEAR_WAIVED_BY_WORLD,
          "durability off in the world: waived, 0 applied, WEAR_WAIVED_BY_WORLD");
    p = plan_wear(0, 180, 5, false, false);
    check(p.waived, "durability off: even a 0 tool is a use with 0 wear (the world has no durability)");
    p = plan_wear(100, 180, 7, false, true);
    check(gear_disposition(p, 7) == KILN_COST_EXACT, "7 applied of 7 expected: EXACT");
    check(gear_disposition(p, 3) == KILN_COST_MODIFIED_BY_OTHER, "3 applied of 7: another hook changed it (MODIFIED_BY_OTHER)");
    check(gear_disposition(p, -2) == KILN_COST_MODIFIED_BY_OTHER, "a repair by another hook (-2) is MODIFIED_BY_OTHER");
    p = plan_restore(170, 180, 30);
    check(p.refusal == KILN_COST_REFUSAL_NONE && p.delta == 10 && p.expect == 10, "restore 30 to 170/180: 10 back (never above max)");
    p = plan_restore(180, 180, 3);
    check(p.refusal == KILN_COST_FULL, "restore to a full item refuses FULL");
    p = plan_restore(0, 180, 3);
    check(p.refusal == KILN_COST_REFUSAL_NONE && p.delta == 3, "restore 3 to a broken item: 3 back");
    p = plan_restore(5, 0, 3);
    check(p.refusal == KILN_COST_NOT_GEAR, "restore to an item without max durability refuses");
}

KilnCostResult committed(const Job& j, uint32_t taken) {
    KilnCostResult r = blank_result(j.ticket, j.cost);
    r.state = KILN_COST_COMMITTED;
    r.quantity_taken = taken;
    r.step = 40;
    return r;
}

void table() {
    Table t;
    uint64_t a = 0, b = 0;
    check(t.submit(10, take(), 100, &a) == KILN_OK && a != 0, "submit queues a cost and gives a ticket");
    check(t.submit(10, take(), 101, &b) == KILN_OK && b == a, "the same request id with the same cost: the same ticket");
    check(t.held() == 1, "nothing queued twice");
    KilnSelectedCost other = take(3);
    check(t.submit(10, other, 101, &b) == KILN_ITEM_COSTS_REQUEST_CONFLICT, "the same request id with another cost: REQUEST_CONFLICT");
    check(t.submit(11, other, 101, &b) == KILN_OK && b != a, "another owner's same request id is its own");
    KilnCostResult r{};
    check(t.peek(10, a, &r) == KILN_OK && r.state == KILN_COST_QUEUED && r.ticket == a && r.item == 0xabcd, "peek: QUEUED until it runs");
    check(t.peek(11, a, &r) == KILN_ERR_NOT_FOUND, "another owner cannot peek the ticket");
    check(t.acknowledge(10, a) == KILN_ERR_INVALID_ARGUMENT, "a queued cost cannot be acknowledged (never forgotten before it settled)");
    std::vector<Job> jobs = t.take(16);
    check(jobs.size() == 2 && jobs[0].ticket == a, "the program takes the queued costs oldest first");
    check(t.take(16).empty(), "a running cost is not taken twice");
    t.end_queued(1000, 0, 4, true, "off", 5);
    check(t.peek(10, a, &r) == KILN_OK && r.state == KILN_COST_QUEUED, "a running cost is not ended by a world change or a switch-off");
    t.settle(committed(jobs[0], 2), 50);
    check(t.peek(10, a, &r) == KILN_OK && r.state == KILN_COST_COMMITTED && r.quantity_taken == 2, "settled: COMMITTED with what was taken");
    check(t.submit(10, take(), 102, &b) == KILN_OK && b == a, "a resubmit after it committed returns the same ticket (no second debit)");
    check(t.take(16).empty(), "and queues nothing");
    t.settle(committed(jobs[0], 99), 51);
    check(t.peek(10, a, &r) == KILN_OK && r.quantity_taken == 2, "a settled receipt is never settled again");
    check(t.acknowledge(10, a) == KILN_OK, "acknowledged");
    check(t.peek(10, a, &r) == KILN_ERR_NOT_FOUND, "an acknowledged receipt is gone");
    check(t.submit(10, take(), 103, &b) == KILN_ITEM_COSTS_REQUEST_CLOSED, "its request id is closed: never a second debit");
    check(t.committed() == 1, "one commit counted");

    // what did not run
    Table w;
    uint64_t x = 0, y = 0, z = 0;
    w.submit(1, take(1, 5, 21), 10, &x);
    KilnSelectedCost old = take(1, 5, 22);
    old.world_epoch = 2;
    w.submit(1, old, 10, &y);
    w.end_queued(12, 12, 3, false, "", 7);
    check(w.peek(1, y, &r) == KILN_OK && r.state == KILN_COST_REFUSED && r.refusal == KILN_COST_WORLD_ENDED,
          "a queued cost of an older world: REFUSED WORLD_ENDED");
    check(w.peek(1, x, &r) == KILN_OK && r.state == KILN_COST_QUEUED, "a cost of this world waits");
    w.end_queued(10 + kNotRunSteps + 1, 9, 3, false, "", 8);
    check(w.peek(1, x, &r) == KILN_OK && r.state == KILN_COST_REFUSED && r.refusal == KILN_COST_NOT_RUN,
          "no program update for 10 s since it was submitted: REFUSED NOT_RUN");
    w.submit(1, take(1, 5, 23), 400, &z);
    w.end_queued(400 + kNotRunSteps + 1, 400 + kNotRunSteps, 3, false, "", 9);
    check(w.peek(1, z, &r) == KILN_OK && r.state == KILN_COST_QUEUED, "the program ran since: it keeps waiting its turn");
    w.end_queued(401, 0, 3, true, "kiln_items_gear was switched off", 9);
    check(w.peek(1, z, &r) == KILN_OK && r.state == KILN_COST_REFUSED && r.refusal == KILN_COST_NOT_RUN &&
              std::strstr(r.reason, "switched off") != nullptr,
          "switched off: queued costs end NOT_RUN with the reason");

    // expiry and owners
    w.expire(7 + kExpireMs - 1);
    check(w.held() == 3, "settled receipts are kept while not acknowledged");
    w.expire(9 + kExpireMs);
    check(w.held() == 0, "after 10 minutes nobody acknowledged them: let go");
    check(w.submit(1, take(1, 5, 21), 999, &x) == KILN_ITEM_COSTS_REQUEST_CLOSED, "an expired receipt's request id stays closed");
    Table o;
    o.submit(5, take(1, 5, 31), 1, &x);
    o.submit(5, take(1, 5, 32), 1, &y);
    std::vector<Job> one = o.take(1);
    o.settle(committed(one[0], 1), 2);
    o.owner_gone(5);
    check(o.peek(5, x, &r) == KILN_ERR_NOT_FOUND && o.peek(5, y, &r) == KILN_OK && r.state == KILN_COST_QUEUED,
          "an owner that went away: its settled receipts go, its queued cost still runs and settles");

    // bounds
    Table full;
    uint64_t tk = 0;
    for (uint32_t i = 0; i < kMaxHeld; ++i) full.submit(1, take(1, 5, 1000 + i), 1, &tk);
    check(full.submit(1, take(1, 5, 5000), 1, &tk) == KILN_ITEM_COSTS_BUSY, "1024 held: BUSY");
    Table closed;
    for (uint32_t i = 0; i < kMaxClosed + 2; ++i) {
        closed.submit(1, take(1, 5, 10 + i), 1, &tk);
        std::vector<Job> j = closed.take(1);
        closed.settle(committed(j[0], 1), 1);
        closed.acknowledge(1, tk);
    }
    check(closed.closed_count() == kMaxClosed, "65,536 closed request ids are remembered, the oldest forgotten first");
    check(closed.submit(1, take(1, 5, 10 + kMaxClosed + 1), 1, &tk) == KILN_ITEM_COSTS_REQUEST_CLOSED, "a recent closed id still refuses");
}

}  // namespace

int main() {
    shapes();
    wear_math();
    table();
    std::printf("%s: %d failure(s)\n", failures ? "FAIL" : "PASS", failures);
    return failures ? 1 : 0;
}
