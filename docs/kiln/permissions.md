# Permissions (`Kiln.Permissions`, like Forge's PermissionAPI)

`managed/services/Kiln.Permissions/`. Tests: `managed/tests/Kiln.Api.Tests/PermissionsTests.cs`; lab:
`mods/tests/api-test`.

## For mods
```csharp
Permissions.Register("mymod.fly", PermissionLevel.Everyone, "fly around");   // default when no rule says otherwise
if (Permissions.Has(player, "mymod.fly")) ...                                // Player, EntityId, PermissionSubject
if (Permissions.Has(commandContext.Source, "mymod.fly")) ...                 // a CommandSource converts
var why = Permissions.Explain(PermissionSubject.ForSteamId(id), "mymod.fly"); // allowed, rule, from, groups
```
Levels: `Everyone` (allowed), `Admin` (allowed for admins), `Console` (players need a rule naming the node exactly).
Unregistered nodes count as `Admin`. Nodes are lower case, dot-separated `[a-z0-9_-:]` segments. Nodes belong to the
mod that registered them. Commands register `<mod>.command.<name>` (see `commands.md`).

Subjects: `PermissionSubject.Console`, `.Remote(name, admin)`, `.ForPlayer(Player)`, `.ForPlayer(EntityId)` (looked up
through Kiln.Players), `.ForSteamId(id, name, rights, userGroupId)` (offline players, the dashboard).

## kiln/config/permissions.json
Written with defaults (and comments) the first time Kiln.Permissions is used; re-read within ~1 s when it or
`enshrouded_server.json` changes (polled on the service tick); a broken file keeps the previous rules.
```jsonc
{
  "kickBanIsAdmin": true,          // a user group that may kick/ban counts as admin for "admin" nodes
  "defaultGroup": "default",       // every player is in it
  "groups": {
    "default": { "admin": false, "inherits": [], "nodes": [] },
    "admin":   { "admin": true,  "inherits": [], "nodes": ["*"] },
    "builders":{ "admin": false, "inherits": ["default"], "nodes": ["mymod.build.*", "-mymod.build.nuke"] }
  },
  "userGroups": { "Admin": ["admin"], "Friend": ["builders"] },   // enshrouded_server.json userGroups name -> groups
  "userGroupIds": { "3": "Friend" },                              // optional: game group id -> name
  "players": { "76561198000000000": { "name": "note", "groups": ["builders"], "nodes": ["-mymod.fly"] } }
}
```
Decision for a player: their own rules first; then the rules of their groups (own groups, the groups mapped from their
server user group, `defaultGroup`, inherited groups); the most specific rule wins (exact > longer `prefix.*` > `*`),
deny wins a tie; wildcards never grant console-level nodes; no rule → the node's level (`Admin`: a group with
`"admin": true`, or the admin check of Kiln.Chat / Kiln.Admin, or with `kickBanIsAdmin` the KickBan right of their user
group from Kiln.Players).

The server user group of a player: `userGroupIds[UserGroupId]` if set, else the only `userGroups` entry of
`enshrouded_server.json` whose rights (canKickBan, canAccessInventories, canEditBase, canEditWorld, canExtendBase) equal
the player's (the game does not tell the group name; two groups with the same rights are ambiguous → no mapping).

## Editing (dashboard)
Tick thread; each edit writes the file (comments regenerated) and posts `PermissionsChangedEvent` ("api"):
`SetPlayerNode(steamId, node, allow?)`, `AddPlayerToGroup`, `RemovePlayerFromGroup`, `SetGroupNode(group, node,
allow?)`, `SetGroup(group, admin?, inherits?)`, `RemoveGroup`, `MapUserGroup(userGroup, groups)`, `Reload()`.
Reads: `Nodes`, `Find`, `GroupsOf`, `UserGroupOf`, `IsAdmin`, `ServerUserGroups`, `ToJson()` (the whole model plus
the registered nodes), `FilePath`.
