// SPDX-License-Identifier: MIT
// The ABI fingerprint pass and the `sig` descriptor (contract: hooks_fingerprint.h). No Windows dependency.
#include "hooks_fingerprint.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <set>
#include <tuple>

#include "nlohmann/json.hpp"
// Zydis.h repeats the static-build defines the target already passes on the command line.
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wmacro-redefined"
#endif
#include "Zydis.h"
#if defined(__clang__)
#pragma clang diagnostic pop
#endif

namespace kiln::hooks::fp {
namespace {

using json = nlohmann::ordered_json;

const char* const kRegNames[kArgRegs] = {"rcx", "rdx", "r8", "r9", "xmm0", "xmm1", "xmm2", "xmm3"};
constexpr uint32_t kRaxBit = 1u << 8;   // in the per-path `written` mask, behind the eight argument registers
constexpr uint32_t kAllWritten = 0x1FF;
constexpr ZydisMachineMode kMode = ZYDIS_MACHINE_MODE_LONG_64;

enum class Last : uint8_t { None, Rax, Xmm0, Callee };

// The mask bit of a register (argument registers 0..7, rax 8), or 0.
uint32_t reg_bit(ZydisRegister r) {
    if (r == ZYDIS_REGISTER_NONE) return 0;
    if (r >= ZYDIS_REGISTER_XMM0 && r <= ZYDIS_REGISTER_XMM3) return 1u << (4 + (r - ZYDIS_REGISTER_XMM0));
    if (r >= ZYDIS_REGISTER_YMM0 && r <= ZYDIS_REGISTER_YMM3) return 1u << (4 + (r - ZYDIS_REGISTER_YMM0));
    if (r >= ZYDIS_REGISTER_ZMM0 && r <= ZYDIS_REGISTER_ZMM3) return 1u << (4 + (r - ZYDIS_REGISTER_ZMM0));
    switch (ZydisRegisterGetLargestEnclosing(kMode, r)) {
        case ZYDIS_REGISTER_RCX: return 1u << 0;
        case ZYDIS_REGISTER_RDX: return 1u << 1;
        case ZYDIS_REGISTER_R8: return 1u << 2;
        case ZYDIS_REGISTER_R9: return 1u << 3;
        case ZYDIS_REGISTER_RAX: return kRaxBit;
        default: return 0;
    }
}
// The index of a general register (rax rcx rdx rbx rsp rbp rsi rdi r8..r15, any width), or -1.
int gpr_index(ZydisRegister r) {
    if (r == ZYDIS_REGISTER_NONE) return -1;
    const ZydisRegister e = ZydisRegisterGetLargestEnclosing(kMode, r);
    return e >= ZYDIS_REGISTER_RAX && e <= ZYDIS_REGISTER_R15 ? static_cast<int>(e - ZYDIS_REGISTER_RAX) : -1;
}
constexpr int kRsp = 4;
constexpr uint16_t kVolatile = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 8) | (1u << 9) | (1u << 10) | (1u << 11);
bool is64(ZydisRegister r) { return ZydisRegisterGetWidth(kMode, r) == 64; }

bool zero_idiom(ZydisMnemonic m) {
    switch (m) {
        case ZYDIS_MNEMONIC_XOR:
        case ZYDIS_MNEMONIC_SUB:
        case ZYDIS_MNEMONIC_PXOR:
        case ZYDIS_MNEMONIC_XORPS:
        case ZYDIS_MNEMONIC_XORPD:
        case ZYDIS_MNEMONIC_VXORPS:
        case ZYDIS_MNEMONIC_VXORPD:
        case ZYDIS_MNEMONIC_VPXOR:
            return true;
        default:
            return false;
    }
}
bool merge_write(ZydisMnemonic m) {
    switch (m) {
        case ZYDIS_MNEMONIC_MOVSS:
        case ZYDIS_MNEMONIC_MOVSD:
        case ZYDIS_MNEMONIC_CVTSI2SS:
        case ZYDIS_MNEMONIC_CVTSI2SD:
        case ZYDIS_MNEMONIC_CVTSS2SD:
        case ZYDIS_MNEMONIC_CVTSD2SS:
        case ZYDIS_MNEMONIC_SQRTSS:
        case ZYDIS_MNEMONIC_SQRTSD:
        case ZYDIS_MNEMONIC_ROUNDSS:
        case ZYDIS_MNEMONIC_ROUNDSD:
        case ZYDIS_MNEMONIC_RCPSS:
        case ZYDIS_MNEMONIC_RSQRTSS:
            return true;
        default:
            return false;
    }
}

struct Path {
    uint64_t addr = 0;
    uint32_t written = 0;
    // General registers known to hold "entry rsp + off" (rsp itself, and copies of it: a frame pointer, the rax or
    // r11 of an MSVC prologue).
    int64_t off[16] = {};
    uint16_t known = 1u << kRsp;
    Last last = Last::None;
    bool knows(int r) const { return r >= 0 && (known & (1u << r)); }
};

uint8_t ret_bit(Last l) {
    switch (l) {
        case Last::Rax: return kRetRax;
        case Last::Xmm0: return kRetXmm0;
        case Last::Callee: return kRetCallee;
        default: return kRetNone;
    }
}

std::string hex(uint64_t v) {
    char b[32];
    std::snprintf(b, sizeof b, "0x%llx", static_cast<unsigned long long>(v));
    return b;
}

}  // namespace

