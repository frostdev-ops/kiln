// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// Session 1: hosted world, the content profile (client-pass and the samples listed in docs/kiln/client-pass.md).
// First the steps done in the launcher and the main menu before the world loads, then everything a player sees in a
// hosted world. Orders 101-199.
public sealed partial class ClientPassMod
{
    // ---- before the world: the launcher and the main menu ----

    [ClientCheck("launcher.one-list", "Play lists every mod, with switches and Load order", Area = "Launcher", Since = Dev24,
        Tags = [Launcher], Order = 101)]
    public Task LauncherOneList(CheckContext c) => Outside(c,
        "Before this start, on the launcher's Play page: every mod you added is in one list, for whatever loader, and mods " +
        "you put into the game folder by hand are there too. Each row has an on/off switch where Kiln can switch it, and " +
        "its menu (three dots or a right click) has settings, versions, folder and Remove. Load order shows every mod in " +
        "start order. Did the list and Load order match the mods you have?");

    [ClientCheck("launcher.profiles-settings", "Profiles live in Settings", Area = "Launcher", Since = Dev24,
        Tags = [Launcher], Order = 102)]
    public Task LauncherProfilesSettings(CheckContext c) => Outside(c,
        "Before this start, in the launcher's Settings, Profiles: save the current set under the name \"Client pass\", load " +
        "My mods, load \"Client pass\" again, rename it and back, and look at the library's storage (size on disk). " +
        "Play has no profile picker. Did each of these work, with \"Client pass\" loaded for this start?");

    [ClientCheck("launcher.archives", "7z, rar and a game-folder zip dropped on the launcher", Area = "Launcher", Since = Dev22,
        Tags = [Launcher], Order = 103)]
    public Task LauncherArchives(CheckContext c) => Outside(c,
        "Before this start, drop three archives on the launcher's window, one at a time: a mod as .7z, a mod as .rar, and " +
        "a zip shaped like the game folder. Read the result line and its Details view each time, then switch off or " +
        "remove what you do not want for this pass. Did all three import, with a result line and Details that read well?");

    [ClientCheck("launcher.game-folder-rows", "Game folder rows, Load them and Add it", Area = "Launcher", Since = Dev22,
        Tags = [Launcher], Order = 104)]
    public Task LauncherGameFolderRows(CheckContext c) => Outside(c,
        "Before this start, with at least one mod extracted into the game folder by hand: the Game folder rows in the list " +
        "on Play name exactly what is in the folder, each saying whether it loads and why. Press Load them (or Add it) on " +
        "one row. Did the rows match the folder, and did the button do what it says?");

    [ClientCheck("launcher.support-report", "A support report holds nothing private", Area = "Launcher", Since = Dev22,
        Tags = [Launcher], Order = 105)]
    public Task LauncherSupportReport(CheckContext c) => Outside(c,
        "In the launcher's Logs page press Save report, open the zip and read summary.txt (and skim the logs in it). Look " +
        "for your user name, your home folder, your machine's name, a Steam id or an IP address. Is none of them in it?");

    [ClientCheck("menu.mods-page", "MODS in the main menu opens the mod manager", Area = "Menus and hooks", Since = Dev22,
        Tags = [Visual], Order = 106)]
    public Task MenuModsPage(CheckContext c) => Outside(c,
        "Before you hosted this world, in the game's main menu: press MODS. A full page lists the mods of every framework " +
        "with their state and reasons, with search and a filter for mods that need attention. Leave it with Back. Did the " +
        "page open with the mods listed, and did leaving bring you back to the main menu?");

    [ClientCheck("menu.mods-with-shroudforge", "MODS is in the main menu while Kiln runs ShroudForge", Area = "Menus and hooks",
        Since = Dev27, Tags = [Visual], Order = 109)]
    public Task MenuModsWithShroudForge(CheckContext c) => Outside(c,
        "With a ShroudForge mod switched on in this profile (the one of frameworks.shroudforge), before you hosted this " +
        "world: was MODS in the game's main menu, above Settings, and did it open the page? Before 0.2.0-dev.27 the main " +
        "menu had no MODS entry in such a start. If ShroudForge's own install is in the game folder too: switch on \"Use " +
        "my installed ShroudForge\" in the launcher's Settings, start the game again and look once more (ShroudForge may " +
        "add a row of its own next to it), then switch it off again. Was MODS in the main menu each time?");

    [ClientCheck("menu.sf-waiting-for", "A waiting ShroudForge mod says what it waits for", Area = "Menus and hooks",
        Since = Dev27, Tags = [Visual], Order = 145)]
    public Task MenuSfWaitingFor(CheckContext c) => Outside(c,
        "With SF Unlimited Flight switched on in this profile (its Upward Gliding setting on, the default), in this world " +
        "you host: stand on the ground, open the pause menu, then Mods, and pick SF Unlimited Flight under ShroudForge. Its " +
        "state says Waiting, and under WHAT IT WAITS FOR the page says it waits for you to glide (\"You to glide. It changes " +
        "the glider only while you are gliding...\"). Did the page say what the mod waits for in those plain words, not " +
        "only Waiting?");

    [ClientCheck("menu.kiln-menu", "Kiln's menu opens and draws", Area = "Menus and hooks", Since = Dev21,
        Tags = [Visual], Order = 107)]
    public Task MenuKilnMenu(CheckContext c) => Outside(c,
        "Open Kiln's menu now (the Kiln entry in the pause menu) and go through two of its pages, then close it. Did it open, " +
        "draw every page without broken text or boxes, and close again with the game still answering?");

    [ClientCheck("menu.config-apply", "A mod's settings keep the applied values, and a slider takes a typed value",
        Area = "Menus and hooks", Since = Dev27, Tags = [Visual], Order = 144)]
    public Task MenuConfigApply(CheckContext c) => Outside(c,
        "Open the pause menu, then Mods, and a mod with settings (or Kiln settings). Change a switch and a slider and " +
        "press Apply: does the table keep the new values (before 0.2.0-dev.27 it showed the old ones until the page was " +
        "opened again)? Beside each slider there is a number field: type a value of the range into one. Did the slider " +
        "follow, and after Apply, closing the menu and opening the page again, is that value still there?");

