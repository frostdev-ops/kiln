// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// 05a-value-providers (chapter 05a): Ui.Values, values in the game's own UI. Every row is held on a game client until its
// check passes (native/core/gates/dev28-05a-value-providers.json): ui.values.detail and ui.values.slot by 05a-c1,
// ui.values.hud by 05a-c2, ui.values.held by 05a-c3, ui.values.tooltip_lines by 05a-c4-viewer, ui.slots.inspect by
// 05a-c5. Each check registers its own providers (the research's payloads as product providers), removes them at its
// end, and expects that the game actually called them: a check whose hook never ran cannot pass. Orders 2400-2499.
// 05a-c4-short and 05a-c4-native are settled research results with no product check (no native line is drawn).
public sealed partial class ClientPassMod
{
    private const string ValuesArea = "Values in the game UI";

    private static bool HasFuel(in UiItemView v) => v.FuelMax > 0;

    [ClientCheck("05a-c1", "Detail numbers and slot bars", Area = ValuesArea, Since = Dev28, Tags = [Visual],
        Needs = ClientCheckNeeds.Hosted, Order = 2400)]
    public async Task ValuesDetailAndSlot(CheckContext c)
    {
        if (!await NeedOpen(c, Ui.Values.DetailCapability, Ui.Values.SlotCapability)) return;
        c.Instruct("Host a world and put a watering can (any container that holds water, with some water in it) in your " +
                   "backpack. Open the inventory and select the can so its detail view shows. Press continue.");
        await c.Verdict();
        int detail = 0, slot = 0;
        var d = Ui.Values.ItemBar(default, (in UiValueContext x, in UiItemView v, UiBarValue a) =>
        {
            if (!HasFuel(v)) return UiProviderDecision.Pass;
            Interlocked.Increment(ref detail);
            return UiProviderDecision.Replace(new UiBarEdit(Fraction: 0.25f, Current: 1_000_098, Max: 2_000_000));
        });
        var s = Ui.Values.SlotBar(default, (in UiValueContext x, in UiItemView v, UiBarValue a) =>
        {
            if (!HasFuel(v)) return UiProviderDecision.Pass;
            Interlocked.Increment(ref slot);
            return UiProviderDecision.Replace(new UiBarEdit(Fraction: 0.25f));
        });
        c.OnEnd(() =>
        {
            d.Dispose();
            s.Dispose();
        });
        c.Expect(d.Ok && s.Ok, $"both providers registered ({d.Reason ?? s.Reason})");
        c.Expect(await c.Until(() => Volatile.Read(ref detail) > 0 && Volatile.Read(ref slot) > 0, TimeSpan.FromSeconds(60),
            "the game drew the can's detail bar and slot bar through the providers"), "detail and slot providers were called");
        c.Instruct("The can's detail view should read 1000098 / 2000000 with the bar a quarter full, and the can's slot " +
                   "bar should be a quarter full. Is that what you see?");
        await c.Verdict();
        d.Dispose();
        s.Dispose();
        c.Instruct("The providers are gone. Are the can's number and both bars back to the game's own values?");
        await c.Verdict();
    }

    [ClientCheck("05a-c2", "Retained post-update HUD writes", Area = ValuesArea, Since = Dev28, Tags = [Visual],
        Needs = ClientCheckNeeds.Hosted, Order = 2401)]
    public async Task ValuesHud(CheckContext c)
    {
        if (!await NeedOpen(c, Ui.Values.HudCapability)) return;
        int calls = 0;
        UiValueRegistration Pair(HudValue vital, uint value) => Ui.Values.Hud(vital, (in UiValueContext x, in UiItemView v, UiBarValue a) =>
        {
            Interlocked.Increment(ref calls);
            return UiProviderDecision.Replace(new UiBarEdit(Current: value, Max: 100));
        });
        var regs = new[] { Pair(HudValue.Health, 37), Pair(HudValue.Mana, 11) };
        c.OnEnd(() =>
        {
            foreach (var r in regs) r.Dispose();
        });
        c.Expect(regs.All(r => r.Ok), "the health and mana providers registered");
        c.Expect(await c.Until(() => Volatile.Read(ref calls) > 0, TimeSpan.FromSeconds(30), "the player-UI update ran the HUD providers"),
            "HUD providers were called");
        c.Instruct("On the HUD, health should show about 37% and mana about 11%, steady (not flickering back to full). Is " +
                   "that what you see?");
        await c.Verdict();
        foreach (var r in regs) r.Dispose();
        c.Instruct("The providers are gone. Are health and mana back to your real values?");
        await c.Verdict();
    }

