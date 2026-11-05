# Upstream: Enshrouded Interactive Map

| | |
|---|---|
| Upstream | https://github.com/nicolasferreiraxn/Enshrouded-Interactive-Map |
| Reference | 1.0.2 (commit `99b33decf13b3a8e556273302a62ed0102ea755f`) |
| Licence | MIT, Nicolas Ferreira ([licenses/Enshrouded-Interactive-Map-LICENSE.txt](../../../../licenses/Enshrouded-Interactive-Map-LICENSE.txt)) |

`engine.ts` and `MapView.tsx` build on its Leaflet map. In `gamemap.ts`, the Leaflet layer and the height read-out
(`Height`) come from its `app/src/gamemap.js`. The search in `MapView.tsx` follows its `match.js`. `map.css` restyles its
map on the launcher's tokens.

The relief's shaders (`reliefShader.ts`) are Kiln's own and share no code with upstream. The map's textures and marker
data are game data: they are not in this repository
([docs/kiln/launcher/gameart.md](../../../../docs/kiln/launcher/gameart.md)).