    [ClientCheck("menu.rows-quit", "Quit stays on the screen with many mods' menu entries (the rule Few)", Area = "Menus and hooks",
        Since = Dev27, Tags = [Visual], Order = 148)]
    public Task MenuRowsLeaveQuitOnScreen(CheckContext c) => Outside(c,
        "Play this one on a 1920x1080 screen, with mods that add more than 3 entries to the pause menu: the test mod " +
        "mods/tests/menu-many-test adds 8 (its own setting Entries, 0 to 24, under Mods, takes a new number at once). In Kiln " +
        "settings set menus-client modEntries to Few and Apply. Open the pause menu: does it show Kiln's Mods entry but " +
        "none of the mods' entries, and are Quit and Return to Main Menu on the screen? Open Mods: under Menu entries every " +
        "one of those entries is listed; press Open on one: does the Kiln menu close and the entry do what it does in the " +
        "menu? Then set modEntries to Always, Apply, and open the pause menu again: how many entries could there be before " +
        "Quit left the screen (say the number in your note)? Set it back to Auto. Was Quit on the screen with Few, and did " +
        "the Mods page reach every entry?");

    [ClientCheck("menu.scroll", "The pause menu scrolls when mods' entries do not fit (experiment)", Area = "Menus and hooks",
        Since = Dev27, Tags = [Visual], Order = 149)]
    public Task MenuScroll(CheckContext c) => Outside(c,
        "An experiment: Kiln wraps the pause menu in the game's own scroll view once it has more than 3 mods' entries. It " +
        "may end the game once; then send kiln/logs/kiln.log and kiln/state/menu-scroll.off. Setup: modEntries Auto (the " +
        "default), mods/tests/menu-many-test with Entries 20 (more than fit), any resolution (say which). kiln.log should " +
        "say \"ui: the pause menu can scroll (experiment)\" at start. 1) Open the pause menu: does the list reach from " +
        "the top of the menu down to near the bottom of the screen (not a third of it), with every entry there, and " +
        "kiln.log say \"ui: the pause menu scrolls: ...\" with a scroll view almost as tall as the column? 2) The " +
        "scrollbar at the list's right: does it look like the game's other list scrollbars (a track and a thumb about as " +
        "long as the visible part of the list), not a plain white bar? 3) Turn the mouse wheel over it: does the list " +
        "scroll and the thumb move, with entries clipped at the menu's edges? 4) With the arrow keys go down past the last " +
        "visible entry to Quit, then back up to Resume: does the list follow the highlighted entry each way, so it stays " +
        "visible? kiln.log says \"ui: the pause menu's scroll view moved: offset ...\" the first times it moves. 5) " +
        "Scroll with the wheel, then press an arrow key: the view stays where the wheel put it until the highlight moves, " +
        "then follows it. 6) The same as 4) with a gamepad (d-pad, then the stick), down to Quit and back. 7) Return to " +
        "Main Menu and back into the world: does it still work? 8) Set menu-many-test's Entries to 2 (3 or fewer mods' " +
        "entries in all): is it exactly as before 0.2.0-dev.27 (no scroll view, centred as the game draws it)? Send " +
        "screenshots of 1), 4) at Quit and 8), and every kiln.log line starting with \"ui: the pause menu\" or " +
        "\"ui: the scrolling pause menu\". Did it fill the column, look like the game's scrollbars, and follow the keys " +
        "and the pad to Quit and back?");

    [ClientCheck("boot.proxy", "The game started with Kiln's proxy", Area = "Menus and hooks", Since = Dev21, Order = 108)]
    public async Task BootProxy(CheckContext c)
    {
        c.Expect(Game.KilnVersion.Length > 0, "Kiln reports its version", Game.KilnVersion);
        c.Expect(Client.Available && Client.IsClient, "Kiln runs inside a game client");
        c.Expect(Client.IsHosting, "this game client hosts the world");
        c.Instruct("Nothing to do: the game is running with Kiln (this list is Kiln's). The assertions below say what Kiln " +
                   "read. Did the game start normally from Steam or the launcher, with no error dialog?");
        await c.Verdict();
    }

    // ---- in the world ----

    [ClientCheck("hooks.camera-render-ui", "Camera, render and game UI with Kiln's hooks", Area = "Menus and hooks", Since = Dev21,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 110)]
    public async Task HooksCameraRenderUi(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        c.Instruct("Turn the camera all the way round, zoom in and out, open the inventory, the map and the crafting menu, " +
                   "close each, and glide or jump once. Watch for stutter, a camera that sticks, or a menu that draws wrong. " +
                   "Did the camera and every menu behave as without Kiln?");
        await c.Verdict();
    }

    [ClientCheck("drawing.render-sample", "Drawing on screen and in the world", Area = "Drawing", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 111)]
    public async Task DrawingRenderSample(CheckContext c)
    {
        if (!await Need(c, RenderSample) || !await NeedPlayer(c)) return;
        c.Instruct("Look at the screen's top: the render sample draws an fps counter and a compass strip. Press Ctrl+J to drop " +
                   "a waypoint where you stand, walk 20 m away and turn round: a marker with a distance label stays on that " +
                   "spot and follows the camera. Ctrl+Shift+J removes it. Did the counter, the compass and the marker draw " +
                   "and stay in place?");
        await c.Verdict();
    }

    [ClientCheck("vfx.spawn-shapes", "VFX spawn shapes and streaks", Area = "VFX", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 112)]
    public async Task VfxSpawnShapes(CheckContext c)
    {
        if (!await Need(c, VfxSample) || !await NeedPlayer(c)) return;
        var at = Ahead(c, 5, up: 1);
        c.Expect(at is not null, "the player's place is known");
        if (at is null) { await c.Verdict(); return; }
        c.Instruct("Stand still and look 5 m ahead: every 3 seconds a spark ring plays there. Sparks start on a ring and a " +
                   "disc, not one point, fly outward, and are drawn as streaks stretched along their path. Do you see a " +
                   "ring of outward-flying streaks?");
        var vfx = VfxId.Named(VfxSample, "spark_ring");
        await RepeatUntilVerdict(c, TimeSpan.FromSeconds(3), () => PlayVfx(c, vfx, at.Value));
    }