const char* reg_name(int bit) { return bit >= 0 && bit < kArgRegs ? kRegNames[bit] : "?"; }

Fingerprint compute(const Reader& reader, uint64_t entry) {
    Fingerprint f;
    f.entry = entry;
    if (!reader.bytes) {
        f.why = "no code reader";
        return f;
    }
    ZydisDecoder dec{};
    if (!ZYAN_SUCCESS(ZydisDecoderInit(&dec, kMode, ZYDIS_STACK_WIDTH_64))) {
        f.why = "decoder";
        return f;
    }
    ZydisDecodedInstruction in{};
    ZydisDecodedOperand ops[ZYDIS_MAX_OPERAND_COUNT];
    uint8_t buf[16];
    auto decode = [&](uint64_t a) -> bool {
        const size_t n = reader.bytes(reader.ctx, a, buf, 15);
        return n && ZYAN_SUCCESS(ZydisDecoderDecodeFull(&dec, buf, n, &in, ops));
    };
    auto rel_target = [&](uint64_t a, uint64_t* t) -> bool {
        if (in.operand_count_visible < 1 || ops[0].type != ZYDIS_OPERAND_TYPE_IMMEDIATE || !ops[0].imm.is_relative)
            return false;
        *t = a + in.length + static_cast<uint64_t>(ops[0].imm.value.s);
        return true;
    };
    // 1. one leading thunk
    if (!decode(entry)) {
        f.why = reader.bytes(reader.ctx, entry, buf, 1) ? "the entry does not decode" : "the exe file's code is not available";
        return f;
    }
    {
        uint64_t t = 0;
        if (in.mnemonic == ZYDIS_MNEMONIC_JMP && in.length == 5 && rel_target(entry, &t)) {
            entry = t;
            f.thunk = true;
            if (!decode(entry)) {
                f.why = "the entry's jump target does not decode";
                return f;
            }
        }
    }
    f.entry = entry;
    f.ok = true;

    bool have_fn = false;
    uint64_t fn_primary = 0, fn_next = UINT64_MAX;
    if (reader.function) have_fn = reader.function(reader.ctx, entry, &fn_primary, &fn_next);
    auto inside = [&](uint64_t t) -> bool {
        if (t < entry || t >= entry + kWindow) return false;
        if (!reader.function) return true;
        uint64_t p = 0, n = 0;
        const bool in_fn = reader.function(reader.ctx, t, &p, &n);
        if (have_fn) return in_fn && p == fn_primary;
        return !in_fn && t < fn_next;  // a leaf function: up to the next function that has unwind data
    };

    std::set<std::tuple<uint64_t, uint32_t, int64_t>> seen;
    auto first_visit = [&](const Path& p) {
        return seen.emplace(p.addr, p.written, p.knows(kRsp) ? p.off[kRsp] : INT64_MIN).second;
    };
    std::vector<Path> work;
    Path start;
    start.addr = entry;
    work.push_back(start);
    uint8_t passes = 0, indirect = 0;

    while (!work.empty() && f.insns < kMaxInstructions) {
        Path p = work.back();
        work.pop_back();
        if (!first_visit(p)) continue;
        while (f.insns < kMaxInstructions) {
            const uint64_t a = p.addr;
            if (!decode(a)) break;
            ++f.insns;
            const ZydisMnemonic m = in.mnemonic;
            const ZydisInstructionCategory cat = in.meta.category;
            const uint8_t nvis = in.operand_count_visible;

            // ---- reads and writes ----
            bool zero = false;
            if (zero_idiom(m) && nvis >= 2) {
                const ZydisDecodedOperand& s1 = ops[nvis - 2];
                const ZydisDecodedOperand& s2 = ops[nvis - 1];
                zero = s1.type == ZYDIS_OPERAND_TYPE_REGISTER && s2.type == ZYDIS_OPERAND_TYPE_REGISTER &&
                       s1.reg.value == s2.reg.value;
            }
            const bool merge = merge_write(m) && nvis >= 1 && ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER;
            const bool cmov = cat == ZYDIS_CATEGORY_CMOV;
            uint32_t reads = 0, writes = 0;
            for (uint8_t i = 0; i < in.operand_count; ++i) {
                const ZydisDecodedOperand& o = ops[i];
                const bool rd = (o.actions & (ZYDIS_OPERAND_ACTION_READ | ZYDIS_OPERAND_ACTION_CONDREAD)) != 0;
                const bool wr = (o.actions & ZYDIS_OPERAND_ACTION_WRITE) != 0;
                if (o.type == ZYDIS_OPERAND_TYPE_REGISTER) {
                    const uint32_t bit = reg_bit(o.reg.value);
                    if (!bit) continue;
                    const bool visible = i < nvis;
                    if (visible && i == 0 && cmov) continue;
                    if (visible && zero) {
                        if (i == 0) writes |= bit;
                        continue;
                    }
                    if (visible && i == 0 && merge) {
                        writes |= bit;
                        continue;
                    }
                    if (rd) reads |= bit;
                    if (wr) writes |= bit;
                } else if (o.type == ZYDIS_OPERAND_TYPE_MEMORY) {
                    reads |= reg_bit(o.mem.base) | reg_bit(o.mem.index);
                    const int base = gpr_index(o.mem.base);
                    if (p.knows(base)) {
                        const int64_t off = p.off[base] + o.mem.disp.value;
                        const bool args = off >= 0x28 && off < 0x28 + 8 * kStackSlots;
                        if (o.mem.index != ZYDIS_REGISTER_NONE || o.mem.type != ZYDIS_MEMOP_TYPE_MEM) {
                            // an indexed access or the address itself at or above the arguments: not followed
                            // (locals are below the entry rsp: an indexed access from there is the function's own)
                            if (o.mem.index != ZYDIS_REGISTER_NONE ? (off >= 0 && off < 0x28 + 8 * kStackSlots) : args)
                                f.stack_blind = true;
                        } else if (rd && args) {
                            f.stack |= 1u << ((off - 0x28) / 8);
                        }
                    }
                }
            }
            f.reads |= static_cast<uint8_t>(reads & ~p.written & 0xFF);
            p.written |= writes;
            if (writes & kRaxBit) p.last = Last::Rax;
            if (writes & (1u << 4)) p.last = Last::Xmm0;

            // ---- registers that hold the entry rsp plus an offset ----
            {
                const uint16_t before = p.known;
                int64_t value = 0;
                int set = -1;  // the register this instruction gives a known value
                const bool d64 = nvis >= 1 && ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER && is64(ops[0].reg.value);
                const int dst = d64 ? gpr_index(ops[0].reg.value) : -1;
                if (dst >= 0 && nvis == 2) {
                    const ZydisDecodedOperand& src = ops[1];
                    if (m == ZYDIS_MNEMONIC_MOV && src.type == ZYDIS_OPERAND_TYPE_REGISTER && is64(src.reg.value) &&
                        p.knows(gpr_index(src.reg.value))) {
                        set = dst, value = p.off[gpr_index(src.reg.value)];
                    } else if (m == ZYDIS_MNEMONIC_LEA && src.type == ZYDIS_OPERAND_TYPE_MEMORY &&
                               src.mem.index == ZYDIS_REGISTER_NONE && p.knows(gpr_index(src.mem.base))) {
                        set = dst, value = p.off[gpr_index(src.mem.base)] + src.mem.disp.value;
                    } else if ((m == ZYDIS_MNEMONIC_ADD || m == ZYDIS_MNEMONIC_SUB) && src.type == ZYDIS_OPERAND_TYPE_IMMEDIATE &&
                               p.knows(dst)) {
                        set = dst, value = p.off[dst] + (m == ZYDIS_MNEMONIC_ADD ? src.imm.value.s : -src.imm.value.s);
                    }
                }
                const bool stack_op = m == ZYDIS_MNEMONIC_PUSH || m == ZYDIS_MNEMONIC_POP || m == ZYDIS_MNEMONIC_PUSHFQ ||
                                      m == ZYDIS_MNEMONIC_POPFQ || m == ZYDIS_MNEMONIC_LEAVE || cat == ZYDIS_CATEGORY_CALL ||
                                      cat == ZYDIS_CATEGORY_RET;
                for (uint8_t i = 0; i < in.operand_count; ++i) {  // every other register write loses its value
                    const ZydisDecodedOperand& o = ops[i];
                    if (o.type != ZYDIS_OPERAND_TYPE_REGISTER) continue;
                    if (!(o.actions & (ZYDIS_OPERAND_ACTION_WRITE | ZYDIS_OPERAND_ACTION_CONDWRITE))) continue;
                    const int r = gpr_index(o.reg.value);
                    if (r < 0 || (r == kRsp && stack_op)) continue;
                    p.known &= static_cast<uint16_t>(~(1u << r));
                }
                if (set >= 0) p.off[set] = value, p.known |= static_cast<uint16_t>(1u << set);
                if (m == ZYDIS_MNEMONIC_PUSH || m == ZYDIS_MNEMONIC_PUSHFQ) {
                    p.off[kRsp] -= 8;
                } else if (m == ZYDIS_MNEMONIC_POP || m == ZYDIS_MNEMONIC_POPFQ) {
                    if (!(nvis >= 1 && ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER && gpr_index(ops[0].reg.value) == kRsp))
                        p.off[kRsp] += 8;
                } else if (m == ZYDIS_MNEMONIC_LEAVE) {  // mov rsp, rbp; pop rbp
                    if (before & (1u << 5)) p.off[kRsp] = p.off[5] + 8, p.known |= 1u << kRsp;
                    else p.known &= static_cast<uint16_t>(~(1u << kRsp));
                    p.known &= static_cast<uint16_t>(~(1u << 5));
                } else if (cat == ZYDIS_CATEGORY_CALL) {
                    p.known &= static_cast<uint16_t>(~kVolatile);
                }
                if ((before & (1u << kRsp)) && !p.knows(kRsp)) f.stack_blind = true;
            }

            // ---- control flow ----
            const uint8_t untouched = static_cast<uint8_t>(~p.written & 0xFF);
            if (cat == ZYDIS_CATEGORY_RET) {
                f.rets |= ret_bit(p.last);
                break;
            }
            if (m == ZYDIS_MNEMONIC_INT3 || m == ZYDIS_MNEMONIC_UD2 || m == ZYDIS_MNEMONIC_HLT) break;
            if (cat == ZYDIS_CATEGORY_CALL) {
                passes |= untouched;
                p.written = kAllWritten;
                p.last = Last::Callee;
            } else if (cat == ZYDIS_CATEGORY_UNCOND_BR) {
                uint64_t t = 0;
                if (rel_target(a, &t) && inside(t)) {
                    p.addr = t;
                    if (!first_visit(p)) break;
                    continue;
                }
                if (!rel_target(a, &t)) indirect |= untouched;
                passes |= untouched;
                f.rets |= kRetCallee;
                f.tail = true;
                break;
            } else if (cat == ZYDIS_CATEGORY_COND_BR) {
                uint64_t t = 0;
                if (rel_target(a, &t) && inside(t)) {
                    Path q = p;
                    q.addr = t;
                    work.push_back(q);
                }
            }
            p.addr = a + in.length;
        }
    }
    f.truncated = !work.empty();
    f.passes = static_cast<uint8_t>(passes & ~f.reads);
    f.indirect = static_cast<uint8_t>(indirect & ~f.reads);
    return f;
}

