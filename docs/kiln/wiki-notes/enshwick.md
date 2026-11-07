# Wiki note: Enshwick mods on Kiln

What changed: Kiln runs Enshwick mods (DLL mods, Luau `main.luau` mods, TOML data packs) through a new plugin,
`kiln-enshwick.dll`. Full page: docs/kiln/engines/enshwick.md.

Suggested text for the guide's "Other mod loaders" page:

> **Enshwick.** Kiln runs Enshwick's own loader for you, so every Enshwick mod works as it does under Enshwick: its
> settings per world, its Mods screen and its join check. Put the mod's folder in `<game>/mods/` as Enshwick's
> instructions say, or add it to a Kiln profile. You don't need Enshwick's `powrprof.dll`, and on Linux or Steam Deck
> you don't need the `WINEDLLOVERRIDES=powrprof=n,b` launch option. If you keep Enshwick's `powrprof.dll` installed
> and it loads, Kiln steps aside and Enshwick runs its mods itself. Either way, each mod runs once.
>
> Enshwick runs every folder in `mods/`. To switch a mod off, move its folder out of `mods/`, or switch it off in your
> Kiln profile if Kiln installed it there.
>
> Logs: `kiln/logs/kiln.log` shows `[enshwick] ...` lines and the key lines of Enshwick's own log, which stays in
> `<game>/logs/enshwick_<date>.log`.

For mod authors (Kiln C# API): new `Points` / `PointRegistry`, Enshwick-style extension points. A mod defines a rule
other mods may observe, advise or override, with a stable link order (priority, before/after), refusals where allowed,
and links that throw switched off alone:

```csharp
var price = Points.Define<Sale, int>("price", PointMode.Override);         // "<your mod id>:price"
int pay = price.Decide(sale, basePrice).ValueOr(0);

Points.Extend<Sale, int>("you.trading-post:price",                          // in another mod
    (sale, value) => (PointVerdict.Set, value + 5), new PointOrder { Priority = 10 });
```
