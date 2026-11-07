// SPDX-License-Identifier: MIT
// A mod's particle hooks, linked into Kiln's sprite (docs/kiln/modding/shaders.md, "How it is kept safe").
//
// The mod ships a *hook module*: SPIR-V built from kiln_hooks.hlsl + kiln_hooks_main.hlsl, with functions kiln_hook_spawn, kiln_hook_update and
// kiln_hook_draw that take KilnSprite / KilnContext / KilnLook by pointer. The bytes are untrusted (a mod can hand-write any SPIR-V), so
// nothing here trusts names, stamps or the compiler. Safety comes from what the module can express at all:
//
//  1. Memory. The hook module may declare no buffer, image, push constant, input or workgroup variable: every pointer type in it is
//     Function or Private (per-invocation memory). Its ids are renumbered into a fresh range and only its own ids are mapped, so hook
//     code can't name one of Kiln's variables; types are shared only where they are structurally identical. Pointers come only from
//     variables, parameters and access chains (no pointer phis, selects, undefs or casts); loads, stores, calls and access chains are
//     type-checked. Every dynamic array/vector index is clamped (UMin len-1) and every constant one checked, so no access leaves the object.
//     The values a hook hands back are checked by Kiln's wrapper (kSanitizeSprite, kSanitizeLook) before Kiln writes anything.
//  2. Time. No recursion (the call graph must be acyclic). Every cycle of every function's CFG must pass through a loop header (every DFS
//     back edge targets a block with OpLoopMerge); each header increments one shared per-invocation counter and, past the cap, branches
//     to its merge (the resulting graph must be acyclic). The counter is reset by Kiln's trampoline before each hook call, and the cap is the budget over the
//     hook's straight-line cost (including inserted clamps and counter operations, with callees counted at every call site), so a hook call runs at most about
//     2 x kHookBudget instructions whatever its loops say.
//  3. Shape. An allowlist of opcodes (arithmetic, logic, composites, structured control flow, GLSL.std.450 without pointer or
//     interpolation forms) and of capabilities (Shader). No barriers, atomics, images, derivatives, group operations, OpKill or extensions.
//     Sizes are bounded: the module, its id bound, the local and private memory, array lengths.
//
// What it does not do: full SPIR-V validation (an invalid module can make the driver's compiler fail when
// the pipeline is created: a crash of the game, which a mod's C# can cause anyway). kiln-vfxcook shader build also runs spirv-val on the
// linked result when it is installed.
#include <algorithm>
#include <cstring>
#include <functional>
#include <map>
#include <set>
#include <unordered_map>

#include "../shaders/kiln_vfx_params.h"
#include "vfxcook.h"

#if defined(__clang__)
#pragma clang diagnostic ignored "-Wc23-extensions"
#endif

namespace kvb {
namespace {

const uint8_t kHooked[] = {
#embed "../shaders/build/kiln_sprite_hooked.spv"
};

using Words = std::vector<uint32_t>;

// Opcodes
enum : uint32_t {
    OpNop = 0, OpUndef = 1, OpSourceContinued = 2, OpSource = 3, OpSourceExtension = 4, OpName = 5, OpMemberName = 6, OpString = 7, OpLine = 8,
    OpExtension = 10, OpExtInstImport = 11, OpExtInst = 12, OpMemoryModel = 14, OpEntryPoint = 15, OpExecutionMode = 16, OpCapability = 17,
    OpTypeVoid = 19, OpTypeBool = 20, OpTypeInt = 21, OpTypeFloat = 22, OpTypeVector = 23, OpTypeMatrix = 24, OpTypeArray = 28,
    OpTypeStruct = 30, OpTypePointer = 32, OpTypeFunction = 33, OpConstantTrue = 41, OpConstantFalse = 42, OpConstant = 43,
    OpConstantComposite = 44, OpConstantNull = 46, OpFunction = 54, OpFunctionParameter = 55, OpFunctionEnd = 56, OpFunctionCall = 57,
    OpVariable = 59, OpLoad = 61, OpStore = 62, OpAccessChain = 65, OpInBoundsAccessChain = 66, OpDecorate = 71, OpMemberDecorate = 72,
    OpDecorationGroup = 73, OpGroupDecorate = 74, OpGroupMemberDecorate = 75, OpVectorExtractDynamic = 77, OpVectorInsertDynamic = 78,
    OpVectorShuffle = 79, OpCompositeConstruct = 80, OpCompositeExtract = 81, OpCompositeInsert = 82, OpCopyObject = 83, OpTranspose = 84,
    OpConvertFToU = 109, OpFConvert = 115, OpQuantizeToF16 = 116, OpBitcast = 124, OpSNegate = 126, OpDot = 148, OpAny = 154, OpIsInf = 157,
    OpLogicalEqual = 164, OpSelect = 169, OpIEqual = 170, OpFUnordGreaterThanEqual = 191, OpShiftRightLogical = 194, OpBitCount = 205,
    OpPhi = 245, OpLoopMerge = 246, OpSelectionMerge = 247, OpLabel = 248, OpBranch = 249, OpBranchConditional = 250, OpSwitch = 251,
    OpReturn = 253, OpReturnValue = 254, OpUnreachable = 255, OpNoLine = 317, OpModuleProcessed = 330, OpExecutionModeId = 331,
    OpDecorateId = 332, OpDecorateString = 5632, OpMemberDecorateString = 5633,
};
constexpr uint32_t kFunction = 7, kPrivate = 6;  // storage classes
constexpr uint32_t kGlslUMin = 38;

// Limits on the untrusted module.
constexpr size_t kMaxHookBytes = 256 * 1024;
constexpr uint32_t kMaxHookBound = 1u << 20;
constexpr uint32_t kMaxArrayLength = 4096;
constexpr uint64_t kMaxTypeBytes = 16 * 1024;
constexpr uint64_t kMaxFunctionLocals = 16 * 1024;  // bytes of Function variables in one function
constexpr uint64_t kMaxPrivates = 4 * 1024;         // bytes of Private variables in the module
constexpr uint32_t kMaxStructMembers = 64;
constexpr uint32_t kMaxFunctionParams = 16;
constexpr uint32_t kMaxCallDepth = 64;

std::string text_of(const uint32_t* w, size_t words) {
    const char* s = reinterpret_cast<const char*>(w);
    return std::string(s, strnlen(s, words * 4));
}
// Text from the module, safe for a log line or JSON: printable ASCII, at most 64 characters.
std::string printable(std::string s) {
    if (s.size() > 64) s.resize(64);
    for (char& c : s)
        if (c < 0x20 || c > 0x7e) c = '?';
    return s;
}
bool terminated(const uint32_t* w, size_t words) { return strnlen(reinterpret_cast<const char*>(w), words * 4) < words * 4; }

bool is_terminator(uint32_t op) {
    return op == OpBranch || op == OpBranchConditional || op == OpSwitch || op == OpReturn || op == OpReturnValue || op == OpUnreachable;
}

// What the operands of an instruction are. T: a type id, V: a value id, L: a label, F: a function, X: an ext inst set, S: a string, R: the
// result id, l: a literal. Built per instruction by layout(); "" = this opcode isn't allowed in a hook module.
struct Layout {
    std::string kinds;  // one per word after the opcode word
};

bool value_op_generic(uint32_t op) {
    // type, result, then value ids only
    if (op == OpCopyObject || op == OpTranspose || op == OpCompositeConstruct || op == OpVectorExtractDynamic || op == OpVectorInsertDynamic) return true;
    if (op >= OpConvertFToU && op <= OpQuantizeToF16) return true;  // ConvertFToU..QuantizeToF16 (109..116)
    if (op == OpBitcast) return true;
    if (op >= OpSNegate && op <= OpDot) return true;  // 126..148
    if (op >= OpAny && op <= OpIsInf) return true;    // 154..157
    if (op >= OpLogicalEqual && op <= OpFUnordGreaterThanEqual) return true;  // 164..191
    if (op >= OpShiftRightLogical && op <= OpBitCount) return true;           // 194..205
    return false;
}

// Value operands of the generic opcodes; 0 = not allowed.
int value_op_arity(uint32_t op) {
    switch (op) {
        case 83: case 84: return 1;                       // CopyObject, Transpose
        case 77: return 2;                                // VectorExtractDynamic
        case 78: return 3;                                // VectorInsertDynamic
        case 124: return 1;                               // Bitcast
        case 126: case 127: return 1;                     // SNegate, FNegate
        case 149: case 150: case 151: case 152: return 0; // carries and extended multiplies: struct results, not used
        case 168: case 200: case 204: case 205: return 1; // LogicalNot, Not, BitReverse, BitCount
        case 169: return 3;                               // Select
        case 201: return 4;                               // BitFieldInsert
        case 202: case 203: return 3;                     // BitField[SU]Extract
        default: break;
    }
    if (op >= 109 && op <= 116) return 1;                 // conversions, QuantizeToF16
    if (op >= 154 && op <= 157) return 1;                 // Any, All, IsNan, IsInf
    if (op >= 128 && op <= 148) return 2;                 // arithmetic, products, Dot
    if (op >= 164 && op <= 167) return 2;                 // logical binary
    if (op >= 170 && op <= 191) return 2;                 // comparisons
    if (op >= 194 && op <= 199) return 2;                 // shifts and bitwise
    return 0;
}

// Fills the operand kinds of one instruction (w[0] is the opcode word). Returns "" or why not.
std::string layout(const uint32_t* w, uint32_t wc, bool in_function, std::string* k) {
    const uint32_t op = w[0] & 0xffff;
    const uint32_t n = wc - 1;  // operand words
    auto fixed = [&](const char* s) -> std::string {
        if (std::strlen(s) != n) return "a malformed instruction (opcode " + std::to_string(op) + ")";
        *k = s;
        return "";
    };
    auto at_least = [&](const char* head, char rest, uint32_t min_rest = 0) -> std::string {
        const size_t h = std::strlen(head);
        if (n < h + min_rest) return "a malformed instruction (opcode " + std::to_string(op) + ")";
        *k = head;
        k->append(n - h, rest);
        return "";
    };
    if (in_function && value_op_generic(op)) {
        if (op == OpCompositeConstruct) return at_least("TR", 'V', 1);
        const int arity = value_op_arity(op);
        if (arity <= 0) return "opcode " + std::to_string(op) + " inside a function is outside what a hook module may use";
        if (n != static_cast<uint32_t>(2 + arity)) return "a malformed instruction (opcode " + std::to_string(op) + ")";
        *k = "TR";
        k->append(static_cast<size_t>(arity), 'V');
        return "";
    }
    switch (op) {
        case OpNop: return fixed("");
        case OpLine: return fixed("Sll");
        case OpNoLine: return fixed("");
        case OpUndef: return fixed("TR");
        case OpExtInst:
            if (!in_function) break;
            return at_least("TRXl", 'V');
        case OpFunctionCall:
            if (!in_function) break;
            return at_least("TRF", 'V');
        case OpVariable:
            if (n == 3) return fixed("TRl");
            return fixed("TRlV");
        case OpLoad:
            if (!in_function) break;
            if (n == 3) return fixed("TRV");
            if (n == 4 && (w[4] & ~1u) == 0) return fixed("TRVl");          // None / Volatile
            if (n == 5 && (w[4] & ~1u) == 2) return fixed("TRVll");         // Aligned (+ Volatile)
            return "a load with memory operands a hook module doesn't use";
        case OpStore:
            if (!in_function) break;
            if (n == 2) return fixed("VV");
            if (n == 3 && (w[3] & ~1u) == 0) return fixed("VVl");
            if (n == 4 && (w[3] & ~1u) == 2) return fixed("VVll");
            return "a store with memory operands a hook module doesn't use";
        case OpAccessChain:
        case OpInBoundsAccessChain:
            if (!in_function) break;
            return at_least("TRV", 'V');
        case OpVectorShuffle:
            if (!in_function) break;
            return at_least("TRVV", 'l');
        case OpCompositeExtract:
            if (!in_function) break;
            return at_least("TRV", 'l', 1);
        case OpCompositeInsert:
            if (!in_function) break;
            return at_least("TRVV", 'l', 1);
        case OpPhi:
            if (!in_function || n < 4 || n % 2) break;
            *k = "TR";
            for (uint32_t i = 2; i < n; i += 2) *k += "VL";
            return "";
        case OpLoopMerge:
            if (!in_function) break;
            if (n != 3 || (w[3] & ~3u) != 0) return "a loop merge with loop controls a hook module doesn't use";
            return fixed("LLl");
        case OpSelectionMerge:
            if (!in_function) break;
            if (n != 2 || (w[2] & ~3u) != 0) return "a selection merge with controls a hook module doesn't use";
            return fixed("Ll");
        case OpLabel: return in_function ? fixed("R") : "a label outside a function";
        case OpBranch: return in_function ? fixed("L") : "a branch outside a function";
        case OpBranchConditional:
            if (!in_function) break;
            if (n == 3) return fixed("VLL");
            if (n == 5) return fixed("VLLll");
            return "a malformed conditional branch";
        case OpSwitch:
            if (!in_function || n < 2 || n % 2) break;
            *k = "VL";
            for (uint32_t i = 2; i < n; i += 2) *k += "lL";
            return "";
        case OpReturn: return in_function ? fixed("") : "a return outside a function";
        case OpReturnValue: return in_function ? fixed("V") : "a return outside a function";
        case OpUnreachable: return in_function ? fixed("") : "unreachable outside a function";
        // global section
        case OpTypeVoid: case OpTypeBool: return in_function ? std::string("a type or constant declared inside a function") : fixed("R");
        case OpTypeInt: return in_function ? std::string("a type or constant declared inside a function") : fixed("Rll");
        case OpTypeFloat: return in_function ? std::string("a type or constant declared inside a function") : fixed("Rl");
        case OpTypeVector: case OpTypeMatrix: return in_function ? std::string("a type or constant declared inside a function") : fixed("RTl");
        case OpTypeArray: return in_function ? std::string("a type or constant declared inside a function") : fixed("RTV");
        case OpTypeStruct: return in_function ? std::string("a type or constant declared inside a function") : at_least("R", 'T');
        case OpTypePointer: return in_function ? std::string("a type or constant declared inside a function") : fixed("RlT");
        case OpTypeFunction: return in_function ? std::string("a type or constant declared inside a function") : at_least("RT", 'T');
        case OpConstantTrue: case OpConstantFalse: case OpConstantNull: return in_function ? std::string("a type or constant declared inside a function") : fixed("TR");
        case OpConstant: return in_function ? std::string("a type or constant declared inside a function") : fixed("TRl");
        case OpConstantComposite: return in_function ? std::string("a type or constant declared inside a function") : at_least("TR", 'V', 1);
        default: break;
    }
    return "opcode " + std::to_string(op) + (in_function ? " inside a function" : " in the global section") + " is outside what a hook module may use";
}

struct Type {
    uint32_t op = 0;
    std::vector<uint32_t> w;  // operand words after the result id
};

struct Inst {
    uint32_t at = 0, wc = 0;  // word index in the module, word count
    std::string kinds;
};

struct Block {
    uint32_t label = 0;
    std::vector<Inst> body;  // after the label, the merge and terminator included
    std::vector<uint32_t> succ;
    bool header = false;  // has OpLoopMerge
};

struct Function {
    uint32_t id = 0, type = 0, ret = 0, control = 0;
    Inst def;
    std::vector<Inst> params;
    std::vector<Block> blocks;
    std::set<uint32_t> callees;
    uint64_t own = 0;  // instructions
    uint64_t locals = 0;  // bytes in this frame; check_calls also bounds the largest live call stack
};

enum class Kind : uint8_t { None, Type, Value, Label, Function, ExtSet, String };

class SpvModule {
public:
    const uint32_t* w = nullptr;
    size_t n = 0;
    uint32_t version = 0, bound = 0;
    std::vector<Inst> caps, imports, entries, debug, annotations, globals;
    Inst memory_model;
    std::vector<Function> funcs;
    std::unordered_map<uint32_t, Kind> kind;
    std::unordered_map<uint32_t, Type> types;
    std::unordered_map<uint32_t, uint32_t> type_of;   // value id -> type id
    std::unordered_map<uint32_t, uint32_t> constant;  // OpConstant of a 32-bit int -> its value
    std::unordered_map<uint32_t, std::string> names;
    // Where a function-local id is defined: its function, block (-1: a parameter) and place in the block.
    struct Loc {
        uint32_t fn = 0;
        int32_t block = -1;
        uint32_t pos = 0;
    };
    std::unordered_map<uint32_t, Loc> loc;
    std::unordered_map<uint32_t, size_t> func_index;
    uint32_t glsl = 0;

