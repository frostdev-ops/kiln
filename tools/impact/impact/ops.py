# SPDX-License-Identifier: MIT
"""The Impact VM's 30 opcodes (enum keen::ImpactOps) with the operand counts, stack effects and control flow read
from the interpreter (server 0x990920; client 0x1239960 is the same code). See docs/re/impact.md section 3.

Stack: u32 slots, sp starts at 0xFFFF (empty), a push writes slot (u16)(sp + 1). Values are plain u32s: integers,
booleans (0/1), times in ms, or variable handles 0xFFFF0000 | index into the program's dataLayout.
"""

# kinds of control flow
FALL, JUMP, CJUMP, STOP = "fall", "jump", "cjump", "stop"


class Op:
    __slots__ = ("code", "name", "nops", "pop", "push", "flow", "doc")

    def __init__(self, code, name, nops, pop, push, flow, doc):
        self.code, self.name, self.nops, self.pop, self.push, self.flow, self.doc = \
            code, name, nops, pop, push, flow, doc

    def __repr__(self):
        return f"Op({self.name})"


# pop/push: None = depends on the operand (ECALL: argc of the native; CALL/RET: frame rules)
_TABLE = [
    (1, "IADD", 0, 2, 1, FALL, "a b -> a+b (32-bit wrap)"),
    (2, "ISUB", 0, 2, 1, FALL, "a b -> a-b"),
    (3, "IMUL", 0, 2, 1, FALL, "a b -> a*b"),
    (4, "IDIV", 0, 2, 1, FALL, "a b -> a/b (signed; b = 0 faults the game)"),
    (5, "ILT", 0, 2, 1, FALL, "a b -> a<b (signed) ? 1 : 0"),
    (6, "IEQ", 0, 2, 1, FALL, "a b -> a==b ? 1 : 0"),
    (7, "ILEQ", 0, 2, 1, FALL, "a b -> a<=b (signed) ? 1 : 0"),
    (8, "BR", 1, 0, 0, JUMP, "pc = target"),
    (9, "BRT", 1, 1, 0, CJUMP, "pop c; if c != 0: pc = target"),
    (10, "BRF", 1, 1, 0, CJUMP, "pop c; if c == 0: pc = target"),
    (11, "ICONST", 1, 0, 1, FALL, "push the operand (a value or a variable handle 0xFFFF0000|index)"),
    (12, "ICONST0", 0, 0, 1, FALL, "push 0 (an unconnected native input)"),
    (13, "ICONST1", 0, 0, 1, FALL, "push 1"),
    (14, "INC", 0, 1, 1, FALL, "top += 1"),
    (15, "DEC", 0, 1, 1, FALL, "top -= 1"),
    (16, "COPY", 2, 0, 0, FALL, "copy variable(src name) -> variable(dst name); same type, `size` bytes"),
    (17, "DUP", 0, 1, 2, FALL, "a -> a a"),
    (18, "CALL", 2, None, None, JUMP, "push n, fp, return pc; fp = sp; pc = target (subroutine; unused by vanilla)"),
    (19, "ECALL", 2, None, 1, FALL, "pop argc args (args[0] = top), call native(hash) with the site id; push its result byte"),
    (20, "RET", 0, None, None, STOP, "return to the CALL; the n call arguments become the result (unused by vanilla)"),
    (21, "LOAD", 1, 0, 1, FALL, "push stack[fp + k] (frame slot; unused by vanilla)"),
    (22, "GLOAD", 1, 0, 1, FALL, "push variable(handle) (uint32/sint32/float32 bits, bool 0/1, Time in ms)"),
    (23, "STORE", 1, 1, 0, FALL, "stack[fp + k] = pop (frame slot; unused by vanilla)"),
    (24, "GSTORE", 1, 1, 0, FALL, "variable(handle) = pop (Time from ms)"),
    (25, "LTIME", 0, 0, 1, FALL, "push the ECS time in ms"),
    (26, "TIMEFF", 0, 1, 1, FALL, "top: float32 seconds -> ms"),
    (27, "POP", 0, 1, 0, FALL, "drop the top"),
    (28, "RVM", 0, 0, 0, FALL, "yield: the instance resumes at the next pc on the next step"),
    (29, "DSELF", 0, 0, 0, FALL, "flag Result_DestroyEntity (0x40); execution continues"),
    (30, "HALT", 0, 0, 0, STOP, "flag Halt (0x08): the program ends"),
]

BY_CODE = {c: Op(c, n, k, po, pu, f, d) for c, n, k, po, pu, f, d in _TABLE}
BY_NAME = {o.name: o for o in BY_CODE.values()}

ECALL = 19
FRAME_OPS = {18, 20, 21, 23}          # CALL RET LOAD STORE: unused by the 571 vanilla programs, rejected by lint
BRANCHES = {8, 9, 10}
HANDLE_OPS = {22, 24}                 # GLOAD GSTORE take a variable handle

HANDLE_TAG = 0xFFFF0000               # ICONST/GLOAD/GSTORE variable handle = HANDLE_TAG | index
MAX_STACK = 31                        # 32 slots per instance, slot 31 holds the canary 0xBAADF00D
CANARY = 0xBAADF00D
MAX_ARGC = 31                         # the interpreter's argument buffer (argc < 0x20)

# GLOAD/GSTORE accept these variable types only (type hash = FNV-1a of the impact type name)
LOADABLE_TYPES = {
    0x32940BEC: "uint32", 0x9859F31E: "sint32", 0xE89F7410: "float32", 0xC894953D: "bool", 0xDFE4E404: "Time",
}


def is_handle(v):
    return (v & 0xFFFF0000) == HANDLE_TAG


def handle(index):
    return HANDLE_TAG | (index & 0xFFFF)


def op(code_or_name):
    if isinstance(code_or_name, int):
        return BY_CODE.get(code_or_name)
    return BY_NAME.get(code_or_name.upper())


class Insn:
    """One decoded instruction: pc (word index), opcode, operand words."""
    __slots__ = ("pc", "code", "args")

    def __init__(self, pc, code, args):
        self.pc, self.code, self.args = pc, code, tuple(args)

    @property
    def op(self):
        return BY_CODE.get(self.code)

    @property
    def size(self):
        return 1 + len(self.args)

    def __repr__(self):
        o = self.op
        return f"{self.pc}: {o.name if o else hex(self.code)} {' '.join(hex(a) for a in self.args)}".rstrip()


class DecodeError(Exception):
    pass


def decode(words, strict=True):
    """Words -> [Insn]. strict: an unknown opcode or a truncated operand raises; else they become 1-word Insns
    with code kept (op = None) so a listing can still show them."""
    out = []
    pc = 0
    n = len(words)
    while pc < n:
        c = words[pc]
        o = BY_CODE.get(c)
        if o is None:
            if strict:
                raise DecodeError(f"pc {pc}: unknown opcode {c:#x}")
            out.append(Insn(pc, c, ()))
            pc += 1
            continue
        if pc + o.nops >= n + (0 if o.nops else 1):
            if strict:
                raise DecodeError(f"pc {pc}: {o.name} needs {o.nops} operand word(s), the code ends")
            out.append(Insn(pc, c, words[pc + 1:]))
            break
        out.append(Insn(pc, c, words[pc + 1:pc + 1 + o.nops]))
        pc += 1 + o.nops
    return out


def encode(insns):
    words = []
    for i in insns:
        words.append(i.code)
        words.extend(i.args)
    return words