std::string ret_text(uint8_t rets) {
    std::string s;
    auto add = [&](uint8_t bit, const char* name) {
        if (!(rets & bit)) return;
        if (!s.empty()) s += '|';
        s += name;
    };
    add(kRetNone, "none");
    add(kRetRax, "rax");
    add(kRetXmm0, "xmm0");
    add(kRetCallee, "callee");
    return s.empty() ? "none" : s;
}

namespace {
// Registers are listed in position order: rcx xmm0 rdx xmm1 r8 xmm2 r9 xmm3.
constexpr int kByPosition[kArgRegs] = {0, 4, 1, 5, 2, 6, 3, 7};
json regs_json(uint8_t mask) {
    json a = json::array();
    for (const int i : kByPosition)
        if (mask & (1u << i)) a.push_back(kRegNames[i]);
    return a;
}
json stack_json(uint32_t mask) {
    json a = json::array();
    for (int k = 0; k < kStackSlots; ++k)
        if (mask & (1u << k)) a.push_back(hex(0x28 + 8ull * k));
    return a;
}
std::string list_text(uint8_t regs, uint32_t stack) {
    std::string s = "[";
    auto add = [&](const std::string& x) {
        if (s.size() > 1) s += ", ";
        s += x;
    };
    for (const int i : kByPosition)
        if (regs & (1u << i)) add(kRegNames[i]);
    for (int k = 0; k < kStackSlots; ++k)
        if (stack & (1u << k)) add("stack+" + hex(0x28 + 8ull * k));
    return s + "]";
}
}  // namespace