    uint32_t word(const Inst& i, uint32_t k) const { return w[i.at + k]; }
    uint32_t op(const Inst& i) const { return w[i.at] & 0xffff; }

    // Splits a module into its sections. strict: an untrusted hook module (operands checked against layout()).
    std::string parse(const uint32_t* words, size_t count, bool strict) {
        w = words;
        n = count;
        if (n < 5 || w[0] != 0x07230203u) return "not SPIR-V";
        version = w[1];
        bound = w[3];
        if (version < 0x00010000u || version > 0x00010500u || (version & 0xffu)) return "SPIR-V version outside 1.0..1.5";
        if (strict && (bound == 0 || bound > kMaxHookBound)) return "an id bound outside 1.." + std::to_string(kMaxHookBound);
        if (w[4] != 0) return "a nonzero schema word";
        int section = 0;  // 0 caps .. 9 functions
        Function* f = nullptr;
        Block* b = nullptr;
        bool in_params = false, may_declare = false;
        for (size_t i = 5; i < n;) {
            const uint32_t op = w[i] & 0xffff, wc = w[i] >> 16;
            if (wc == 0 || i + wc > n) return "a broken instruction";
            Inst in{static_cast<uint32_t>(i), wc, {}};
            const bool in_function = f != nullptr;
            if (strict) {
                std::string why;
                // headers and debug/annotation instructions are checked below by hand
                const bool special = op == OpCapability || op == OpExtension || op == OpExtInstImport || op == OpMemoryModel || op == OpEntryPoint ||
                                     op == OpExecutionMode || op == OpExecutionModeId || op == OpSource || op == OpSourceContinued || op == OpSourceExtension ||
                                     op == OpName || op == OpMemberName || op == OpString || op == OpModuleProcessed || op == OpDecorate ||
                                     op == OpMemberDecorate || op == OpDecorateString || op == OpMemberDecorateString || op == OpDecorationGroup ||
                                     op == OpGroupDecorate || op == OpGroupMemberDecorate || op == OpDecorateId || op == OpFunction ||
                                     op == OpFunctionParameter || op == OpFunctionEnd;
                if (!special) {
                    why = layout(w + i, wc, in_function, &in.kinds);
                    if (!why.empty()) return why;
                }
            }
            auto define = [&](uint32_t id, Kind k) -> std::string {
                if (id == 0 || id >= bound) return "id " + std::to_string(id) + " outside the bound";
                if (kind.count(id)) return "id " + std::to_string(id) + " defined twice";
                kind[id] = k;
                return "";
            };
            auto order = [&](int s) -> std::string {
                if (s < section) return "instructions out of the order SPIR-V requires (opcode " + std::to_string(op) + ")";
                section = s;
                return "";
            };
            std::string why;
            switch (op) {
                case OpCapability:
                    if ((why = order(0)).size()) return why;
                    if (wc != 2) return "a broken capability instruction";
                    caps.push_back(in);
                    break;
                case OpExtension:
                    if ((why = order(1)).size()) return why;
                    if (strict) return "an extension (" + printable(text_of(w + i + 1, wc - 1)) + "): a hook module uses none";
                    break;
                case OpExtInstImport:
                    if ((why = order(2)).size()) return why;
                    if (wc < 3 || !terminated(w + i + 2, wc - 2)) return "a broken import";
                    if (text_of(w + i + 2, wc - 2) != "GLSL.std.450") return "an instruction set import other than GLSL.std.450";
                    if ((why = define(w[i + 1], Kind::ExtSet)).size()) return why;
                    if (glsl) return "GLSL.std.450 imported twice";
                    glsl = w[i + 1];
                    imports.push_back(in);
                    break;
                case OpMemoryModel:
                    if ((why = order(3)).size()) return why;
                    if (wc != 3 || w[i + 1] != 0 || w[i + 2] != 1) return "a memory model other than Logical GLSL450";
                    memory_model = in;
                    section = 4;
                    break;
                case OpEntryPoint:
                    if ((why = order(4)).size()) return why;
                    entries.push_back(in);
                    break;
                case OpExecutionMode:
                case OpExecutionModeId:
                    if ((why = order(5)).size()) return why;
                    break;
                case OpString:
                    if ((why = order(6)).size()) return why;
                    if (wc < 3) return "a broken string";
                    if ((why = define(w[i + 1], Kind::String)).size()) return why;
                    debug.push_back(in);
                    break;
                case OpSource: case OpSourceContinued: case OpSourceExtension: case OpModuleProcessed:
                    if ((why = order(6)).size()) return why;
                    debug.push_back(in);
                    break;
                case OpName: case OpMemberName:
                    if ((why = order(7)).size()) return why;
                    if (op == OpName && wc >= 3) names[w[i + 1]] = text_of(w + i + 2, wc - 2);
                    debug.push_back(in);
                    break;
                case OpDecorate: case OpMemberDecorate: case OpDecorateString: case OpMemberDecorateString: case OpDecorateId:
                case OpDecorationGroup: case OpGroupDecorate: case OpGroupMemberDecorate:
                    if ((why = order(8)).size()) return why;
                    if (strict && (op == OpDecorationGroup || op == OpGroupDecorate || op == OpGroupMemberDecorate))
                        return "decoration groups: a hook module uses none";
                    if (wc < 2) return "a broken decoration";
                    if (op == OpDecorationGroup && (why = define(w[i + 1], Kind::None)).size()) return why;
                    annotations.push_back(in);
                    break;
                case OpFunction: {
                    if ((why = order(9)).size()) return why;
                    if (f) return "a function inside a function";
                    if (wc != 5) return "a broken function";
                    if ((why = define(w[i + 2], Kind::Function)).size()) return why;
                    funcs.push_back(Function{});
                    f = &funcs.back();
                    f->id = w[i + 2];
                    f->ret = w[i + 1];
                    f->control = w[i + 3];
                    f->type = w[i + 4];
                    f->def = in;
                    in_params = true;
                    b = nullptr;
                    break;
                }
                case OpFunctionParameter:
                    if (!f || !in_params) return "a parameter outside a function's head";
                    if (wc != 3) return "a broken parameter";
                    if ((why = define(w[i + 2], Kind::Value)).size()) return why;
                    type_of[w[i + 2]] = w[i + 1];
                    loc[w[i + 2]] = Loc{f->id, -1, 0};
                    f->params.push_back(in);
                    break;
                case OpFunctionEnd:
                    if (!f) return "a function end outside a function";
                    if (f->blocks.empty()) return "a function declaration without a body (imports are not allowed)";
                    if (!b || b->body.empty() || !is_terminator(op_at(b->body.back()))) return "a block without a terminator";
                    f = nullptr;
                    b = nullptr;
                    break;
                default:
                    if (!f) {  // types, constants, global variables, undefs
                        if (op == OpLine || op == OpNoLine || op == OpNop) break;
                        if ((why = order(9)).size()) return why;
                        globals.push_back(in);
                        if (op >= OpTypeVoid && op <= 39 && op != 25 && op != 26 && op != 27) {  // types
                            if (wc < 2) return "a broken type";
                            if ((why = define(w[i + 1], Kind::Type)).size()) return why;
                            Type t;
                            t.op = op;
                            t.w.assign(w + i + 2, w + i + wc);
                            types[w[i + 1]] = std::move(t);
                        } else if (wc >= 3 && (op == OpConstantTrue || op == OpConstantFalse || op == OpConstant || op == OpConstantComposite ||
                                               op == OpConstantNull || op == OpUndef || op == OpVariable || (!strict && op >= OpConstantTrue && op <= 52))) {
                            if ((why = define(w[i + 2], Kind::Value)).size()) return why;
                            type_of[w[i + 2]] = w[i + 1];
                            if (op == OpConstant && wc == 4) constant[w[i + 2]] = w[i + 3];
                        } else if (strict) {
                            return "opcode " + std::to_string(op) + " in the global section is outside what a hook module may use";
                        }
                        break;
                    }
                    in_params = false;
                    if (op == OpLabel) {
                        if (wc != 2) return "a broken label";
                        if (b && (b->body.empty() || !is_terminator(op_at(b->body.back())))) return "a block without a terminator";
                        if ((why = define(w[i + 1], Kind::Label)).size()) return why;
                        f->blocks.push_back(Block{});
                        b = &f->blocks.back();
                        b->label = w[i + 1];
                        may_declare = f->blocks.size() == 1;
                        break;
                    }
                    if (!b) return "an instruction before a function's first label";
                    if (b->body.size() && is_terminator(op_at(b->body.back()))) return "an instruction after a block's terminator";
                    if (op == OpLine || op == OpNoLine || op == OpNop) break;  // dropped
                    if (op == OpVariable) {
                        if (!may_declare) return "a function variable outside the start of the first block";
                    } else {
                        may_declare = false;
                    }
                    if (strict && in.kinds.size() >= 2 && in.kinds[1] == 'R') {
                        if ((why = define(w[i + 2], Kind::Value)).size()) return why;
                        type_of[w[i + 2]] = w[i + 1];
                        loc[w[i + 2]] = Loc{f->id, static_cast<int32_t>(f->blocks.size() - 1), static_cast<uint32_t>(b->body.size())};
                    }
                    if (op == OpLoopMerge) b->header = true;
                    b->body.push_back(in);
                    ++f->own;
                    break;
            }
            i += wc;
        }
        if (f) return "a function without an end";
        for (size_t i = 0; i < funcs.size(); ++i) func_index[funcs[i].id] = i;
        return "";
    }

