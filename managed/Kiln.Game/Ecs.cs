// SPDX-License-Identifier: MIT
// Hand-written part of the root class of the generated game types. The generated part (every struct, enum, bitmask
// and inline array of the dump's component and event closure) is written at build time by tools/typegen/typegen.py
// into the build's obj folder (docs/kiln/typegen.md §3). The class name is the generator's --root (Kiln.Game.csproj:
// KilnGameRoot); this file, GameLayoutStamps.cs and the tests that name the class are listed in the doc.
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>
/// The game's ECS types, generated from its reflection table: <c>Ecs.Health</c>, <c>Ecs.NetworkHealth</c>,
/// <c>Ecs.Enemy01_State</c>. <c>keen::ecs::X</c> and <c>keen::X</c> are <c>Ecs.X</c>; <c>keen::ns::X</c> is
/// <c>Ecs.Ns_X</c>. Every struct has the engine's exact layout on the build it was generated for, its game name, hash,
/// size and layout hash as static members, and implements <see cref="IGameStruct{TSelf}"/> (components
/// <see cref="IGameComponent{TSelf}"/>, events <see cref="IGameEvent{TSelf}"/>).
/// </summary>
public static partial class Ecs
{
    /// <summary>A game object reference (reflection kind 28): the 16-byte guid of the object it names. The field's
    /// comment names the referenced type.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    public readonly struct ObjectReference : IEquatable<ObjectReference>
    {
        private readonly uint _a, _b, _c, _d;

        /// <summary>A reference from its 16 bytes, in memory order.</summary>
        /// <exception cref="ArgumentException"><paramref name="bytes"/> is not 16 bytes long.</exception>
        public ObjectReference(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 16) throw new ArgumentException("an object reference is 16 bytes", nameof(bytes));
            _a = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            _b = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            _c = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
            _d = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        }

        /// <summary>True when all 16 bytes are zero (no object).</summary>
        public bool IsEmpty => (_a | _b | _c | _d) == 0;

        /// <summary>Copies the 16 bytes, in memory order, into <paramref name="destination"/>.</summary>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than 16 bytes.</exception>
        public void CopyTo(Span<byte> destination) => GameGuidBytes.Write(destination, _a, _b, _c, _d);

        /// <inheritdoc/>
        public bool Equals(ObjectReference other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is ObjectReference o && Equals(o);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

        /// <summary>The 16 bytes as 32 lower-case hex digits, in memory order.</summary>
        public override string ToString() => GameGuidBytes.Hex(_a, _b, _c, _d);

        /// <summary>True when both name the same object.</summary>
        public static bool operator ==(ObjectReference left, ObjectReference right) => left.Equals(right);

        /// <summary>True when they name different objects.</summary>
        public static bool operator !=(ObjectReference left, ObjectReference right) => !left.Equals(right);
    }

    /// <summary>A game guid (reflection kind 29, <c>keen::Guid</c>): 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    public readonly struct GameGuid : IEquatable<GameGuid>
    {
        private readonly uint _a, _b, _c, _d;

        /// <summary>A guid from its 16 bytes, in memory order.</summary>
        /// <exception cref="ArgumentException"><paramref name="bytes"/> is not 16 bytes long.</exception>
        public GameGuid(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 16) throw new ArgumentException("a game guid is 16 bytes", nameof(bytes));
            _a = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            _b = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            _c = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
            _d = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        }

        /// <summary>True when all 16 bytes are zero.</summary>
        public bool IsEmpty => (_a | _b | _c | _d) == 0;

        /// <summary>Copies the 16 bytes, in memory order, into <paramref name="destination"/>.</summary>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than 16 bytes.</exception>
        public void CopyTo(Span<byte> destination) => GameGuidBytes.Write(destination, _a, _b, _c, _d);

        /// <inheritdoc/>
        public bool Equals(GameGuid other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is GameGuid o && Equals(o);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

        /// <summary>The 16 bytes as 32 lower-case hex digits, in memory order.</summary>
        public override string ToString() => GameGuidBytes.Hex(_a, _b, _c, _d);

        /// <summary>True when the guids are equal.</summary>
        public static bool operator ==(GameGuid left, GameGuid right) => left.Equals(right);

        /// <summary>True when the guids differ.</summary>
        public static bool operator !=(GameGuid left, GameGuid right) => !left.Equals(right);
    }
}

/// <summary>The 16 bytes of an <see cref="Ecs.ObjectReference"/> or <see cref="Ecs.GameGuid"/>.</summary>
internal static class GameGuidBytes
{
    public static void Write(Span<byte> destination, uint a, uint b, uint c, uint d)
    {
        if (destination.Length < 16) throw new ArgumentException("16 bytes are needed", nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, a);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], b);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], c);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], d);
    }

    public static string Hex(uint a, uint b, uint c, uint d)
    {
        Span<byte> s = stackalloc byte[16];
        Write(s, a, b, c, d);
        return Convert.ToHexStringLower(s);
    }
}
