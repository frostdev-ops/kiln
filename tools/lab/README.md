# Lab servers

`tools/lab/lab.sh` makes disposable dedicated servers for Kiln development under `~/srv/enshrouded/labs/<name>/`
(game files reflinked from a vanilla SteamCMD install, a Wine prefix of their own, a query port and save of their own).
It never touches a Steam-installed server. Usage is in the script's header; deploy Kiln into a lab with
`KILN_OUT=<build dir> tools/deploy-server.sh ~/srv/enshrouded/labs/<name> --policy any`.

A lab test script usually: makes or resets the lab, deploys a build and its test mods, writes
`kiln/config/network.json` with a `labPort` (kiln.net's lab transport, `docs/kiln/network.md` "Lab tests"), starts the
server, drives it over the lab transport (tools/nettest, tools/labbot), stops it and keeps the logs.

## Bot players (tools/labbot)

A **bot player** is a player the dedicated lab server hosts itself: no game client, no Steam account, no password. It
logs in through the game's own login, gets a new character from the game's player template, spawns in the world, is in
the user group you ask for, acknowledges its character saves, and logs out (or drops) when told. Players, chat
commands, `CharacterData` and kiln.net see it as a player like any other, so tests that need an owner, a tamer or a
rider (pets, taming, mounts) can run end to end in a lab. How it works: `docs/re/labbot.md`; the native service:
`native/core/abi/services/kiln_labbot.h`; the managed API: `Kiln.LabBots` (Kiln.Players, lab-only `KILNX001`).

What a bot is not: it has no network connection (what the server sends it goes nowhere), it never acknowledges
replication, and it moves only when something moves it (`move_to`, a teleport). It cannot test what a client shows.

### Set-up

1. `kiln.json` policy `"any"` (`deploy-server.sh --policy any`) and `kiln/config/network.json` with a `labPort`. Without
   both, kiln.labbot stays off (capability `labbot.join`, reason "policy") and nothing of it is hooked.
2. The mod `mods/tests/labbot-test` in the lab (`dotnet build mods/tests/labbot-test/LabBotTest.csproj -c Release -o
   $KILN_OUT/mods/labbot-test` before the deploy): it opens the control channel `labbot:control` v1.
3. A world must be running (the bot joins the server world; on a new lab the first start generates it).

### From a lab script (Python)

```python
import sys; sys.path.insert(0, "<worktree>/tools/labbot"); import labbot
lab = labbot.Lab(lab_port)                    # a lab peer on 127.0.0.1:<labPort>
print(lab.status())                           # {"available": true, "groups": 4, ...}
bot = lab.spawn("Bot Alder")                  # join + wait until its player entity exists (group "admin" by default)
print(bot.info()["entity"], bot.character())  # its entity and CharacterKey
bot.say("/pet give hound")                    # a chat line typed by the bot: Kiln's commands run with the bot as source
print(bot.command("pet list"))                # Commands.Execute as the bot: found/denied/done/error and the output
bot.move_to(x, y, z)                          # teleport its entity
bot.stay(300)                                 # it stays where it is (its info every 5 s keeps the lab peer)
bot.leave()                                   # logout: final save, the game removes it; leave(drop=True) = lost connection
```

kiln.net forgets a lab peer that sent it nothing for 60 s, and its requests then reach no channel. So `bot.stay`
asks for the bot's info every 5 s (and raises `LabBotError` when the bot left or failed meanwhile), and a `Lab` call after
45 s without a frame from it (a script's own `time.sleep`) says HELLO again first: a long stay or pause needs nothing
of the script.

`labbot.Lab.join(name, group)` starts a join without waiting; `Bot.wait_ready(timeout)` waits for `InGame` and raises
`LabBotError` with the bot's reason when it failed or left. Groups: `admin` (the userGroups entry with kick/ban and the
most rights), `least`, or `index` with `index=n`. The same name is the same character (and account) on that world, so a
bot that joins again finds its per-character data (pets, quests). At most 4 bots at once.

`bot.told(since)` / `bot.wait_told(text)`: the server's chat lines to the bot (a command's replies, "[Server] ..."; an
async command such as `/pet summon` answers there). `wait_told` takes only a line added after the bot's last `say` or
`command` (their replies carry that point as `"told"`; `since=` picks another), so a reply to an earlier command never
passes for the newest one. Two lines added in the same server step show only the second.

Known limit: a name joins **once per server session**. The game does not spawn a character again after its logout
while the server keeps running (it stays in the spawn position search; `docs/re/labbot.md`), so a join under a name
that logged in earlier in that session is refused at once: `LabBotError` "... logged in earlier in this server
session ...". Use a new name for each join in one server session, or restart the lab between a logout and the same
character's next login (the first join of a session works, with the character's per-character data).

From a shell: `python3 -I tools/labbot/labbot.py --port <labPort> status|list|players|spawn NAME|info ID|wait ID|say ID
TEXT|command ID LINE|move ID X Y Z|position ID|character ID|leave ID [--drop]` (JSON out; exit 1 = the server answered
an error, 2 = no answer).

Tests of the client without a server: `python3 -I tools/labbot/test_labbot.py`; of the native core without a game:
`native/services/labbot/tests/test-labbot-host.sh`.