std::string to_json(const Fingerprint& f) {
    json j;
    j["result"] = f.ok && !f.indirect ? "ok" : "unknown";
    if (!f.ok) j["why"] = f.why;
    j["reads"] = regs_json(f.reads);
    j["passes"] = regs_json(f.passes);
    j["indirect"] = regs_json(f.indirect);
    j["stack"] = stack_json(f.stack);
    j["ret"] = ret_text(f.rets);
    j["insns"] = f.insns;
    if (f.thunk) j["thunk"] = true;
    if (f.tail) j["tail"] = true;
    if (f.stack_blind) j["stack_blind"] = true;
    if (f.truncated) j["truncated"] = true;
    return j.dump();
}

std::string reads_text(const Fingerprint& f) { return list_text(f.reads, f.stack); }

// ---------------------------------------------------------------------------------------------------------------------
// Descriptors

namespace {

struct Scalar {
    const char* name;
    uint32_t kind, size;
};
const Scalar kScalars[] = {
    {"s8", KILN_SIG_S8, 1},   {"u8", KILN_SIG_U8, 1},   {"s16", KILN_SIG_S16, 2}, {"u16", KILN_SIG_U16, 2},
    {"s32", KILN_SIG_S32, 4}, {"u32", KILN_SIG_U32, 4}, {"s64", KILN_SIG_S64, 8}, {"u64", KILN_SIG_U64, 8},
    {"f32", KILN_SIG_F32, 4}, {"f64", KILN_SIG_F64, 8}, {"bool", KILN_SIG_BOOL, 1}, {"ptr", KILN_SIG_PTR, 8},
};

// A type string of an argument or return: a scalar, "T*" (a pointer), or a struct by value.
bool type_of(const std::string& type, const Structs& structs, uint32_t* kind, uint32_t* size, std::string* error) {
    if (type.empty()) {
        *error = "a type is missing";
        return false;
    }
    for (const Scalar& s : kScalars)
        if (type == s.name) {
            *kind = s.kind, *size = s.size;
            return true;
        }
    if (type.back() == '*') {
        *kind = KILN_SIG_PTR, *size = 8;
        return true;
    }
    auto it = structs.find(type);
    if (it == structs.end()) {
        *error = "type '" + type + "' is neither a scalar (s8..u64, f32, f64, bool, ptr), a pointer (T*) nor a struct "
                 "declared under \"structs\"";
        return false;
    }
    *kind = KILN_SIG_BYVAL, *size = it->second.size;
    return true;
}

bool regs_of(const json& a, const char* key, uint8_t* out, std::string* error) {
    *out = 0;
    if (!a.contains(key)) return true;
    if (!a[key].is_array()) {
        *error = std::string("abi.") + key + " must be an array of register names";
        return false;
    }
    for (const auto& e : a[key]) {
        const std::string name = e.is_string() ? e.get<std::string>() : std::string();
        int bit = -1;
        for (int i = 0; i < kArgRegs; ++i)
            if (name == kRegNames[i]) bit = i;
        if (bit < 0) {
            *error = std::string("abi.") + key + ": '" + name + "' is not one of rcx rdx r8 r9 xmm0 xmm1 xmm2 xmm3";
            return false;
        }
        *out |= static_cast<uint8_t>(1u << bit);
    }
    return true;
}

}  // namespace

