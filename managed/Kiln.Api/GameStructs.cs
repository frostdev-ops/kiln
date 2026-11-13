// SPDX-License-Identifier: MIT
// The marker interfaces of the generated game types (Kiln.Game's Kiln.Ecs.*). They name no Kiln.Game type and live here
// since 0.2.0-dev.27 (before: in Kiln.Game, which forwards them), so that Kiln.World and the test kit can take a
// component by its type (World.Write<T>, World.Update<T>) without referencing the generated assembly.
namespace Kiln;

/// <summary>
/// A struct generated from the game's reflection table (every nested type of <c>Kiln.Ecs</c> that is a struct). Its
/// layout is the engine's on the build it was generated for. Generic code reads the type's game name, hash, size and
/// layout hash through <typeparamref name="TSelf"/>: <c>T.Name</c>, <c>T.Hash</c>.
/// </summary>
/// <typeparam name="TSelf">The generated struct itself.</typeparam>
public interface IGameStruct<TSelf> where TSelf : unmanaged, IGameStruct<TSelf>
{
    /// <summary>The engine's type hash: FNV-1a-32 of <see cref="QualifiedName"/>.</summary>
    static abstract uint Hash { get; }

    /// <summary>The game's short name (for an ECS component, the name kiln.world uses).</summary>
    static abstract string Name { get; }

    /// <summary>The game's qualified name, such as <c>keen::ecs::Health</c>.</summary>
    static abstract string QualifiedName { get; }

    /// <summary>The size in bytes, the engine's and the C# struct's.</summary>
    static abstract int Size { get; }

    /// <summary>The layout hash: FNV-1a-64 over the type's canonical layout text, which covers every type inside
    /// it (docs/kiln/typegen.md §3). Equal hashes, equal layouts.</summary>
    static abstract ulong Layout { get; }
}

/// <summary>A generated struct that is an ECS component of the game (the dump's component list).</summary>
/// <typeparam name="TSelf">The generated struct itself.</typeparam>
public interface IGameComponent<TSelf> : IGameStruct<TSelf> where TSelf : unmanaged, IGameComponent<TSelf>;

/// <summary>A generated struct that is an ECS event of the game (the dump's event list).</summary>
/// <typeparam name="TSelf">The generated struct itself.</typeparam>
public interface IGameEvent<TSelf> : IGameStruct<TSelf> where TSelf : unmanaged, IGameEvent<TSelf>;

/// <summary>
/// A generated ECS component that is a game attribute (its base chain runs through <c>keen::ecs::AttributeComponent</c>:
/// <c>Health</c>, <c>Stamina</c>, <c>Fuel</c>, ...). Its value slots are its <c>dataStorage</c> array; which slot holds
/// the current, the base or the maximum value is the game's data and differs per attribute, so the slots have no names.
/// </summary>
/// <typeparam name="TSelf">The generated struct itself.</typeparam>
/// <typeparam name="TValue">The type of one slot (<c>int</c>, <c>float</c>, <c>uint</c>).</typeparam>
public interface IGameAttribute<TSelf, TValue> : IGameComponent<TSelf>
    where TSelf : unmanaged, IGameAttribute<TSelf, TValue>
    where TValue : unmanaged
{
    /// <summary>The value slots of <paramref name="self"/>, over its own memory.</summary>
    static abstract Span<TValue> ValuesOf(ref TSelf self);
}
