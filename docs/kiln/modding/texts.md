# English game texts

`Kiln.Content` provides the game's English texts by loca tag, including on a dedicated server:

```csharp
var title = Texts.GetPlain(shrine.NameTag);
Log.Info(title ?? shrine.Id);
```

`Texts.Get(uint)`, `Texts.Get(Guid)` and `Texts.Get(string)` return the original text, including game markup, or `null`.
A string accepts a GUID, a hexadecimal tag id such as `0x12345678`, or a decimal id. `GetPlain` has the same overloads and
removes markup tags. `TagOf(Guid)` hashes a resource's LocaTag reference into its tag id. All these APIs allow any thread.

A dedicated server ships no English loca collection. On a machine with the game installed, run
`kiln catalog build --side client`, then copy that generated `client.json` to the server's `kiln/catalog/client.json`.
Keep it for your installation; it contains game data and does not belong in a mod package. `KILN_CATALOG` can instead
name the file or its directory. The usual Kiln data directories are also searched, including the Linux home under Wine.
Use a catalog from the same game build; `Texts.Status` reports its source, build, text count and load result.

The first lookup reads the table once. `await Texts.Preload()` can load it on a background thread beforehand. A missing,
unreadable or malformed catalog returns `null` with a reason in `Status`; failed loads may retry after 60 seconds. Input
is limited to a 512 MiB catalog, two million texts and 64 KiB per text.

SET's `Res.text(locaGuid)` reads this same catalog for tags beyond items and templates. Its item and template names keep
using Kiln's shipped names. A tag the available sources do not know returns `nil`.