    [ClientCheck("vfx.play-params", "One VFX recoloured and resized by parameters", Area = "VFX", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 113)]
    public async Task VfxPlayParams(CheckContext c)
    {
        if (!await Need(c, VfxSample) || !await NeedPlayer(c)) return;
        if (Ahead(c, 5, right: -2, up: 1) is not { } plain || Ahead(c, 5, right: 2, up: 1) is not { } blue)
        {
            c.Expect(false, "the player's place is known");
            await c.Verdict();
            return;
        }
        var burst = VfxId.Named(VfxSample, "ember_burst");
        var p = new VfxParams().Set("color", new Color(64, 160, 255)).Set("size", 2f).Set("count", 2f).Set("brightness", 0.5f);
        c.Instruct("Look 5 m ahead: every 3 seconds two ember bursts play side by side. One is the burst as written (orange " +
                   "sparks and smoke), the other the same burst with parameters: blue, twice the size, twice as many sparks, " +
                   "half as bright. Is one burst clearly blue and bigger than the other?");
        await RepeatUntilVerdict(c, TimeSpan.FromSeconds(3), () =>
        {
            PlayVfx(c, burst, plain);
            PlayVfx(c, burst, blue, p);
        });
    }

    [ClientCheck("vfx.game-by-name", "A game VFX by name and a Kiln VFX", Area = "VFX", Since = Dev21,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 114)]
    public async Task VfxGameByName(CheckContext c)
    {
        if (!await Need(c, VfxSample) || !await NeedPlayer(c)) return;
        if (Ahead(c, 6, up: 1) is not { } at)
        {
            c.Expect(false, "the player's place is known");
            await c.Verdict();
            return;
        }
        var game = VfxId.Game("Explosion_Player_Spell_Fireball_1");
        var glow = VfxId.Named(VfxSample, "warm_glow");
        c.Instruct("Look 6 m ahead: every 4 seconds the game's own fireball explosion plays there, then a warm light from " +
                   "the sample glows on the ground. Nothing is magenta. Do both show, in their own colours?");
        await RepeatUntilVerdict(c, TimeSpan.FromSeconds(4), () =>
        {
            PlayVfx(c, game, at);
            PlayVfx(c, glow, at);
        });
    }

    [ClientCheck("shaders.particle-swirl", "Particles moved by a mod's own shader", Area = "Shaders and materials", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 115)]
    public async Task ShadersParticleSwirl(CheckContext c)
    {
        if (!await Need(c, ShaderSample) || !await NeedPlayer(c)) return;
        if (Ahead(c, 5, right: -2, up: 0.5) is not { } swirl || Ahead(c, 5, right: 2, up: 0.5) is not { } plain)
        {
            c.Expect(false, "the player's place is known");
            await c.Verdict();
            return;
        }
        c.Instruct("Look 5 m ahead: every 5 seconds two clouds of blue dots rise side by side. One is moved by the sample's " +
                   "own shader and swirls round itself for 4 seconds; the other is the same block on Kiln's own particles " +
                   "and only drifts and slows. Does one cloud clearly swirl while the other drifts?");
        await RepeatUntilVerdict(c, TimeSpan.FromSeconds(5), () =>
        {
            PlayVfx(c, VfxId.Named(ShaderSample, "swirl"), swirl);
            PlayVfx(c, VfxId.Named(ShaderSample, "swirl_plain"), plain);
        });
    }

    [ClientCheck("shaders.material-reskin", "A game material reskinned by a mod", Area = "Shaders and materials", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 116)]
    public async Task ShadersMaterialReskin(CheckContext c)
    {
        if (!await Need(c, MaterialSample) || !await NeedPlayer(c)) return;
        c.Instruct("The material sample draws the wood of the game's workbench props as pale blue frost steel. Go to a " +
                   "workbench (your base's, or one in a camp) and look at its wooden parts up close. Is the wood drawn as " +
                   "pale blue metal instead of wood, with no magenta or black patches?");
        await c.Verdict();
    }

    [ClientCheck("blocks.status-icons", "Status and perk icons of a mod's own", Area = "Blocks", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 117)]
    public async Task BlocksStatusIcons(CheckContext c)
    {
        if (!await Need(c, BlocksMore) || !await NeedPlayer(c)) return;
        foreach (var (name, seconds) in new[] { ("chill", 30), ("ember-blessing", 30) })
        {
            if (Blocks.Find(name, BlocksMore) is not { } status)
            {
                c.Expect(false, $"status {name} is defined");
                continue;
            }
            var e = await Abilities.ApplyBuff(c.Player, status.Guid, TimeSpan.FromSeconds(seconds));
            c.Expect(!e.IsNone, $"applied {name} to the player", e.ToString());
        }
        c.Instruct("Two statuses are on you for 30 seconds: Chill (a small cold tick) and Ember Blessing. Look at the buff " +
                   "bar. Each has an icon of its own, not a blank square and not the game's frost icon for both. Do both " +
                   "icons show and differ from each other?");
        await c.Verdict();
    }

    [ClientCheck("blocks.tools", "Tools of every kind", Area = "Blocks", Since = Dev16,
        Needs = ClientCheckNeeds.Hosted, Order = 118)]
    public async Task BlocksTools(CheckContext c)
    {
        if (!await Need(c, BlocksMore) || !await NeedPlayer(c)) return;
        foreach (var tool in new[] { "garden-rake", "long-hook", "strong-rod", "slow-torch", "kite", "fine-sieve", "ash-axe" })
            await GiveBlock(c, BlocksMore, tool);
        c.Instruct("You have Garden Rake, Long Hook, Strong Rod, Slow Torch, Kite, Fine Sieve and Ash Axe. Try each where it " +
                   "works: rake soil, hook a grapple point, cast the rod into water, light the torch, open the kite from a " +
                   "height, sieve, chop a tree. Did every tool work as its kind does in the game?");
        await c.Verdict();
    }

