// SPDX-License-Identifier: MIT
// Compile the production admin handlers against a registration-only bridge: malformed requests must fail before
// item lookup/submission, even when the native game services are absent.
extern alias rulesapi;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Admin;

internal sealed class AdminError(string text) : Exception(text);
internal sealed class Logger { public void Info(string _) { } }
internal sealed class Bridge
{
    public readonly Dictionary<string, Func<JsonObject, Task<JsonNode?>>> Async = new();
    public void Register(string _, Func<JsonObject, JsonNode?> op) { }
    public void RegisterAsync(string name, Func<JsonObject, Task<JsonNode?>> op) => Async[name] = op;
}
internal static partial class Ops
{
    private static Player RequirePlayer(JsonObject _) => new() { Entity = new EntityId(3), Name = "test" };
    // The test namespace Kiln.Rules.Tests shadows the public Rules type when production admin source is linked.
    private static class Rules
    {
        public static RulesClient ClientOf(EntityId e) => rulesapi::Kiln.Rules.ClientOf(e);
        public static EffectiveRules Of(EntityId e) => rulesapi::Kiln.Rules.Of(e);
        public static RulesStatus? Status() => rulesapi::Kiln.Rules.Status();
        public static IReadOnlyList<string> Presets => rulesapi::Kiln.Rules.Presets;
        public static IReadOnlyList<RuleSet> Active => rulesapi::Kiln.Rules.Active;
    }
}
public sealed class AdminOpsReviewTests
{
    [Theory]
    [InlineData("count", "\"oops\"", "count must be an integer")]
    [InlineData("count", "1.5", "count must be an integer")]
    [InlineData("level", "{}", "level must be an integer")]
    [InlineData("rarity", "\"255\"", "rarity is")]
    [InlineData("rarity", "\"typo\"", "rarity is")]
    [InlineData("rarity", "255", "rarity is")]
    public async Task InvalidAdminGiveFieldsNeverBecomeDefaults(string field, string json, string error)
    {
        var bridge = new Bridge();
        Assert.True(Ops.RegisterRulesSafe(bridge, new Logger()));
        var request = new JsonObject { ["item"] = "Wood" };
        request[field] = JsonNode.Parse(json);
        var e = await Assert.ThrowsAsync<AdminError>(() => bridge.Async["creative_give"](request));
        Assert.StartsWith(error, e.Message);
    }
}