    uint32_t op_at(const Inst& in) const { return w[in.at] & 0xffff; }
};

// ---- the checks on a hook module ----------------------------------------------------------------------------------------------------

class Verifier {
public:
    explicit Verifier(SpvModule& m) : m_(m) {}

    std::string run() {
        std::string why;
        for (const Inst& c : m_.caps)
            if (m_.word(c, 1) != 1) return "capability " + std::to_string(m_.word(c, 1)) + ": a hook module may use only Shader";
        if (m_.caps.empty()) return "no Shader capability";
        if ((why = check_types()).size()) return why;
        if ((why = check_globals()).size()) return why;
        for (Function& f : m_.funcs)
            if ((why = check_function(f)).size()) return m_name(f) + ": " + why;
        if ((why = check_calls()).size()) return why;
        return "";
    }

    uint64_t bytes(uint32_t t) const {
        const auto it = sizes_.find(t);
        return it == sizes_.end() ? 0 : it->second;
    }

private:
    SpvModule& m_;
    std::unordered_map<uint32_t, uint64_t> sizes_;

    std::string m_name(const Function& f) const {
        const auto it = m_.names.find(f.id);
        std::string s = it == m_.names.end() ? "function %" + std::to_string(f.id) : it->second;
        if (const size_t p = s.find('('); p != std::string::npos) s.resize(p);
        return printable(s);
    }
    bool is_type(uint32_t id) const { return m_.types.count(id) != 0; }
    const Type* type(uint32_t id) const {
        const auto it = m_.types.find(id);
        return it == m_.types.end() ? nullptr : &it->second;
    }
    bool is_pointer(uint32_t t) const {
        const Type* ty = type(t);
        return ty && ty->op == OpTypePointer;
    }
    uint32_t value_type(uint32_t v) const {
        const auto it = m_.type_of.find(v);
        return it == m_.type_of.end() ? 0 : it->second;
    }

    std::string check_types() {
        // Types are declared before use, so one pass in order computes sizes.
        for (const Inst& g : m_.globals) {
            const uint32_t op = m_.op(g);
            if (!(op >= OpTypeVoid && op <= OpTypeFunction)) continue;
            const uint32_t id = m_.word(g, 1);
            const Type& t = m_.types.at(id);
            auto known = [&](uint32_t x) { return sizes_.count(x) != 0; };
            uint64_t size = 0;
            switch (op) {
                case OpTypeVoid: size = 0; break;
                case OpTypeBool: size = 4; break;
                case OpTypeInt:
                    if (t.w[0] != 32 || t.w[1] > 1) return "an integer type other than 32-bit";
                    size = 4;
                    break;
                case OpTypeFloat:
                    if (t.w[0] != 32) return "a float type other than 32-bit";
                    size = 4;
                    break;
                case OpTypeVector: {
                    const Type* c = type(t.w[0]);
                    if (!c || !known(t.w[0]) || !(c->op == OpTypeBool || c->op == OpTypeInt || c->op == OpTypeFloat) || t.w[1] < 2 || t.w[1] > 4)
                        return "a malformed vector type";
                    size = 4ull * t.w[1];
                    break;
                }
                case OpTypeMatrix: {
                    const Type* c = type(t.w[0]);
                    if (!c || !known(t.w[0]) || c->op != OpTypeVector || type(c->w[0])->op != OpTypeFloat || t.w[1] < 2 || t.w[1] > 4)
                        return "a malformed matrix type";
                    size = sizes_[t.w[0]] * t.w[1];
                    break;
                }
                case OpTypeArray: {
                    const auto len = m_.constant.find(t.w[1]);
                    if (!known(t.w[0]) || sizes_[t.w[0]] == 0 || len == m_.constant.end()) return "an array without a constant length";
                    const Type* lt = type(value_type(t.w[1]));
                    if (!lt || lt->op != OpTypeInt) return "an array length that is not an integer constant";
                    if (len->second == 0 || len->second > kMaxArrayLength) return "an array of " + std::to_string(len->second) + " elements (1.." + std::to_string(kMaxArrayLength) + ")";
                    size = sizes_[t.w[0]] * len->second;
                    break;
                }
                case OpTypeStruct:
                    if (t.w.empty() || t.w.size() > kMaxStructMembers) return "a struct with 1.." + std::to_string(kMaxStructMembers) + " members expected";
                    for (uint32_t mbr : t.w) {
                        if (!known(mbr) || sizes_[mbr] == 0) return "a struct member of an unknown or empty type";
                        size += sizes_[mbr];
                    }
                    break;
                case OpTypePointer: {
                    if (t.w[0] != kFunction && t.w[0] != kPrivate) return "a pointer to storage class " + std::to_string(t.w[0]) + ": hook code may only point at its own Function or Private memory";
                    if (!known(t.w[1]) || sizes_[t.w[1]] == 0) return "a pointer to an unknown type";
                    size = 0;
                    break;
                }
                case OpTypeFunction: {
                    if (!known(t.w[0])) return "a function type with an unknown return type";
                    if (is_pointer(t.w[0])) return "a function that returns a pointer";
                    if (!sizes_[t.w[0]] && type(t.w[0])->op != OpTypeVoid) return "a function return that isn't a data type or void";
                    if (t.w.size() - 1 > kMaxFunctionParams) return "a function with more than " + std::to_string(kMaxFunctionParams) + " parameters";
                    for (size_t i = 1; i < t.w.size(); ++i)
                        if (!known(t.w[i]) || (sizes_[t.w[i]] == 0 && !is_pointer(t.w[i]))) return "a function parameter of an unknown type";
                    size = 0;
                    break;
                }
                default: return "a type a hook module may not use";
            }
            if (size > kMaxTypeBytes) return "a type of " + std::to_string(size) + " bytes (at most " + std::to_string(kMaxTypeBytes) + ")";
            sizes_[id] = size;
        }
        return "";
    }

    // Operand kinds against definitions: every id the module names is its own, and of the right sort.
    std::string check_operands(const Inst& in, const Function* f, const std::set<uint32_t>* labels) {
        for (size_t k = 0; k < in.kinds.size(); ++k) {
            const char c = in.kinds[k];
            const uint32_t id = m_.word(in, static_cast<uint32_t>(k + 1));
            if (c == 'l' || c == 'R' || c == 'S') continue;
            const auto it = m_.kind.find(id);
            const Kind kd = it == m_.kind.end() ? Kind::None : it->second;
            switch (c) {
                case 'T': if (kd != Kind::Type) return "an operand that should be a type is not"; break;
                case 'V': {
                    if (kd != Kind::Value) return "an operand that should be a value is not (a type, label or undefined id)";
                    const auto l = m_.loc.find(id);
                    if (l != m_.loc.end() && (!f || l->second.fn != f->id)) return "a value used outside the function that defines it";
                    break;
                }
                case 'F': if (kd != Kind::Function) return "a call to something that is not a function of the module"; break;
                case 'X': if (kd != Kind::ExtSet) return "an extended instruction from an unknown set"; break;
                case 'L':
                    if (kd != Kind::Label || !labels || !labels->count(id)) return "a branch to a label outside this function";
                    break;
                default: return "an operand Kiln can't classify";
            }
        }
        (void)f;
        return "";
    }

