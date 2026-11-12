// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Call this on the tick thread only. The KILN101 analyzer warns about other threads.</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class TickThreadOnlyAttribute : Attribute;

/// <summary>An advanced API: the mod's manifest needs <c>advanced: true</c> (analyzer rule).</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class AdvancedApiAttribute : Attribute;

/// <summary>Works on the server only (a dedicated server, or the host of a local game).</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class ServerOnlyAttribute : Attribute;

/// <summary>Works on a game client only.</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class ClientOnlyAttribute : Attribute;

/// <summary>Safe to call from any thread.</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class AnyThreadAttribute : Attribute;

/// <summary>Call this on the game's render thread only (client).</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class RenderThreadAttribute : Attribute;

/// <summary>Runs on, or may be called from, a worker thread; never touch game state there.</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class WorkerThreadAttribute : Attribute;