bool parse_structs(std::string_view json_text, Structs* out, std::string* error) {
    json doc;
    try {
        doc = json::parse(json_text);
    } catch (const std::exception& e) {
        *error = std::string("structs: ") + e.what();
        return false;
    }
    if (!doc.is_object()) {
        *error = "structs must be an object";
        return false;
    }
    for (auto& [name, s] : doc.items()) {
        StructDef def;
        if (!s.is_object() || !s.contains("size") || !s["size"].is_number_unsigned()) {
            *error = "struct " + name + ": \"size\" is missing";
            return false;
        }
        def.size = s["size"].get<uint32_t>();
        def.align = s.value("align", 8u);
        if (def.size == 0 || def.size > 4096 || (def.align != 1 && def.align != 2 && def.align != 4 && def.align != 8 &&
                                                  def.align != 16)) {
            *error = "struct " + name + ": size must be 1..4096 and align 1, 2, 4, 8 or 16";
            return false;
        }
        if (s.contains("fields")) {
            if (!s["fields"].is_array()) {
                *error = "struct " + name + ": \"fields\" must be an array";
                return false;
            }
            for (const auto& fj : s["fields"]) {
                StructField fd;
                if (!fj.is_object()) {
                    *error = "struct " + name + ": a field must be an object";
                    return false;
                }
                fd.name = fj.value("name", "");
                fd.type = fj.value("type", "");
                fd.offset = fj.value("offset", 0u);
                uint32_t kind = 0, size = 0;
                std::string terr;
                if (fd.name.empty() || !type_of(fd.type, Structs{}, &kind, &size, &terr) || kind == KILN_SIG_BYVAL) {
                    *error = "struct " + name + ": field '" + fd.name + "' needs a name and a scalar or pointer type";
                    return false;
                }
                if (static_cast<uint64_t>(fd.offset) + size > def.size) {
                    *error = "struct " + name + ": field '" + fd.name + "' ends behind the struct's size";
                    return false;
                }
                def.fields.push_back(std::move(fd));
            }
        }
        if (!out->emplace(name, std::move(def)).second) {
            *error = "struct " + name + " is declared twice";
            return false;
        }
    }
    return true;
}