    std::string check_globals() {
        uint64_t privates = 0;
        std::set<uint32_t> seen;  // global ids are used only after they are declared
        for (const Inst& g : m_.globals) {
            std::string why = check_operands(g, nullptr, nullptr);
            if (!why.empty()) return why;
            for (size_t k = 0; k < g.kinds.size(); ++k) {
                const uint32_t id = m_.word(g, static_cast<uint32_t>(k + 1));
                if ((g.kinds[k] == 'T' || g.kinds[k] == 'V') && !seen.count(id)) return "a global that names an id declared after it";
                if (g.kinds[k] == 'R') seen.insert(id);
            }
            const uint32_t op = m_.op(g);
            if (op == OpVariable) {
                const uint32_t pt = m_.word(g, 1);
                const Type* p = type(pt);
                if (!p || p->op != OpTypePointer || p->w[0] != kPrivate || m_.word(g, 3) != kPrivate) return "a global variable that isn't Private";
                if (g.wc == 5) {
                    const uint32_t init = m_.word(g, 4);
                    const auto k = m_.kind.find(init);
                    if (k == m_.kind.end() || value_type(init) != p->w[1] || !is_constant(init)) return "a variable initialiser that isn't a constant of its type";
                }
                privates += bytes(p->w[1]);
            } else if (op == OpUndef || op == OpConstantNull) {
                if (is_pointer(m_.word(g, 1))) return "a null or undefined pointer";
                if (!bytes(m_.word(g, 1))) return "a null or undefined value that isn't a data type";
            } else if (op == OpConstant) {
                const Type* t = type(m_.word(g, 1));
                if (!t || (t->op != OpTypeInt && t->op != OpTypeFloat)) return "a constant of a type other than a 32-bit number";
            } else if (op == OpConstantTrue || op == OpConstantFalse) {
                const Type* t = type(m_.word(g, 1));
                if (!t || t->op != OpTypeBool) return "a boolean constant that isn't a bool";
            } else if (op == OpConstantComposite) {
                const Type* t = type(m_.word(g, 1));
                if (!t || !(t->op == OpTypeVector || t->op == OpTypeMatrix || t->op == OpTypeArray || t->op == OpTypeStruct)) return "a composite constant of a scalar type";
                for (uint32_t k = 3; k < g.wc; ++k)
                    if (!is_constant(m_.word(g, k))) return "a composite constant made of something that isn't a constant";
                if (std::string bad = check_composite(m_.word(g, 1), m_.w + g.at + 3, g.wc - 3); !bad.empty()) return bad;
            }
        }
        if (privates > kMaxPrivates) return "Private variables of " + std::to_string(privates) + " bytes (at most " + std::to_string(kMaxPrivates) + ")";
        return "";
    }

    bool is_constant(uint32_t id) const {
        for (const Inst& g : m_.globals) {
            const uint32_t op = m_.op(g);
            if (op >= OpConstantTrue && op <= OpConstantNull && op != 45 && g.wc >= 3 && m_.word(g, 2) == id) return true;
            if (op == OpUndef && m_.word(g, 2) == id) return true;
        }
        return false;
    }

    std::string check_function(Function& f) {
        const Type* ft = type(f.type);
        if (!ft || ft->op != OpTypeFunction) return "a function whose type isn't a function type";
        if (ft->w[0] != f.ret) return "a return type that differs from its function type";
        if (ft->w.size() - 1 != f.params.size()) return "a parameter count that differs from its function type";
        for (size_t i = 0; i < f.params.size(); ++i)
            if (m_.word(f.params[i], 1) != ft->w[i + 1]) return "a parameter type that differs from its function type";
        std::set<uint32_t> labels;
        for (const Block& b : f.blocks) labels.insert(b.label);
        uint64_t locals = 0;
        for (Block& b : f.blocks) {
            // the merge instruction, if any, is second to last
            for (size_t i = 0; i < b.body.size(); ++i) {
                const Inst& in = b.body[i];
                const uint32_t op = m_.op(in);
                if ((op == OpLoopMerge || op == OpSelectionMerge) && i + 2 != b.body.size()) return "a merge instruction that isn't just before the terminator";
                if (op == OpPhi && i > 0 && m_.op(b.body[i - 1]) != OpPhi) return "a phi after other instructions";
                std::string why = check_operands(in, &f, &labels);
                if (!why.empty()) return why;
                if ((why = check_instruction(f, in, &locals)).size()) return why;
            }
            const Inst& t = b.body.back();
            const uint32_t top = m_.op(t);
            for (size_t k = 0; k < t.kinds.size(); ++k)
                if (t.kinds[k] == 'L') b.succ.push_back(m_.word(t, static_cast<uint32_t>(k + 1)));
            for (uint32_t s : b.succ)
                if (s == f.blocks.front().label) return "a branch back to the function's first block";
            if (top == OpReturnValue && value_type(m_.word(t, 1)) != f.ret) return "a return value of another type than the function's";
            if (top == OpReturn && type(f.ret)->op != OpTypeVoid) return "a plain return from a function that returns a value";
            if (top == OpReturnValue && type(f.ret)->op == OpTypeVoid) return "a return value from a void function";
            if (top == OpBranchConditional) {
                const Type* ct = type(value_type(m_.word(t, 1)));
                if (!ct || ct->op != OpTypeBool) return "a conditional branch on something that isn't a bool";
            }
            if (top == OpSwitch) {
                const Type* st = type(value_type(m_.word(t, 1)));
                if (!st || st->op != OpTypeInt) return "a switch on something that isn't a 32-bit integer";
            }
        }
        if (locals > kMaxFunctionLocals) return "local variables of " + std::to_string(locals) + " bytes (at most " + std::to_string(kMaxFunctionLocals) + ")";
        f.locals = locals;
        std::string why = check_loops(f);
        if (!why.empty()) return why;
        if ((why = check_dominance(f)).size()) return why;
        // Instrumentation can branch straight from a header to its merge. Check SSA again with those new edges: even a valid
        // original module may use a body-defined value at the merge, or have phis without values for the new predecessors.
        Function instrumented = f;
        for (Block& b : instrumented.blocks)
            if (b.header) {
                const uint32_t merge = m_.word(b.body[b.body.size() - 2], 1);
                if (std::find(b.succ.begin(), b.succ.end(), merge) == b.succ.end()) b.succ.push_back(merge);
            }
        return check_dominance(instrumented);
    }

    std::string check_instruction(Function& f, const Inst& in, uint64_t* locals) {
        const uint32_t op = m_.op(in);
        const bool has_result = in.kinds.size() >= 2 && in.kinds[1] == 'R';
        if (has_result) {
            const uint32_t rt = m_.word(in, 1);
            if (!is_type(rt)) return "a result type that isn't a type";
            const bool pointer_ok = op == OpVariable || op == OpAccessChain || op == OpInBoundsAccessChain;
            if (is_pointer(rt) && !pointer_ok) return "an instruction (opcode " + std::to_string(op) + ") that makes a pointer: only variables and access chains may";
            if (type(rt)->op == OpTypeVoid && op != OpFunctionCall) return "a value of type void";
            if (!is_pointer(rt) && !bytes(rt) && op != OpFunctionCall) return "a result that isn't a data type";
        }
        if (op == OpLoad || op == OpStore) {
            const uint32_t mask_at = op == OpLoad ? 4 : 3;
            if (in.wc > mask_at && (m_.word(in, mask_at) & 2)) {
                const uint32_t alignment = m_.word(in, mask_at + 1);
                // All allowed objects consist of 32-bit scalars. No access proves more than four-byte alignment.
                if (!alignment || (alignment & (alignment - 1)) || alignment > 4) return "an unproven memory alignment";
            }
        }
        // pointer operands of value instructions: only loads, stores, access chains and calls may take pointers
        if (op != OpLoad && op != OpStore && op != OpAccessChain && op != OpInBoundsAccessChain && op != OpFunctionCall && op != OpVariable) {
            for (size_t k = 0; k < in.kinds.size(); ++k)
                if (in.kinds[k] == 'V' && is_pointer(value_type(m_.word(in, static_cast<uint32_t>(k + 1)))))
                    return "a pointer used as a value (opcode " + std::to_string(op) + ")";
        }
        if (value_op_generic(op) && op != OpCompositeConstruct && op != OpVectorExtractDynamic && op != OpVectorInsertDynamic) {
            const std::string why = check_shapes(in);
            if (!why.empty()) return why;
        }
        switch (op) {
            case OpVariable: {
                const Type* p = type(m_.word(in, 1));
                if (!p || p->op != OpTypePointer || p->w[0] != kFunction || m_.word(in, 3) != kFunction) return "a function variable that isn't Function storage";
                if (in.wc == 5 && (value_type(m_.word(in, 4)) != p->w[1] || !is_constant(m_.word(in, 4)))) return "a variable initialiser that isn't a constant of its type";
                *locals += bytes(p->w[1]);
                break;
            }
            case OpLoad: {
                const Type* p = type(value_type(m_.word(in, 3)));
                if (!p || p->op != OpTypePointer || p->w[1] != m_.word(in, 1)) return "a load through something that isn't a pointer to its type";
                break;
            }
            case OpStore: {
                const Type* p = type(value_type(m_.word(in, 1)));
                if (!p || p->op != OpTypePointer || p->w[1] != value_type(m_.word(in, 2))) return "a store through something that isn't a pointer to the value's type";
                break;
            }
            case OpAccessChain:
            case OpInBoundsAccessChain: {
                const Type* base = type(value_type(m_.word(in, 3)));
                if (!base || base->op != OpTypePointer) return "an access chain on something that isn't a pointer";
                uint32_t t = base->w[1];
                for (uint32_t k = 4; k < in.wc; ++k) {
                    const uint32_t idx = m_.word(in, k);
                    const Type* it = type(value_type(idx));
                    if (!it || it->op != OpTypeInt) return "an index that isn't a 32-bit integer";
                    const Type* ty = type(t);
                    const auto cv = m_.constant.find(idx);
                    uint32_t count = 0, next = 0;
                    if (ty->op == OpTypeStruct) {
                        if (cv == m_.constant.end()) return "a struct member chosen by a value that isn't a constant";
                        count = static_cast<uint32_t>(ty->w.size());
                        if (cv->second >= count) return "a struct member index out of range";
                        next = ty->w[cv->second];
                    } else if (ty->op == OpTypeArray) {
                        count = m_.constant.at(ty->w[1]);
                        next = ty->w[0];
                    } else if (ty->op == OpTypeVector || ty->op == OpTypeMatrix) {
                        count = ty->w[1];
                        next = ty->w[0];
                    } else {
                        return "an access chain into a scalar";
                    }
                    if (cv != m_.constant.end() && cv->second >= count) return "a constant index past the end of its array or vector";
                    t = next;
                }
                const Type* rt = type(m_.word(in, 1));
                if (!rt || rt->op != OpTypePointer || rt->w[0] != base->w[0] || rt->w[1] != t) return "an access chain whose result type doesn't match where it points";
                break;
            }
            case OpFunctionCall: {
                const auto fi = m_.func_index.find(m_.word(in, 3));
                if (fi == m_.func_index.end()) return "a call to an unknown function";
                const Function& callee = m_.funcs[fi->second];
                if (callee.ret != m_.word(in, 1)) return "a call whose result type isn't the callee's";
                if (callee.params.size() != in.wc - 4) return "a call with the wrong number of arguments";
                for (size_t i = 0; i < callee.params.size(); ++i)
                    if (value_type(m_.word(in, static_cast<uint32_t>(4 + i))) != m_.word(callee.params[i], 1)) return "a call argument of another type than its parameter";
                f.callees.insert(callee.id);
                break;
            }
            case OpExtInst: {
                const std::string why = check_glsl(in);
                if (!why.empty()) return why;
                break;
            }
            case OpVectorExtractDynamic:
            case OpVectorInsertDynamic: {
                const Type* v = type(value_type(m_.word(in, 3)));
                if (!v || v->op != OpTypeVector) return "a dynamic vector access on something that isn't a vector";
                const Type* it = type(value_type(m_.word(in, op == OpVectorExtractDynamic ? 4 : 5)));
                if (!it || it->op != OpTypeInt) return "a vector index that isn't a 32-bit integer";
                const uint32_t idx = m_.word(in, op == OpVectorExtractDynamic ? 4 : 5);
                if (m_.constant.count(idx) && m_.constant.at(idx) >= v->w[1]) return "a constant vector index out of range";
                if (op == OpVectorExtractDynamic && m_.word(in, 1) != v->w[0]) return "a vector extract of another type than the component";
                if (op == OpVectorInsertDynamic && (m_.word(in, 1) != value_type(m_.word(in, 3)) || value_type(m_.word(in, 4)) != v->w[0]))
                    return "a vector insert of mismatched types";
                break;
            }
            case OpVectorShuffle: {
                const Type* a = type(value_type(m_.word(in, 3)));
                const Type* b = type(value_type(m_.word(in, 4)));
                if (!a || !b || a->op != OpTypeVector || b->op != OpTypeVector) return "a shuffle of something that isn't a vector";
                for (uint32_t k = 5; k < in.wc; ++k) {
                    const uint32_t c = m_.word(in, k);
                    if (c != 0xffffffffu && c >= a->w[1] + b->w[1]) return "a shuffle component out of range";
                }
                const Type* r = type(m_.word(in, 1));
                if (!r || r->op != OpTypeVector || r->w[1] != in.wc - 5 || r->w[0] != a->w[0] || b->w[0] != a->w[0]) return "a shuffle of mismatched types";
                break;
            }
            case OpCompositeExtract:
            case OpCompositeInsert: {
                uint32_t t = value_type(m_.word(in, op == OpCompositeExtract ? 3 : 4));
                for (uint32_t k = op == OpCompositeExtract ? 4 : 5; k < in.wc; ++k) {
                    const Type* ty = type(t);
                    const uint32_t i = m_.word(in, k);
                    if (!ty) return "a composite access on something unknown";
                    if (ty->op == OpTypeStruct) {
                        if (i >= ty->w.size()) return "a composite index out of range";
                        t = ty->w[i];
                    } else if (ty->op == OpTypeArray) {
                        if (i >= m_.constant.at(ty->w[1])) return "a composite index out of range";
                        t = ty->w[0];
                    } else if (ty->op == OpTypeVector || ty->op == OpTypeMatrix) {
                        if (i >= ty->w[1]) return "a composite index out of range";
                        t = ty->w[0];
                    } else {
                        return "a composite index into a scalar";
                    }
                }
                if (op == OpCompositeExtract && m_.word(in, 1) != t) return "a composite extract of another type than the member";
                if (op == OpCompositeInsert && (m_.word(in, 1) != value_type(m_.word(in, 4)) || value_type(m_.word(in, 3)) != t))
                    return "a composite insert of mismatched types";
                break;
            }
            case OpPhi:
                for (uint32_t k = 3; k < in.wc; k += 2)
                    if (value_type(m_.word(in, k)) != m_.word(in, 1)) return "a phi value of another type than the phi";
                break;
            case OpCompositeConstruct: {
                const std::string why = check_composite(m_.word(in, 1), m_.w + in.at + 3, in.wc - 3);
                if (!why.empty()) return why;
                break;
            }
            default: break;
        }
        return "";
    }

