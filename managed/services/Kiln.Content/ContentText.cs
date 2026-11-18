// SPDX-License-Identifier: MIT
// Content.Text: the game's own text of a loca tag, in the language the player's game shows (kiln.content's text(), since
// 0.2.0-dev.29.18; game client only), and Content.TooltipValues: the rows of an item's tooltip with their labels.
// docs/kiln/services/content.md "The game's own texts".
//
// Threads. Text runs on the game client's main thread: the thread of the game's UI frame and the client world's step
// (Ui.Screens' UI ticks, Client.Tick, Client.Post, a Kiln.Ui menu entry's callback). The game switches its text language
// there and reloads the language's table; another thread could read the table while it is replaced, so native refuses
// other threads and Text throws there. Nothing is cached: each call reads the game's language and asks the game, so a
// language change applies at the next call. TextAvailable and TextLanguage: any thread.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>How the game shows a tooltip row's number (<c>impact::ImpcatValueNumberFormat</c>, spelled as the game
/// spells it).</summary>
public enum TooltipValueFormat
{
    /// <summary>The number as it is (a mana cost: 53).</summary>
    Normal,
    /// <summary>A percentage: the raw value is the percent (a config of 0.05 is 5).</summary>
    Percentage,
    /// <summary>A duration: the raw value is milliseconds (a config of 2.5 s is 2500), shown in seconds.</summary>
    Duration,
    /// <summary>A duration kept in milliseconds. Kiln shows the raw value with "ms"; not seen in a game tooltip yet.</summary>
    DurationMilliseconds,
    /// <summary>A number with two decimals: the raw value is a hundredth (a config of 2.6 is 260).</summary>
    Float,
    /// <summary>Only the label, no number.</summary>
    Numberless,
    /// <summary>A format this Kiln does not know (a later game build): the raw value as it is.</summary>
    Unknown,
}