bool parse_sig(std::string_view json_text, const Structs& structs, bool allow_trust, Sig* out, std::string* error) {
    json j;
    try {
        j = json::parse(json_text);
    } catch (const std::exception& e) {
        *error = std::string("sig: ") + e.what();
        return false;
    }
    if (!j.is_object()) {
        *error = "sig must be an object";
        return false;
    }
    Sig s;
    try {
        if (j.value("conv", "win64") != "win64") {
            *error = "conv must be \"win64\" (varargs and other conventions cannot be described)";
            return false;
        }
        if (j.value("varargs", false)) {
            *error = "a varargs function cannot be described";
            return false;
        }
        s.ret_type = j.value("ret", "void");
        if (j.contains("sret")) {
            const std::string st = j["sret"].get<std::string>();
            auto it = structs.find(st);
            if (it == structs.end()) {
                *error = "sret: struct '" + st + "' is not declared under \"structs\"";
                return false;
            }
            if (j.contains("ret") && s.ret_type != "void" && s.ret_type != st) {
                *error = "\"ret\" and \"sret\" disagree: a function returning through a hidden buffer has no other return";
                return false;
            }
            s.sret = true;
            s.ret_type = st;
            s.ret_kind = KILN_SIG_STRUCT;
            s.ret_size = it->second.size;
        } else if (s.ret_type == "void") {
            s.ret_kind = KILN_SIG_VOID;
        } else {
            std::string terr;
            if (!type_of(s.ret_type, structs, &s.ret_kind, &s.ret_size, &terr)) {
                *error = "ret: " + terr;
                return false;
            }
            if (s.ret_kind == KILN_SIG_BYVAL) {
                if (s.ret_size != 1 && s.ret_size != 2 && s.ret_size != 4 && s.ret_size != 8) {
                    *error = "ret: struct '" + s.ret_type + "' is " + std::to_string(s.ret_size) +
                             " bytes: it returns through a hidden buffer, declare it as \"sret\"";
                    return false;
                }
                s.ret_kind = KILN_SIG_STRUCT;
            }
        }
        if (j.contains("args")) {
            if (!j["args"].is_array()) {
                *error = "args must be an array";
                return false;
            }
            for (const auto& aj : j["args"]) {
                SigArg a;
                if (!aj.is_object()) {
                    *error = "an argument must be an object";
                    return false;
                }
                a.name = aj.value("name", "");
                a.type = aj.value("type", "");
                std::string terr;
                if (a.type == "sret") {
                    *error = "arg '" + a.name + "': a hidden return buffer is declared as \"sret\": \"<Struct>\", never in args";
                    return false;
                }
                if (!type_of(a.type, structs, &a.kind, &a.size, &terr)) {
                    *error = "arg '" + a.name + "': " + terr;
                    return false;
                }
                for (const SigArg& b : s.args)
                    if (!a.name.empty() && b.name == a.name) {
                        *error = "arg '" + a.name + "' is declared twice";
                        return false;
                    }
                s.args.push_back(std::move(a));
            }
        }
        if (s.positions() > KILN_SIG_MAX_ARGS) {
            *error = "more than " + std::to_string(KILN_SIG_MAX_ARGS) + " argument positions";
            return false;
        }
        s.invoke = j.value("invoke", "none");
        if (s.invoke != "none" && s.invoke != "tick" && s.invoke != "any") {
            *error = "invoke must be \"none\", \"tick\" or \"any\"";
            return false;
        }
        if (j.contains("abi")) {
            const json& a = j["abi"];
            if (a.is_string()) {
                if (a.get<std::string>() != "trust" || !allow_trust) {
                    *error = allow_trust ? "abi must be an object or \"trust\""
                                         : "\"abi\": \"trust\" is allowed only in a mod's own fragment";
                    return false;
                }
                s.trust = true;
            } else if (a.is_object()) {
                s.has_abi = true;
                if (!regs_of(a, "reads", &s.abi_reads, error) || !regs_of(a, "passes", &s.abi_passes, error)) return false;
                if (a.contains("stack")) {
                    if (!a["stack"].is_array()) {
                        *error = "abi.stack must be an array of offsets (\"0x28\", ...)";
                        return false;
                    }
                    for (const auto& e : a["stack"]) {
                        uint64_t off = 0;
                        if (e.is_string()) off = std::strtoull(e.get<std::string>().c_str(), nullptr, 0);
                        else if (e.is_number_unsigned()) off = e.get<uint64_t>();
                        if (off < 0x28 || off >= 0x28 + 8 * kStackSlots || (off & 7)) {
                            *error = "abi.stack: offsets are 0x28 + 8k above the entry rsp, k < 16";
                            return false;
                        }
                        s.abi_stack |= 1u << ((off - 0x28) / 8);
                    }
                }
                s.abi_ret = a.value("ret", "");
            } else {
                *error = "abi must be an object or \"trust\"";
                return false;
            }
        }
    } catch (const std::exception& e) {
        *error = std::string("sig: ") + e.what();
        return false;
    }
    *out = std::move(s);
    return true;
}

