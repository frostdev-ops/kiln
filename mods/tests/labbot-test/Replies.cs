// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace LabBotTest;

/// <summary>Every request gets an answer: an op that throws answers <c>{"ok": false, "error": "&lt;type&gt;: &lt;message&gt;"}</c>
/// instead of none (a lab script would wait for its timeout and learn nothing). Kiln.Network.Tests' LabBotRepliesTests
/// compile this file.</summary>
internal static class Replies
{
    /// <summary>The answer of <paramref name="op"/>, or the error it threw.</summary>
    internal static JsonObject Run(Func<JsonObject> op)
    {
        try
        {
            return op();
        }
        catch (Exception e) when (Answerable(e))
        {
            return Error(e);
        }
    }

    /// <summary>The answer of an op that ends later (teleport, give, use, spawn), or the error it threw.</summary>
    internal static async Task<JsonObject> RunAsync(Func<Task<JsonObject>> op)
    {
        try
        {
            return await op();
        }
        catch (Exception e) when (Answerable(e))
        {
            return Error(e);
        }
    }

    internal static JsonObject Error(string why) => new() { ["ok"] = false, ["error"] = why };

    private static JsonObject Error(Exception e) => Error($"{e.GetType().Name}: {e.Message}");

    // everything but the process's own failures: a request's bad field (a null, a number past its type) is the script's
    // to hear about
    private static bool Answerable(Exception e) => e is not (OutOfMemoryException or InsufficientExecutionStackException);
}
