// Implementação de window.api para a versão Tauri: a lógica do save roda aqui (JS no WebView)
// e o Rust cuida de arquivos, zstd, backups e da detecção do jogo.
// Kiln: the Rust side is the launcher's editor_* commands (launcher/src-tauri/src/commands/editor.rs on
// kiln_manage::saves::editor, Linux-aware), reached through launcher/src/api/commands.ts. The Guardian server copies
// ("servidor:<steamid>", SFTP) are left out: the launcher manages servers on its own pages.
import * as kiln from '../../api/commands';
import { pickPaths } from '../../ui/DropZone';
import * as sv2c from '../shared/sv2c-web.mjs';
import { parseCharacter, buildCharacter } from '../shared/character.mjs';
import * as know from '../shared/knowledge.mjs';
import items from '../data/items.json';
import skills from '../data/skills.json';
import { t } from './i18n.js';

// Kiln: commands reject with a KilnError {code, message}; the editor shows e.message, so wrap it in an Error.
const wrap = (p) => p.catch((e) => {
  throw e instanceof Error ? e : new Error(e?.message ?? (typeof e === 'string' ? e : JSON.stringify(e)));
});
const COMMANDS = {
  list_saves: () => kiln.editorListSaves(),
  file_info: (a) => kiln.editorFileInfo(a.path),
  read_file: (a) => kiln.editorReadFile(a.path),
  zstd_decompress: (a) => kiln.editorZstdDecompress(a.data),
  zstd_compress: (a) => kiln.editorZstdCompress(a.data),
  game_running: () => kiln.editorGameRunning(),
  backup_file: (a) => kiln.editorBackupFile(a.path),
  write_save: (a) => kiln.editorWriteSave(a.path, a.data),
  list_backups: (a) => kiln.editorListBackups(a.target),
  restore_backup: (a) => kiln.editorRestoreBackup(a.src, a.target),
  open_backups: () => kiln.editorOpenBackups(),
};
const invoke = (cmd, args = {}) => wrap(Promise.resolve().then(() => COMMANDS[cmd](args)));

const isItem = (id) => Object.prototype.hasOwnProperty.call(items, id);
const isSkill = (id) => Object.prototype.hasOwnProperty.call(skills.nodes, id);
// O que pode estar vestido, pelo campo slot do item: separa a lista de equipamento da mochila,
// que também é densa e começa no índice 0. As armas ficam fora desta lista.
const ESPACOS = new Set(['Armour_Head', 'Armour_UpperBody', 'Armour_Arms', 'Armour_LowerBody', 'Armour_Feet',
  'Armour_Ring', 'Shield', 'HookShot', 'Glider', 'Backpack']);
const ehEquipavel = (id) => ESPACOS.has(items[id]?.slot);
const sessions = new Map();

const readFile = async (path) => new Uint8Array(await invoke('read_file', { path }));
const decompress = async (bytes) => new Uint8Array(await invoke('zstd_decompress', { data: Array.from(bytes) }));
const compress = async (bytes) => new Uint8Array(await invoke('zstd_compress', { data: Array.from(bytes) }));

async function load(path) {
  await sv2c.init();
  const save = await sv2c.parse(await readFile(path), decompress, ['character_data', 'player_character', 'player_knowledge']);
  const pc = save.blocks.find((b) => b.name === 'player_character');
  let character = null, error = null;
  try { character = parseCharacter(pc.payload, isItem, isSkill, ehEquipavel); } catch (e) { error = e.message; }
  // Kiln: a save that holds items nobody defines any more (a mod that is gone) is read again accepting any item id;
  // those stacks show as "Unknown item (0x…)" and saving stays refused (checkModel), so nothing is written blindly
  if (!character) {
    try {
      character = parseCharacter(pc.payload, (id) => id > 0xffff, isSkill, (id) => !isItem(id) || ehEquipavel(id));
      error = t('Itens desconhecidos neste save (mod removido?): somente leitura');
    } catch { /* keep the first error */ }
  }
  let knowledge = null;
  const pk = save.blocks.find((b) => b.name === 'player_knowledge');
  try { if (pk?.payload) knowledge = know.parseBlock(pk.payload); } catch (e) { knowledge = null; }
  return { save, character, knowledge, error };
}

function publicModel(path, modified, s) {
  const c = s.character;
  return {
    path, name: sv2c.characterName(s.save), modified, error: s.error, readOnly: !c,
    level: c?.level, xp: c?.xp, skills: c?.skills ?? [], equipment: c?.equipment ?? [], main: c?.main ?? [], extra: c?.extra ?? [],
    knowledge: s.knowledge?.entries ?? null,
    equipMask: c?._equipMask ?? 0, // posiciona cada peça no espaço certo do boneco
    blocks: s.save.blocks.map((b) => ({ name: b.name, size: b.payload ? b.payload.length : b.size })),
  };
}