    [ClientCheck("blocks.piercing", "A projectile that pierces three creatures", Area = "Blocks", Since = Dev16,
        Needs = ClientCheckNeeds.Hosted, Order = 119)]
    public async Task BlocksPiercing(CheckContext c)
    {
        if (!await Need(c, BlocksMore) || !await NeedPlayer(c)) return;
        await GiveGame(c, Staff);
        await GiveBlock(c, BlocksMore, "piercing-shard", 10);
        for (var i = 0; i < 3; i++)
            if (Ahead(c, 10 + 2.5 * i) is { } at) await SpawnHere(c, Vukah, at, 180);
        c.Instruct("Three Vukahs stand in a line in front of you. Equip the staff with Piercing Shard and cast once along the " +
                   "line before they reach you. One shard should hit each of the three and fly on. Did a single cast hit " +
                   "all three? (They are removed when the check ends.)");
        await c.Verdict();
    }

    [ClientCheck("blocks.prop-model", "A prop with its own model and collision", Area = "Blocks", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 120)]
    public async Task BlocksPropModel(CheckContext c)
    {
        if (!await Need(c, BlocksMore) || !await NeedPlayer(c)) return;
        await GiveBlock(c, BlocksMore, "crate-lantern");
        await GiveBlock(c, BlocksMore, "lantern-prop");
        c.Instruct("Place the Crate Lantern and the Lantern from your inventory inside your base. Each is drawn with the " +
                   "mod's own lantern model, and the crate is solid: you can stand on it. Leave the Lantern where it is: " +
                   "session 2 asks whether it is still there. Do both draw as a lantern, and does the crate hold you?");
        await c.Verdict();
    }

    [ClientCheck("blocks.sound-block", "Sounds declared as blocks", Area = "Blocks", Since = Dev16,
        Tags = [Audio], Needs = ClientCheckNeeds.Hosted, Order = 121)]
    public async Task BlocksSoundBlock(CheckContext c)
    {
        if (!await Need(c, BlocksMore) || !await NeedPlayer(c)) return;
        if (Ahead(c, 4) is not { } at)
        {
            c.Expect(false, "the player's place is known");
            await c.Verdict();
            return;
        }
        c.Expect(Sound.Available, "kiln.audio is there");
        var ping = SoundId.Named(BlocksMore, "ping-json");
        c.Instruct("Every 3 seconds a short ping plays 4 m ahead of you (two takes, slightly different). Turn away and walk " +
                   "off: it should come from that spot and fade with distance. Do you hear the ping from that spot?");
        await RepeatUntilVerdict(c, TimeSpan.FromSeconds(3), () =>
        {
            try { Sound.PlayAt(ping, at); }
            catch (Exception ex) when (ex is not OperationCanceledException) { c.Expect(false, "played ping-json", ex.Message); }
        });
    }

    [ClientCheck("blocks.look-vfx", "A VFX on a weapon's look and on a placed thing", Area = "Blocks", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 122)]
    public async Task BlocksLookVfx(CheckContext c)
    {
        if (!await Need(c, BlocksMore) || !await NeedPlayer(c)) return;
        await GiveBlock(c, BlocksMore, "glow-sword");
        await GiveBlock(c, BlocksMore, "glow-vase");
        c.Instruct("Equip the Glow Sword and place the Glow Vase. Each carries a glow: on the sword at the blade above the " +
                   "grip, on the vase where it stands. Swing the sword once. Does the glow stay on the blade as it moves, " +
                   "and glow on the vase?");
        await c.Verdict();
    }

    [ClientCheck("blocks.frostbolt-area", "Frostbolt's area damage and its frost VFX", Area = "Blocks", Since = Dev10,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 123)]
    public async Task BlocksFrostboltArea(CheckContext c)
    {
        if (!await Need(c, Frostbolt) || !await NeedPlayer(c)) return;
        await GiveGame(c, Staff);
        await GiveBlock(c, Frostbolt, "frostbolt", 10);
        if (Ahead(c, 12) is { } mid)
        {
            await SpawnHere(c, Vukah, mid, 180);
            if (Ahead(c, 12, right: 1.5) is { } a) await SpawnHere(c, Vukah, a, 180);
            if (Ahead(c, 12, right: -1.5) is { } b) await SpawnHere(c, Vukah, b, 180);
        }
        c.Instruct("Three Vukahs stand close together 12 m ahead. Equip the staff with Frostbolt and cast at the middle one. " +
                   "A shard flies with a blue trail, bursts where it lands, and every Vukah within 3 m takes damage and gets " +
                   "a frost aura (Chilled). Did one cast damage all three and show the burst and the aura?");
        await c.Verdict();
    }

    [ClientCheck("blocks.frost-hits", "Frost VFX on each sword hit", Area = "Blocks", Since = Dev10,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 124)]
    public async Task BlocksFrostHits(CheckContext c)
    {
        if (!await Need(c, Frostbolt) || !await NeedPlayer(c)) return;
        await GiveBlock(c, Frostbolt, "frost-edge");
        if (Ahead(c, 8) is { } at) await SpawnHere(c, Vukah, at, 180);
        c.Instruct("Equip Frost Edge and hit the Vukah in front of you three times. Each hit should burst with blue frost " +
                   "shards and a short blue light. Did a frost burst show on every hit?");
        await c.Verdict();
    }

