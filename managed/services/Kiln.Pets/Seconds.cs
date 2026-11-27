// SPDX-License-Identifier: MIT
// Durations are TimeSpan in C# (as Npcs.Say, AbilityTiming and BuffEntry.Duration); JSON keeps them as numbers of seconds
// (blocks/*.json "channelSeconds": 3, taming JSON "inactivitySeconds": 30). The converter maps one to the other; Seconds.Of
// is the internal ...Seconds aliases' way to a TimeSpan (a value no TimeSpan holds clamps, so the range checks refuse it).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

internal static class Seconds
{
    private const double Bound = 1e12;   // seconds (about 31,700 years): every TimeSpan.FromSeconds below it is exact enough

    /// <summary><paramref name="seconds"/> as a TimeSpan; NaN or a value past about 31,700 years clamps to the TimeSpan
    /// bounds (a range check refuses it then).</summary>
    internal static TimeSpan Of(double seconds) =>
        double.IsNaN(seconds) || seconds > Bound ? TimeSpan.MaxValue : seconds < -Bound ? TimeSpan.MinValue : TimeSpan.FromSeconds(seconds);

    /// <summary>A TimeSpan as JSON's number of seconds: a JSON number (not a string, not past about 31,700 years).</summary>
    internal sealed class Json : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number) throw new JsonException("a duration is a number of seconds");
            var s = reader.GetDouble();
            if (!double.IsFinite(s) || Math.Abs(s) > Bound) throw new JsonException($"{s} is not a number of seconds");
            return TimeSpan.FromSeconds(s);
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) => writer.WriteNumberValue(value.TotalSeconds);
    }
}