function checkModel(m) {
  const ints = (v, max) => Number.isInteger(v) && v >= 0 && v <= max;
  if (!ints(m.level, 1000) || !ints(m.xp, 0xffffffff)) throw new Error(t('Nível ou XP inválido'));
  for (const s of m.skills) if (!isSkill(s.id) || !ints(s.level, 255)) throw new Error(t('Habilidade inválida'));
  for (const list of [m.equipment, m.main, m.extra]) {
    for (const e of list) {
      if (!isItem(e.itemId)) throw new Error(t('Item desconhecido: {id}', { id: e.itemId }));
      if (!ints(e.slot, 255) || !ints(e.count, 65535)) throw new Error(t('Slot ou quantidade inválidos'));
    }
  }
  const slots = m.main.map((e) => e.slot);
  if (new Set(slots).size !== slots.length) throw new Error(t('Dois itens no mesmo espaço do inventário'));
  if (m.knowledge) {
    for (const k of m.knowledge) {
      if (!ints(k.id, 0xffffffff) || !ints(k.value, 0xffffffff)) throw new Error(t('Entrada de conhecimento inválida'));
    }
    const ids = m.knowledge.map((k) => k.id);
    if (new Set(ids).size !== ids.length) throw new Error(t('Conhecimento com id repetido'));
  }
}

// o conhecimento só é regravado se mudou: assim um save aberto e salvo sem mexer nele fica igual
function mudouConhecimento(s, model) {
  if (!s.knowledge || !model.knowledge) return false;
  return JSON.stringify(s.knowledge.entries) !== JSON.stringify(model.knowledge);
}

export default {
  async listSaves() {
    const files = await invoke('list_saves');
    const out = [];
    for (const f of files) {
      try {
        const s = await load(f.path);
        out.push({ ...f, name: sv2c.characterName(s.save), level: s.character?.level, error: s.error });
      } catch (e) {
        out.push({ ...f, name: '?', error: e.message });
      }
    }
    return out;
  },
  // Kiln: no Guardian server copies here (see the header)
  serverState: () => ({ configurado: false, erro: null }),
  serverStamp: async () => null,
  async openSave(path) {
    const s = await load(path);
    sessions.set(path, s);
    const { modified } = await invoke('file_info', { path });
    return publicModel(path, modified, s);
  },
  async browseSave() {
    // Kiln: the launcher's dialog helper (plugin-dialog; null in a plain browser)
    const r = await pickPaths({ filters: [{ name: t('Save do Enshrouded'), extensions: ['ksd'] }], multiple: false });
    return r?.[0] ?? null;
  },
  async writeSave(path, model) {
    if (await invoke('game_running')) throw new Error(t('Feche o Enshrouded antes de salvar: o jogo sobrescreveria a edição.'));
    const s = sessions.get(path);
    if (!s?.character) throw new Error(t('Save não aberto para edição'));
    checkModel(model);
    const c = { ...s.character, level: model.level, xp: model.xp, skills: model.skills, equipment: model.equipment, main: model.main, extra: model.extra };
    const payload = buildCharacter(c);
    parseCharacter(payload, isItem, isSkill, ehEquipavel); // confere que o bloco novo é lido de volta
    const pk = mudouConhecimento(s, model) ? know.buildBlock({ ...s.knowledge, entries: model.knowledge }) : null;
    const blocks = s.save.blocks.map((b) => {
      if (b.name === 'player_character') return { ...b, payload, changed: true };
      if (b.name === 'player_knowledge' && pk) return { ...b, payload: pk, changed: true };
      return b;
    });
    const data = await sv2c.build({ header: s.save.header, blocks }, compress);
    await sv2c.parse(data, decompress, []); // valida hashes e estrutura
    const backupDir = await invoke('backup_file', { path });
    await invoke('write_save', { path, data: Array.from(data) });
    const fresh = await load(path);
    sessions.set(path, fresh);
    const { modified } = await invoke('file_info', { path });
    return { backupDir, model: publicModel(path, modified, fresh) };
  },
  gameRunning: () => invoke('game_running'),
  listBackups: (target) => invoke('list_backups', { target }),
  restoreBackup: (src, target) => invoke('restore_backup', { src, target }).then(() => { sessions.delete(target); return true; }),
  openBackups: () => invoke('open_backups'),
};
