# SPDX-License-Identifier: MIT
"""A small read-only SPIR-V reflector (stdlib only): capabilities, entry point, descriptor interface, struct layouts.

For checking shader modules against the game's pipeline layouts (docs/re/vfx.md §6). It decodes only what those checks
need; it is no validator (that is spirv-val's job).
"""
import struct

MAGIC = 0x07230203

OP_EXTENSION = 10
OP_MEMORY_MODEL = 14
OP_ENTRY_POINT = 15
OP_EXECUTION_MODE = 16
OP_CAPABILITY = 17
OP_TYPE_VOID = 19
OP_TYPE_BOOL = 20
OP_TYPE_INT = 21
OP_TYPE_FLOAT = 22
OP_TYPE_VECTOR = 23
OP_TYPE_MATRIX = 24
OP_TYPE_IMAGE = 25
OP_TYPE_SAMPLER = 26
OP_TYPE_SAMPLED_IMAGE = 27
OP_TYPE_ARRAY = 28
OP_TYPE_RUNTIME_ARRAY = 29
OP_TYPE_STRUCT = 30
OP_TYPE_POINTER = 32
OP_CONSTANT = 43
OP_VARIABLE = 59
OP_DECORATE = 71
OP_MEMBER_DECORATE = 72

DEC_BUILTIN = 11
DEC_ARRAY_STRIDE = 6
DEC_BINDING = 33
DEC_DESCRIPTOR_SET = 34
DEC_OFFSET = 35
DEC_BLOCK = 2
DEC_BUFFER_BLOCK = 3

SC_UNIFORM_CONSTANT = 0
SC_UNIFORM = 2
SC_PUSH_CONSTANT = 9
SC_STORAGE_BUFFER = 12

CAPABILITIES = {
    1: "Shader", 9: "Float16", 10: "Float64", 11: "Int64", 22: "Int16", 25: "ImageQuery", 39: "Int8",
    49: "StorageImageExtendedFormats", 50: "ImageQuery", 56: "StorageImageReadWithoutFormat",
    61: "GroupNonUniform", 62: "GroupNonUniformVote", 63: "GroupNonUniformArithmetic", 64: "GroupNonUniformBallot",
    65: "GroupNonUniformShuffle", 4427: "ShaderDrawParameters", 4466: "SignedZeroInfNanPreserve",
    4472: "RayQueryKHR", 5283: "MeshShadingEXT", 5301: "ShaderNonUniform", 5307: "RuntimeDescriptorArray",
}

EXECUTION_LOCAL_SIZE = 17


def _string(words):
    return struct.pack(f"<{len(words)}I", *words).split(b"\0")[0].decode("utf-8", "replace")