    [ClientCheck("blocks.wand-attacks", "A wand's three attacks", Area = "Blocks", Since = Dev10,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 125)]
    public async Task BlocksWandAttacks(CheckContext c)
    {
        if (!await Need(c, ItemBlocks) || !await NeedPlayer(c)) return;
        await GiveBlock(c, ItemBlocks, "ember-wand");
        if (Ahead(c, 15) is { } at) await SpawnHere(c, Vukah, at, 180);
        c.Instruct("Equip the Ember Wand and use its primary attack, its heavy attack and its special at the Vukah ahead. " +
                   "Each throws a fireball: the heavy one arcs and bursts wider, the special flies fast and knocks back. Did " +
                   "all three attacks fire and hit?");
        await c.Verdict();
    }

    [ClientCheck("blocks.armour-look", "Armour's worn look", Area = "Blocks", Since = Dev10,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 126)]
    public async Task BlocksArmourLook(CheckContext c)
    {
        if (!await Need(c, ItemBlocks) || !await NeedPlayer(c)) return;
        await GiveBlock(c, ItemBlocks, "ember-helm");
        await GiveBlock(c, ItemBlocks, "ember-mail");
        c.Instruct("Equip the Ember Helm and the Ember Mail, then look at your character from the front (inventory preview " +
                   "and in the world). Are both drawn on the character, with no missing body parts or stretched pieces?");
        await c.Verdict();
    }

    [ClientCheck("creatures.ai-feud", "Who fights whom: a ranger on your side, a warden that ignores you", Area = "Creature AI",
        Since = Dev16, Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 127)]
    public async Task CreaturesAiFeud(CheckContext c)
    {
        if (!await Need(c, CreatureAi) || !await NeedPlayer(c)) return;
        if (Ahead(c, 6) is { } r) await SpawnHere(c, $"{CreatureAi}:ranger", r);
        if (Ahead(c, 6, right: 4) is { } w) await SpawnHere(c, $"{CreatureAi}:warden", w);
        if (Ahead(c, 25) is { } v) await SpawnHere(c, Vukah, v, 180);
        c.Instruct("Near you stand a Ranger and a Vukah Warden (both Vukah bodies); a plain Vukah is 25 m ahead. Stand still " +
                   "and watch for a minute: the Ranger goes for the Vukah and fights it, the Warden ignores you even when you " +
                   "walk up to it. Did the Ranger fight the Vukah and leave you alone, and did the Warden ignore you?");
        await c.Verdict();
    }

    [ClientCheck("creatures.caller", "A Vukah that calls for help and throws ice", Area = "Creature AI", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 128)]
    public async Task CreaturesCaller(CheckContext c)
    {
        if (!await Need(c, CreatureAi) || !await NeedPlayer(c)) return;
        if (Ahead(c, 12) is { } at) await SpawnHere(c, $"{CreatureAi}:vukah-caller", at, 180);
        c.Instruct("A Vukah Caller stands 12 m ahead. Fight it and bring it below half health, then keep away for a while. " +
                   "Below half it summons up to two Vukahs and throws ice at you. Did it summon helpers and throw ice? " +
                   "(Summoned Vukahs are not removed by the check.)");
        await c.Verdict();
    }

    [ClientCheck("models.retarget", "A creature playing retargeted animations", Area = "Animations and models", Since = Dev16,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 129)]
    public async Task ModelsRetarget(CheckContext c)
    {
        if (!await Need(c, RetargetTest) || !await NeedPlayer(c)) return;
        if (Ahead(c, 10) is { } at) await SpawnHere(c, $"{RetargetTest}:packling", at, 180);
        c.Instruct("A Packling (a plain figure with a club) stands 10 m ahead. Its clips were made on another rig with other " +
                   "joint names and longer bones. Watch it idle, then let it walk to you and swing. Does it move like a " +
                   "figure (idle, walk, swing), not frozen in a T-pose or folded up?");
        await c.Verdict();
    }

    [ClientCheck("models.skinned-creature", "A creature with a skinned body cooked from glTF", Area = "Animations and models",
        Since = Dev10, Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 130)]
    public async Task ModelsSkinnedCreature(CheckContext c)
    {
        if (!await Need(c, CreatureBlocks) || !await NeedPlayer(c)) return;
        if (Ahead(c, 10) is { } at) await SpawnHere(c, $"{CreatureBlocks}:stoneling", at, 180);
        c.Instruct("A Stoneling stands 10 m ahead: a body of the mod's own, cooked from a glTF, not a game creature. Watch " +
                   "it idle and let it come to you and slam. Is its own body drawn (not invisible, not a Vukah) and does it " +
                   "bend as it moves?");
        await c.Verdict();
    }

    [ClientCheck("gameplay.items-quick-stack", "Item moves and quick stack", Area = "Gameplay", Since = Dev21,
        Needs = ClientCheckNeeds.Hosted, Order = 131)]
    public async Task GameplayItemsQuickStack(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        await GiveGame(c, Twigs, 30);
        c.Instruct("You got 30 Twigs. Put a chest down (or use one) and move one stack in by dragging, then use quick stack " +
                   "and move all from the inventory, then take it all back. Nothing is lost or doubled. Did every move and " +
                   "quick stack work as in the game without Kiln?");
        await c.Verdict();
    }

    [ClientCheck("gameplay.combat-abilities", "Combat and an ability on a key", Area = "Gameplay", Since = Dev21,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 132)]
    public async Task GameplayCombatAbilities(CheckContext c)
    {
        if (!await Need(c, AbilityBlocks) || !await NeedPlayer(c)) return;
        if (Ahead(c, 8) is { } at) await SpawnHere(c, Vukah, at, 180);
        c.Instruct("A Vukah stands 8 m ahead. Fight it with any weapon, block once, dodge once, and press G to cast the " +
                   "sample's cone at it. Damage numbers show and the cone hits. Did combat feel as usual and the G ability " +
                   "cast and hit?");
        await c.Verdict();
    }

