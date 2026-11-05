# SPDX-License-Identifier: MIT
"""Kiln's clean-room toolchain for Enshrouded's Impact programs (keen::ImpactProgram).

Written from the game's own reflection (keen::ImpactOps, keen::ImpactProgram, keen::ImpactVariable), Kiln's
disassembly of the interpreter (server 0x990920, client 0x1239960), createImpactInstance (0x993eb0) and
registerNative (0x996b80), and the 571 programs the game ships, read with tools/kfc. No kfc-parser code. Format and VM
semantics: docs/re/impact.md. Modder guide: docs/kiln/modding/impact.md.

Modules:
    ops       the 30 opcodes: operands, stack effect, control flow
    program   Program / Variable model and the byte-exact ImpactProgram codec (no game files needed)
    catalog   natives (vanilla + Kiln built-ins + mod declarations), impact types, config vocabulary
    values    typed initial values <-> bytes (driven by the type catalog)
    disasm    Program -> .kimp text
    asm       .kimp text -> Program
    lint      the verifier (the same rules kiln.abilities applies before registering a program)
    patch     clone a program under a new GUID and edit it (constants, configs, native swaps, inserted code)
    cook      impact/cook.json recipes: mod sources + vanilla inputs -> cooked resources for kiln.assets
    game      read programs from the installed game (tools/kfc), build catalogs from evidence
"""
import sys
from pathlib import Path

_KFC = Path(__file__).resolve().parents[2] / "kfc"
if str(_KFC) not in sys.path:
    sys.path.insert(0, str(_KFC))

FORMAT = 1          # .kimp text format version
TOOL_VERSION = "1.0.0"
