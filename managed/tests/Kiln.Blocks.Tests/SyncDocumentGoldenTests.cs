// SPDX-License-Identifier: MIT
// The content sync's documents, pinned: what a server sends (its exported state as Build hands it on) and what a client applies after matchServer's resets
// (ContentSync.AddResets), for a made-up state with clones (their "add" lists), their values, a sub-sequence, items, recipes, item data and buffs. The goldens in
// Goldens/sync/ were written once from the sync as it was before machine-local fields, so a state with no such field must give exactly these bytes.
// Writing them (only for a case added here, or a change that is meant): KILN_GOLDEN_WRITE=1 dotnet test managed/tests/Kiln.Blocks.Tests --filter SyncDocumentGoldenTests
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class SyncDocumentGoldenTests
{
    internal const string Held = "0713a111-b0ee-5bea-8e13-d75d5e72acd1", Lamp = "ea12005b-d9a5-5df5-8901-9f6b707aad93", Swing = "a25dda57-162a-5475-b1f5-e4d879ea4534";
    internal const string Rigid = "fd6c8517-5120-5cc0-bbaf-79a7d39a6457", Skinned = "19796b32-33cb-507e-827a-b2c23f720126", Graph = "85c7e67f-5f6a-5abc-b492-81ee1b1b0ca8";

    /// <summary>What a dedicated server that did not cook exports: a held template with its rigid model, a lamp, a sequence clone with a second sub-sequence,
    /// a game template, items, a recipe, item data and a buff.</summary>
    internal const string ServerExport = """
        {"format":1,"@fields":14,
         "templates":{"add":[{"guid":"0713a111-b0ee-5bea-8e13-d75d5e72acd1","name":"m.sword.moving","base":"Visuals_Staff_01_Magic_Basic01","fields":{"ModelResource.model":"fd6c8517-5120-5cc0-bbaf-79a7d39a6457","VfxComponentResource.vfx":"none"},"removeComponents":["VfxParametersTemplateComponent"]},
                            {"guid":"ea12005b-d9a5-5df5-8901-9f6b707aad93","name":"k.lamp","base":"Prop_Lamp_01","fields":{"LifeTime.lifeTime":5}}],
                      "0713a111-b0ee-5bea-8e13-d75d5e72acd1":{"@name":"m.sword.moving","ModelResource.model":"fd6c8517-5120-5cc0-bbaf-79a7d39a6457","VfxComponentResource.vfx":"none","SlotAttachment.attachmentSlot":"HandL"},
                      "ea12005b-d9a5-5df5-8901-9f6b707aad93":{"@name":"k.lamp","LifeTime.lifeTime":5},
                      "0x1f2e3d4c":{"@name":"Enemy_Vukah_Melee","Level.dataStorage[0]":20}},
         "sequences":{"add":[{"guid":"a25dda57-162a-5475-b1f5-e4d879ea4534","name":"m.sword.heavy","base":"Attack_Player_Melee_Heavy_2H_Axe_01","fields":{}}],
                      "a25dda57-162a-5475-b1f5-e4d879ea4534":{"@name":"m.sword.heavy","length":2.2,"events[SpawnImpact#0].time":0.9},
                      "a25dda57-162a-5475-b1f5-e4d879ea4534#1":{"@name":"m.sword.heavy#1","length":2.2}},
         "items":{"0x2b0cf1d5":{"@name":"Material_T1_Wood_Logs","maxStackSize":1000}},
         "recipes":{"0x4da88f73":{"@name":"Recipe_Torch","duration":2}},
         "itemdata":{"0x77aa11bb":{"@name":"m.sword","@refs":true,"equipment.visualEntity":"0713a111-b0ee-5bea-8e13-d75d5e72acd1"}},
         "buffs":{"0x5f425faf":{"@name":"buff_5f425faf","defaultLifeTime":9}}}
        """;

    /// <summary>What a cooked client exports of its own: the held template's skinned model and graph, its hand, a local-only change of a game template, a wood
    /// stack that differs from the server's, a local-only item field and a local-only buff field.</summary>
    internal const string ClientExport = """
        {"format":1,"@fields":9,
         "templates":{"add":[{"guid":"0713a111-b0ee-5bea-8e13-d75d5e72acd1","name":"m.sword.moving","base":"Visuals_Staff_01_Magic_Basic01","fields":{"ModelResource.model":"19796b32-33cb-507e-827a-b2c23f720126","AnimationGraphResourceComponent2.animationGraph":"85c7e67f-5f6a-5abc-b492-81ee1b1b0ca8","VfxComponentResource.vfx":"none"}}],
                      "0713a111-b0ee-5bea-8e13-d75d5e72acd1":{"@name":"m.sword.moving","ModelResource.model":"19796b32-33cb-507e-827a-b2c23f720126","AnimationGraphResourceComponent2.animationGraph":"85c7e67f-5f6a-5abc-b492-81ee1b1b0ca8","VfxComponentResource.vfx":"none","SlotAttachment.attachmentSlot":"HandL"},
                      "0x0badcafe":{"@name":"Enemy_Fell_Warrior","Health.maxHealth":500}},
         "items":{"0x2b0cf1d5":{"@name":"Material_T1_Wood_Logs","maxStackSize":200,"weight":0.5}},
         "buffs":{"0x5f425faf":{"@name":"buff_5f425faf","color":4294967295}}}
        """;

    private static string Dir([CallerFilePath] string self = "") => Path.Combine(Path.GetDirectoryName(self)!, "Goldens", "sync");

    /// <summary>Marks of a clone the state above does not have: the machine-local code runs, and must change nothing here.</summary>
    internal static MachineLocalFields Elsewhere()
    {
        var m = new MachineLocalFields();
        m.Add("templates", Guid.Parse("24e2460b-1615-5b86-ae02-556d8d39b6a4"), "ModelResource.model");
        return m;
    }

    /// <summary>What the server sends of its export (definitions and programs are added beside it, by other code).</summary>
    internal static string Sent(string export, MachineLocalFields? marks = null) => ContentSync.WithoutMachineLocal(export, marks ?? Elsewhere(), out _);

    internal static JsonObject Reset(string server, string local, MachineLocalFields? marks = null)
    {
        var doc = JsonNode.Parse(server)!.AsObject();
        var n = ContentSync.AddResets(doc, JsonNode.Parse(local)!.AsObject(), marks ?? Elsewhere());
        doc["@resets"] = n;
        return doc;
    }

    [Fact]
    public void Without_marks_of_its_own_the_export_is_sent_byte_for_byte() =>
        Assert.Same(ServerExport, Sent(ServerExport, new MachineLocalFields()));

    [Fact]
    public void What_a_server_sends_is_its_golden() =>
        ItemCompileGoldenTests.Compare(Dir(), "sent", JsonNode.Parse(Sent(ServerExport))!.AsObject());

    [Fact]
    public void What_a_client_applies_after_matching_the_server_is_its_golden() =>
        ItemCompileGoldenTests.Compare(Dir(), "applied", Reset(Sent(ServerExport), ClientExport));

    [Fact]
    public void Matching_a_vanilla_server_puts_back_everything_of_its_own() =>
        ItemCompileGoldenTests.Compare(Dir(), "vanilla", Reset("""{"format":1}""", ClientExport));
}