Shape shape_of(const Sig& sig) {
    Shape sh;
    sh.sret = sig.sret;
    if (sig.sret) sh.names.push_back("(return buffer)");
    for (const SigArg& a : sig.args) {
        const uint32_t pos = static_cast<uint32_t>(sh.names.size());
        if (pos < 4 && (a.kind == KILN_SIG_F32 || a.kind == KILN_SIG_F64)) sh.floats |= static_cast<uint8_t>(1u << pos);
        sh.names.push_back(a.name);
    }
    sh.positions = static_cast<uint32_t>(sh.names.size());
    return sh;
}

Shape shape_of(const KilnSig& sig) {
    Shape sh;
    sh.sret = sig.sret != 0;
    if (sig.sret) sh.names.push_back("(return buffer)");
    for (uint32_t i = 0; i < sig.argc; ++i) {
        const KilnSigArg& a = sig.args[i];
        const uint32_t pos = static_cast<uint32_t>(sh.names.size());
        if (pos < 4 && (a.kind == KILN_SIG_F32 || a.kind == KILN_SIG_F64)) sh.floats |= static_cast<uint8_t>(1u << pos);
        sh.names.emplace_back(a.name.ptr ? std::string(a.name.ptr, a.name.len) : std::string());
    }
    sh.positions = static_cast<uint32_t>(sh.names.size());
    return sh;
}

const char* verdict_name(Verdict v) {
    switch (v) {
        case Verdict::Match: return "match";
        case Verdict::Mismatch: return "mismatch";
        default: return "unknown";
    }
}

namespace {
// The register of position k (< 4) of a shape, as a mask bit index.
int reg_of(const Shape& sh, uint32_t k) { return (sh.floats & (1u << k)) ? 4 + static_cast<int>(k) : static_cast<int>(k); }

std::string arg_label(const Shape& sh, uint32_t pos) {
    // 1-based over the declared arguments; the hidden buffer has its own words
    if (sh.sret && pos == 0) return "the return buffer";
    const uint32_t n = pos + 1 - (sh.sret ? 1 : 0);
    std::string s = "arg " + std::to_string(n);
    if (pos < sh.names.size() && !sh.names[pos].empty()) s += " '" + sh.names[pos] + "'";
    return s;
}
std::string place(const Shape& sh, uint32_t pos) {
    return pos < 4 ? std::string(kRegNames[reg_of(sh, pos)]) : "stack+" + hex(0x28 + 8ull * (pos - 4));
}
}  // namespace

Verdict check(const Shape& sh, const Fingerprint& f, std::string* text) {
    const std::string reads = reads_text(f);
    auto say = [&](const std::string& s) {
        if (text) *text = s;
    };
    if (!f.ok) {
        say("the function's code could not be read: " + f.why);
        return Verdict::Unknown;
    }
    uint8_t declared = 0;
    for (uint32_t k = 0; k < sh.positions && k < 4; ++k) declared |= static_cast<uint8_t>(1u << reg_of(sh, k));
    const uint32_t nstack = sh.positions > 4 ? sh.positions - 4 : 0;
    const std::string count = std::to_string(sh.positions - (sh.sret ? 1 : 0)) + " argument" +
                              (sh.positions - (sh.sret ? 1 : 0) == 1 ? "" : "s") + (sh.sret ? " and a return buffer" : "");
    // a position whose register class is the other one (a float where the descriptor has an integer, or the reverse)
    for (uint32_t k = 0; k < sh.positions && k < 4; ++k) {
        const int other = (sh.floats & (1u << k)) ? static_cast<int>(k) : 4 + static_cast<int>(k);
        if (f.reads & (1u << other)) {
            say("descriptor " + arg_label(sh, k) + " is " + place(sh, k) + ", the function reads " + reads);
            return Verdict::Mismatch;
        }
    }
    // a read the descriptor lacks
    for (int i = 0; i < kArgRegs; ++i)
        if ((f.reads & (1u << i)) && !(declared & (1u << i))) {
            say("the function reads " + std::string(kRegNames[i]) + ", the descriptor has " + count + "; it reads " + reads);
            return Verdict::Mismatch;
        }
    for (int k = 0; k < kStackSlots; ++k)
        if ((f.stack & (1u << k)) && static_cast<uint32_t>(k) >= nstack) {
            say("the function reads stack+" + hex(0x28 + 8ull * k) + ", the descriptor has " + count + "; it reads " + reads);
            return Verdict::Mismatch;
        }
    // a declared position the function neither reads nor passes; one whose use could not be followed is unknown
    bool unknown = false;
    std::string unknown_text;
    for (uint32_t k = 0; k < sh.positions; ++k) {
        bool used, lost;
        if (k < 4) {
            const uint8_t bit = static_cast<uint8_t>(1u << reg_of(sh, k));
            used = ((f.reads | f.passes) & bit) != 0;
            lost = (f.indirect & bit) != 0;
        } else {
            const bool read = (f.stack & (1u << (k - 4))) != 0;
            used = read || f.tail || f.stack_blind;  // a tail call takes the caller's stack arguments with it
            lost = !read && (f.indirect != 0 || f.stack_blind);
        }
        if (!used) {
            say("descriptor " + arg_label(sh, k) + " is " + place(sh, k) + ", the function reads " + reads +
                (f.passes ? " and passes " + list_text(f.passes, 0) : std::string()));
            return Verdict::Mismatch;
        }
        if (lost && !unknown) {
            unknown = true;
            unknown_text = "descriptor " + arg_label(sh, k) + " is " + place(sh, k) +
                           ": the function's use of it could not be followed (it reads " + reads + ")";
        }
    }
    if (unknown) {
        say(unknown_text);
        return Verdict::Unknown;
    }
    std::string places;
    for (uint32_t k = 0; k < sh.positions; ++k) places += (k ? ", " : "") + place(sh, k);
    say(count + " (" + places + "), the function reads " + reads);
    return Verdict::Match;
}

