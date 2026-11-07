// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.BlockTests;

public sealed class ProjectileTests
{
    [Fact]
    public void A_killed_target_still_consumes_a_pierce_and_repeated_collisions_do_not_hit_twice()
    {
        var vm = Flight(2, [1, 1], [1, 2, 2], [3]);
        vm.Run();
        Assert.True(vm.Destroyed);
        Assert.Equal(new uint[] { 1, 2, 3 }, vm.Hits);
        Assert.Equal(2, vm.Impacts); // the two impact templates, only where the flight stops
    }

    [Fact]
    public void The_first_world_collision_stops_a_piercing_projectile()
    {
        var vm = Flight(8, [1, 0, 2]); // zero: world geometry, not a combat target
        vm.Run();
        Assert.True(vm.Destroyed);
        Assert.Equal(new uint[] { 1 }, vm.Hits);
    }

    private static FlightVm Flight(int pierce, params uint[][] collisions)
    {
        var ctx = new BlockContext(new ModBlocks("pierce-test", 1), "spell shard");
        var (_, bytes, _, _) = EffectEmitter.ProjectileProgram(ctx, "shard", null, "onCast", [new EffectBlock { Heal = 1 }], "onHit", false, pierce);
        return new FlightVm(bytes, collisions);
    }

    // Execute the generated Impact bytecode, stubbing collision and effect natives. Killing every hit target exercises the
    // persistent slot state across yields, rather than checking a textual listing of the implementation.
    private sealed class FlightVm
    {
        private readonly uint[] _code;
        private readonly uint[] _values;
        private readonly Stack<uint> _stack = new();
        private readonly uint[][] _frames;
        private readonly HashSet<uint> _dead = [];
        private int _frame, _pc;
        public bool Destroyed;
        public int Impacts;
        public readonly List<uint> Hits = [];
        public FlightVm(byte[] bytes, uint[][] frames)
        {
            uint Word(int at) => BitConverter.ToUInt32(bytes, at);
            var start = 0x18 + (int)Word(0x18);
            _code = Enumerable.Range(0, (int)Word(0x1c)).Select(i => Word(start + i * 4)).ToArray();
            var layout = 0x28 + (int)Word(0x28);
            var data = 0x30 + (int)Word(0x30);
            _values = Enumerable.Range(0, (int)Word(0x2c)).Select(i =>
            {
                var entry = layout + 24 * i;
                var offset = BitConverter.ToUInt16(bytes, entry + 14);
                var size = BitConverter.ToUInt16(bytes, entry + 12);
                return size == 1 ? bytes[data + offset] : Word(data + offset);
            }).ToArray();
            _frames = frames;
        }
        private uint Get(uint handle) => handle == 0 ? 0 : _values[(int)(handle & 0xffff)];
        private void Put(uint handle, uint value) { if (handle != 0) _values[(int)(handle & 0xffff)] = value; }
        public void Run()
        {
            for (var steps = 0; steps < 10000; steps++)
            {
                var op = _code[_pc++];
                switch (op)
                {
                    case 8: _pc = (int)_code[_pc]; break;
                    case 9:
                    case 10:
                        var at = (int)_code[_pc++];
                        if ((_stack.Pop() != 0) == (op == 9)) _pc = at;
                        break;
                    case 11: _stack.Push(_code[_pc++]); break;
                    case 12: _stack.Push(0); break;
                    case 19:
                        _pc++; // call site
                        _stack.Push(Call(_code[_pc++]));
                        break;
                    case 22: _stack.Push(Get(_code[_pc++])); break;
                    case 24: Put(_code[_pc++], _stack.Pop()); break;
                    case 27: _stack.Pop(); break;
                    case 28:
                        if (++_frame >= _frames.Length) return;
                        break;
                    case 29: Destroyed = true; break;
                    case 30: return;
                    default: throw new InvalidOperationException($"unexpected opcode {op}");
                }
            }
            throw new InvalidOperationException("flight did not yield or stop");
        }
        private uint Call(uint hash)
        {
            var count = hash switch
            {
                0xbf5ad13b or 0xcf3810a6 => 1,
                0x45dbf538 or 0x530b5054 or 0xd471249f => 2,
                0x0294c6df => 3,
                0xd6bb2b78 or 0xf52a5619 => 4,
                0x044e4187 => 6,
                0xad415283 => 8,
                0xd0a532b0 => 11,
                0x61b11b03 => 13,
                _ => throw new InvalidOperationException($"unexpected native {hash:x8}"),
            };
            var args = Enumerable.Range(0, count).Select(_ => _stack.Pop()).ToArray();
            switch (hash)
            {
                case 0xd0a532b0:
                    var index = Get(args[9]);
                    if (index >= _frames[_frame].Length) return 0;
                    var entity = _frames[_frame][index];
                    Put(args[1], entity);
                    Put(args[5], 1); // a material other than water
                    Put(args[7], entity == 0 ? 0u : 1u);
                    Put(args[9], index + 1);
                    return 1;
                case 0x530b5054: return Get(args[0]) == Get(args[1]) ? 1u : 0u;
                case 0xcf3810a6: return Get(args[0]) != 0 && !_dead.Contains(Get(args[0])) ? 1u : 0u;
                case 0x0294c6df: return Get(args[1]) == Get(args[2]) ? 1u : 0u;
                case 0x044e4187:
                    Hits.Add(Get(args[3]));
                    _dead.Add(Get(args[3]));
                    return 1;
                case 0x61b11b03: Impacts++; return 1;
                default: return 1;
            }
        }
    }
}
