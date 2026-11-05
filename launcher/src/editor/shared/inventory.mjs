// UI keys include the container: extra slot 0 is distinct from main slot 0.
export const keyOf = (container, slot) => `${container}:${slot}`;
export function inventoryLayout(model, items, capacities) {
  const extent = list => Math.max(0, ...list.map(e => e.slot + 1));
  // A lista de equipamento é densa: o índice de uma peça depende de quais espaços a máscara
  // acende, então ele muda de personagem para personagem. Achamos a mochila pelo tipo do item;
  // procurar por um índice fixo acertava por coincidência e deixava a mochila menor do que é.
  const backpack = model.equipment.find(e => items[e.itemId]?.slot === 'Backpack' && e.count > 0);
  const mainCount = Math.max(40, extent(model.main));
  const extraCount = Math.max(capacities[items[backpack?.itemId]?.debug] || 0, extent(model.extra));
  const main = Array.from({length:mainCount}, (_,slot) => keyOf('main',slot));
  const bag = [...main.filter((_,s) => s < 16 || s >= 32), ...Array.from({length:extraCount},(_,s)=>keyOf('extra',s))];
  return {bag, all:[...bag,...main.slice(16,32)]};
}
export function entryAt(model, key) {
  if (key == null) return null;
  const [container, slot] = key.split(':');
  return model[container].find(e => e.slot === Number(slot) && e.count > 0) || null;
}
export function replaceSlots(model, changes) {
  const next = {...model, main:[...model.main], extra:[...model.extra]};
  for (const [key, entry] of changes) {
    const [container, rawSlot] = key.split(':'), slot = Number(rawSlot);
    next[container] = next[container].filter(e => e.slot !== slot);
    if (entry) next[container].push({...entry,slot});
  }
  for (const c of ['main','extra']) next[c].sort((a,b)=>a.slot-b.slot);
  return next;
}
