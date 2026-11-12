---
uid: Kiln.Ecs
remarks: *content
---
**Generated types.** Every type nested in `Ecs` except `ObjectReference` and `GameGuid` (its game structs, ECS
components and events, enums, bitmasks and `StaticArrayN<T>`) is generated when Kiln.Game is built:
`tools/typegen/typegen.py` writes them from a layout dump of the game's reflection table, taken from one verified game
build (the dump is named by the SHA-256 of that build's executable). Each struct has that build's exact layout. At
its first use each type is checked against the running game (its live type table, or else that SHA-256), and a type
that differs reads as nothing (`Supports<T>()` is false, `WhyNot<T>()` says what differs) instead of wrong bytes. Their
field and enum-value docs come from the game's names and are not written by hand. How they are generated, named and
checked: [Typed game data (typegen.md)](https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/typegen.md).