    [ClientCheck("quests.npc-sample", "A Kiln NPC and quest in the game's own UI", Area = "Gameplay", Since = Dev21,
        Tags = [Visual, Audio], Needs = ClientCheckNeeds.Hosted, Order = 133)]
    public async Task QuestsNpcSample(CheckContext c)
    {
        if (!await Need(c, NpcSample) || !await NeedPlayer(c)) return;
        c.Instruct("Type /npcsample here in chat: Tobin appears next to you. Talk to him: his topics are in the game's NPC " +
                   "menu, his greeting is voiced, and his quest goes into the game's journal with a map marker. Did his " +
                   "menu, his voice and the journal entry all show?");
        await c.Verdict();
    }

    [ClientCheck("rules.flight-creative", "Flight and creative rules", Area = "Creative mode", Since = Dev21,
        Needs = ClientCheckNeeds.Hosted, Order = 134)]
    public async Task RulesFlightCreative(CheckContext c)
    {
        if (!await Need(c, RulesSample) || !await NeedPlayer(c)) return;
        c.Instruct("Type /flight in chat: for 60 seconds you can use the building fly mode anywhere, also outside your base. " +
                   "Then type /creative, fly and build one block outside a base, and type /creative again to leave it. Did " +
                   "flying and building work outside the base, and stop when the time ran out or you left creative mode?");
        await c.Verdict();
    }

    [ClientCheck("glider.steer", "The steered glider in creative mode", Area = "Creative mode", Since = Dev24,
        Needs = ClientCheckNeeds.Hosted, Order = 135)]
    public async Task GliderSteer(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        c.Instruct("Type /creative, climb or fly high, and open the glider. Hold S: the nose rises (up to a steep climb); hold " +
                   "W: it dips. Boost: you speed up along the nose. Type /creative again when done. Did S raise the nose, W " +
                   "lower it and the boost push along it?");
        await c.Verdict();
    }

    [ClientCheck("rules.noclip", "NoClip: through the ground, and safely off inside it", Area = "Creative mode", Since = Dev27,
        Needs = ClientCheckNeeds.Hosted, Order = 150)]
    public async Task RulesNoClip(CheckContext c)
    {
        if (!await NeedPlayer(c) || !await NeedNoClip(c)) return;
        var ended = new List<PlayerNoClipEndedEvent>();
        var sub = Events.On<PlayerNoClipEndedEvent>(e => { if (e.Player == c.Player) ended.Add(e); });
        c.OnEnd(() =>
        {
            sub.Dispose();
            NoClip.Set(c.Player, false);
        });
        var on = NoClip.Set(c.Player, true);
        if (!c.Expect(on.Ok, "NoClip turned on for you", on.Message))
        {
            c.Instruct($"NoClip could not be turned on: {on.Message} Press fail, with that line as the note.");
            await c.Verdict();
            return;
        }
        c.Instruct("NoClip is on and you lift off. Fly with the movement keys; jump climbs, sneak sinks, sprint is faster. " +
                   "Sink into the ground until you are well inside it, fly through a wall or a rock, and stop INSIDE the " +
                   "ground. Then type /noclip in chat to turn it off.");
        var active = await c.Until(() => NoClip.Of(c.Player).Active, TimeSpan.FromSeconds(20), "the server skips your character's collision");
        c.Expect(active, "the server's world moves your character without collision", NoClip.Of(c.Player).ToString());
        var off = await c.Until(() => ended.Count > 0, TimeSpan.FromMinutes(5), "you turned NoClip off");
        if (off)
        {
            c.Expect(ended[0].Reason == NoClipEnd.RuleEnded, "it ended because it was turned off", ended[0].ToString());
            c.Expect(ended[0].RestoredTo is not null, "it was turned off inside something, and Kiln put you back at your last free position",
                ended[0].ToString());
        }
        await c.Ticks(60);
        c.Expect(!NoClip.Of(c.Player).Active, "collision is back on the server", NoClip.Of(c.Player).ToString());
        c.Instruct("Three things, all must be yes to pass. While NoClip was on, did you pass through the ground and the wall " +
                   "smoothly, without being pulled back? When you turned it off inside the ground, were you put back in " +
                   "open space near where you went in? Are you now standing or landing normally, not inside the ground " +
                   "and not falling under the world? (In the fail note, say which one was not so, and what the camera did " +
                   "inside the ground.)");
        await c.Verdict();
    }

    [ClientCheck("rules.noclip-teleport", "NoClip ends on a teleport", Area = "Creative mode", Since = Dev27,
        Needs = ClientCheckNeeds.Hosted, Order = 151)]
    public async Task RulesNoClipTeleport(CheckContext c)
    {
        if (!await NeedPlayer(c) || !await NeedNoClip(c)) return;
        var ended = new List<PlayerNoClipEndedEvent>();
        var sub = Events.On<PlayerNoClipEndedEvent>(e => { if (e.Player == c.Player) ended.Add(e); });
        c.OnEnd(() =>
        {
            sub.Dispose();
            NoClip.Set(c.Player, false);
        });
        var on = NoClip.Set(c.Player, true);
        if (!c.Expect(on.Ok, "NoClip turned on for you", on.Message))
        {
            c.Instruct($"NoClip could not be turned on: {on.Message} Press fail, with that line as the note.");
            await c.Verdict();
            return;
        }
        c.Instruct("NoClip is on. Open the map and fast travel to any Flame Altar (or sleep in a bed, or use a teleport of " +
                   "your own).");
        var tripped = await c.Until(() => ended.Count > 0, TimeSpan.FromMinutes(5), "you teleported");
        if (tripped) c.Expect(ended[0].Reason == NoClipEnd.Teleport, "it ended because of the teleport", ended[0].ToString());
        await c.Ticks(60);
        c.Expect(!Rules.Of(c.Player).NoClip && !NoClip.Of(c.Player).Active, "NoClip is off after the teleport", NoClip.Of(c.Player).ToString());
        c.Instruct("Did you arrive standing on the ground with normal collision, and did a chat line say that NoClip ended " +
                   "because you were teleported?");
        await c.Verdict();
    }

