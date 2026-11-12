// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

/// <summary>A mod faulted (an exception Kiln's fault policy counted) during a harness call. In the game Kiln logs the
/// exception and goes on; in a test that would be a wrong pass, so the call throws this once it has finished. Turn it
/// off with <see cref="KilnHarnessOptions.ThrowOnModFault"/> to test the fault policy itself.</summary>
public sealed class ModFaultException : Exception
{
    internal ModFaultException(IReadOnlyList<string> faults)
        : base(faults.Count == 1 ? faults[0] : $"{faults.Count} mod faults:\n" + string.Join("\n", faults)) => Faults = faults;

    /// <summary>Each fault as Kiln logged it (mod, where, the exception with its stack).</summary>
    public IReadOnlyList<string> Faults { get; }
}

/// <summary>A mod did not load: the loader refused it (manifest, resolver, a missing dependency or feature) or its
/// <c>OnLoad</c> threw.</summary>
public sealed class ModLoadException : Exception
{
    internal ModLoadException(string modId, ModState state, string reason)
        : base($"mod {modId} did not load ({state.ToString().ToLowerInvariant()}): {reason}")
    {
        ModId = modId;
        State = state;
        Reason = reason;
    }

    /// <summary>The mod that did not load (several, comma-separated, when more failed at the first step).</summary>
    public string ModId { get; }

    /// <summary>Failed or Skipped, as the mod list says.</summary>
    public ModState State { get; }

    /// <summary>The loader's reason, followed by what the capability rows say about any feature it named.</summary>
    public string Reason { get; }
}

/// <summary>After an unload, a mod's load context was still alive: something outside the mod holds its code or
/// objects, and in the game every reload of it would leak.</summary>
public sealed class ModLeakException : Exception
{
    internal ModLeakException(IReadOnlyList<string> modIds)
        : base($"the load context of {string.Join(", ", modIds)} is still alive after the unload and a full collection. Something outside " +
               "the mod still references its code or objects: a handler on a process-wide event (AppDomain, TaskScheduler, Console), a " +
               "static of a shared assembly, a thread or timer of its own, or a test variable holding ModHandle.Instance or ModHandle.Context.")
        => ModIds = modIds;

    /// <summary>The mods whose load context is still alive.</summary>
    public IReadOnlyList<string> ModIds { get; }
}

/// <summary>The harness's tick thread was given up on (a test blocked it and the watchdog stopped the harness): no call
/// works any more.</summary>
public sealed class HarnessStoppedException : Exception
{
    internal HarnessStoppedException(string reason) : base($"the KilnHarness was stopped: {reason}") { }
}
