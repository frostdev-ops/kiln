// Registers every locale file beside en.json (src/i18n/<tag>.json, docs/kiln/translating.md): a translation is one
// file dropped in here, picked up by the next build. Imported once by main.tsx (Vite only: unit tests register tables
// themselves).
import { registerLocale } from "./index";

const files = import.meta.glob<{ default: unknown }>("./*.json", { eager: true });
for (const [path, mod] of Object.entries(files)) {
  const tag = path.slice(2, -".json".length);
  if (tag !== "en" && /^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$/.test(tag)) registerLocale(tag, mod.default);
}
