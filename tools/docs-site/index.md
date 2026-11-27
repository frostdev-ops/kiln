---
title: Kiln modding
description: The modder's guide and API reference of Kiln, the mod framework for Enshrouded.
---
# Kiln modding

Kiln loads C# and native mods into Enshrouded. Start with [Writing Kiln mods](modding/README.md), or look up a type in
the [API reference](api/index.md): one page for each service, and each type and member says since which mod API it exists.
Press <kbd>/</kbd> to search every type, member and guide page.

<ul class="kd-start">
<li><a href="api/index.md"><span class="who">Look up</span><strong>API reference</strong><span>Every assembly, type and member a mod can use, by service.</span></a></li>
<li><a href="modding/README.md"><span class="who">Learn</span><strong>Modder's guide</strong><span>The MDK, the manifest, the lifecycle, blocks, tests and the kiln command.</span></a></li>
<li><a href="https://frostdev.io/kiln/wiki/first-mod"><span class="who">Start</span><strong>Your first mod</strong><span>From a template to a mod in a test server, on the Kiln wiki.</span></a></li>
</ul>

By task: keep creatures that follow a player ([pets guide](modding/pets.md), [`Kiln.Pets`](api/services/Kiln.Pets.md)); let a
player tame a creature ([taming guide](modding/taming.md), [`Kiln.Taming`](api/services/Kiln.Taming.md)); change the game's data
by stable names ([registries](https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md),
[`Kiln.Registry`](api/services/Kiln.Registry.md)); put lines and topics on an NPC's dialogue (`Npcs` in
[`Kiln.Blocks`](api/services/Kiln.Blocks.md)); take an exact cost from a chosen slot with a receipt (`ItemCosts` in
[`Kiln.Items`](api/services/Kiln.Items.md)).

The obsolete namespace `Kiln.GameTypes` of Kiln.Game (the legacy component structs) is not in this reference; its
replacement is [`Kiln.Ecs`](api/Kiln.Ecs.yml). The class `Kiln.GameTypes` of Kiln.Api is.
