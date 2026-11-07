// Where this launcher build comes from (app_info.distribution, src-tauri/src/distribution.rs):
//   direct  frostdev.io and GitHub Releases: the self-update (Update notice, Settings > Updates, About's Check now)
//   nexus   Nexus Mods, which does not allow programs that update themselves: no update checks, notices or buttons
//           anywhere (desktop and Deck UI), and update_* is never called; Settings, About and Deck Settings show one
//           line, "New versions: Nexus Mods", that opens the Kiln page there in the browser.
// `useDistribution()` (api/hooks.ts) reads it. Browser mock: `?dist=nexus` (api/mock.ts).
// A leaf module (type imports only): the mock imports it while commands.ts is still loading.
import type { AppInfo, Distribution } from "./commands";

/** The Kiln page on Nexus Mods (opened with app_open_url; nothing is fetched). */
export const NEXUS_PAGE = "https://www.nexusmods.com/enshrouded/mods/301";

/** The distribution app_info names; an older backend without the field is a direct build. */
export const distributionOf = (info: AppInfo): Distribution => info.distribution ?? "direct";