    // NoClip's checks need the hooks on this game build and the world's rules.json allowing it.
    private static async Task<bool> NeedNoClip(CheckContext c)
    {
        if (!NoClip.Available)
        {
            c.Expect(false, "NoClip works on this game build (rules.noclip)", Capabilities.Why(NoClip.Capability));
            c.Instruct($"NoClip does not work in this game: {Capabilities.Why(NoClip.Capability)} Press skip (or fail, if this " +
                       "is a game build Kiln supports and the Creative Mode mod is not installed).");
            await c.Verdict();
            return false;
        }
        if (Rules.Policy.AllowNoClip) return true;
        c.Instruct("NoClip is switched off in this world's rules. Open kiln/config/rules.json in the folder of the game (or " +
                   "of the server), set \"allowNoClip\": true and save: Kiln rereads the file within a second. Then start " +
                   "this check again. Press skip now.");
        await c.Verdict();
        return false;
    }

    [ClientCheck("time.set", "Time of day set by a mod", Area = "Gameplay", Since = Dev21,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 136)]
    public async Task TimeSet(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        c.Expect(Time.Available, "kiln.time is there");
        var before = Time.OfDay;
        c.OnEnd(() => { if (Time.Available) Time.Set(before); });
        Time.Set(22);
        await c.Ticks(30);
        c.Expect(Time.OfDay is >= 21.5 or < 1, "the clock reads night after the change", $"{Time.OfDay:0.00} h");
        c.Instruct($"The check set the time to 22:00 (it was {before:0.0} h; it is set back to that when the check ends, " +
                   "which moves the clock forward). Look at the sky and the light. Did it turn to night at once?");
        await c.Verdict();
    }

    [ClientCheck("spawns.vukah", "A creature spawned by a mod", Area = "Gameplay", Since = Dev21,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 137)]
    public async Task SpawnsVukah(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        var at = Ahead(c, 10);
        c.Expect(at is not null, "the player's place is known");
        var e = at is { } p ? await SpawnHere(c, Vukah, p, 180) : EntityId.None;
        if (!e.IsNone) c.Expect(Spawn.Exists(e), "the Vukah exists in the world");
        c.Instruct("A Vukah appeared 10 m ahead of you. It stands on the ground, looks and moves like any Vukah and comes " +
                   "for you. Did it appear where the check put it, and act normally? (It is removed when the check ends.)");
        await c.Verdict();
    }

    [ClientCheck("abilities.remove-buff", "A buff taken off by a mod", Area = "Gameplay", Since = Dev27,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 139)]
    public async Task AbilitiesRemoveBuff(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        // the heal-over-time potion's buff: an icon in the buff row while it lasts
        var hot = new Guid("be0e7626-e95f-4a14-844a-2af52501698e");
        c.Expect(Capabilities.Is("abilities.buffs"), "abilities.buffs is available", Capabilities.Why("abilities.buffs"));
        await Abilities.ApplyBuff(c.Player, hot, TimeSpan.FromSeconds(120), c.Player);
        c.OnEnd(() => { if (Capabilities.Is("abilities.buffs")) _ = Abilities.RemoveBuff(c.Player, hot); });
        await c.Until(() => Abilities.BuffsOf(c.Player).Any(b => b.Is(hot)), TimeSpan.FromSeconds(5), "the buff is on the player");
        c.Instruct("The check gave you the healing potion's buff for 2 minutes: its icon is in the buff row of your HUD. Look " +
                   "at it now. The check takes the buff off in 10 seconds through Abilities.RemoveBuff; watch the icon.");
        await c.Ticks(240);
        var r = await Abilities.RemoveBuff(c.Player, hot);
        c.Expect(r.Ok, "RemoveBuff says the game removed it", r.ToString());
        // the game answers at once and clears the slot a step later
        await c.Until(() => !Abilities.BuffsOf(c.Player).Any(b => b.Is(hot)), TimeSpan.FromSeconds(5), "the buff is no longer in the player's buff slots");
        c.Instruct("Did the buff's icon appear in your HUD, and did it go away when the check took the buff off (about 10 " +
                   "seconds later), with no other buff touched?");
        await c.Verdict();
    }

    [ClientCheck("players.aim", "Where the player looks: Player.Aim follows the camera", Area = "Gameplay", Since = Dev27,
        Needs = ClientCheckNeeds.Hosted, Order = 140)]
    public async Task PlayersAim(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        c.Expect(Capabilities.Is("players.aim"), "players.aim is available", Capabilities.Why("players.aim"));
        PlayerAim? Aim() { try { return PlayerStates.AimOf(c.Player); } catch (KilnUnavailableException) { return null; } }
        var first = Aim();
        var body = World.Position(c.Player);
        c.Expect(first is not null, "the player's client has sent a camera", first?.ToString());
        if (first is not null && body is { } b)
            c.Expect(first.Origin.DistanceTo(b) < 15, "the camera is near the character (third person)",
                $"{first.Origin.DistanceTo(b):0.0} m from the character, distance setting {first.Distance:0.0}");
        c.Instruct("Step 1 of 3: look straight down at your feet with the camera.");
        if (await c.Until(() => Aim() is { PitchDegrees: < -45 }, TimeSpan.FromSeconds(60), "Aim's pitch went below -45 (looking down)"))
            c.Expect(true, "Aim's pitch went below -45 (looking down)", $"{Aim()?.PitchDegrees:0} degrees");
        c.Instruct("Step 2 of 3: Kiln saw you look down. Now look up at the sky.");
        if (await c.Until(() => Aim() is { PitchDegrees: > 30 }, TimeSpan.FromSeconds(60), "Aim's pitch went above 30 (looking up)"))
            c.Expect(true, "Aim's pitch went above 30 (looking up)", $"{Aim()?.PitchDegrees:0} degrees");
        var yaw0 = Aim()?.YawDegrees ?? 0;
        c.Instruct("Step 3 of 3: Kiln saw you look up. Now turn the camera half a turn (180 degrees) left or right.");
        if (await c.Until(() => Aim() is { } a && Math.Abs(((a.YawDegrees - yaw0 + 540) % 360) - 180) > 150, TimeSpan.FromSeconds(60),
                "Aim's yaw turned by more than 150 degrees"))
            c.Expect(true, "Aim's yaw turned by more than 150 degrees", $"{yaw0:0} -> {Aim()?.YawDegrees:0} degrees");
        var last = Aim();
        c.Instruct($"Kiln read your camera as yaw {last?.YawDegrees:0} and pitch {last?.PitchDegrees:0} degrees. Did each step's " +
                   "text move on right after you moved the camera that way, and not before?");
        await c.Verdict();
    }

