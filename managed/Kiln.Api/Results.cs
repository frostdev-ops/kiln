// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>A capability is off in this process: its service is not loaded, this game build lacks it, or it does not
/// exist on this side. Derives from <see cref="NotSupportedException"/>, so <c>catch (NotSupportedException)</c> still
/// catches it. A refusal by the game is not this: that comes back as a result with <c>Ok = false</c>
/// (<see cref="IKilnResult"/>); a bad argument throws <see cref="ArgumentException"/>.</summary>
public sealed class KilnUnavailableException : NotSupportedException
{
    /// <summary>A new exception for <paramref name="capability"/>.</summary>
    /// <param name="capability">The capability's name, e.g. <c>identity.entities</c>.</param>
    /// <param name="reason">Why it is off, in plain words; null when unknown.</param>
    /// <param name="innerException">The error that made it unavailable, if any.</param>
    /// <exception cref="ArgumentException"><paramref name="capability"/> is null or empty.</exception>
    public KilnUnavailableException(string capability, string? reason = null, Exception? innerException = null)
        : base(MessageOf(capability, reason), innerException)
    {
        Capability = capability;
        Reason = reason;
    }

    /// <summary>The capability that is off.</summary>
    public string Capability { get; }

    /// <summary>Why it is off; null when unknown.</summary>
    public string? Reason { get; }

    private static string MessageOf(string capability, string? reason)
    {
        if (string.IsNullOrEmpty(capability)) throw new ArgumentException("a capability name is required", nameof(capability));
        return string.IsNullOrEmpty(reason) ? $"{capability} is not available here." : $"{capability} is not available: {reason}";
    }
}

/// <summary>The shape of a result by Kiln's error rule: <see cref="Ok"/> false with a <see cref="Reason"/> when the game
/// refused. The services' result records implement it from dev.26 on.</summary>
public interface IKilnResult
{
    /// <summary>True when the action happened.</summary>
    bool Ok { get; }

    /// <summary>Why it did not happen; null when <see cref="Ok"/>.</summary>
    string? Reason { get; }
}
