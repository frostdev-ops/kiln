// Acesso aos recursos extraídos do jogo (servidos pelo protocolo game://) e à configuração
// visual do FbUiBundle (data/ui.json).
import ui from '../data/ui.json';
import { items } from './gamedata.js';
import { ASSET_BASE } from './platform.js';

export { ui };

export const tex = (name) => (name ? `${ASSET_BASE}ui/${name}.webp` : undefined);
// texturas já vêm tingidas com as cores do jogo em build-assets: sem máscaras CSS
export const ttex = (name) => `${ASSET_BASE}ui/tinted/${name}.webp`;
export const itemIcon = (itemId) => {
  const it = items[itemId];
  if (!it) return undefined;
  if (it.icon !== undefined) return it.icon ? `${ASSET_BASE}${it.icon}` : undefined; // Kiln: mod items (overlay)
  return `${ASSET_BASE}icons/${it.debug}_icon.webp`;
};

// cores do jogo já convertidas (r, g, b, alfa); intensidade > 1 clareia
export function rgba(c, alphaMul = 1) {
  if (!c) return 'transparent';
  const k = Math.max(1, c.intensity || 1);
  const ch = (v) => Math.min(255, Math.round(v * k));
  return `rgba(${ch(c.r)}, ${ch(c.g)}, ${ch(c.b)}, ${(c.a * alphaMul).toFixed(3)})`;
}

// raridade da instância gravada no save (1..5); 0 = sem raridade
export const RARITY_KEYS = ['undefined', 'common', 'uncommon', 'rare', 'epic', 'legendary'];
// chaves de tradução: quem mostra passa por t()
export const RARITY_PT = ['Sem raridade', 'Comum', 'Incomum', 'Raro', 'Épico', 'Lendário'];
const BASE_RARITY = { Common: 1, Uncommon: 2, Rare: 3, Epic: 4, Legendary: 5 };

export function rarityOf(entry) {
  if (entry?.maxUpgrades !== undefined) return entry.maxUpgrades;
  return BASE_RARITY[items[entry?.itemId]?.rarity] ?? 0;
}
export const rarityColor = (r) => rgba(ui.rarity[RARITY_KEYS[r]] || ui.rarity.undefined);

// imagem branca do jogo tingida com uma cor (o jogo faz isso no shader)
export const tinted = (name, color) => ({
  backgroundColor: color,
  WebkitMaskImage: `url(${tex(name)})`,
  WebkitMaskSize: '100% 100%',
  maskImage: `url(${tex(name)})`,
  maskSize: '100% 100%',
});
