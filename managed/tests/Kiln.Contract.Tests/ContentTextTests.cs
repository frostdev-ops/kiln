// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Content.Text and Content.TooltipValues (0.2.0-dev.29.18) on a fake kiln.content table with the game-text slots:
/// the text is copied out (a long one too), not found and unavailable are null, another thread throws, the tag forms
/// hash with the game's FNV-1a, an older table is never called; and the tooltip rows' formats.</summary>
public sealed unsafe class ContentTextTests : FakeTableTest
{
    private static readonly Dictionary<uint, string> Game = new();
    private static int _rc, _infoState, _calls;
    private static uint _lastId;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeText(uint id, byte* buf, nuint cap, nuint* len)
    {
        _calls++;
        _lastId = id;
        *len = 0;
        if (_rc != ContentNative.Ok) return _rc;
        if (!Game.TryGetValue(id, out var s)) return ContentNative.NotFound;
        var b = Encoding.UTF8.GetBytes(s);
        *len = (nuint)b.Length;
        if (buf != null && cap > 0)
        {
            var n = Math.Min(b.Length, (int)cap - 1);
            for (var i = 0; i < n; i++) buf[i] = b[i];
            buf[n] = 0;
        }
        return buf != null && cap > (nuint)b.Length ? ContentNative.Ok : ContentNative.BufferTooSmall;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeInfo(ContentNative.TextInfo* i)
    {
        if (i->Size < (uint)sizeof(ContentNative.TextInfo)) return ContentNative.InvalidArgument;
        i->State = (uint)_infoState;
        i->Reason = _infoState == 1 ? 0u : 9u;
        i->Language = 3;
        var key = "de-de"u8;
        for (var k = 0; k < key.Length; k++) i->LanguageKey[k] = key[k];
        return ContentNative.Ok;
    }

    private ContentNative.Table* _t;

    public ContentTextTests()
    {
        Game.Clear();
        Game[0xbe5b2600] = "Manakosten";
        _rc = ContentNative.Ok;
        _infoState = 1;
        _calls = 0;
        _t = Alloc<ContentNative.Table>();
        _t->Header = new ServiceHeader { Size = (uint)sizeof(ContentNative.Table), Version = 4 };
        _t->Text = &FakeText;
        _t->GetTextInfo = &FakeInfo;
        Swap(typeof(Content), "_table", Box(_t));
    }

    [Fact]
    public void A_text_is_copied_out_in_the_games_language()
    {
        Assert.True(Content.TextAvailable);
        Assert.Equal("de-de", Content.TextLanguage);
        Assert.Equal("Manakosten", Content.Text(0xbe5b2600));
        Assert.Equal(0xbe5b2600u, _lastId);
    }

    [Fact]
    public void A_text_longer_than_the_first_buffer_comes_whole()
    {
        var longText = string.Concat(Enumerable.Repeat("Ä Lore. ", 400));  // 3,600 bytes of UTF-8
        Game[0x1234] = longText;
        Assert.Equal(longText, Content.Text(0x1234));
        Assert.Equal(2, _calls);  // the first call said how long it is
    }

    [Fact]
    public void Not_found_and_unavailable_are_null()
    {
        Assert.Null(Content.Text(0x99999999));
        _rc = ContentNative.Unavailable;  // a dedicated server, or the game has looked nothing up yet
        Assert.Null(Content.Text(0xbe5b2600));
    }

    [Fact]
    public void Another_thread_throws()
    {
        _rc = ContentNative.WrongThread;
        var e = Assert.Throws<InvalidOperationException>(() => Content.Text(0xbe5b2600));
        Assert.Contains("main thread", e.Message);
    }

    [Fact]
    public void Tag_forms_hash_with_the_games_fnv1a()
    {
        var tag = Guid.Parse("8f5a2f3e-1b0c-4d7e-9a6b-2c1d0e9f8a7b");
        var id = Texts.TagOf(tag);
        Game[id] = "Label";
        Assert.Equal("Label", Content.Text(tag));
        Assert.Equal("Label", Content.Text(tag.ToString()));
        Assert.Equal("Label", Content.Text($"0x{id:x8}"));
        Assert.Equal("Label", Content.Text(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Null(Content.Text("not a tag"));
        Assert.Null(Content.Text(Guid.Empty));
        // the game's own: FNV-1a-32 over the GUID's 16 bytes in .NET's byte order
        var h = 0x811c9dc5u;
        foreach (var b in tag.ToByteArray()) h = (h ^ b) * 0x01000193u;
        Assert.Equal(h, id);
    }

    [Fact]
    public void Pending_is_not_available_and_has_no_language()
    {
        _infoState = 2;
        Assert.False(Content.TextAvailable);
        Assert.Null(Content.TextLanguage);
    }

    [Fact]
    public void An_older_table_is_not_available_and_is_never_called()
    {
        _t->Header = new ServiceHeader { Size = (uint)Marshal.OffsetOf<ContentNative.Table>(nameof(ContentNative.Table.Text)), Version = 4 };
        Assert.False(Content.TextAvailable);
        Assert.Null(Content.Text(0xbe5b2600));
        Assert.Equal(0, _calls);
        Assert.False(ContentNative.HasText(_t));
    }

    [Fact]
    public void Members_carry_their_thread_and_side()
    {
        var text = typeof(Content).GetMethod(nameof(Content.Text), [typeof(uint)])!;
        Assert.NotNull(text.GetCustomAttributes(typeof(ClientMainThreadAttribute), false).SingleOrDefault());
        Assert.NotNull(text.GetCustomAttributes(typeof(ClientOnlyAttribute), false).SingleOrDefault());
        var avail = typeof(Content).GetProperty(nameof(Content.TextAvailable))!;
        Assert.NotNull(avail.GetCustomAttributes(typeof(AnyThreadAttribute), false).SingleOrDefault());
        var rows = typeof(Content).GetMethod(nameof(Content.TooltipValues))!;
        Assert.NotNull(rows.GetCustomAttributes(typeof(TickThreadOnlyAttribute), false).SingleOrDefault());
    }
}

/// <summary>A tooltip row's number as a player reads it, from rows shaped as kiln.gamedata's GetJson("uiValues") gives
/// them (Fireball I: mana cost, charge time, a Float value; a perk's signed percentage).</summary>
public sealed class TooltipValueTests
{
    private static TooltipValue Row(string json, Func<uint, string?>? label = null) =>
        TooltipValue.Parse(JsonNode.Parse(json), label ?? (id => id == 0xbe5b2600 ? "Mana Cost" : null))!;

    [Fact]
    public void Normal_is_the_number_with_its_label()
    {
        var r = Row("""{"locaId": 3193644544, "valueFormat": "Normal", "value": 53, "scaleData": {}, "isSigned": false, "isScaled": false}""");
        Assert.Equal((0xbe5b2600u, "Mana Cost", 53, 53.0, "53", TooltipValueFormat.Normal), (r.LocaId, r.Label, r.Raw, r.Value, r.Display, r.Format));
        Assert.Equal("Mana Cost: 53", r.ToString());
    }

    [Fact]
    public void Duration_is_milliseconds_shown_in_seconds()
    {
        var r = Row("""{"locaId": 1551270780, "valueFormat": "Duration", "value": 2500, "isSigned": false, "isScaled": false}""");
        Assert.Equal(2.5, r.Value);
        Assert.Equal("2.5s", r.Display);
        Assert.Null(r.Label);
        Assert.Equal("0x5c76837c: 2.5s", r.ToString());
    }

    [Fact]
    public void Float_is_hundredths_and_percentage_keeps_its_sign()
    {
        Assert.Equal("2.6", Row("""{"locaId": 1, "valueFormat": "Float", "value": 260}""").Display);
        var p = Row("""{"locaId": 2, "valueFormat": "Percentage", "value": 5, "isSigned": true, "isScaled": true}""");
        Assert.Equal(("+5%", true, true), (p.Display, p.IsSigned, p.IsScaled));
        Assert.Equal("-3", Row("""{"locaId": 3, "valueFormat": "Normal", "value": -3, "isSigned": true}""").Display);
    }

    [Fact]
    public void Numberless_and_unknown_formats()
    {
        var n = Row("""{"locaId": 4, "valueFormat": "Numberless", "value": 9}""");
        Assert.Equal((0.0, "", 9), (n.Value, n.Display, n.Raw));
        var u = Row("""{"locaId": 5, "valueFormat": "SomethingNew", "value": 7}""");
        Assert.Equal((TooltipValueFormat.Unknown, "7"), (u.Format, u.Display));
        Assert.Equal("250ms", Row("""{"locaId": 6, "valueFormat": "DurationMilliseconds", "value": 250}""").Display);
        Assert.Null(TooltipValue.Parse(JsonValue.Create("..."), _ => null));
    }
}
