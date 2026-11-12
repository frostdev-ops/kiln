// SPDX-License-Identifier: MIT
// Under Wine, built as the plugin is (clang-cl, /EHa): the stack check and the guarded copy that Hooks.rule field
// writes go through (hooks.cpp on_stack, write_memory, memoryFault). The host tests replace both with fakes
// (kset::hooktest), so only this case compiles the production code. Run by test-wine.sh; exit 0 = pass.
#include "../src/hooks.cpp"

#include <cstdio>
#include <memory>
#include <thread>

// game.cpp's reflection, which hooks.cpp uses for captures and rule fields: not on the path tested here (the host tests
// stub it the same way).
bool refl::plain(Type) { return true; }
uint32_t refl::Type::u32(size_t) const { return 0; }
uint8_t refl::Type::prim() const { return 0; }
std::string_view refl::Type::str(size_t) const { return {}; }
bool refl::path(Type, std::string_view, uint32_t&, Type&) { return false; }

namespace {
int failures = 0;
void check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "ok  " : "FAIL", what);
    std::fflush(stdout);  // a fault the guard misses ends the process: what passed before it still shows
    if (!ok) ++failures;
}
DWORD protectionOf(const void* p) {
    MEMORY_BASIC_INFORMATION m{};
    return VirtualQuery(p, &m, sizeof m) ? m.Protect : 0;
}
}  // namespace

int main() {
    // ---- on_stack: the calling thread's stack only, whole ranges, no wrap-around ----
    alignas(8) volatile uint8_t local[64]{};
    const auto at = reinterpret_cast<uintptr_t>(local);
    check(on_stack(at, sizeof local), "a local of this thread is on its stack");
    const auto* tib = reinterpret_cast<const NT_TIB*>(NtCurrentTeb());
    const auto base = reinterpret_cast<uintptr_t>(tib->StackBase), limit = reinterpret_cast<uintptr_t>(tib->StackLimit);
    check(on_stack(base - 8, 8) && !on_stack(base - 4, 8) && !on_stack(base, 1), "a range must end at the stack base or below");
    check(on_stack(limit, 8) && !on_stack(limit - 1, 2) && !on_stack(limit - 8, 8), "...and start at the committed limit or above");
    check(!on_stack(at, SIZE_MAX) && !on_stack(at, base) && !on_stack(UINTPTR_MAX - 4, 8), "huge sizes and addresses don't wrap");
    const auto heap = std::make_unique<uint64_t[]>(8);
    check(!on_stack(reinterpret_cast<uintptr_t>(heap.get()), 8), "a heap block is not on the stack");
    {
        std::atomic<uintptr_t> other{0};
        std::atomic<bool> done{false};
        std::thread t([&] {
            volatile uint8_t mine[16]{};
            other = reinterpret_cast<uintptr_t>(mine);
            while (!done) Sleep(1);
        });
        while (!other) Sleep(1);
        check(!on_stack(other, 8), "another thread's stack is not this thread's");
        done = true;
        t.join();
    }

    // ---- write_memory: a plain guarded copy, never lifting protection; a fault is a refusal, not a crash ----
    uint64_t value = 0;
    const uint64_t seven = 7;
    check(write_memory(reinterpret_cast<uintptr_t>(&value), &seven, sizeof seven) && value == 7, "writable memory is written");
    auto* page = static_cast<uint8_t*>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    check(page != nullptr, "a test page");
    if (!page) return 1;
    std::memset(page, 0x11, 4096);
    DWORD old = 0;
    VirtualProtect(page, 4096, PAGE_READONLY, &old);
    check(!write_memory(reinterpret_cast<uintptr_t>(page) + 8, &seven, sizeof seven), "a read-only page is refused");
    check(page[8] == 0x11 && protectionOf(page) == PAGE_READONLY, "...unchanged, and still read-only");
    VirtualProtect(page, 4096, PAGE_NOACCESS, &old);
    check(!write_memory(reinterpret_cast<uintptr_t>(page) + 16, &seven, sizeof seven), "a no-access page is refused");
    VirtualProtect(page, 4096, PAGE_READWRITE | PAGE_GUARD, &old);
    check(!write_memory(reinterpret_cast<uintptr_t>(page) + 24, &seven, sizeof seven), "a guard page is refused");
    VirtualProtect(page, 4096, PAGE_READWRITE, &old);
    check(page[24] == 0x11, "...unchanged");
    // A copy that starts writable and runs into a read-only page faults part-way: still a refusal.
    auto* two = static_cast<uint8_t*>(VirtualAlloc(nullptr, 8192, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (two) {
        VirtualProtect(two + 4096, 4096, PAGE_READONLY, &old);
        uint8_t src[16];
        std::memset(src, 0x22, sizeof src);
        check(!write_memory(reinterpret_cast<uintptr_t>(two) + 4096 - 8, src, sizeof src) && two[4096] == 0,
              "a copy running into a read-only page is refused");
        VirtualFree(two, 0, MEM_RELEASE);
    }
    void* reserved = VirtualAlloc(nullptr, 4096, MEM_RESERVE, PAGE_NOACCESS);
    check(reserved && !write_memory(reinterpret_cast<uintptr_t>(reserved), &seven, sizeof seven), "reserved, uncommitted memory is refused");
    if (reserved) VirtualFree(reserved, 0, MEM_RELEASE);
    VirtualFree(page, 0, MEM_RELEASE);
    check(!write_memory(reinterpret_cast<uintptr_t>(page), &seven, sizeof seven), "released memory is refused");
    // Only memory faults are caught; anything else goes on to the game's own handlers.
    check(memoryFault(EXCEPTION_ACCESS_VIOLATION) == EXCEPTION_EXECUTE_HANDLER && memoryFault(EXCEPTION_IN_PAGE_ERROR) == EXCEPTION_EXECUTE_HANDLER &&
              memoryFault(STATUS_GUARD_PAGE_VIOLATION) == EXCEPTION_EXECUTE_HANDLER,
          "access violations, in-page errors and guard pages are refusals");
    check(memoryFault(EXCEPTION_INT_DIVIDE_BY_ZERO) == EXCEPTION_CONTINUE_SEARCH && memoryFault(EXCEPTION_BREAKPOINT) == EXCEPTION_CONTINUE_SEARCH,
          "other exceptions are not swallowed");
    std::printf(failures ? "wine rules guard test: FAIL (%d)\n" : "wine rules guard test: PASS\n", failures);
    return failures;
}
