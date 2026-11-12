// SPDX-License-Identifier: MIT
// Machine-local fields in the content sync: a held template's model and graph stay each machine's own. The server leaves them out of what it sends, a client's
// matchServer leaves its own alone, and every other field of the same clone syncs and wins as before. Over the made-up state of SyncDocumentGoldenTests.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("served seam")]
public sealed class MachineLocalSyncTests
{
    private static MachineLocalFields HeldMarks()
    {
        var m = new MachineLocalFields();
        foreach (var f in Compiler.MachineLocalPaths) m.Add("templates", Guid.Parse(SyncDocumentGoldenTests.Held), f);
        return m;
    }

    private const string Held = SyncDocumentGoldenTests.Held;

    [Fact]
    public void The_server_leaves_the_held_templates_model_and_graph_out()
    {
        var sent = JsonNode.Parse(SyncDocumentGoldenTests.Sent(SyncDocumentGoldenTests.ServerExport, HeldMarks()))!.AsObject();
        var held = sent["templates"]![Held]!.AsObject();
        Assert.Null(held["ModelResource.model"]);
        Assert.Null(held["AnimationGraphResourceComponent2.animationGraph"]);
        // the rest of it goes as before: the hand and the VFX, its definition, and every other clone
        Assert.Equal("HandL", (string)held["SlotAttachment.attachmentSlot"]!);
        Assert.Equal("none", (string)held["VfxComponentResource.vfx"]!);
        Assert.Equal(2, sent["templates"]!["add"]!.AsArray().Count);
        Assert.Equal(SyncDocumentGoldenTests.Rigid, (string)sent["templates"]!["add"]![0]!["fields"]!["ModelResource.model"]!);
        Assert.Equal(5, (int)sent["templates"]![SyncDocumentGoldenTests.Lamp]!["LifeTime.lifeTime"]!);
        Assert.Equal(13, (int)sent["@fields"]!);
    }

    [Fact]
    public void A_cooked_client_keeps_its_own_model_and_graph_and_the_server_wins_the_rest()
    {
        // the client has the hand on the other side and one field of its own the server lacks
        var local = JsonNode.Parse(SyncDocumentGoldenTests.ClientExport)!.AsObject();
        local["templates"]![Held]!["SlotAttachment.attachmentSlot"] = "HandR";
        local["templates"]![Held]!["VfxComponent.slot"] = "Root";
        var applied = SyncDocumentGoldenTests.Reset(SyncDocumentGoldenTests.Sent(SyncDocumentGoldenTests.ServerExport, HeldMarks()), local.ToJsonString(), HeldMarks());
        var held = applied["templates"]![Held]!.AsObject();
        // nothing over its own model, and its graph not put back
        Assert.Null(held["ModelResource.model"]);
        Assert.Null(held["AnimationGraphResourceComponent2.animationGraph"]);
        // an unmarked field of the same clone: the server's value is sent and wins, and one of its own only is put back
        Assert.Equal("HandL", (string)held["SlotAttachment.attachmentSlot"]!);
        Assert.Equal(1, (int)held["VfxComponent.slot"]!["mul"]!);
        // without the marks, today's: the server's rigid model over the client's, and the client's graph back to the staff's
        var before = SyncDocumentGoldenTests.Reset(SyncDocumentGoldenTests.ServerExport, local.ToJsonString(), new MachineLocalFields());
        Assert.Equal(SyncDocumentGoldenTests.Rigid, (string)before["templates"]![Held]!["ModelResource.model"]!);
        Assert.Equal(1, (int)before["templates"]![Held]!["AnimationGraphResourceComponent2.animationGraph"]!["mul"]!);
    }

    [Fact]
    public void A_sub_sequence_key_and_an_emptied_entry_follow()
    {
        var m = new MachineLocalFields();
        m.Add("sequences", Guid.Parse(SyncDocumentGoldenTests.Swing), "length");
        var sent = JsonNode.Parse(ContentSync.WithoutMachineLocal(SyncDocumentGoldenTests.ServerExport, m, out var removed))!.AsObject();
        Assert.Equal(2, removed);
        Assert.Null(sent["sequences"]![$"{SyncDocumentGoldenTests.Swing}#1"]);           // nothing left in it but its name: left out
        Assert.Equal(0.9, (double)sent["sequences"]![SyncDocumentGoldenTests.Swing]!["events[SpawnImpact#0].time"]!);
    }

    [Fact]
    public void An_animated_look_marks_each_hands_model_and_graph_and_nothing_else()
    {
        var real = Compiler.Served;
        Compiler.Served = (_, _) => true;
        try
        {
            JsonObject L(WeaponClass c) => Compiler.Lower(new ModBlocks("sync-held", 1) { ManifestClients = "mod" }, "brand",
                new WeaponBlock { Name = "Brand", Class = c, Visual = new VisualBlock { Animated = new AnimatedModel { Model = "m", Graph = "g" } } }).Low.Json;
            var one = L(WeaponClass.Sword)["animatedHeld"]!["machineLocal"]!.AsArray();
            Assert.Equal([(BlockIds.Derive("sync-held", "template", "brand.moving").ToString(), "ModelResource.model"), (BlockIds.Derive("sync-held", "template", "brand.moving").ToString(), "AnimationGraphResourceComponent2.animationGraph")],
                one.Select(x => ((string)x!["template"]!, (string)x["field"]!)));
            var pair = L(WeaponClass.Daggers)["animatedHeld"]!["machineLocal"]!.AsArray();
            Assert.Equal(4, pair.Count);
            Assert.Equal([BlockIds.Derive("sync-held", "template", "brand.moving.r").ToString(), BlockIds.Derive("sync-held", "template", "brand.moving.l").ToString()],
                pair.Select(x => (string)x!["template"]!).Distinct());
        }
        finally { Compiler.Served = real; }
    }
}
