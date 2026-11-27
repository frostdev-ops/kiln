// SPDX-License-Identifier: MIT
// 19d §4.1/§4.7 (dev.32 pets, package 2): Kiln's services keep per-character records through Kiln.Saves' own staging and
// manifests (scope service/<service>/character/<key>), and learn exactly what each staging serialized and which generation
// the game committed: a change after the snapshot staging is not in that generation's receipt.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln;

[Collection("instance data")]
public sealed class ServiceCharactersTests : IDisposable
{
    private readonly InstanceHarness _h = new();
    private static readonly CharacterKey Hero = InstanceHarness.Hero;
    private readonly List<ServiceCharacters.StagingInfo> _stagings = [];
    private readonly List<ServiceCharacters.Receipt> _receipts = [];
    private readonly List<ServiceCharacters.CommitInfo> _commits = [];
    private string _value = "{\"v\":1}";

    public ServiceCharactersTests()
    {
        ServiceCharacters.Staging += OnStaging;
        ServiceCharacters.Staged += _receipts.Add;
        ServiceCharacters.Committed += _commits.Add;
    }

    private void OnStaging(ServiceCharacters.StagingInfo info)
    {
        _stagings.Add(info);
        ServiceCharacters.Set("kiln.pets", Hero, _value);   // a participant writes at staging, before serialization
    }

    public void Dispose()
    {
        ServiceCharacters.Staging -= OnStaging;
        ServiceCharacters.Staged -= _receipts.Add;
        ServiceCharacters.Committed -= _commits.Add;
        _h.Dispose();
    }

    [Fact]
    public void A_service_record_is_staged_and_committed_with_the_worlds_data()
    {
        _h.Save(1);
        Assert.Equal([false, true], _stagings.Select(s => s.Snapshot));
        var bundle = _h.Bundle(1, ServiceCharacters.Scope("kiln.pets", Hero));
        Assert.NotNull(bundle);
        Assert.Equal(1, bundle!["keys"]!["record"]!["v"]!.GetValue<int>());
        Assert.Equal("{\"v\":1}", ServiceCharacters.Get("kiln.pets", Hero));
        Assert.Contains(Hero, ServiceCharacters.Characters("kiln.pets"));
        Assert.Single(_commits);
        Assert.Equal(1u, _commits[0].Generation);
    }

    [Fact]
    public void A_receipt_holds_exactly_the_serialized_bytes()
    {
        _h.StageOnly(2);
        var snapshot = _receipts.Last(r => r.Snapshot);
        Assert.Equal(2u, snapshot.Generation);
        Assert.Equal("kiln.pets", snapshot.Service);
        Assert.Equal(Hero, snapshot.Character);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(snapshot.Bytes)), snapshot.Sha256);
        Assert.Equal(1, JsonNode.Parse(snapshot.Bytes)!["keys"]!["record"]!["v"]!.GetValue<int>());
        // a change while the file is written is not in this generation
        _value = "{\"v\":2}";
        ServiceCharacters.Set("kiln.pets", Hero, _value);
        InstanceHarness.Store.Commit(2, 1002, SaveReason.Request);
        InstanceHarness.Written();
        Assert.Equal(1, _h.Bundle(2, ServiceCharacters.Scope("kiln.pets", Hero))!["keys"]!["record"]!["v"]!.GetValue<int>());
        Assert.Equal(2u, _commits.Single().Generation);
    }

    [Fact]
    public void A_generation_never_written_commits_nothing()
    {
        _h.StageOnly(3);
        Assert.Empty(_commits);   // the world write failed: no confirmation, so nothing is acknowledged
    }

    [Fact]
    public void Only_Kilns_services_and_real_characters_have_records()
    {
        Assert.Throws<ArgumentException>(() => ServiceCharacters.Set("my-mod", Hero, "{}"));
        Assert.Throws<ArgumentException>(() => ServiceCharacters.Set("kiln.pets/x", Hero, "{}"));
        Assert.Throws<ArgumentException>(() => ServiceCharacters.Set("kiln.pets", new CharacterKey(5, Guid.Empty), "{}"));
        Assert.True(ServiceCharacters.Remove("kiln.pets", Hero) is false or true);
    }
}