    // ---- shapes: scalar class and component count of a value's type, for the arithmetic rules ----
    struct Shape {
        char cls = 0;  // 'F' float, 'I' int, 'B' bool, 0 other
        uint32_t count = 0;
    };
    Shape shape_of_type(uint32_t t) const {
        const Type* ty = type(t);
        if (!ty) return {};
        uint32_t count = 1;
        if (ty->op == OpTypeVector) {
            count = ty->w[1];
            ty = type(ty->w[0]);
            if (!ty) return {};
        }
        const char cls = ty->op == OpTypeFloat ? 'F' : ty->op == OpTypeInt ? 'I' : ty->op == OpTypeBool ? 'B' : 0;
        return cls ? Shape{cls, count} : Shape{};
    }
    Shape shape(uint32_t value) const { return shape_of_type(value_type(value)); }
    static bool same(Shape a, Shape b) { return a.cls && a.cls == b.cls && a.count == b.count; }

    // The operand types of the arithmetic, logic and conversion opcodes against their result: what spirv-val checks, so a driver's
    // compiler never sees an ill-typed instruction from a hook.
    std::string check_shapes(const Inst& in) const {
        const uint32_t op = m_.op(in);
        const uint32_t rt = m_.word(in, 1);
        const Shape r = shape_of_type(rt);
        auto v = [&](uint32_t k) { return m_.word(in, 2 + k); };  // k-th value operand, from 1
        const uint32_t n = in.wc - 3;
        auto all_type = [&](uint32_t from) {
            for (uint32_t k = from; k <= n; ++k)
                if (value_type(v(k)) != rt) return false;
            return true;
        };
        auto all_shape = [&](char cls, uint32_t from, uint32_t to) {
            for (uint32_t k = from; k <= to; ++k)
                if (!same(shape(v(k)), Shape{cls, r.count})) return false;
            return true;
        };
        const std::string bad = "operands of the wrong type for opcode " + std::to_string(op);
        switch (op) {
            case 127: case 129: case 131: case 133: case 136: case 140: case 141:  // float arithmetic
            case 116:                                                              // QuantizeToF16
                return r.cls == 'F' && all_type(1) ? "" : bad;
            case 126: case 128: case 130: case 132: case 134: case 135: case 137: case 138: case 139:  // int arithmetic
            case 194: case 195: case 196: case 197: case 198: case 199: case 200: case 204: case 205:  // shifts, bitwise
                return r.cls == 'I' && all_shape('I', 1, n) ? "" : bad;
            case 201:  // BitFieldInsert base insert offset count
                return r.cls == 'I' && all_shape('I', 1, 2) && same(shape(v(3)), {'I', 1}) && same(shape(v(4)), {'I', 1}) ? "" : bad;
            case 202: case 203:
                return r.cls == 'I' && all_shape('I', 1, 1) && same(shape(v(2)), {'I', 1}) && same(shape(v(3)), {'I', 1}) ? "" : bad;
            case 164: case 165: case 166: case 167: case 168:  // logical
                return r.cls == 'B' && all_type(1) ? "" : bad;
            case 154: case 155: {  // Any, All
                const Shape a = shape(v(1));
                return r.cls == 'B' && r.count == 1 && a.cls == 'B' ? "" : bad;
            }
            case 156: case 157:  // IsNan, IsInf
                return r.cls == 'B' && all_shape('F', 1, 1) ? "" : bad;
            case 169: {  // Select
                const Shape c = shape(v(1));
                return c.cls == 'B' && (c.count == 1 || c.count == r.count) && value_type(v(2)) == rt && value_type(v(3)) == rt && r.cls ? "" : bad;
            }
            case 109: case 110: return r.cls == 'I' && all_shape('F', 1, 1) ? "" : bad;  // ConvertFTo[US]
            case 111: case 112: return r.cls == 'F' && all_shape('I', 1, 1) ? "" : bad;  // Convert[SU]ToF
            case 113: case 114: return r.cls == 'I' && all_shape('I', 1, 1) ? "" : bad;  // [US]Convert
            case 115: return r.cls == 'F' && all_shape('F', 1, 1) ? "" : bad;             // FConvert
            case 124: {  // Bitcast: same width (everything is 32-bit), no pointers (checked before)
                const Shape a = shape(v(1));
                return r.cls && r.cls != 'B' && a.cls && a.cls != 'B' && a.count == r.count ? "" : bad;
            }
            case 83:  // CopyObject
                return value_type(v(1)) == rt ? "" : bad;
            case 142: {  // VectorTimesScalar
                const Shape s = shape(v(2));
                return r.cls == 'F' && r.count > 1 && value_type(v(1)) == rt && s.cls == 'F' && s.count == 1 ? "" : bad;
            }
            case 148: {  // Dot
                const Shape a = shape(v(1));
                return r.cls == 'F' && r.count == 1 && a.cls == 'F' && a.count > 1 && value_type(v(1)) == value_type(v(2)) ? "" : bad;
            }
            case 143: case 144: case 145: case 146: case 147: case 84: {  // matrix products, Transpose: float matrices and vectors
                // Dimensions are (rows, columns), with a vector treated as one column.
                auto dims = [&](uint32_t id) -> std::pair<uint32_t, uint32_t> {
                    const Type* t = type(id);
                    if (t && t->op == OpTypeMatrix) return {type(t->w[0])->w[1], t->w[1]};
                    const Shape s = shape_of_type(id);
                    return s.cls == 'F' ? std::pair{s.count, 1u} : std::pair{0u, 0u};
                };
                auto matrix = [&](uint32_t id) { return type(id)->op == OpTypeMatrix; };
                const uint32_t a = value_type(v(1));
                const auto [rr, rc] = dims(rt);
                const auto [ar, ac] = dims(a);
                if (op == 84) return matrix(rt) && matrix(a) && rr == ac && rc == ar ? "" : bad;
                const uint32_t b = value_type(v(2));
                const auto [br, bc] = dims(b);
                switch (op) {
                    case 143: return matrix(rt) && a == rt && br == 1 && bc == 1 ? "" : bad;
                    case 144: return r.cls == 'F' && rr > 1 && ac == 1 && ar > 1 && matrix(b) && ar == br && rr == bc ? "" : bad;
                    case 145: return r.cls == 'F' && rr > 1 && matrix(a) && !matrix(b) && br > 1 && ac == br && rr == ar ? "" : bad;
                    case 146: return matrix(rt) && matrix(a) && matrix(b) && ac == br && rr == ar && rc == bc ? "" : bad;
                    case 147: return matrix(rt) && !matrix(a) && !matrix(b) && ar > 1 && br > 1 && rr == ar && rc == br ? "" : bad;
                }
                return bad;
            }
            default:
                if (op >= 170 && op <= 179) return r.cls == 'B' && all_shape('I', 1, 2) ? "" : bad;   // int comparisons
                if (op >= 180 && op <= 191) return r.cls == 'B' && all_shape('F', 1, 2) ? "" : bad;   // float comparisons
                return "";
        }
    }