    [ClientCheck("world.programs", "A world program with a player in the world", Area = "Gameplay", Since = Dev21,
        Needs = ClientCheckNeeds.Hosted, Order = 138)]
    public async Task WorldProgramsCheck(CheckContext c)
    {
        if (!await Need(c, WorldPrograms) || !await NeedPlayer(c)) return;
        c.Instruct("The world programs sample runs a program in this hosted world that sends itself an event. Open " +
                   "kiln/logs/kiln.log in the game folder and search for \"world program event echoed\". Is that line there " +
                   "for this start, with no error from world-programs after it?");
        await c.Verdict();
    }

    // ---- equip and unequip by a mod (Items.Equip / Items.Unequip, dev.27): the server's game equips, the player sees ----
    private const string Hat = "Armor_T1_Mage_Wizard_DPS_Helmet";  // Alchemist Apprentice Hat: shows on the head

    [ClientCheck("items.equip", "A mod puts a hat on the character, and it comes off by hand", Area = "Gameplay",
        Since = Dev27, Tags = [Visual], Order = 141)]
    public async Task ItemsEquip(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        if (!c.Expect(Capabilities.Is("items.equip"), "items.equip is available", Capabilities.Why("items.equip"))) return;
        await GiveGame(c, Hat);
        await c.Ticks(4);
        var hat = Items.Lookup(Hat);
        var slot = hat is null ? null : Items.Read(c.Player)?.Slots.FirstOrDefault(s => s.Item == hat.Id && !s.IsEquipped);
        if (!c.Expect(slot is not null, "the hat lies in the inventory, not worn")) return;
        var r = await Items.Equip(c.Player, slot!.Slot, EquipmentSlot.ArmourHead);
        c.Expect(r.Ok, "Items.Equip put it on the head", r.ToString());
        await c.Ticks(8);
        c.Expect(Items.Equipped(c.Player, EquipmentSlot.ArmourHead).Item == hat!.Id, "the server's head slot holds the hat");
        c.Instruct("The check put an Alchemist Apprentice Hat on your character's head through the game's own equip (what " +
                   "was on the head before went into your backpack). Look at your character: does it wear the hat? Open " +
                   "the inventory: is the hat in the head slot of the equipment, and does the preview show it? Then take it " +
                   "off by hand (drag it into the backpack) and put it on again by hand. Did all of that work as usual?");
        await c.Verdict();
    }

    [ClientCheck("items.unequip", "A mod takes the hat off the character", Area = "Gameplay", Since = Dev27,
        Tags = [Visual], Order = 142)]
    public async Task ItemsUnequip(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        if (!c.Expect(Capabilities.Is("items.equip"), "items.equip is available", Capabilities.Why("items.equip"))) return;
        var hat = Items.Lookup(Hat);
        if (hat is not null && Items.Equipped(c.Player, EquipmentSlot.ArmourHead).Item != hat.Id &&
            Items.Read(c.Player)?.Slots.FirstOrDefault(s => s.Item == hat.Id && !s.IsEquipped) is { } loose)
            await Items.Equip(c.Player, loose.Slot, EquipmentSlot.ArmourHead);
        await c.Ticks(8);
        var r = await Items.Unequip(c.Player, EquipmentSlot.ArmourHead);
        c.Expect(r.Ok, "Items.Unequip took the head slot off", r.ToString());
        await c.Ticks(8);
        c.Expect(Items.Equipped(c.Player, EquipmentSlot.ArmourHead).IsNone, "the server's head slot is empty");
        c.Instruct("The check took the hat (or whatever your character wore on the head) off through the game's own " +
                   "unequip: it went into a free slot of your backpack. Is the head bare on your character and empty in " +
                   "the equipment screen, and is the hat in the backpack? Can you put it on again by hand?");
        await c.Verdict();
    }

    // ---- the weather a mod chose (Time.SetWeather, dev.27): the server's game starts it, the player's game shows it ----
    [ClientCheck("time.weather", "A mod chooses the weather", Area = "Gameplay", Since = Dev27, Tags = [Visual], Order = 143)]
    public async Task TimeWeather(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        if (!c.Expect(Capabilities.Is("time.weather_choose") && Time.WeatherChoiceAvailable, "the weather can be chosen",
                Capabilities.Why("time.weather_choose"))) return;
        c.OnEnd(async () => await Time.ReleaseWeather(now: true));
        var none = await Time.SetWeather("none", layer: 0);
        c.Expect(none.Ok, "no clouds on the clouds layer, held", none.ToString());
        c.Instruct("Step 1 of 2: the check took the clouds away (the game's \"nothing\" on its clouds layer, held). Look at " +
                   "the sky for half a minute: do the clouds thin out and go? Then press pass or fail for step 1 and 2 " +
                   "together after step 2 below.");
        await c.Ticks(30 * 25);
        var overcast = await Time.SetWeather("clouds_overcast", layer: 0);
        var rain = await Time.SetWeather("rain", TimeSpan.FromMinutes(10));
        c.Expect(overcast.Ok && rain.Ok, "overcast held, rain in every region for ten game minutes", $"{overcast}; {rain}");
        c.Instruct("Step 2: the check made the sky overcast and started rain in every region of the world (each rain is a " +
                   "cloud at a random place in its region, so it may be far from you). Within a minute, does the sky turn " +
                   "overcast? If rain reaches you, does it look like the game's own? Did the clouds go away in step 1? When " +
                   "the check ends, the weather goes back to the game.");
        await c.Verdict();
    }
}
