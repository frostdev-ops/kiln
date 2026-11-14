# The kit's fakes

A fake is a native service table written in C#: a mirror of the service's header in `native/core/abi/services/`, which
records what mods ask and answers from the test's script, so the service's own managed code runs unchanged under
`dotnet test` ([docs/kiln/modding/unit-tests.md](../../../docs/kiln/modding/unit-tests.md)).

## Adding one (0.2.0-dev.28 on)
Everything of a fake lives in `Fakes/<Service>/`:

| File | What |
|---|---|
| `<Service>Fake.cs` | a class that implements `IKitFake` (`KitFake.cs`): `Register` publishes the table and its capability rows, `Start`/`BeforeStep`/`End` follow a harness, `AddCoverage` adds the rows of the coverage table |
| `<Service>Table.cs` | the table: `[UnmanagedCallersOnly]` functions that answer from `FakeTables.Active(service)`'s harness, and as the native service does when it is unavailable otherwise |
| `KilnHarness.<Service>.cs` | the part of `KilnHarness` a test uses: what it scripts (the next ray's hit, a refused equip) and what it reads (the calls recorded) |

The kit finds every `IKitFake` in its assembly. So a new fake edits **no shared file**: not `FakeTables.Register`, not
`KilnHarness.Fakes.cs`, not `Coverage.Fakes.cs`, not the harness's other parts. State for one harness:
`KitFakes.State<T>(harness)`. The service's name goes into `ServiceNames.cs` (one line, sorted) with the header.

Rules a fake keeps:
- a function it does not model answers `KILN_ERR_UNAVAILABLE` (or `NOT_FOUND`), never "ok";
- every game feature it serves is a capability row a test can switch off (`FakeTables.Features`); what it does not
  model is a row with the reason `offline` (`FakeTables.Offline`); a session row uses `FakeTables.Session`;
- the member of the service a test calls while the row is off throws `KilnUnavailableException` naming the row;
- public additions to `KilnHarness` go into `PublicAPI.Unshipped.txt` of the kit;
- a test of the fake in `managed/tests/Kiln.TestKit.Tests/`, which fails when the fake stops answering.

The fakes of dev.27 and earlier (`SpawnTable.cs`, `ItemsTable.cs`, …) are wired by hand in the three shared files.