Verdict check_sig(const Sig& sig, const Fingerprint& f, std::string* text) {
    auto say = [&](const std::string& s) {
        if (text) *text = s;
    };
    if (f.ok && sig.has_abi) {
        if (sig.abi_reads != f.reads || sig.abi_stack != f.stack) {
            say("the function reads " + reads_text(f) + ", the descriptor's fingerprint says " +
                list_text(sig.abi_reads, sig.abi_stack) + " (the function changed: review the descriptor)");
            return Verdict::Mismatch;
        }
        if (sig.abi_passes & ~f.passes) {
            say("the function passes " + list_text(f.passes, 0) + ", the descriptor's fingerprint says " +
                list_text(sig.abi_passes, 0) + " (the function changed: review the descriptor)");
            return Verdict::Mismatch;
        }
    }
    std::string t;
    Verdict v = check(shape_of(sig), f, &t);
    if (v == Verdict::Unknown && sig.trust) {
        v = Verdict::Match;
        t += " (trusted by the fragment)";
    }
    say(t);
    return v;
}

// ---------------------------------------------------------------------------------------------------------------------
// The exception table

namespace {
uint32_t rd32(const uint8_t* p) {
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;
}
uint16_t rd16(const uint8_t* p) {
    uint16_t v;
    std::memcpy(&v, p, 2);
    return v;
}
}  // namespace

bool pdata_init(Pdata* out, const uint8_t* image, size_t image_size) {
    *out = Pdata{};
    if (!image || image_size < 0x200 || rd16(image) != 0x5A4D) return false;
    const uint32_t nt = rd32(image + 0x3C);
    if (nt + 0x108 > image_size || rd32(image + nt) != 0x00004550) return false;
    const uint8_t* opt = image + nt + 24;
    if (rd16(opt) != 0x20B) return false;  // PE32+
    const uint32_t ndirs = rd32(opt + 108);
    if (ndirs < 4) return false;
    const uint32_t rva = rd32(opt + 112 + 3 * 8), size = rd32(opt + 112 + 3 * 8 + 4);  // IMAGE_DIRECTORY_ENTRY_EXCEPTION
    if (!rva || size < 12 || static_cast<uint64_t>(rva) + size > image_size) return false;
    out->image = image;
    out->image_size = image_size;
    out->table = image + rva;
    out->count = size / 12;
    return true;
}

bool pdata_lookup(const Pdata& p, uint64_t rva, uint64_t* primary, uint64_t* next) {
    *next = UINT64_MAX;
    if (!p.table || !p.count) return false;
    // the first entry whose begin is behind rva
    size_t lo = 0, hi = p.count;
    while (lo < hi) {
        const size_t mid = (lo + hi) / 2;
        if (rd32(p.table + mid * 12) > rva) hi = mid;
        else lo = mid + 1;
    }
    if (lo < p.count) *next = rd32(p.table + lo * 12);
    if (lo == 0) return false;
    const uint8_t* e = p.table + (lo - 1) * 12;
    if (rva >= rd32(e + 4)) return false;  // behind that entry's end: in no function
    // chained parts lead to the first part
    for (int hop = 0; hop < 8; ++hop) {
        uint32_t unwind = rd32(e + 8);
        if (unwind & 1) {  // an indirect entry: it names another RUNTIME_FUNCTION
            unwind &= ~1u;
            if (static_cast<uint64_t>(unwind) + 12 > p.image_size) break;
            e = p.image + unwind;
            continue;
        }
        if (static_cast<uint64_t>(unwind) + 4 > p.image_size) break;
        const uint8_t* u = p.image + unwind;
        const uint8_t flags = u[0] >> 3;
        if (!(flags & 4)) break;  // UNW_FLAG_CHAININFO
        const uint32_t codes = (u[2] + 1u) & ~1u;
        if (static_cast<uint64_t>(unwind) + 4 + codes * 2 + 12 > p.image_size) break;
        e = u + 4 + codes * 2;
    }
    *primary = rd32(e);
    return true;
}

}  // namespace kiln::hooks::fp
