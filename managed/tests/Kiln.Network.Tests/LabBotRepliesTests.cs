// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using LabBotTest;
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>mods/tests/labbot-test answers every labbot:control request: an op that throws answers with the error (its
/// type and message) instead of no answer, which left a lab script waiting for its timeout with nothing to go on.</summary>
public sealed class LabBotRepliesTests
{
    [Fact]
    public void AnOpsAnswerPassesThrough()
    {
        var r = Replies.Run(() => new JsonObject { ["id"] = 3 });
        Assert.Equal(3, r["id"]!.GetValue<int>());
        Assert.False(r.ContainsKey("ok"));
    }

    [Theory]
    [InlineData("null")]      // a request without a field the op reads with !, e.g. teleport's x
    [InlineData("overflow")]  // a number past its type: a slot of 17 hex digits, a camera 2^31 m away
    [InlineData("argument")]
    [InlineData("unavailable")]
    public void AnOpThatThrowsAnswersWithTheError(string kind)
    {
        Exception e = kind switch
        {
            "null" => new NullReferenceException("Object reference not set to an instance of an object."),
            "overflow" => new OverflowException("Arithmetic operation resulted in an overflow."),
            "argument" => new ArgumentException("no id"),
            _ => new KilnUnavailableException("labbot.join", "lab servers only"),
        };
        var r = Replies.Run(() => throw e);
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.StartsWith(e.GetType().Name + ": ", r["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnOpThatEndsLaterAnswersWithTheErrorToo()
    {
        var r = await Replies.RunAsync(async () =>
        {
            await Task.Yield();
            return ((JsonObject?)null)!["x"]!.AsObject();
        });
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.StartsWith("NullReferenceException: ", r["error"]!.GetValue<string>());
        r = await Replies.RunAsync(() => Task.FromResult(new JsonObject { ["entity"] = 4242 }));
        Assert.Equal(4242, r["entity"]!.GetValue<int>());
    }
}
