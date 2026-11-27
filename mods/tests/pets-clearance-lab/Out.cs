// SPDX-License-Identifier: MIT
// The lab's evidence: one JSON object per line (runs.jsonl in the mod's data folder), read by tools/pets-clearance/profiles.py.
using System.Globalization;
using System.Text.Json.Nodes;
using Kiln;

namespace PetsClearanceLab;

internal sealed class Out(string path) : IDisposable
{
    private readonly StreamWriter _w = new(path, append: false) { AutoFlush = true };

    public void Write(string kind, JsonObject o)
    {
        o["kind"] = kind;
        _w.WriteLine(o.ToJsonString());
    }

    public void Dispose() => _w.Dispose();

    /// <summary>A number rounded to millimetres; null (JSON null) when it is not finite (JSON has no NaN).</summary>
    public static JsonNode? R(double v) => double.IsFinite(v) ? JsonValue.Create(Math.Round(v, 3)) : null;

    public static JsonArray Pos(WorldPosition p) => new(R(p.X), R(p.Y), R(p.Z));

    public static JsonArray Box(double x0, double x1, double y0, double y1, double z0, double z1) => new(R(x0), R(x1), R(y0), R(y1), R(z0), R(z1));

    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
