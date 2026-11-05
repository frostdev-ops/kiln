// SPDX-License-Identifier: MIT
namespace SampleLib.Api;

/// <summary>Something sample-lib and its dependents share.</summary>
public interface IGreeter
{
    string Greet(string who);
}

/// <summary>Registry that lives in sample-lib's load context: every dependent sees the same list.</summary>
public static class Greeters
{
    private static readonly List<IGreeter> All_ = [];

    public static void Register(IGreeter greeter)
    {
        lock (All_) All_.Add(greeter);
    }

    public static IReadOnlyList<IGreeter> All
    {
        get
        {
            lock (All_) return All_.ToArray();
        }
    }
}

/// <summary>An IMC payload type both sides know (it comes from the exported assembly).</summary>
public sealed record EchoRequest(string Text, int Number);