    [ClientCheck("05a-c3", "Nonzero held water and visible stamina identity", Area = ValuesArea, Since = Dev28, Tags = [Visual],
        Needs = ClientCheckNeeds.Hosted, Order = 2402)]
    public async Task ValuesHeldAndStamina(CheckContext c)
    {
        if (!await NeedOpen(c, Ui.Values.HeldCapability, Ui.Values.HudCapability)) return;
        c.Instruct("Put a watering can with some water (not empty) on your action bar and hold it in your hands. Press continue.");
        await c.Verdict();
        int held = 0, stamina = 0;
        var h = Ui.Values.HeldBar((in UiValueContext x, in UiItemView v, UiBarValue a) =>
        {
            Interlocked.Increment(ref held);
            return UiProviderDecision.Replace(new UiBarEdit(Current: 1, Max: 4));
        });
        var st = Ui.Values.Hud(HudValue.Stamina, (in UiValueContext x, in UiItemView v, UiBarValue a) =>
        {
            Interlocked.Increment(ref stamina);
            return UiProviderDecision.Replace(new UiBarEdit(Current: 23, Max: 100));
        });
        c.OnEnd(() =>
        {
            h.Dispose();
            st.Dispose();
        });
        c.Expect(await c.Until(() => Volatile.Read(ref held) > 0, TimeSpan.FromSeconds(30), "the held provider ran"),
            "the held provider was called (a held item with water)");
        c.Instruct("The held can's water indicator should show a quarter (1 of 4). Is that what you see?");
        await c.Verdict();
        c.Expect(Volatile.Read(ref stamina) > 0, "the stamina provider was called");
        c.Instruct("One HUD bar should now show about 23% and stay there. Which bar is it? It should be stamina: is it?");
        await c.Verdict();
        h.Dispose();
        st.Dispose();
        c.Instruct("The providers are gone. Are the held indicator and that bar back to normal?");
        await c.Verdict();
    }

    [ClientCheck("05a-c4-viewer", "Replacement owned details viewer", Area = ValuesArea, Since = Dev28, Tags = [Visual],
        Needs = ClientCheckNeeds.Hosted, Order = 2403)]
    public async Task ValuesDetailsViewer(CheckContext c)
    {
        if (!await NeedOpen(c, Ui.Values.TooltipCapability)) return;
        var multi = false;
        var english = "LAYOUT-LONG-BEGIN " + string.Join(" ", Enumerable.Range(1, 220).Select(i => $"word{i}")) + " LAYOUT-LONG-END";
        var german = "LAYOUT-DE-BEGIN Größenänderung der Wasserkanne, Füllstandsanzeige für den Brennstoffbehälter, " +
                     "Donaudampfschifffahrtsgesellschaftskapitänswitwenrente, Übergrößenträgerin, Maßkrüge ÄÖÜäöüß LAYOUT-DE-END";
        int calls = 0;
        var reg = Ui.Values.TooltipLines(default, (in UiValueContext x, in UiItemView v) =>
        {
            Interlocked.Increment(ref calls);
            return multi
                ? UiTooltipDecision.With(Enumerable.Range(1, 16).Select(i => (UiText)$"LAYOUT-MULTI-{i:00}").ToArray())
                : UiTooltipDecision.With(english, german);
        });
        c.OnEnd(() =>
        {
            Ui.Values.CloseTooltipDetails();
            reg.Dispose();
        });
        c.Instruct("Open the inventory and select any item so its detail view shows (selecting, not only hovering the action " +
                   "bar). Press continue.");
        await c.Verdict();
        c.Expect(await c.Until(() => Volatile.Read(ref calls) > 0, TimeSpan.FromSeconds(30), "the detail view collected the lines"),
            "the tooltip-lines provider was called");
        var open = Ui.Values.OpenTooltipDetails();
        c.Expect(open.Ok, $"the Item details window opened ({open.Reason})");
        c.Instruct("A window \"Item details\" shows a long English text ending in LAYOUT-LONG-END and a German one ending in " +
                   "LAYOUT-DE-END. Scroll its own pane: both END markers must be readable, every line inside the window (none " +
                   "running past its border), the German letters right. The game's own item tooltip is untouched. Is that so?");
        await c.Verdict();
        multi = true;
        c.Instruct("Select the same item again (or another one and back), then press Refresh in the Item details window. It " +
                   "should list LAYOUT-MULTI-01 to LAYOUT-MULTI-16 in order; scroll to see 01 and 16, none missing or overlapping. " +
                   "Is that so?");
        await c.Verdict();
        c.Instruct("Change the game's UI scale (Settings, Interface) to another value (1.5 if you can), reopen the window from " +
                   "the pause menu (\"Item details\") and check the same: everything inside, 01 and 16 reachable. Is that so?");
        await c.Verdict();
        Ui.Values.CloseTooltipDetails();
        c.Instruct("The window is closed. Are the item's own stats and description fully visible and as the game draws them?");
        await c.Verdict();
    }

