# SPDX-License-Identifier: MIT
"""The untrusted boundary of mods' particle hooks (cook/spvlink.cpp): hand-written hostile hook modules, malformed SPIR-V and fuzzing.

    cd tools/vfxcook && python3 tests/test_shader_safety.py -v

Nothing here is dispatched: what runs is in native/services/vfx/shaders/test/test_mod_module.py (CPU) and test_gpu.py --stage hooks (GPU).
Needs spirv-as (SPIRV-Tools) for the hand-written modules; the malformed and fuzz cases need nothing.
"""
import json
import random
import shutil
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path

import test_vfx_abi2 as abi2
from test_vfxcook import Mod, P, REPO, checks, cook, run, sprite

SAMPLE = REPO / "mods/samples/ShaderSample"
HAVE_AS = shutil.which("spirv-as") is not None


def stamped(code, marker=None):
    """A module with a stamp OpSourceExtension where SPIR-V's layout allows it (the hook stamp by default)."""
    marker = marker or f"kiln-vfx-hooks-{P['KILN_HOOKS_ABI']}"
    words = list(struct.unpack(f"<{len(code) // 4}I", code))
    at = 5
    while at < len(words) and words[at] & 0xffff in (10, 11, 14, 15, 16, 17, 331):
        at += words[at] >> 16
    text = marker.encode() + b"\0"
    text += b"\0" * (-len(text) % 4)
    words[at:at] = [((1 + len(text) // 4) << 16) | 4, *struct.unpack(f"<{len(text) // 4}I", text)]
    return struct.pack(f"<{len(words)}I", *words)


# A minimal hook module with Kiln's signatures, in SPIR-V assembly. Placeholders: CAPS, ANNOT, DECL (globals), BODY (kiln_hook_update's
# blocks after its first label, ending in a terminator), P1 and P2 (its parameter types), FUNCS (more functions).
TEMPLATE = """
OpCapability Shader
{CAPS}
%glsl = OpExtInstImport "GLSL.std.450"
OpMemoryModel Logical GLSL450
OpEntryPoint GLCompute %main "cs_main"
OpExecutionMode %main LocalSize 32 1 1
OpName %up "kiln_hook_update(struct-KilnSprite;struct-KilnContext;"
{ANNOT}
%void = OpTypeVoid
%float = OpTypeFloat 32
%v3 = OpTypeVector %float 3
%v4 = OpTypeVector %float 4
%uint = OpTypeInt 32 0
%int = OpTypeInt 32 1
%bool = OpTypeBool
%Play = OpTypeStruct %v3 %float %float %float %float %float
%Sprite = OpTypeStruct %v3 %v3 %float %float %float %float %float %uint %bool
%Ctx = OpTypeStruct %float %float %uint %v3 %v4 %float %v3 %v3 %Play %v4 %v4 %uint
%pS = OpTypePointer Function %Sprite
%pC = OpTypePointer Function %Ctx
%pf = OpTypePointer Function %float
%fvoid = OpTypeFunction %void
%fup = OpTypeFunction %void {P1} {P2}
%int_6 = OpConstant %int 6
%uint_2 = OpConstant %uint 2
%f1 = OpConstant %float 1
%true = OpConstantTrue %bool
{DECL}
%main = OpFunction %void None %fvoid
%m0 = OpLabel
OpReturn
OpFunctionEnd
%up = OpFunction %void None %fup
%s = OpFunctionParameter {P1}
%c = OpFunctionParameter {P2}
%u0 = OpLabel
{BODY}
OpFunctionEnd
{FUNCS}
"""
AUX = "%aux = OpAccessChain %pf %s %int_6\n%a0 = OpLoad %float %aux\n%a1 = OpFAdd %float %a0 %f1\nOpStore %aux %a1\n"


def assemble(stamp=True, **parts):
    fields = dict(CAPS="", ANNOT="", DECL="", BODY="OpReturn", P1="%pS", P2="%pC", FUNCS="")
    fields.update(parts)
    text = TEMPLATE.format(**fields)
    with tempfile.TemporaryDirectory() as d:
        src, out = Path(d) / "m.spvasm", Path(d) / "m.spv"
        src.write_text(text)
        p = subprocess.run(["spirv-as", "--target-env", "vulkan1.2", str(src), "-o", str(out)], capture_output=True, text=True)
        if p.returncode:
            raise AssertionError(p.stderr + text)
        code = out.read_bytes()
    return stamped(code) if stamp else code


def check(code, tmp):
    f = Path(tmp) / f"m{random.random()}.spv"
    f.write_bytes(code)
    p = run("shader", "check", f, ok=(0, 1))
    return json.loads(p.stdout)[0]


@unittest.skipUnless(HAVE_AS, "spirv-as not installed")
class Hostile(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = self._tmp.name

    def tearDown(self):
        self._tmp.cleanup()

    def refused(self, why, **parts):
        r = check(assemble(**parts), self.tmp)
        self.assertFalse(r["ok"], r)
        self.assertIn(why, r["problem"])
        return r

    def test_the_baseline_links(self):
        r = check(assemble(BODY=AUX + "OpReturn"), self.tmp)
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["hooks"], ["update"])

    def test_a_storage_buffer_and_a_store_into_it(self):
        self.refused("storage class",
                     ANNOT="OpDecorate %blk Block\nOpMemberDecorate %blk 0 Offset 0\nOpDecorate %buf DescriptorSet 1\nOpDecorate %buf Binding 42",
                     DECL="%blk = OpTypeStruct %uint\n%pblk = OpTypePointer StorageBuffer %blk\n%buf = OpVariable %pblk StorageBuffer\n"
                          "%pu = OpTypePointer StorageBuffer %uint",
                     BODY="%e = OpAccessChain %pu %buf %int_6\nOpStore %e %uint_2\nOpReturn")

    def test_a_push_constant_or_input(self):
        self.refused("storage class", DECL="%pin = OpTypePointer Input %v3\n%gid = OpVariable %pin Input")

    def test_workgroup_memory(self):
        self.refused("storage class", DECL="%pw = OpTypePointer Workgroup %float\n%sh = OpVariable %pw Workgroup")

    def test_a_barrier(self):
        self.refused("opcode 224", DECL="%u264 = OpConstant %uint 264", BODY="OpControlBarrier %uint_2 %uint_2 %u264\nOpReturn")

    def test_an_atomic_on_local_memory(self):
        self.refused("opcode 234", DECL="%pu = OpTypePointer Function %uint\n%u0c = OpConstant %uint 0",
                     BODY="%rng = OpAccessChain %pu %s %int_6\n%old = OpAtomicIAdd %uint %rng %uint_2 %u0c %uint_2\nOpReturn")

    def test_a_loop_without_a_header(self):
        self.refused("doesn't pass a loop header", BODY="OpBranch %l1\n%l1 = OpLabel\n" + AUX + "OpBranch %l1")

    def test_a_merge_block_that_leads_back_into_its_loop(self):
        self.refused("merge block leads back",
                     BODY="OpBranch %h\n%h = OpLabel\nOpLoopMerge %m %ct None\nOpBranch %b\n%b = OpLabel\nOpBranchConditional %true %ct %m\n"
                          "%ct = OpLabel\nOpBranch %h\n%m = OpLabel\nOpBranch %h")

    def test_an_endless_structured_loop_is_capped(self):
        r = check(assemble(BODY="OpBranch %h\n%h = OpLabel\nOpLoopMerge %m %h None\nOpBranch %h\n%m = OpLabel\nOpReturn"), self.tmp)
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["loops"], 1)
        self.assertGreater(r["loopCap"], 0)

    def test_recursion(self):
        self.refused("recursion", BODY="%r = OpFunctionCall %void %up %s %c\nOpReturn")

    def test_a_pointer_made_by_select(self):
        self.refused("makes a pointer", BODY="%x = OpAccessChain %pf %s %int_6\n%y = OpSelect %pf %true %x %x\nOpStore %y %f1\nOpReturn")

    def test_a_pointer_from_undef(self):
        self.refused("null or undefined pointer", DECL="%bad = OpUndef %pf")

    def test_an_id_the_module_never_defines(self):
        self.refused("should be a value", BODY="OpStore %nowhere %f1\nOpReturn")

    def test_a_huge_local_array(self):
        self.refused("elements", DECL="%n = OpConstant %uint 1000000\n%arr = OpTypeArray %float %n")

    def test_large_local_memory(self):
        self.refused("local variables", DECL="%n = OpConstant %uint 512\n%arr = OpTypeArray %v4 %n\n%parr = OpTypePointer Function %arr",
                     BODY="%a = OpVariable %parr Function\n%b = OpVariable %parr Function\n%d = OpVariable %parr Function\nOpReturn")

    def test_an_extension(self):
        self.refused("extension", CAPS='OpExtension "SPV_KHR_storage_buffer_storage_class"')

    def test_a_capability(self):
        self.refused("capability", CAPS="OpCapability Int64")

    def test_kill(self):
        self.refused("opcode 252", BODY="OpKill")

    def test_an_image(self):
        self.refused("opcode 25", DECL="%img = OpTypeImage %float 2D 0 0 0 2 R32f")

    def test_interpolation(self):
        self.refused("a pointer used as a value", BODY="%x = OpAccessChain %pf %s %int_6\n%y = OpExtInst %float %glsl InterpolateAtCentroid %x\nOpReturn")

    def test_another_signature(self):
        self.refused("another signature", P1="%pC", P2="%pC", BODY="OpReturn")

    def test_an_out_of_range_constant_index(self):
        self.refused("out of range", DECL="%int_99 = OpConstant %int 99", BODY="%x = OpAccessChain %pf %s %int_99\nOpStore %x %f1\nOpReturn")

    def test_a_struct_member_by_variable(self):
        self.refused("isn't a constant", BODY="%k = OpUndef %int\n%x = OpAccessChain %pf %s %k\nOpStore %x %f1\nOpReturn")

    def test_a_store_of_the_wrong_type(self):
        self.refused("store through", BODY="%x = OpAccessChain %pf %s %int_6\nOpStore %x %uint_2\nOpReturn")

    def test_no_stamp(self):
        r = check(assemble(stamp=False), self.tmp)
        self.assertIn("no Kiln hook stamp", r["problem"])

    def test_no_hooks(self):
        r = check(assemble().replace(b"kiln_hook_update(", b"kiln_hook_noop__("), self.tmp)
        self.assertIn("defines none of", r["problem"])

    def test_a_huge_module(self):
        code = assemble() + struct.pack("<I", (1 << 16) | 0) * (70 * 1024)
        r = check(code, self.tmp)
        self.assertIn("at most 262144", r["problem"])


class Malformed(abi2.Base):
    def test_every_truncation_is_refused_without_a_crash(self):
        code = (SAMPLE / "shaders/build/swirl.spv").read_bytes()
        files = []
        for i, n in enumerate(range(0, len(code), 4 * max(1, len(code) // 4 // 400))):
            f = Path(self.tmp) / f"t{i}.spv"
            f.write_bytes(code[:n])
            files.append(f)
        p = subprocess.run([run.__globals__["tool"](), "shader", "check", *map(str, files)], capture_output=True, text=True)
        self.assertEqual(p.returncode, 1, p.stderr[-2000:])
        r = json.loads(p.stdout)
        self.assertEqual(len(r), len(files))
        self.assertFalse(any(x["ok"] for x in r))

    def test_random_mutations_never_crash_the_verifier(self):
        code = bytearray((SAMPLE / "shaders/build/swirl.spv").read_bytes())
        rng = random.Random(20261107)
        files = []
        for i in range(600):
            m = bytearray(code)
            for _ in range(rng.choice((1, 1, 2, 4, 16))):
                at = rng.randrange(20, len(m))
                m[at] = rng.choice((rng.randrange(256), 0, 0xff, m[at] ^ (1 << rng.randrange(8))))
            f = Path(self.tmp) / f"f{i}.spv"
            f.write_bytes(bytes(m))
            files.append(f)
        p = subprocess.run([run.__globals__["tool"](), "shader", "check", *map(str, files)], capture_output=True, text=True)
        self.assertIn(p.returncode, (0, 1), p.stderr[-2000:])
        r = json.loads(p.stdout)
        self.assertEqual(len(r), len(files))
        accepted = [f for f, x in zip(files, r) if x["ok"]]
        # Mutations of literals (a constant's value) are fine; the linked module of anything accepted must still validate.
        if shutil.which("spirv-val"):
            for f in accepted[:40]:
                out = Path(self.tmp) / "l.spv"
                run("shader", "link", f, "--out", out)
                v = subprocess.run(["spirv-val", "--target-env", "vulkan1.2", "--scalar-block-layout", str(out)], capture_output=True, text=True)
                self.assertEqual(v.returncode, 0, f"{f.name}: {v.stderr}")

    def test_header_garbage(self):
        code = (SAMPLE / "shaders/build/swirl.spv").read_bytes()
        for at, value in ((4, 0xffffffff), (4, 0x00010600), (12, 0xffffffff), (12, 0), (16, 1)):
            m = code[:at] + struct.pack("<I", value) + code[at + 4:]
            f = Path(self.tmp) / "h.spv"
            f.write_bytes(m)
            r = json.loads(run("shader", "check", f, ok=(1,)).stdout)[0]
            self.assertFalse(r["ok"])

    def test_sparse_oversize_module_is_refused_before_allocation(self):
        path = Path(self.tmp) / "big.spv"
        with path.open("wb") as f:
            f.truncate(1 << 32)
        r = json.loads(run("shader", "check", path, ok=(1,)).stdout)[0]
        self.assertIn("size limit", r["problem"])


class LegacyCooked(abi2.Base):
    def test_strict_checker_accepts_abi1_but_refuses_mixed_or_relabelled_data(self):
        c = cook(Mod(self.tmp, "legacy", {"a": {"particles": [sprite()]}}), tmp=self.tmp)
        v = c.decoded(c.blocks()["a"]["guid"], "keen::VfxResource")
        data = bytearray(checks.resource_data(v))
        # Reconstruct ABI 1's original ranges and state: the v2 sprite adds 56 bytes and one state float.
        child = v["particles"][1]
        start = child["evaluateParametersGpuResourceDataRange"]["start"]
        del data[start + 272:]
        for p in v["particles"]:
            struct.pack_into("<I", data, p["evaluateParametersGpuResourceDataRange"]["start"], 1)
        child["evaluateParametersGpuResourceDataRange"]["count"] = 272
        child["stateSizeInBytes"] = 60
        v["stateInfos"][1]["sizeInBytes"] = 60
        v["stateInfos"][1]["members"] = v["stateInfos"][1]["members"][:-1]
        v["gpuResourceData"] = list(data)
        v["parameters"] = []
        v["defaultParameterData"]["data"] = [0] * 16
        self.assertEqual(checks.check_vfx(v, strict=True), [])
        v["defaultParameterData"]["data"][15] = 0x4e4c494b
        self.assertIn("ABI 1 must have no play parameters and zero defaults", checks.check_vfx(v, strict=True))


if __name__ == "__main__":
    unittest.main()
