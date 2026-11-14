# The client gate's list

Kiln ships some capabilities switched off because only a real game client can confirm them: nobody but a player
can say that a camera does not clip, that a marker shows on the compass, that a joined vanilla player still sees a
sane world. Such a capability is **held** until its guided client checks (`mods/tests/client-pass/`,
[docs/kiln/client-pass.md](../../../docs/kiln/client-pass.md)) have passed in the real game.

Each file here lists held capabilities. One file per work area, so two areas never edit the same file:

```json
{ "gates": [
    { "name": "camera.fov", "checks": ["03-c11", "03-c12"] },
    { "name": "markers.compass", "checks": ["04-m03"], "side": "client" }
] }
```

- `name`: the capability's name in kiln.caps ([docs/kiln/services/caps.md](../../../docs/kiln/services/caps.md)). A
  sigdb feature, a service's own row or a managed row: the gate holds any of them. A name appears in one file only.
- `checks`: the ids of the guided client checks that open it, lower-case, at least one.
- `side` (optional): `"server"` or `"client"` when the capability is held on that side only. Without it, on both.

The list is compiled into `kiln-core.dll` (`gen.py` writes `kiln_gates.g.h` when the core is built), so an install
cannot lose it. While a name is held the core reports it `unavailable`, reason `policy`, text
`game test pending: <check ids>`; `KilnApi.feature()` answers false for it; a service installs the hooks of that
capability only when the row is not held (`kiln_caps_held` in `kiln_caps.h`, `Capabilities.IsHeld` in managed code).
`"preview"` in `kiln.json` lifts the gate: `true` for every name, or a list of names. The guided pass runs with
`"preview": true`.

To open a capability for good once its checks have passed: remove its entry here, in the commit that records the pass.