    // GLSL.std.450: the instructions a hook may use, with their operand count and types (no pointer forms, no interpolation, no doubles).
    std::string check_glsl(const Inst& in) const {
        const uint32_t g = m_.word(in, 4), rt = m_.word(in, 1), n = in.wc - 5;
        const Shape r = shape_of_type(rt);
        auto arg = [&](uint32_t k) { return m_.word(in, 4 + k); };  // from 1
        const std::string bad = "GLSL.std.450 instruction " + std::to_string(g) + " with operands of the wrong type or number";
        auto same_type = [&](char cls, uint32_t arity) {
            if (n != arity || r.cls != cls) return false;
            for (uint32_t k = 1; k <= n; ++k)
                if (value_type(arg(k)) != rt) return false;
            return true;
        };
        if ((g >= 1 && g <= 4) || g == 6 || (g >= 8 && g <= 24) || (g >= 27 && g <= 32) || g == 69) return same_type('F', 1) ? "" : bad;
        if (g == 5 || g == 7 || (g >= 73 && g <= 75)) return same_type('I', 1) ? "" : bad;
        if (g == 25 || g == 26 || g == 37 || g == 40 || g == 48 || g == 71 || g == 79 || g == 80) return same_type('F', 2) ? "" : bad;
        if (g == 38 || g == 39 || g == 41 || g == 42) return same_type('I', 2) ? "" : bad;
        if (g == 43 || g == 46 || g == 49 || g == 50 || g == 70 || g == 81) return same_type('F', 3) ? "" : bad;
        if (g == 44 || g == 45) return same_type('I', 3) ? "" : bad;
        switch (g) {
            case 66:  // Length
                return n == 1 && r.cls == 'F' && r.count == 1 && shape(arg(1)).cls == 'F' ? "" : bad;
            case 67:  // Distance
                return n == 2 && r.cls == 'F' && r.count == 1 && shape(arg(1)).cls == 'F' && value_type(arg(1)) == value_type(arg(2)) ? "" : bad;
            case 68:  // Cross
                return same_type('F', 2) && r.count == 3 ? "" : bad;
            case 72:  // Refract I N eta
                return n == 3 && r.cls == 'F' && value_type(arg(1)) == rt && value_type(arg(2)) == rt && same(shape(arg(3)), {'F', 1}) ? "" : bad;
            case 53:  // Ldexp x exp
                return n == 2 && r.cls == 'F' && value_type(arg(1)) == rt && same(shape(arg(2)), {'I', r.count}) ? "" : bad;
            case 58:  // PackHalf2x16
                return n == 1 && same(r, {'I', 1}) && same(shape(arg(1)), {'F', 2}) ? "" : bad;
            case 62:  // UnpackHalf2x16
                return n == 1 && same(r, {'F', 2}) && same(shape(arg(1)), {'I', 1}) ? "" : bad;
            default: break;
        }
        return "GLSL.std.450 instruction " + std::to_string(g) + " is outside what a hook module may use";
    }

    // A composite (constant or built) from parts of the right types and number.
    std::string check_composite(uint32_t rt, const uint32_t* parts, uint32_t n) const {
        const Type* r = type(rt);
        if (!r) return "a composite of an unknown type";
        if (r->op == OpTypeStruct) {
            if (n != r->w.size()) return "a struct built from the wrong number of members";
            for (uint32_t k = 0; k < n; ++k)
                if (value_type(parts[k]) != r->w[k]) return "a struct member of the wrong type";
        } else if (r->op == OpTypeArray || r->op == OpTypeMatrix) {
            const uint32_t count = r->op == OpTypeArray ? m_.constant.at(r->w[1]) : r->w[1];
            if (n != count) return "an array or matrix built from the wrong number of parts";
            for (uint32_t k = 0; k < n; ++k)
                if (value_type(parts[k]) != r->w[0]) return "an array or matrix part of the wrong type";
        } else if (r->op == OpTypeVector) {
            uint32_t total = 0;
            for (uint32_t k = 0; k < n; ++k) {
                const uint32_t t = value_type(parts[k]);
                const Type* ty = type(t);
                if (t == r->w[0]) ++total;
                else if (ty && ty->op == OpTypeVector && ty->w[0] == r->w[0]) total += ty->w[1];
                else return "a vector built from parts of the wrong type";
            }
            if (total != r->w[1]) return "a vector built from the wrong number of components";
        } else {
            return "a composite of a scalar type";
        }
        return "";
    }

    // Each use of a value is dominated by its definition (phi operands: by the end of the matching predecessor). Unreachable blocks are
    // not checked: they never run.
    std::string check_dominance(const Function& f) const {
        const size_t nb = f.blocks.size();
        std::unordered_map<uint32_t, size_t> index;
        for (size_t i = 0; i < nb; ++i) index[f.blocks[i].label] = i;
        std::vector<std::vector<size_t>> preds(nb);
        for (size_t i = 0; i < nb; ++i)
            for (uint32_t s : f.blocks[i].succ) preds[index.at(s)].push_back(i);
        // reverse postorder from the entry
        std::vector<size_t> order;
        std::vector<int> rpo(nb, -1);
        std::vector<uint8_t> seen(nb, 0);
        std::vector<std::pair<size_t, size_t>> stack{{0, 0}};
        seen[0] = 1;
        while (!stack.empty()) {
            auto& [b, next] = stack.back();
            if (next < f.blocks[b].succ.size()) {
                const size_t s = index.at(f.blocks[b].succ[next++]);
                if (!seen[s]) {
                    seen[s] = 1;
                    stack.push_back({s, 0});
                }
                continue;
            }
            order.push_back(b);
            stack.pop_back();
        }
        std::reverse(order.begin(), order.end());
        for (size_t i = 0; i < order.size(); ++i) rpo[order[i]] = static_cast<int>(i);
        // Cooper, Harvey, Kennedy: "A Simple, Fast Dominance Algorithm"
        std::vector<int> idom(nb, -1);
        idom[0] = 0;
        auto intersect = [&](int a, int b) {
            while (a != b) {
                while (rpo[a] > rpo[b]) a = idom[a];
                while (rpo[b] > rpo[a]) b = idom[b];
            }
            return a;
        };
        for (bool changed = true; changed;) {
            changed = false;
            for (size_t i = 1; i < order.size(); ++i) {
                const size_t b = order[i];
                int nd = -1;
                for (size_t p : preds[b]) {
                    if (rpo[p] < 0 || idom[p] < 0) continue;
                    nd = nd < 0 ? static_cast<int>(p) : intersect(static_cast<int>(p), nd);
                }
                if (nd >= 0 && idom[b] != nd) {
                    idom[b] = nd;
                    changed = true;
                }
            }
        }
        auto dominates = [&](int a, int b) {  // block a dominates block b (both reachable)
            for (int x = b;; x = idom[x]) {
                if (x == a) return true;
                if (x == 0) return a == 0;
            }
        };
        auto defined_before = [&](uint32_t id, int use_block, uint32_t use_pos, bool at_end) -> bool {
            const auto l = m_.loc.find(id);
            if (l == m_.loc.end() || l->second.block < 0) return true;  // a global or a parameter
            const int db = l->second.block;
            if (rpo[db] < 0) return false;                              // defined where nothing runs
            if (db == use_block) return at_end || l->second.pos < use_pos;
            return dominates(db, use_block);
        };
        for (size_t bi = 0; bi < nb; ++bi) {
            const Block& b = f.blocks[bi];
            for (size_t pos = 0; pos < b.body.size(); ++pos) {
                const Inst& in = b.body[pos];
                if (m_.op(in) == OpPhi) {
                    std::set<size_t> incoming;
                    for (uint32_t k = 3; k + 1 < in.wc; k += 2) {
                        const auto pi = index.find(m_.word(in, k + 1));
                        if (pi == index.end()) return "a phi naming a block that isn't a predecessor";
                        if (std::find(preds[bi].begin(), preds[bi].end(), pi->second) == preds[bi].end()) return "a phi naming a block that isn't a predecessor";
                        if (!incoming.insert(pi->second).second) return "a phi repeats a predecessor";
                        if (rpo[pi->second] >= 0 && !defined_before(m_.word(in, k), static_cast<int>(pi->second), 0, true))
                            return "a phi value not defined on its edge";
                    }
                    const std::set<size_t> expected(preds[bi].begin(), preds[bi].end());
                    if (incoming != expected) return "a phi without one value for each predecessor";
                    continue;
                }
                if (rpo[bi] < 0) continue;
                for (size_t k = 0; k < in.kinds.size(); ++k)
                    if (in.kinds[k] == 'V' && !defined_before(m_.word(in, static_cast<uint32_t>(k + 1)), static_cast<int>(bi), static_cast<uint32_t>(pos), false))
                        return "a value used where its definition doesn't dominate";
            }
        }
        return "";
    }

    // Every cycle passes a loop header: a DFS back edge must target a block with OpLoopMerge. Loop headers get the iteration cap.
    std::string check_loops(const Function& f) {
        std::unordered_map<uint32_t, size_t> index;
        for (size_t i = 0; i < f.blocks.size(); ++i) index[f.blocks[i].label] = i;
        std::vector<uint8_t> state(f.blocks.size(), 0);  // 0 new, 1 on the stack, 2 done
        std::vector<std::pair<size_t, size_t>> stack;     // block, next successor
        stack.push_back({0, 0});
        state[0] = 1;
        while (!stack.empty()) {
            auto& [b, next] = stack.back();
            const Block& blk = f.blocks[b];
            if (next == blk.succ.size()) {
                state[b] = 2;
                stack.pop_back();
                continue;
            }
            const size_t s = index.at(blk.succ[next++]);
            if (state[s] == 1) {
                if (!f.blocks[s].header) return "a cycle in its control flow that doesn't pass a loop header (OpLoopMerge)";
            } else if (state[s] == 0) {
                state[s] = 1;
                stack.push_back({s, 0});
            }
        }
        // Past the cap every header branches to its merge block. That ends for sure only if the graph where each header has just that one
        // edge has no cycle: true of any structured loop (its body is entered only through its header), checked here for any module.
        std::vector<std::vector<size_t>> capped(f.blocks.size());
        for (size_t i = 0; i < f.blocks.size(); ++i) {
            const Block& b = f.blocks[i];
            if (!b.header) {
                for (uint32_t s : b.succ) capped[i].push_back(index.at(s));
                continue;
            }
            const Inst& t = b.body.back();
            const uint32_t top = m_.op(t);
            const uint32_t mlabel = m_.word(b.body[b.body.size() - 2], 1);
            capped[i].push_back(index.at(mlabel));
            if (top == OpBranch) continue;
            if (top == OpBranchConditional && (m_.word(t, 2) == mlabel || m_.word(t, 3) == mlabel)) continue;
            return "a loop header whose branch Kiln can't cap (it should branch into the loop, or conditionally to its merge block)";
        }
        std::fill(state.begin(), state.end(), 0);
        for (size_t root = 0; root < f.blocks.size(); ++root) {
            if (state[root]) continue;
            stack.assign(1, {root, 0});
            state[root] = 1;
            while (!stack.empty()) {
                auto& [b, next] = stack.back();
                if (next == capped[b].size()) {
                    state[b] = 2;
                    stack.pop_back();
                    continue;
                }
                const size_t s = capped[b][next++];
                if (state[s] == 1) return "a loop whose merge block leads back into it without passing an enclosing loop's header";
                if (state[s] == 0) {
                    state[s] = 1;
                    stack.push_back({s, 0});
                }
            }
        }
        return "";
    }

    std::string check_calls() {
        // no recursion: the call graph is a DAG
        std::vector<uint8_t> state(m_.funcs.size(), 0);
        std::vector<uint32_t> depth(m_.funcs.size(), 0);
        std::vector<uint64_t> frames(m_.funcs.size(), 0);
        std::function<std::string(size_t, uint32_t)> visit = [&](size_t i, uint32_t active) -> std::string {
            if (state[i] == 1) return "recursion: a function calls itself, directly or through others";
            if (active > kMaxCallDepth) return "call depth exceeds " + std::to_string(kMaxCallDepth);
            if (state[i] == 2) return "";
            state[i] = 1;
            uint32_t child_depth = 0;
            uint64_t child_frame = 0;
            for (uint32_t c : m_.funcs[i].callees) {
                const size_t ci = m_.func_index.at(c);
                if (std::string why = visit(ci, active + 1); !why.empty()) return why;
                child_depth = std::max(child_depth, depth[ci]);
                child_frame = std::max(child_frame, frames[ci]);
            }
            depth[i] = child_depth + 1;
            frames[i] = child_frame + m_.funcs[i].locals;
            if (depth[i] > kMaxCallDepth) return "call depth exceeds " + std::to_string(kMaxCallDepth);
            if (frames[i] > kMaxFunctionLocals) return "local variables on a call stack exceed " + std::to_string(kMaxFunctionLocals) + " bytes";
            state[i] = 2;
            return "";
        };
        for (size_t i = 0; i < m_.funcs.size(); ++i)
            if (std::string why = visit(i, 1); !why.empty()) return why;
        return "";
    }
};

// ---- the link ------------------------------------------------------------------------------------------------------------------------

const char* const kSlots[3] = {"kiln_hook_spawn(", "kiln_hook_update(", "kiln_hook_draw("};
const char* const kSlotNames[3] = {"spawn", "update", "draw"};

class Linker {
public:
    Linker(SpvModule& wrap, SpvModule& hook) : w_(wrap), h_(hook) {}

