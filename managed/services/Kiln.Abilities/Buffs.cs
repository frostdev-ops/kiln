// SPDX-License-Identifier: MIT
using System.Text;

namespace Kiln;

public static partial class Abilities
{
    /// <summary>The program behind <see cref="ApplyBuff"/>: applyBuff(Buff, Target, -, -, Duration), then DSELF. Built
    /// here (a keen::ImpactProgram blob) and registered at first use.</summary>
    internal static readonly Guid BuffProgram = new("6b696c6e-6275-4666-8000-000000000001");

    private static bool _buffProgramReady;

    /// <summary>Applies a buff or debuff to an entity through the game's own applyBuff native (the path the game's
    /// programs use: the buff entity, its program, BuffNetworkData for clients). <paramref name="buff"/> is the
    /// BuffType's GUID (its id is FNV-1a of it: Abilities.IdOf(buff)); <paramref name="duration"/> null = the buff's
    /// default lifetime. <paramref name="source"/> becomes the impact's root (credit, faction checks). Returns the
    /// short-lived impact entity that applies it (it destroys itself). Tick thread.</summary>
    public static Task<EntityId> ApplyBuff(EntityId target, Guid buff, TimeSpan? duration = null, EntityId source = default)
    {
        try
        {
            Game.AssertTickThread();
            if (target.IsNone) throw new ArgumentException("no target", nameof(target));
            EnsureBuffProgram();
            var at = World.Position(target) ?? throw new ArgumentException($"entity {target} has no position", nameof(target));
            return RunProgram(BuffProgram, at, new ImpactOptions
            {
                Root = source.IsNone ? target : source,
                Configs = new ImpactConfigs { ["Buff"] = buff, ["Target"] = target, ["Duration"] = duration ?? TimeSpan.Zero },
                Tag = "abilities-buff",
            });
        }
        catch (Exception e)
        {
            return Task.FromException<EntityId>(e);
        }
    }

    private static void EnsureBuffProgram()
    {
        if (_buffProgramReady && Programs.Info(Programs.IdOf(BuffProgram)) is not null) return;
        if (Programs.Info(Programs.IdOf(BuffProgram)) is null)
        {
            Programs.RegisterCore(BuildBuffProgram(), out var waiting);  // Kiln's program: not the gated mod call
            if (waiting) throw new InvalidOperationException("ApplyBuff: the game's ImpactRegistry is not loaded yet");
        }
        _buffProgramReady = true;
    }

    private static byte[] BuildBuffProgram()
    {
        const uint hBuff = 0xFFFF0000, hTarget = 0xFFFF0001, hDuration = 0xFFFF0002;
        uint[] code =
        [
            11, hDuration, 12, 12, 11, hTarget, 11, hBuff,  // args[4] Duration, [3] 0, [2] 0 (no damage), [1] Target, [0] Buff
            19, 0x6b696c01, 0x91f30b26,                      // ECALL applyBuff
            27, 29, 30,                                      // POP, DSELF, HALT
        ];
        // keen types: BuffTypeReference 0x305916d9 (16 B), ecs EntityId 0x737c8f86 (4 B), Time 0xdfe4e404 (8 B)
        (string Name, uint Type, int Size)[] vars = [("Buff::value", 0x305916d9, 16), ("Target::value", 0x737c8f86, 4),
            ("Duration::value", 0xdfe4e404, 8)];
        return ProgramBlob.Build(BuffProgram, code, [30], vars);
    }
}

/// <summary>Writes keen::ImpactProgram blobs (docs/kiln/services/abilities.md, "contract").</summary>
internal static class ProgramBlob
{
    public static byte[] Build(Guid guid, uint[] code, uint[] shutdown, (string Name, uint Type, int Size)[] vars)
    {
        var names = vars.Select(v => Encoding.UTF8.GetBytes(v.Name)).ToArray();
        var dataSize = vars.Sum(v => v.Size);
        var codeOff = 64;
        var shutOff = codeOff + code.Length * 4;
        var layoutOff = shutOff + shutdown.Length * 4;
        var namesOff = layoutOff + vars.Length * 24;
        var dataOff = namesOff + names.Sum(n => n.Length);
        var b = new byte[(dataOff + dataSize + 3) & ~3];
        var g = guid.ToByteArray();
        BitConverter.GetBytes(AbilitiesNative.Fnv(g)).CopyTo(b, 0);
        g.CopyTo(b, 4);
        BitConverter.GetBytes((ushort)256).CopyTo(b, 0x14);
        void Arr(int field, int target, int count)
        {
            BitConverter.GetBytes(count == 0 ? 0 : target - field).CopyTo(b, field);
            BitConverter.GetBytes(count).CopyTo(b, field + 4);
        }
        Arr(0x18, codeOff, code.Length);
        Arr(0x20, shutOff, shutdown.Length);
        Arr(0x28, layoutOff, vars.Length);
        Arr(0x30, dataOff, dataSize);
        for (var i = 0; i < code.Length; i++) BitConverter.GetBytes(code[i]).CopyTo(b, codeOff + i * 4);
        for (var i = 0; i < shutdown.Length; i++) BitConverter.GetBytes(shutdown[i]).CopyTo(b, shutOff + i * 4);
        int nameAt = namesOff, dataAt = 0;
        for (var i = 0; i < vars.Length; i++)
        {
            var e = layoutOff + i * 24;
            BitConverter.GetBytes(AbilitiesNative.Fnv(names[i])).CopyTo(b, e);
            BitConverter.GetBytes(vars[i].Type).CopyTo(b, e + 8);
            BitConverter.GetBytes((ushort)vars[i].Size).CopyTo(b, e + 12);
            BitConverter.GetBytes((ushort)dataAt).CopyTo(b, e + 14);
            BitConverter.GetBytes(nameAt - (e + 16)).CopyTo(b, e + 16);
            BitConverter.GetBytes(names[i].Length).CopyTo(b, e + 20);
            names[i].CopyTo(b, nameAt);
            nameAt += names[i].Length;
            dataAt += vars[i].Size;
        }
        return b;
    }
}
