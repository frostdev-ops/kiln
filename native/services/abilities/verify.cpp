// SPDX-License-Identifier: MIT
// Clean-room from the interpreter (0x990920) and the contract with tools/impact: opcodes 1..30, operand words,
// stack effects, a computed max depth <= 31 (32 slots, slot 31 = the 0xBAADF00D canary), HALT-terminated code.
#include "verify.h"

#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "engine.h"

namespace kab {
namespace {

constexpr int kMaxDepth = 31;

enum Op : uint32_t {
    IADD = 1, ISUB, IMUL, IDIV, ILT, IEQ, ILEQ, BR = 8, BRT, BRF, ICONST = 11, ICONST0, ICONST1, INC, DEC, COPY = 16,
    DUP, CALL, ECALL, RET, LOAD, GLOAD, STORE, GSTORE, LTIME, TIMEFF, POP, RVM, DSELF, HALT = 30,
};

int operand_words(uint32_t op) {
    switch (op) {
        case BR: case BRT: case BRF: case ICONST: case LOAD: case GLOAD: case STORE: case GSTORE: return 1;
        case COPY: case CALL: case ECALL: return 2;
        default: return 0;
    }
}

// Natives that read these arguments with no null check, as a bit per args[] index: unconnected, they dereference null and the process dies.
// Found in the natives' decompiles and only where every vanilla call connects the argument, so no working program is refused. A push of
// nothing right before the ECALL is what to look for: args[0] is pushed last.
struct UncheckedArgs {
    uint32_t hash, mask;
};
constexpr UncheckedArgs kUnchecked[] = {
    {0x0294c6dfu, 0x00000001u},
    {0x0afc5954u, 0x00000003u},
    {0x19143416u, 0x0000003cu},
    {0x236b6555u, 0x00000001u},
    {0x2d3e3e5fu, 0x00000002u},
    {0x3e1c0b5du, 0x0000000cu},
    {0x45dbf538u, 0x00000002u},
    {0x530b5054u, 0x00000003u},
    {0x55d4c71du, 0x0000000eu},
    {0x61946152u, 0x0000000eu},
    {0x61b11b03u, 0x00000004u},
    {0x640f8053u, 0x00000001u},
    {0x69190348u, 0x00000001u},
    {0x6b4da871u, 0x00000016u},
    {0x6e453d91u, 0x00000002u},
    {0x72fdd0f5u, 0x01000000u},
    {0x79b5a97bu, 0x00000002u},
    {0x84891ee2u, 0x00000002u},
    {0x866dfbebu, 0x00000010u},
    {0x9866d55au, 0x00000002u},
    {0x9870a3b4u, 0x00000004u},
    {0x9aabd181u, 0x00000004u},
    {0x9f34f4eau, 0x00800000u},
    {0xa5e145f8u, 0x0000000au},
    {0xa5ff238bu, 0x00000006u},
    {0xa8787e70u, 0x0000001cu},
    {0xad415283u, 0x0000008fu},
    {0xb04d0e7au, 0x0000003eu},
    {0xbd6aa1d0u, 0x00000016u},
    {0xbe0e869du, 0x00000003u},
    {0xc5453cfbu, 0x00000008u},
    {0xc691c4dau, 0x00000001u},
    {0xca687904u, 0x00000006u},
    {0xcf3810a6u, 0x00000001u},
    {0xd6bb2b78u, 0x00000006u},
    {0xd8e52c57u, 0x00000002u},
    {0xdb41ca5bu, 0x00000001u},
    {0xdb4bf324u, 0x00000006u},
    {0xec2558deu, 0x00000001u},
    {0xf39c6af3u, 0x00000002u},
    {0xf52a5619u, 0x00000006u},
    {0xf56c44f1u, 0x00000006u},
};

uint32_t unchecked_args(uint32_t hash) {
    for (const UncheckedArgs& u : kUnchecked)
        if (u.hash == hash) return u.mask;
    return 0;
}

bool allowed_type(uint32_t t) {
    return t == 0x32940becu || t == 0x9859f31eu || t == 0xe89f7410u || t == 0xc894953du || t == 0xdfe4e404u;
}

std::string fmt(const char* f, ...) {
    char b[256];
    va_list ap;
    va_start(ap, f);
    std::vsnprintf(b, sizeof b, f, ap);
    va_end(ap);
    return b;
}

struct Arrays {
    const uint32_t* code = nullptr;
    uint32_t code_n = 0;
    const uint32_t* shut = nullptr;
    uint32_t shut_n = 0;
    const ImpactVariable* vars = nullptr;
    uint32_t vars_n = 0;
    uint32_t data_n = 0;
    const uint32_t* queries = nullptr;
    uint32_t queries_n = 0;
};

// One array (code or shutdown): decode, check operands and targets, then the depth over the control-flow graph.
bool check_code(const uint32_t* code, uint32_t n, const char* what, const Arrays& a,
                const std::function<int(uint32_t)>& argc_of, VerifyResult& r) {
    if (n == 0) {
        r.error = fmt("%s is empty (it must end in HALT)", what);
        return false;
    }
    if (n > 65535) {  // pc, sp and branch targets are u16 in the interpreter
        r.error = fmt("%s has %u words (at most 65535)", what, n);
        return false;
    }
    std::vector<int8_t> start(n, 0);  // 1 = an instruction starts here
    for (uint32_t pc = 0; pc < n;) {
        const uint32_t op = code[pc];
        if (op < 1 || op > 30) {
            r.error = fmt("%s pc %u: invalid opcode %u", what, pc, op);
            return false;
        }
        if (op == CALL || op == RET || op == LOAD || op == STORE) {
            r.error = fmt("%s pc %u: opcode %u (CALL/RET/LOAD/STORE) is not accepted yet", what, pc, op);
            return false;
        }
        const int w = operand_words(op);
        if (pc + w >= n && !(w == 0 && pc + 1 <= n)) {
            r.error = fmt("%s pc %u: operands run past the end", what, pc);
            return false;
        }
        start[pc] = 1;
        pc += 1 + w;
    }
    // operands
    std::vector<uint32_t> ops;  // the opcodes so far, the last one this instruction's: what a native's arguments were
    for (uint32_t pc = 0; pc < n; ++pc) {
        if (!start[pc]) continue;
        const uint32_t op = code[pc];
        ops.push_back(op);
        if (op == BR || op == BRT || op == BRF) {
            const uint32_t t = code[pc + 1];
            if (t >= n || !start[t]) {
                r.error = fmt("%s pc %u: jump target %u is not an instruction start", what, pc, t);
                return false;
            }
        } else if (op == GLOAD || op == GSTORE) {
            const uint32_t h = code[pc + 1];
            const uint32_t idx = h & 0xFFFF;
            if ((h & 0xFFFF0000u) != 0xFFFF0000u || idx >= a.vars_n) {
                r.error = fmt("%s pc %u: variable handle 0x%08x out of range", what, pc, h);
                return false;
            }
            if (!allowed_type(a.vars[idx].type)) {
                r.error = fmt("%s pc %u: %s of variable %u with type 0x%08x", what, pc, op == GLOAD ? "GLOAD" : "GSTORE",
                              idx, a.vars[idx].type);
                return false;
            }
        } else if (op == COPY) {
            const ImpactVariable* v[2] = {nullptr, nullptr};
            for (int k = 0; k < 2; ++k)
                for (uint32_t i = 0; i < a.vars_n && !v[k]; ++i)
                    if (a.vars[i].name == code[pc + 1 + k]) v[k] = &a.vars[i];
            if (!v[0] || !v[1] || v[0]->type != v[1]->type) {
                r.error = fmt("%s pc %u: COPY between unknown variables or different types", what, pc);
                return false;
            }
        } else if (op == ECALL) {
            const uint32_t hash = code[pc + 2];
            // Query natives keep per-site state that createImpactInstance preallocates only for queryCallIds.
            if (hash == 0x19143416u || hash == 0x93e40aa9u || hash == 0xa8787e70u || hash == 0x22a62ea4u ||
                hash == 0x61946152u) {
                bool listed = false;
                for (uint32_t q = 0; q < a.queries_n && !listed; ++q) listed = a.queries[q] == code[pc + 1];
                if (!listed) {
                    r.error = fmt("%s pc %u: query native 0x%08x at site 0x%08x, which queryCallIds does not list", what,
                                  pc, hash, code[pc + 1]);
                    return false;
                }
            }
            if (const uint32_t mask = unchecked_args(hash)) {
                for (uint32_t i = 0; i < 32 && i + 1 < ops.size(); ++i) {
                    const uint32_t pushed = ops[ops.size() - 2 - i];
                    if (pushed != ICONST && pushed != ICONST0 && pushed != ICONST1) break;
                    if ((mask >> i & 1) && pushed == ICONST0) {
                        r.error = fmt("%s pc %u: native 0x%08x reads args[%u] with no check, and it is unconnected: this would kill the process", what, pc, hash, i);
                        return false;
                    }
                }
            }
            if (argc_of(hash) < 0) {
                bool seen = false;
                for (uint32_t h : r.unknown_natives) seen |= h == hash;
                if (!seen) r.unknown_natives.push_back(hash);
            }
        }
    }
    // depth: worklist over the CFG (unknown natives: argc 0 for the depth here; the program waits for them anyway)
    std::vector<int> depth(n, -1);
    std::vector<uint32_t> work{0};
    depth[0] = 0;
    while (!work.empty()) {
        const uint32_t pc = work.back();
        work.pop_back();
        const uint32_t op = code[pc];
        const int d = depth[pc];
        int pop = 0, push = 0;
        switch (op) {
            case IADD: case ISUB: case IMUL: case IDIV: case ILT: case IEQ: case ILEQ: pop = 2; push = 1; break;
            case BR: case COPY: case RVM: case DSELF: case HALT: break;
            case BRT: case BRF: case GSTORE: case POP: pop = 1; break;
            case ICONST: case ICONST0: case ICONST1: case LTIME: case GLOAD: push = 1; break;
            case DUP: pop = 1; push = 2; break;
            case INC: case DEC: case TIMEFF: pop = 1; push = 1; break;
            case ECALL: {
                const int argc = argc_of(code[pc + 2]);
                pop = argc < 0 ? 0 : argc;
                push = 1;
                break;
            }
            default: break;
        }
        if (d - pop < 0) {
            r.error = fmt("%s pc %u: stack underflow (depth %d, pops %d)", what, pc, d, pop);
            return false;
        }
        const int nd = d - pop + push;
        if (nd > kMaxDepth || d > kMaxDepth) {
            r.error = fmt("%s pc %u: stack depth %d exceeds %d (32 slots, the last is the canary)", what, pc, nd, kMaxDepth);
            return false;
        }
        if (nd > r.max_depth) r.max_depth = nd;
        if (d > r.max_depth) r.max_depth = d;
        uint32_t succ[2];
        int ns = 0;
        if (op == HALT) {
            // no successor
        } else if (op == BR) {
            succ[ns++] = code[pc + 1];
        } else {
            const uint32_t next = pc + 1 + operand_words(op);
            if (op == BRT || op == BRF) succ[ns++] = code[pc + 1];
            if (next >= n) {
                r.error = fmt("%s pc %u: execution falls off the end (no HALT)", what, pc);
                return false;
            }
            succ[ns++] = next;
        }
        for (int i = 0; i < ns; ++i) {
            const uint32_t s = succ[i];
            if (depth[s] < 0) {
                depth[s] = nd;
                work.push_back(s);
            } else if (depth[s] != nd) {
                r.error = fmt("%s pc %u: stack depth %d here, %d on another path", what, s, nd, depth[s]);
                return false;
            }
        }
    }
    // the last instruction must be HALT
    uint32_t last = 0;
    for (uint32_t pc = 0; pc < n; ++pc)
        if (start[pc]) last = pc;
    if (code[last] != HALT) {
        r.error = fmt("%s does not end in HALT", what);
        return false;
    }
    return true;
}

bool array_in(const uint8_t* blob, size_t len, const BlobArray& arr, size_t elem, const uint8_t** out) {
    *out = nullptr;
    if (arr.count == 0) return true;
    const uint8_t* field = reinterpret_cast<const uint8_t*>(&arr);
    const int64_t start = (field - blob) + static_cast<int64_t>(arr.offset);
    const int64_t end = start + static_cast<int64_t>(arr.count) * static_cast<int64_t>(elem);
    if (arr.offset == 0 || start < 64 || end > static_cast<int64_t>(len) || end < start) return false;
    *out = blob + start;
    return true;
}

}  // namespace

VerifyResult verify_program(const uint8_t* blob, size_t len, const std::function<int(uint32_t)>& argc_of) {
    VerifyResult r;
    if (!blob || len < sizeof(ProgramHeader) || len > (16u << 20)) {
        r.error = "not a keen::ImpactProgram blob (too small or too large)";
        return r;
    }
    const auto* h = reinterpret_cast<const ProgramHeader*>(blob);
    r.id = h->id;
    std::memcpy(r.guid, h->guid, 16);
    if (h->id != fnv1a(h->guid, 16)) {
        r.error = fmt("id 0x%08x is not FNV-1a of the GUID (0x%08x)", h->id, fnv1a(h->guid, 16));
        return r;
    }
    bool zero = true;
    for (uint8_t b : h->guid) zero &= b == 0;
    if (zero) {
        r.error = "the GUID is all zero";
        return r;
    }
    Arrays a;
    const uint8_t *code, *shut, *vars, *data, *queries;
    if (!array_in(blob, len, h->code, 4, &code) || !array_in(blob, len, h->shutdown, 4, &shut) ||
        !array_in(blob, len, h->layout, sizeof(ImpactVariable), &vars) || !array_in(blob, len, h->data, 1, &data) ||
        !array_in(blob, len, h->query_calls, 4, &queries)) {
        r.error = "an array lies outside the blob";
        return r;
    }
    a.code = reinterpret_cast<const uint32_t*>(code);
    a.code_n = h->code.count;
    a.shut = reinterpret_cast<const uint32_t*>(shut);
    a.shut_n = h->shutdown.count;
    a.vars = reinterpret_cast<const ImpactVariable*>(vars);
    a.vars_n = h->layout.count;
    a.data_n = h->data.count;
    a.queries = reinterpret_cast<const uint32_t*>(queries);
    a.queries_n = h->query_calls.count;
    if (a.vars_n > 0xFFFF) {
        r.error = "too many variables";
        return r;
    }
    for (uint32_t i = 0; i < a.vars_n; ++i) {
        const ImpactVariable& v = a.vars[i];
        if (static_cast<uint32_t>(v.offset) + v.size > a.data_n) {
            r.error = fmt("variable %u lies outside data (%u + %u > %u)", i, v.offset, v.size, a.data_n);
            return r;
        }
        if (v.dbg_len) {
            const uint8_t* f = reinterpret_cast<const uint8_t*>(&v.dbg_off);
            const int64_t s = (f - blob) + v.dbg_off;
            if (v.dbg_off == 0 || s < 0 || s + v.dbg_len > static_cast<int64_t>(len)) {
                r.error = fmt("variable %u's debug name lies outside the blob", i);
                return r;
            }
        }
    }
    if (h->stack_size == 0 || h->stack_size > 4096) {
        r.error = fmt("stackSize %u out of range", h->stack_size);
        return r;
    }
    if (!check_code(a.code, a.code_n, "code", a, argc_of, r)) return r;
    if (!check_code(a.shut, a.shut_n, "codeShutdown", a, argc_of, r)) return r;
    r.ok = true;
    return r;
}

}  // namespace kab
