// A chave de tradução é o próprio texto em português: o que não for traduzido aparece em
// português em vez de virar "menu.botao.salvar" na tela. O preço é que mudar o texto quebra a
// chave. Como contribuir com um idioma está no README.

import en from './i18n/en.json';

export const IDIOMAS = [
  { codigo: 'pt-BR', nome: 'Português', locale: 'pt-BR', dicionario: null },
  { codigo: 'en', nome: 'English', locale: 'en-US', dicionario: en },
];

const CHAVE_GUARDADA = 'enshrouded-save-editor:idioma';

function inicial() {
  try {
    const salvo = localStorage.getItem(CHAVE_GUARDADA);
    if (salvo && IDIOMAS.some((i) => i.codigo === salvo)) return salvo;
  } catch { /* navegador sem armazenamento: segue no padrão */ }
  // sem escolha salva, tenta o idioma do sistema e cai no português
  const sistema = (typeof navigator !== 'undefined' && navigator.language) || '';
  if (sistema.toLowerCase().startsWith('pt')) return 'pt-BR';
  // Kiln: the launcher is English, so anything but Portuguese falls back to English (upstream: Portuguese)
  return IDIOMAS.find((i) => sistema.toLowerCase().startsWith(i.codigo.toLowerCase()))?.codigo ?? 'en';
}

let atual = inicial();
const ouvintes = new Set();

export const idiomaAtual = () => atual;
export const localeAtual = () => IDIOMAS.find((i) => i.codigo === atual)?.locale ?? 'pt-BR';

// Nome de item e de habilidade não é traduzido aqui: o jogo já traz os dois, e `data/items.json`
// guarda `pt` e `en` por item. Isto diz de qual campo ler. Um idioma que não seja português cai no
// inglês, porque é a outra localização que temos — o README avisa.
export const campoDeNome = () => (atual === 'pt-BR' ? 'pt' : 'en');

// o `lang` do documento guia leitor de tela, hifenização e correção ortográfica dos campos
function marcarNoDocumento() {
  if (typeof document !== 'undefined') document.documentElement.lang = localeAtual();
}
marcarNoDocumento();

export function definirIdioma(codigo) {
  if (!IDIOMAS.some((i) => i.codigo === codigo) || codigo === atual) return;
  atual = codigo;
  try { localStorage.setItem(CHAVE_GUARDADA, codigo); } catch { /* sem armazenamento: vale só nesta sessão */ }
  marcarNoDocumento();
  for (const f of ouvintes) f(codigo);
}

export function assinarIdioma(f) {
  ouvintes.add(f);
  return () => ouvintes.delete(f);
}

/** t('nível {n}', { n: 5 }). Sem tradução, devolve a própria chave. */
export function t(texto, vars) {
  const dic = IDIOMAS.find((i) => i.codigo === atual)?.dicionario;
  let s = (dic && dic[texto]) || texto;
  if (vars) {
    for (const [k, v] of Object.entries(vars)) s = s.split(`{${k}}`).join(String(v));
  }
  return s;
}

export const dataHora = (ms) => new Date(ms).toLocaleString(localeAtual());
export const hora = (ms) => new Date(ms).toLocaleTimeString(localeAtual());
