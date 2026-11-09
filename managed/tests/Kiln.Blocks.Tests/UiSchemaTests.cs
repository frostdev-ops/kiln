// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Kiln.BlockTests;

public sealed class UiSchemaTests
{
    private static string SchemaFile([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", "schema/kiln-blocks.schema.json"));

    [Fact]
    public void Every_nested_UI_record_field_is_in_the_schema_and_back()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaFile()));
        var defs = schema.RootElement.GetProperty("$defs");
        (string Def, Type Type)[] records =
        [
            ("uiAction", typeof(UiAction)), ("uiSet", typeof(UiSet)), ("uiMenu", typeof(UiMenu)),
            ("uiRowText", typeof(TextRow)), ("uiRowValue", typeof(ValueRow)), ("uiRowToggle", typeof(ToggleRow)),
            ("uiRowSlider", typeof(SliderRow)), ("uiRowChoice", typeof(ChoiceRow)),
            ("uiRowSeparator", typeof(SeparatorRow)), ("uiRowButton", typeof(ButtonRow)),
        ];
        foreach (var (def, type) in records)
        {
            var fields = type.GetProperties().Where(p => p.SetMethod is { IsPublic: true }).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order().ToArray();
            var keys = defs.GetProperty(def).GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n != "type").Order().ToArray();
            Assert.Equal(fields, keys);
        }
        var rowKinds = typeof(UiRow).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => (string)a.TypeDiscriminator!).Order().ToArray();
        var schemaKinds = defs.GetProperty("uiRow").GetProperty("oneOf").EnumerateArray()
            .Select(r => r.GetProperty("$ref").GetString()!.Split('/')[^1])
            .Select(r => defs.GetProperty(r).GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!).Order().ToArray();
        Assert.Equal(rowKinds, schemaKinds);
    }
}
