local player, hp, maxHp, healNow = nil, 0, 0, false

-- The player picks the key in the overlay's Bindings window.
Keys.hotkey("heal", "Heal to full", function() healNow = true end)

-- Every server tick in a world you host (every frame at the main menu, where there's no player): read and change the
-- game here.
Tick.add(function()
    player = player or Ecs.player()
    local h = player and Ecs.get(player, "Health")
    if not h then player = nil return end
    if healNow then
        -- slot 0 = current HP, slot 2 = max HP before gear and buffs: only ever raise HP to it
        h.dataStorage[0] = math.max(h.dataStorage[0], h.dataStorage[2])
        healNow = false
    end
    hp, maxHp = h.dataStorage[0], h.dataStorage[2]
end)

-- Every frame while the overlay is open: show what Tick read. Never touch the game here.
Draw.add(function()
    if not Overlay.isOpen() then return end
    if ImGui.Begin("My heal") then
        ImGui.Text(("HP: %d / %d"):format(hp, maxHp))
        if ImGui.Button("Heal") then healNow = true end
    end
    ImGui.End()
end)