    std::string run(Bytes* out, HookLinkInfo* info) {
        next_ = w_.bound;
        std::string why;
        // the wrapper's link points
        uint32_t stub[3] = {0, 0, 0};
        for (const Function& f : w_.funcs) {
            const auto it = w_.names.find(f.id);
            if (it == w_.names.end()) continue;
            for (int s = 0; s < 3; ++s)
                if (it->second.rfind(kSlots[s], 0) == 0) stub[s] = f.id;
        }
        if (!stub[0] || !stub[1] || !stub[2]) return "Kiln's linkable sprite has no link points (a broken build of Kiln)";
        // the hook module's entries, by name: anything verified is pure, so a name is only a lookup
        uint32_t hook[3] = {0, 0, 0};
        for (const Function& f : h_.funcs) {
            const auto it = h_.names.find(f.id);
            if (it == h_.names.end()) continue;
            for (int s = 0; s < 3; ++s)
                if (it->second.rfind(kSlots[s], 0) == 0) {
                    if (hook[s]) return std::string("two functions named ") + kSlots[s] + ")";
                    hook[s] = f.id;
                }
        }
        if (!hook[0] && !hook[1] && !hook[2]) return "defines none of kiln_hook_spawn, kiln_hook_update, kiln_hook_draw (define KILN_SPAWN, KILN_UPDATE or KILN_DRAW before including kiln_hooks_main.hlsl)";

        // wrapper type table for dedupe
        std::set<uint32_t> decorated;
        for (const Inst& a : w_.annotations) decorated.insert(w_.word(a, 1));
        for (const Inst& g : w_.globals) {
            const uint32_t op = w_.op(g);
            if (op < OpTypeVoid || op > OpTypeFunction) continue;
            const uint32_t id = w_.word(g, 1);
            if (decorated.count(id) && (op == OpTypeStruct || op == OpTypeArray)) continue;
            type_table_[key(op, w_.w + g.at + 2, g.wc - 2)] = id;
        }
        map_[h_.glsl] = w_.glsl;
        if (!w_.glsl) return "Kiln's linkable sprite has no GLSL.std.450 import";

        // hook globals: types deduped, constants and variables renumbered
        Words globals;
        for (const Inst& g : h_.globals) {
            const uint32_t op = h_.op(g);
            Words ins(h_.w + g.at, h_.w + g.at + g.wc);
            if (op >= OpTypeVoid && op <= OpTypeFunction) {
                for (size_t k = 1; k < g.kinds.size(); ++k)
                    if (g.kinds[k] == 'T' || g.kinds[k] == 'V') ins[k + 1] = mapped(ins[k + 1]);
                const std::string kk = key(op, ins.data() + 2, g.wc - 2);
                const auto found = type_table_.find(kk);
                if (found != type_table_.end()) {
                    map_[h_.word(g, 1)] = found->second;
                    continue;
                }
                const uint32_t id = fresh();
                map_[h_.word(g, 1)] = id;
                ins[1] = id;
                type_table_[kk] = id;
                globals.insert(globals.end(), ins.begin(), ins.end());
                continue;
            }
            remap(g, ins);
            if (op == OpVariable) privates_.push_back(ins[2]);
            globals.insert(globals.end(), ins.begin(), ins.end());
        }
        // signatures of the hooks against Kiln's link points
        for (int s = 0; s < 3; ++s) {
            if (!hook[s]) continue;
            const Function& hf = h_.funcs[h_.func_index.at(hook[s])];
            const Function& wf = w_.funcs[w_.func_index.at(stub[s])];
            if (mapped(hf.type) != wf.type)
                return std::string(kSlots[s]) + ") has another signature than Kiln's (built against another kiln_hooks.hlsl?)";
            info->hooks.push_back(kSlotNames[s]);
        }

        // what is reachable from the hooks, and its straight-line cost
        std::vector<uint8_t> reach(h_.funcs.size(), 0);
        std::function<void(size_t)> mark = [&](size_t i) {
            if (reach[i]) return;
            reach[i] = 1;
            for (uint32_t c : h_.funcs[i].callees) mark(h_.func_index.at(c));
        };
        for (uint32_t id : hook)
            if (id) mark(h_.func_index.at(id));
        std::vector<uint64_t> cost(h_.funcs.size(), 0);
        std::vector<uint8_t> costed(h_.funcs.size(), 0);
        std::function<uint64_t(size_t)> cost_of = [&](size_t i) -> uint64_t {
            if (costed[i]) return cost[i];
            uint64_t c = h_.funcs[i].own;
            for (const Block& b : h_.funcs[i].blocks)
                for (const Inst& in : b.body)
                    if (h_.op(in) == OpFunctionCall) c += cost_of(h_.func_index.at(h_.word(in, 3)));
                    else if (h_.op(in) == OpLoopMerge) c += 6;  // counter load/add/store/compare, plus up to two condition operations
                    else if (h_.op(in) == OpVectorExtractDynamic || h_.op(in) == OpVectorInsertDynamic) {
                        if (!h_.constant.count(h_.word(in, h_.op(in) == OpVectorExtractDynamic ? 4 : 5))) ++c;
                    } else if (h_.op(in) == OpAccessChain || h_.op(in) == OpInBoundsAccessChain) {
                        for (uint32_t k = 4; k < in.wc; ++k)
                            if (!h_.constant.count(h_.word(in, k))) ++c;  // one emitted UMin per computed index
                    }
            c = std::min<uint64_t>(c, 1ull << 40);
            costed[i] = 1;
            return cost[i] = c;
        };
        uint64_t root = 1;
        bool loops = false;
        for (uint32_t id : hook)
            if (id) root = std::max(root, cost_of(h_.func_index.at(id)));
        for (size_t i = 0; i < h_.funcs.size(); ++i)
            if (reach[i])
                for (const Block& b : h_.funcs[i].blocks)
                    if (b.header) {
                        loops = true;
                        ++info->loops;
                    }
        info->cost = root;
        if (root > kHookBudget)
            return "its hooks are " + std::to_string(root) + " instructions long counting every call (at most " + std::to_string(kHookBudget) + "): make them smaller";
        cap_ = loops ? std::max<uint64_t>(1, kHookBudget / root) : 0;
        info->loop_cap = static_cast<uint32_t>(cap_);

        // Kiln's bits: the counter and the constants the instrumentation needs
        uint_t_ = type_id(OpTypeInt, {32, 0}, &globals);
        bool_t_ = type_id(OpTypeBool, {}, &globals);
        const uint32_t ptr_uint = type_id(OpTypePointer, {kPrivate, uint_t_}, &globals);
        zero_ = constant(uint_t_, 0, &globals);
        one_ = constant(uint_t_, 1, &globals);
        if (cap_) {
            cap_c_ = constant(uint_t_, static_cast<uint32_t>(cap_), &globals);
            counter_ = fresh();
            globals.insert(globals.end(), {5u << 16 | OpVariable, ptr_uint, counter_, kPrivate, zero_});
            privates_.push_back(counter_);
        }

        // hook functions
        Words functions;
        for (size_t i = 0; i < h_.funcs.size(); ++i)
            if (reach[i]) {
                std::string why2 = emit_function(h_.funcs[i], &functions, &globals);
                if (!why2.empty()) return why2;
            }
        info->clamped = clamped_;

        // assemble: the wrapper with its stubs turned into trampolines, the hook globals after its own, the hook functions last
        Words o(w_.w, w_.w + 5);
        auto copy = [&](const Inst& in) { o.insert(o.end(), w_.w + in.at, w_.w + in.at + in.wc); };
        size_t head_end = 5;
        for (size_t i = 5; i < w_.n;) {
            const uint32_t op = w_.w[i] & 0xffff, wc = w_.w[i] >> 16;
            if (op == OpEntryPoint) {
                Words ep(w_.w + i, w_.w + i + wc);
                ep.insert(ep.end(), privates_.begin(), privates_.end());
                ep[0] = static_cast<uint32_t>(ep.size()) << 16 | OpEntryPoint;
                o.insert(o.end(), ep.begin(), ep.end());
            } else if (op == OpCapability || op == OpExtension || op == OpExtInstImport || op == OpMemoryModel || op == OpExecutionMode ||
                       op == OpExecutionModeId || op == OpString || op == OpSource || op == OpSourceContinued || op == OpSourceExtension ||
                       op == OpName || op == OpMemberName || op == OpModuleProcessed || op == OpDecorate || op == OpMemberDecorate ||
                       op == OpDecorateId || op == OpDecorateString || op == OpMemberDecorateString || op == OpDecorationGroup ||
                       op == OpGroupDecorate || op == OpGroupMemberDecorate) {
                o.insert(o.end(), w_.w + i, w_.w + i + wc);
            } else {
                head_end = i;
                break;
            }
            i += wc;
        }
        (void)head_end;
        for (const Inst& g : w_.globals) copy(g);
        o.insert(o.end(), globals.begin(), globals.end());
        for (const Function& f : w_.funcs) {
            int slot = -1;
            for (int s = 0; s < 3; ++s)
                if (f.id == stub[s] && hook[s]) slot = s;
            if (slot < 0) {
                copy(f.def);
                for (const Inst& p : f.params) copy(p);
                for (const Block& b : f.blocks) {
                    o.insert(o.end(), {2u << 16 | OpLabel, b.label});
                    for (const Inst& in : b.body) copy(in);
                }
                o.push_back(1u << 16 | OpFunctionEnd);
                continue;
            }
            // trampoline: reset the loop counter, call the hook with the same arguments
            o.insert(o.end(), {5u << 16 | OpFunction, f.ret, f.id, 0u, f.type});
            Words args;
            for (const Inst& p : f.params) {
                copy(p);
                args.push_back(w_.word(p, 2));
            }
            o.insert(o.end(), {2u << 16 | OpLabel, fresh()});
            if (cap_) o.insert(o.end(), {3u << 16 | OpStore, counter_, zero_});
            Words call = {static_cast<uint32_t>(4 + args.size()) << 16 | OpFunctionCall, f.ret, fresh(), map_.at(hook[slot])};
            call.insert(call.end(), args.begin(), args.end());
            o.insert(o.end(), call.begin(), call.end());
            o.push_back(1u << 16 | OpReturn);
            o.push_back(1u << 16 | OpFunctionEnd);
        }
        o.insert(o.end(), functions.begin(), functions.end());
        o[3] = next_;
        if (o.size() * 4 > kMaxModModuleBytes) return "the linked module would be larger than " + std::to_string(kMaxModModuleBytes) + " bytes";
        out->resize(o.size() * 4);
        std::memcpy(out->data(), o.data(), out->size());
        return "";
    }

private:
    SpvModule& w_;
    SpvModule& h_;
    uint32_t next_ = 0;
    std::unordered_map<uint32_t, uint32_t> map_;  // hook id -> output id
    std::map<std::string, uint32_t> type_table_;
    std::map<std::pair<uint32_t, uint32_t>, uint32_t> constants_;
    std::vector<uint32_t> privates_;
    uint32_t uint_t_ = 0, bool_t_ = 0, zero_ = 0, one_ = 0, cap_c_ = 0, counter_ = 0;
    uint64_t cap_ = 0;
    uint32_t clamped_ = 0;