    [ClientCheck("05a-c5", "Cursor, gamepad, geometry and chest transfer", Area = ValuesArea, Since = Dev28, Tags = [Visual],
        Needs = ClientCheckNeeds.Hosted, Order = 2404)]
    public async Task ValuesSlotGeometry(CheckContext c)
    {
        if (!await NeedOpen(c, Ui.Values.SlotsCapability)) return;
        c.Instruct("Open the inventory (backpack). Press continue.");
        await c.Verdict();
        VisibleSlotsResult last = new(false, "not read", null);
        c.Expect(await c.Until(() => (last = Ui.Values.Slots()) is { Ok: true, Snapshot.Slots.Count: > 0 }, TimeSpan.FromSeconds(20),
            "the slots of a drawn frame"), $"Slots() returned the drawn slots ({last.Reason})");
        if (last.Snapshot is not { } snap) return;
        c.Expect(snap.Slots.Any(s => s.OuterUi is not null), "at least one slot has an outer rectangle");
        c.Expect(snap.Slots.Any(s => (s.HolderFlags & 2) != 0), "the backpack's slots name the local player as holder");
        // outlines over every slot the game drew, in pixels from the UI units (viewport + units x pixels per unit)
        var latest = snap;
        var sub = Ui.Values.SubscribeSlots(r =>
        {
            if (r.Snapshot is { } s) latest = s;
        });
        Action<UiDraw> draw = ui =>
        {
            var s = latest;
            foreach (var slot in s.Slots)
            {
                if (slot.OuterUi is not { } u) continue;
                var x = s.ViewportPixels.X + u.X * s.PixelsPerUnit;
                var y = s.ViewportPixels.Y + u.Y * s.PixelsPerUnit;
                var color = slot.NativeState.Count > 0 && slot.NativeState[0] != 0 ? new Color(255, 200, 0) : new Color(0, 200, 255);
                ui.DrawRect(x, y, x + u.Width * s.PixelsPerUnit, y + u.Height * s.PixelsPerUnit, color, 0, filled: false);
            }
        };
        Ui.Frame += draw;
        c.OnEnd(() =>
        {
            Ui.Frame -= draw;
            sub.Dispose();
        });
        c.Instruct("Thin blue outlines are drawn where Kiln thinks each slot is. Do they sit exactly on the slots (no offset, " +
                   "same size)? Hover a slot with the mouse: its outline should turn yellow. Is that so?");
        await c.Verdict();
        c.Instruct("Now use a gamepad (or the keyboard's navigation) to move the selection between slots. Does the yellow " +
                   "outline follow the gamepad's selection too? Say what you see in the notes.");
        await c.Verdict();
        c.Instruct("Change the UI scale to another value and open the inventory again. Do the outlines still sit on the slots?");
        await c.Verdict();
        c.Instruct("Open a chest next to you and move an item from the backpack into it. Do outlines sit on the chest's slots " +
                   "too, and does the item's outline move with it?");
        await c.Verdict();
        var chest = Ui.Values.Slots();
        c.Expect(chest.Snapshot?.Slots.Any(s => (s.HolderFlags & 1) != 0 && (s.HolderFlags & 2) == 0) ?? false,
            "with a chest open, some slots name a holder that is not the local player");
    }
}