/// <summary>One row of a tooltip (an entry of <c>uiValues</c>): its label, its number and how the game formats it.
/// Rows belong to the item type (the game's data for every item of that type), not to one item in a bag: a stack's
/// level, rarity or the player's attributes are not in them.</summary>
/// <param name="LocaId">The label's loca tag id (FNV-1a of the config's loca tag GUID); <see cref="Content.Text(uint)"/>
/// gives its text.</param>
/// <param name="Label">The label in the player's language, with the game's markup, or null where it could not be looked
/// up here (a dedicated server, a thread other than the game client's main thread, no such text).</param>
/// <param name="Raw">The value as the game's data stores it.</param>
/// <param name="Value">The number a player reads: <see cref="Raw"/> for Normal and Percentage (percent), seconds for
/// Duration, milliseconds for DurationMilliseconds, <see cref="Raw"/> / 100 for Float, 0 for Numberless.</param>
/// <param name="Display">Kiln's rendering of the number ("53", "+5%", "2.5s", "2.6", "" for Numberless), invariant culture.
/// The game's own formatting (decimal separator, spacing) may differ; format <see cref="Value"/> yourself to match it.</param>
/// <param name="IsSigned">The game shows a sign ("+5").</param>
/// <param name="IsScaled">The game scales this number when it shows it (by the weapon's power, an attribute:
/// <c>scaleData</c>); <see cref="Raw"/> and <see cref="Value"/> are the unscaled base.</param>
public sealed record TooltipValue(uint LocaId, string? Label, int Raw, double Value, string Display, TooltipValueFormat Format,
    bool IsSigned, bool IsScaled)
{
    public override string ToString() => $"{Label ?? $"0x{LocaId:x8}"}: {Display}";

    // One row of GetJson("uiValues")["value"]: {locaId, valueFormat, value, scaleData, isSigned, isScaled}.
    internal static TooltipValue? Parse(JsonNode? row, Func<uint, string?> label)
    {
        if (row is not JsonObject o) return null;
        var id = o["locaId"] is JsonValue idv && idv.TryGetValue<long>(out var l) ? (uint)l : 0u;
        var raw = o["value"] is JsonValue vv && vv.TryGetValue<long>(out var r) ? (int)r : 0;
        var name = o["valueFormat"] is JsonValue fv && fv.TryGetValue<string>(out var fs) ? fs : null;
        var format = name is not null && Enum.TryParse<TooltipValueFormat>(name, ignoreCase: false, out var f) && f != TooltipValueFormat.Unknown
            ? f : TooltipValueFormat.Unknown;
        var signed = o["isSigned"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
        var scaled = o["isScaled"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) && cb;
        var (value, display) = Render(raw, format, signed);
        return new TooltipValue(id, id == 0 ? null : label(id), raw, value, display, format, signed, scaled);
    }

    internal static (double Value, string Display) Render(int raw, TooltipValueFormat format, bool signed)
    {
        var value = format switch
        {
            TooltipValueFormat.Duration => raw / 1000.0,
            TooltipValueFormat.Float => raw / 100.0,
            TooltipValueFormat.Numberless => 0,
            _ => raw,
        };
        if (format == TooltipValueFormat.Numberless) return (0, "");
        var n = value.ToString("0.##", CultureInfo.InvariantCulture);
        if (signed && value > 0) n = "+" + n;
        return (value, format switch
        {
            TooltipValueFormat.Percentage => n + "%",
            TooltipValueFormat.Duration => n + "s",
            TooltipValueFormat.DurationMilliseconds => n + "ms",
            _ => n,
        });
    }
}

public static unsafe partial class Content
{
    /// <summary>The capability row of <see cref="Text(uint)"/>: available on a game client once the game has looked up its
    /// first text (its title screen does, right after boot); pending before that; unavailable on a dedicated server
    /// (wrong-side), on a game build without the text lookup, or with an older kiln.content.</summary>
    public const string TextCapability = "content.text";

    /// <summary>The row <see cref="TextCapability"/>, read from kiln.content now (the capability table follows it).</summary>
    private static (CapabilityState State, CapabilityReason Reason, string Text, uint Language, string? Key) TextState()
    {
        var t = T;
        if (t == null) return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.content is not loaded", 0, null);
        if (!ContentNative.HasText(t))
            return (CapabilityState.Unavailable, CapabilityReason.ServiceOlder, ContentCaps.TextOlder, 0, null);
        ContentNative.TextInfo i = default;
        i.Size = (uint)sizeof(ContentNative.TextInfo);
        if (t->GetTextInfo(&i) != ContentNative.Ok) return (CapabilityState.Unavailable, CapabilityReason.Faulted, "kiln.content's text_info failed", 0, null);
        var key = ContentNative.Text(i.LanguageKey, 16);
        return ((CapabilityState)i.State, (CapabilityReason)i.Reason, ContentNative.Text(i.Detail, 160), i.Language, key.Length == 0 ? null : key);
    }

    /// <summary>True when <see cref="Text(uint)"/> can answer here: a game client whose game has looked up a text already.
    /// False on a dedicated server; <c>Capabilities.Why("content.text")</c> says why.</summary>
    [AnyThread, ClientOnly]
    public static bool TextAvailable => TextState().State == CapabilityState.Available;

    /// <summary>The language the game shows its texts in now, as the game's key (<c>"en-us"</c>, <c>"de-de"</c>,
    /// <c>"pt-br"</c>), or null when <see cref="Text(uint)"/> is not available. Keep texts you looked up per language:
    /// when this changes, look them up again.</summary>
    [AnyThread, ClientOnly]
    public static string? TextLanguage => TextState() is { State: CapabilityState.Available } s ? s.Key : null;

    /// <summary>The game's own text of a loca tag id (<c>FNV-1a</c> over the tag GUID's 16 bytes: a tooltip row's
    /// <c>locaId</c>, <see cref="Texts.TagOf"/> of <c>ItemInfo.name</c>) in the language the player's game shows now, as the
    /// game's UI gets it: the texts of Kiln's new content answer in that language too. With the game's markup
    /// (<c>&lt;b&gt;</c>, ...); <see cref="Texts.GetPlain(uint)"/>'s rule strips it. A copy: keep it as long as you like.
    /// Null when the game has no text for the id, or when this is not available here (a dedicated server, before the game
    /// looked up its first text, the game's English table still loading): see <see cref="TextAvailable"/>. Nothing is
    /// cached: each call asks the game, so a language change applies at the next call.</summary>
    /// <exception cref="InvalidOperationException">Not on the game client's main thread (Ui.Screens' UI ticks,
    /// <c>Client.Tick</c>, <c>Client.Post</c>): not the overlay's render thread, not the server tick of a hosted game.</exception>
    [ClientOnly, ClientMainThread]
    public static string? Text(uint locaId) => TextCore(locaId, throwOnWrongThread: true);

    /// <summary>The text of a LocaTag reference (a GUID, as <c>ItemInfo.name</c>); see <see cref="Text(uint)"/>.</summary>
    [ClientOnly, ClientMainThread]
    public static string? Text(Guid locaTag) => locaTag == Guid.Empty ? null : Text(Texts.TagOf(locaTag));

    /// <summary>A loca tag as a GUID (hashed with the game's FNV-1a), as <c>0x1234abcd</c> or as a decimal id; see
    /// <see cref="Text(uint)"/>. Null for a string that is none of these.</summary>
    [ClientOnly, ClientMainThread]
    public static string? Text(string locaTag)
    {
        ArgumentNullException.ThrowIfNull(locaTag);
        var s = locaTag.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? Text(h) : null;
        if (Guid.TryParse(s, out var g)) return Text(g);
        return uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? Text(d) : null;
    }

    private const int MaxTextBytes = 1 << 20;

    internal static string? TextCore(uint locaId, bool throwOnWrongThread)
    {
        var t = T;
        if (t == null || !ContentNative.HasText(t)) return null;
        nuint len = 0;
        var small = stackalloc byte[512];
        var rc = t->Text(locaId, small, 512, &len);
        if (rc == ContentNative.Ok) return Encoding.UTF8.GetString(small, (int)len);
        if (rc == ContentNative.BufferTooSmall && len > 0 && len < MaxTextBytes)
        {
            var big = new byte[(int)len + 1];
            fixed (byte* b = big)
            {
                rc = t->Text(locaId, b, (nuint)big.Length, &len);
                if (rc == ContentNative.Ok) return Encoding.UTF8.GetString(b, (int)len);
            }
        }
        if (rc == ContentNative.WrongThread && throwOnWrongThread)
            throw new InvalidOperationException("Content.Text runs on the game client's main thread (a Ui.Screens UI tick, Client.Tick, " +
                                                "Client.Post), not on the overlay's render thread or the server tick of a hosted game");
        return null;  // not found, unavailable, or (TooltipValues) another thread
    }

    /// <summary>The rows of an item type's tooltip (its <c>uiValues</c>) with their labels in the player's language: the
    /// numbers a spell or tool shows (mana cost, charge time, radius, ...). Per item type, not per item in a bag. Empty
    /// for an item with no rows or one this world does not have. Damage and its element are not rows (the game computes
    /// them from <c>damageSetup</c>). On kiln.content's world thread, as <see cref="GameDataEntry"/>: a game client
    /// joined to a server is on its main thread, so the labels are there too; in a hosted game (the server tick) the rows
    /// come without labels: look them up with <c>Client.Post(() =&gt; Content.Text(row.LocaId))</c>. Not cached: every call
    /// reads the game's data (the numbers follow data packs); keep the result.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.gamedata")]
    public static IReadOnlyList<TooltipValue> TooltipValues(ItemId item) =>
        ItemData.Get(item.Value)?.TooltipValues() ?? [];
}

public sealed unsafe partial class GameDataEntry
{
    /// <summary>The tooltip rows of this item, perk, buff or skill node (<paramref name="list"/>: <c>uiValues</c>, or an
    /// item's <c>uiBuffValues</c>), with labels where <see cref="Content.Text(uint)"/> works on this thread; see
    /// <see cref="Content.TooltipValues(ItemId)"/>. Empty when the entry has no such list.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.gamedata")]
    public IReadOnlyList<TooltipValue> TooltipValues(string list = "uiValues")
    {
        ArgumentException.ThrowIfNullOrEmpty(list);
        JsonNode? node;
        try
        {
            node = GetJson(list);
        }
        catch (KeyNotFoundException)
        {
            return [];  // no such list on this entry
        }
        if (node?["value"] is not JsonArray rows) return [];
        var labels = Content.TextAvailable;
        var result = new List<TooltipValue>(rows.Count);
        foreach (var row in rows)
            if (TooltipValue.Parse(row, id => labels ? Content.TextCore(id, throwOnWrongThread: false) : null) is { } v)
                result.Add(v);
        return result;
    }
}