    uint32_t fresh() { return next_++; }
    uint32_t mapped(uint32_t hid) {
        const auto it = map_.find(hid);
        if (it != map_.end()) return it->second;
        const uint32_t id = fresh();
        map_[hid] = id;
        return id;
    }
    static std::string key(uint32_t op, const uint32_t* w, size_t n) {
        std::string k = std::to_string(op);
        for (size_t i = 0; i < n; ++i) k += "," + std::to_string(w[i]);
        return k;
    }
    uint32_t type_id(uint32_t op, std::vector<uint32_t> operands, Words* globals) {
        const std::string k = key(op, operands.data(), operands.size());
        const auto it = type_table_.find(k);
        if (it != type_table_.end()) return it->second;
        const uint32_t id = fresh();
        globals->push_back(static_cast<uint32_t>(2 + operands.size()) << 16 | op);
        globals->push_back(id);
        globals->insert(globals->end(), operands.begin(), operands.end());
        type_table_[k] = id;
        return id;
    }
    uint32_t constant(uint32_t type, uint32_t value, Words* globals) {
        const auto it = constants_.find({type, value});
        if (it != constants_.end()) return it->second;
        const uint32_t id = fresh();
        globals->insert(globals->end(), {4u << 16 | OpConstant, type, id, value});
        constants_[{type, value}] = id;
        return id;
    }
    void remap(const Inst& in, Words& ins) {
        for (size_t k = 0; k < in.kinds.size(); ++k) {
            const char c = in.kinds[k];
            if (c == 'T' || c == 'V' || c == 'L' || c == 'F' || c == 'X' || c == 'R') ins[k + 1] = mapped(ins[k + 1]);
        }
    }

    // A dynamic index clamped to count-1, before `out` gets the instruction that uses it.
    uint32_t clamp(uint32_t hidx, uint32_t count, Words* out, Words* globals) {
        const uint32_t t = mapped(h_.type_of.at(hidx));
        const uint32_t limit = constant(t, count - 1, globals);
        const uint32_t id = fresh();
        out->insert(out->end(), {7u << 16 | OpExtInst, t, id, w_.glsl, kGlslUMin, mapped(hidx), limit});
        ++clamped_;
        return id;
    }

    std::string emit_function(const Function& f, Words* out, Words* globals) {
        Words def(h_.w + f.def.at, h_.w + f.def.at + f.def.wc);
        def[1] = mapped(def[1]);
        def[2] = mapped(def[2]);
        def[3] = 0;  // no control hints
        def[4] = mapped(def[4]);
        out->insert(out->end(), def.begin(), def.end());
        for (const Inst& p : f.params) out->insert(out->end(), {3u << 16 | OpFunctionParameter, mapped(h_.word(p, 1)), mapped(h_.word(p, 2))});
        for (const Block& b : f.blocks) {
            out->insert(out->end(), {2u << 16 | OpLabel, mapped(b.label)});
            for (size_t i = 0; i < b.body.size(); ++i) {
                const Inst& in = b.body[i];
                const uint32_t op = h_.op(in);
                Words ins(h_.w + in.at, h_.w + in.at + in.wc);
                if (op == OpLoopMerge && cap_) {
                    // counter += 1; ok = counter <= cap. Past the cap the header branches to its merge block (check_loops proved that ends).
                    const uint32_t c0 = fresh(), c1 = fresh(), ok = fresh();
                    out->insert(out->end(), {4u << 16 | OpLoad, uint_t_, c0, counter_});
                    out->insert(out->end(), {5u << 16 | 128u /*OpIAdd*/, uint_t_, c1, c0, one_});
                    out->insert(out->end(), {3u << 16 | OpStore, counter_, c1});
                    out->insert(out->end(), {5u << 16 | 178u /*OpULessThanEqual*/, bool_t_, ok, c1, cap_c_});
                    const Inst& t = b.body[i + 1];
                    const uint32_t merge = mapped(h_.word(in, 1));
                    Words branch;
                    if (h_.op(t) == OpBranch) {
                        branch = {4u << 16 | OpBranchConditional, ok, mapped(h_.word(t, 1)), merge};
                    } else {  // OpBranchConditional c A B with A or B the merge block
                        const uint32_t c = mapped(h_.word(t, 1)), ta = mapped(h_.word(t, 2)), tb = mapped(h_.word(t, 3));
                        const uint32_t joined = fresh();
                        if (tb == merge) {
                            out->insert(out->end(), {5u << 16 | 167u /*OpLogicalAnd*/, bool_t_, joined, c, ok});
                        } else {
                            const uint32_t over = fresh();
                            out->insert(out->end(), {4u << 16 | 168u /*OpLogicalNot*/, bool_t_, over, ok});
                            out->insert(out->end(), {5u << 16 | 166u /*OpLogicalOr*/, bool_t_, joined, c, over});
                        }
                        branch = {4u << 16 | OpBranchConditional, joined, ta, tb};
                    }
                    remap(in, ins);
                    out->insert(out->end(), ins.begin(), ins.end());
                    out->insert(out->end(), branch.begin(), branch.end());
                    break;  // the terminator is done
                }
                if (op == OpAccessChain || op == OpInBoundsAccessChain) {
                    uint32_t t = h_.types.at(h_.type_of.at(h_.word(in, 3))).w[1];
                    for (uint32_t k = 4; k < in.wc; ++k) {
                        const uint32_t idx = h_.word(in, k);
                        const Type& ty = h_.types.at(t);
                        const bool dynamic = !h_.constant.count(idx);
                        if (ty.op == OpTypeStruct) {
                            t = ty.w[h_.constant.at(idx)];
                            ins[k] = mapped(idx);
                            continue;
                        }
                        const uint32_t count = ty.op == OpTypeArray ? h_.constant.at(ty.w[1]) : ty.w[1];
                        ins[k] = dynamic ? clamp(idx, count, out, globals) : mapped(idx);
                        t = ty.w[0];
                    }
                    ins[1] = mapped(ins[1]);
                    ins[2] = mapped(ins[2]);
                    ins[3] = mapped(ins[3]);
                    out->insert(out->end(), ins.begin(), ins.end());
                    continue;
                }
                if (op == OpVectorExtractDynamic || op == OpVectorInsertDynamic) {
                    const uint32_t vec = h_.word(in, 3);
                    const uint32_t count = h_.types.at(h_.type_of.at(vec)).w[1];
                    const uint32_t at = op == OpVectorExtractDynamic ? 4 : 5;
                    const uint32_t idx = h_.word(in, at);
                    remap(in, ins);
                    if (!h_.constant.count(idx)) ins[at] = clamp(idx, count, out, globals);
                    else if (h_.constant.at(idx) >= count) return "a constant vector index out of range";
                    out->insert(out->end(), ins.begin(), ins.end());
                    continue;
                }
                if (op == OpUnreachable) {
                    // A capped loop leaves through its merge block, which the compiler marks unreachable after an endless loop: running
                    // OpUnreachable is undefined, so it returns instead.
                    if (h_.types.at(f.ret).op == OpTypeVoid) {
                        out->push_back(1u << 16 | OpReturn);
                    } else {
                        const uint32_t undef = fresh();
                        globals->insert(globals->end(), {3u << 16 | OpUndef, mapped(f.ret), undef});
                        out->insert(out->end(), {2u << 16 | OpReturnValue, undef});
                    }
                    continue;
                }
                remap(in, ins);
                out->insert(out->end(), ins.begin(), ins.end());
            }
        }
        out->push_back(1u << 16 | OpFunctionEnd);
        return "";
    }
};

}  // namespace

std::string hook_abi_marker() { return "kiln-vfx-hooks-" + std::to_string(KILN_HOOKS_ABI); }

const ModuleInfo& linkable_sprite() {
    static const ModuleInfo info{"kiln_sprite_hooked", kHooked, sizeof kHooked, content_hash(kHooked, sizeof kHooked)};
    return info;
}

std::string link_hook_module(const uint8_t* data, size_t size, Bytes* linked, HookLinkInfo* info) {
    HookLinkInfo local;
    HookLinkInfo& out = info ? *info : local;
    out = HookLinkInfo{};
    if (linked) linked->clear();
    if (!data || size < 20 || size % 4) return "not whole SPIR-V words";
    if (size > kMaxHookBytes) return "a hook module of " + std::to_string(size) + " bytes (at most " + std::to_string(kMaxHookBytes) + ")";
    Words hw(size / 4);
    std::memcpy(hw.data(), data, size);
    // the stamp
    for (size_t i = 5; i < hw.size();) {
        const uint32_t op = hw[i] & 0xffff, wc = hw[i] >> 16;
        if (wc == 0 || i + wc > hw.size()) return "a broken instruction";
        if (op == OpSourceExtension && wc >= 2) {
            const std::string s = text_of(hw.data() + i + 1, wc - 1);
            if (s.rfind("kiln-vfx-hooks-", 0) == 0) {
                if (!out.abi.empty()) return "has multiple Kiln hook stamps";
                out.abi = printable(s.substr(15));
            } else if (s.rfind("kiln-vfx-abi-", 0) == 0) {
                return "is a whole particle module from an older Kiln (kiln-vfx-abi stamp): rebuild it with this Kiln's kiln-vfxcook shader build, which builds hook modules";
            }
        }
        i += wc;
    }
    if (out.abi.empty()) return "has no Kiln hook stamp: build it with kiln-vfxcook shader build";
    if (out.abi != std::to_string(KILN_HOOKS_ABI))
        return "was built for hook ABI " + out.abi + ", and this Kiln has hook ABI " + std::to_string(KILN_HOOKS_ABI) + ": rebuild it with this Kiln's kiln-vfxcook shader build";
    SpvModule hook;
    std::string why = hook.parse(hw.data(), hw.size(), true);
    if (!why.empty()) return why;
    for (const Function& f : hook.funcs) out.instructions += static_cast<uint32_t>(f.own);
    Verifier v(hook);
    if ((why = v.run()).size()) return why;
    const ModuleInfo& wi = linkable_sprite();
    Words ww(wi.size / 4);
    std::memcpy(ww.data(), wi.data, wi.size);
    SpvModule wrap;
    if ((why = wrap.parse(ww.data(), ww.size(), false)).size()) return "Kiln's linkable sprite doesn't parse (" + why + ")";
    Bytes result;
    Linker l(wrap, hook);
    if ((why = l.run(&result, &out)).size()) return why;
    if ((why = check_module(result.data(), result.size())).size()) return "the linked module fails Kiln's own check (" + why + ")";
    out.linked_size = result.size();
    if (linked) *linked = std::move(result);
    return "";
}

}  // namespace kvb