class Module:
    def __init__(self, data):
        n = len(data) // 4
        w = struct.unpack(f"<{n}I", bytes(data[:n * 4]))
        if not n or w[0] != MAGIC:
            raise ValueError("not a SPIR-V module")
        self.version = (w[1] >> 16 & 0xff, w[1] >> 8 & 0xff)
        self.generator = w[2]
        self.bound = w[3]
        self.capabilities = set()
        self.extensions = set()
        self.entry_points = []   # (execution model, name)
        self.local_size = None
        self.types = {}          # id -> (kind, ...)
        self.constants = {}      # id -> first word
        self.variables = {}      # id -> (pointer type id, storage class)
        self.dec = {}            # id -> {decoration: first operand}
        self.member_dec = {}     # (id, member) -> {decoration: first operand}
        i = 5
        while i < n:
            op = w[i] & 0xffff
            wc = w[i] >> 16
            if wc == 0:
                raise ValueError(f"zero word count at word {i}")
            a = w[i + 1:i + wc]
            if op == OP_CAPABILITY:
                self.capabilities.add(a[0])
            elif op == OP_EXTENSION:
                self.extensions.add(_string(a))
            elif op == OP_ENTRY_POINT:
                self.entry_points.append((a[0], _string(a[2:])))
            elif op == OP_EXECUTION_MODE:
                if a[1] == EXECUTION_LOCAL_SIZE:
                    self.local_size = tuple(a[2:5])
            elif op == OP_TYPE_VOID:
                self.types[a[0]] = ("void",)
            elif op == OP_TYPE_BOOL:
                self.types[a[0]] = ("bool",)
            elif op == OP_TYPE_INT:
                self.types[a[0]] = ("int", a[1], a[2])
            elif op == OP_TYPE_FLOAT:
                self.types[a[0]] = ("float", a[1])
            elif op == OP_TYPE_VECTOR:
                self.types[a[0]] = ("vec", a[1], a[2])
            elif op == OP_TYPE_MATRIX:
                self.types[a[0]] = ("mat", a[1], a[2])
            elif op == OP_TYPE_IMAGE:
                self.types[a[0]] = ("image", a[1], a[2])
            elif op == OP_TYPE_SAMPLER:
                self.types[a[0]] = ("sampler",)
            elif op == OP_TYPE_SAMPLED_IMAGE:
                self.types[a[0]] = ("sampled", a[1])
            elif op == OP_TYPE_ARRAY:
                self.types[a[0]] = ("array", a[1], a[2])
            elif op == OP_TYPE_RUNTIME_ARRAY:
                self.types[a[0]] = ("rtarray", a[1])
            elif op == OP_TYPE_STRUCT:
                self.types[a[0]] = ("struct", tuple(a[1:]))
            elif op == OP_TYPE_POINTER:
                self.types[a[0]] = ("ptr", a[1], a[2])
            elif op == OP_CONSTANT:
                self.constants[a[1]] = a[2]
            elif op == OP_VARIABLE:
                self.variables[a[1]] = (a[0], a[2])
            elif op == OP_DECORATE:
                self.dec.setdefault(a[0], {})[a[1]] = a[2] if len(a) > 2 else True
            elif op == OP_MEMBER_DECORATE:
                self.member_dec.setdefault((a[0], a[1]), {})[a[2]] = a[3] if len(a) > 3 else True
            i += wc

    def resolve_pointee(self, var):
        ptr, _ = self.variables[var]
        return self.types[ptr][2]

    def bindings(self):
        """{(set, binding): variable id} for every variable with a descriptor decoration."""
        out = {}
        for v in self.variables:
            d = self.dec.get(v, {})
            if DEC_DESCRIPTOR_SET in d:
                out[(d[DEC_DESCRIPTOR_SET], d.get(DEC_BINDING, 0))] = v
        return out

    def has_push_constants(self):
        return any(sc == SC_PUSH_CONSTANT for _, sc in self.variables.values())

    def type_name(self, t):
        ty = self.types.get(t)
        if ty is None:
            return f"?{t}"
        k = ty[0]
        if k == "int":
            return ("i" if ty[2] else "u") + str(ty[1])
        if k == "float":
            return "f" + str(ty[1])
        if k == "bool":
            return "bool"
        if k == "vec":
            return f"{self.type_name(ty[1])}x{ty[2]}"
        if k == "mat":
            return f"mat<{self.type_name(ty[1])}>x{ty[2]}"
        if k == "array":
            return f"{self.type_name(ty[1])}[{self.constants.get(ty[2], '?')}]"
        if k == "rtarray":
            return f"{self.type_name(ty[1])}[]"
        if k == "struct":
            return f"struct#{t}"
        if k == "ptr":
            return f"ptr<{self.type_name(ty[2])}>"
        return k

    def describe_struct(self, t, indent=0):
        """Lines: offset, member type, recursively. The layout is in the Offset decorations."""
        ty = self.types.get(t)
        lines = []
        if ty is None:
            return lines
        if ty[0] in ("array", "rtarray"):
            stride = self.dec.get(t, {}).get(DEC_ARRAY_STRIDE)
            lines.append(" " * indent + f"{self.type_name(t)} stride {stride}")
            lines.extend(self.describe_struct(ty[1], indent + 2))
        elif ty[0] == "struct":
            for i, m in enumerate(ty[1]):
                off = self.member_dec.get((t, i), {}).get(DEC_OFFSET)
                lines.append(" " * indent + f"+{off}: {self.type_name(m)}")
                sub = self.types.get(m)
                if sub and sub[0] in ("struct", "array", "rtarray"):
                    lines.extend(self.describe_struct(m, indent + 2))
        return lines
