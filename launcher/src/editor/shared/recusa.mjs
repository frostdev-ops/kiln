// Por que o guardião recusou um save: a diferença entre o que o cliente mandou e a cópia do
// servidor. O guardião recusa qualquer save que não bata byte a byte com a cópia guardada; estas
// funções traduzem isso no que de fato mudou.
//
// A conta é por item somando os três lugares, e não por lugar. Sem isso, um item que só foi
// movido da mochila para o contêiner extra aparece duas vezes — como "a mais" num lugar e "a
// menos" no outro — e esconde o que realmente entrou ou saiu do personagem.

const LUGARES = [
  ['equipment', 'vestido'],
  ['main', 'mochila'],
  ['extra', 'contêiner extra'],
];

// itemId -> { vestido: n, mochila: n, 'contêiner extra': n }
function porItem(c) {
  const out = new Map();
  for (const [campo, lugar] of LUGARES) {
    for (const e of c?.[campo] ?? []) {
      if (!out.has(e.itemId)) out.set(e.itemId, {});
      const q = out.get(e.itemId);
      q[lugar] = (q[lugar] ?? 0) + (e.count ?? 1);
    }
  }
  return out;
}

const total = (q) => Object.values(q ?? {}).reduce((a, b) => a + b, 0);

/**
 * Compara o save que o cliente mandou com a cópia do servidor.
 * `nomeItem` e `nomeConhecimento` traduzem ids em nomes; sem eles o id aparece cru.
 */
export function compararSaves(cliente, servidor, { nomeItem = String, nomeConhecimento = String } = {}) {
  const dif = {
    nivel: null,
    xp: null,
    itens: { aMais: [], aMenos: [], mudouDeLugar: [] },
    conhecimento: { aMais: [], aMenos: [] },
  };
  if (!cliente || !servidor) return dif;

  if (cliente.level !== servidor.level) dif.nivel = { servidor: servidor.level, cliente: cliente.level };
  if (cliente.xp !== servidor.xp) dif.xp = { servidor: servidor.xp, cliente: cliente.xp };

  const pc = porItem(cliente);
  const ps = porItem(servidor);
  for (const id of new Set([...pc.keys(), ...ps.keys()])) {
    const qc = pc.get(id) ?? {};
    const qs = ps.get(id) ?? {};
    const tc = total(qc);
    const ts = total(qs);
    const nome = nomeItem(id);
    const lugares = [...new Set([...Object.keys(qc), ...Object.keys(qs)])];
    if (tc !== ts) {
      // onde a quantidade subiu (ou caiu), para dizer de onde veio ou para onde foi
      const onde = lugares.filter((l) => (tc > ts ? (qc[l] ?? 0) > (qs[l] ?? 0) : (qc[l] ?? 0) < (qs[l] ?? 0)));
      const linha = { itemId: id, nome, delta: tc - ts, servidor: ts, cliente: tc, onde };
      (tc > ts ? dif.itens.aMais : dif.itens.aMenos).push(linha);
    } else if (lugares.some((l) => (qc[l] ?? 0) !== (qs[l] ?? 0))) {
      const de = lugares.filter((l) => (qc[l] ?? 0) < (qs[l] ?? 0));
      const para = lugares.filter((l) => (qc[l] ?? 0) > (qs[l] ?? 0));
      const qtd = de.reduce((a, l) => a + ((qs[l] ?? 0) - (qc[l] ?? 0)), 0);
      dif.itens.mudouDeLugar.push({ itemId: id, nome, qtd, de, para, total: tc });
    }
  }
  const porTamanho = (a, b) => Math.abs(b.delta) - Math.abs(a.delta);
  dif.itens.aMais.sort(porTamanho);
  dif.itens.aMenos.sort(porTamanho);
  dif.itens.mudouDeLugar.sort((a, b) => b.qtd - a.qtd);

  const kc = new Set((cliente.knowledge ?? []).map((e) => e.id));
  const ks = new Set((servidor.knowledge ?? []).map((e) => e.id));
  for (const id of kc) if (!ks.has(id)) dif.conhecimento.aMais.push({ id, nome: nomeConhecimento(id) });
  for (const id of ks) if (!kc.has(id)) dif.conhecimento.aMenos.push({ id, nome: nomeConhecimento(id) });
  return dif;
}

const lista = (itens, formata, limite = 6, t = (s) => s) => itens.slice(0, limite).map(formata).join('; ')
  + (itens.length > limite ? `; ${t('e mais {n}').split('{n}').join(String(itens.length - limite))}` : '');

// Ids que o editor não sabe nomear ("key_crafted", "id 123…") não dizem nada ao leitor e, em
// grande quantidade, afogam os nomes que importam: contamos à parte.
const semNome = (e) => !e.nome || e.nome.startsWith('id ') || !/[ áéíóúãõçA-Z]/.test(e.nome);

function descobertas(rotulo, itens, t = (s) => s) {
  const nomeadas = itens.filter((e) => !semNome(e));
  const anonimas = itens.length - nomeadas.length;
  if (!itens.length) return null;
  const partes = [];
  if (nomeadas.length) partes.push(lista(nomeadas, (e) => e.nome, 8, t));
  if (anonimas) partes.push(t('{n} sem nome conhecido').split('{n}').join(String(anonimas)));
  return `${rotulo} (${itens.length}): ${partes.join('; ')}`;
}

/**
 * O motivo em frases curtas. Quantidades como +12 e -5, para não confundir com "de 0 para 12".
 *
 * Devolve `[{ texto, aviso }]`. O `aviso` marca a linha que diz que a comparação não é confiável,
 * para quem desenha destacá-la sem precisar reconhecer o texto — reconhecer texto quebra calado
 * quando a frase muda, e já quebrou aqui uma vez.
 *
 * `referenciaAntiga` diz que a cópia comparada é mais nova que a recusa, caso em que parte da
 * diferença é só o que o jogador fez depois. `t` traduz; sem ela as frases saem em português, que é
 * o que serve para uso fora da interface.
 */
export function motivoEmTexto(dif, {
  referenciaAntiga = false, referenciaQuando = null, registro = null, t = (s) => s,
} = {}) {
  if (!dif) return [];
  const linhas = [];
  const diga = (texto, aviso = false) => linhas.push({ texto, aviso });
  const vars = (texto, v) => {
    let s = t(texto);
    for (const [k, valor] of Object.entries(v)) s = s.split(`{${k}}`).join(String(valor));
    return s;
  };

  // O registro anotou os tamanhos dos dois lados no momento da entrada: é o único dado exato que
  // sobra quando a cópia de referência já foi sobrescrita.
  const doRegistro = registro?.guardadoTamA
    ? vars('o registro daquele momento: personagem de {tamA} bytes contra {guardadoTamA} guardados, '
      + 'e {nK} descobertas contra {guardadoNK}', registro)
    : null;

  // Sem a cópia daquele instante, a comparação mistura o que o jogador trouxe com o que ele jogou
  // depois. Uma lista errada com um aviso em cima continua sendo lida como lista, então não
  // listamos: dizemos o que o registro garante e por que o resto falta.
  if (referenciaAntiga) {
    diga(referenciaQuando
      ? vars('não dá para dizer o que ele trouxe nesta entrada: a cópia que o servidor tinha neste instante foi '
        + 'sobrescrita pelos saves seguintes (a de agora é de {quando}), e comparar com ela mostraria o que ele '
        + 'jogou depois, não o que ele trouxe', { quando: referenciaQuando })
      : t('não dá para dizer o que ele trouxe nesta entrada: a cópia que o servidor tinha neste instante foi '
        + 'sobrescrita pelos saves seguintes, e comparar com ela mostraria o que ele jogou depois, não o que ele '
        + 'trouxe'), true);
    if (doRegistro) diga(doRegistro);
    if (registro && registro.nK === registro.guardadoNK) {
      diga(vars('descobertas: as duas listas tinham as mesmas {nK} entradas, então o que divergiu foi só '
        + 'personagem e inventário', registro));
    }
    diga(t('das recusas a partir do próximo reinício do servidor o guardião guarda a cópia de referência junto, '
      + 'e aí esta tela mostra exatamente o que veio a mais'));
    return linhas;
  }
  if (doRegistro) diga(doRegistro);

  if (dif.nivel) {
    const d = dif.nivel.cliente - dif.nivel.servidor;
    diga(vars('nível {delta}: o servidor tinha {servidor}, o cliente mandou {cliente}',
      { delta: `${d > 0 ? '+' : ''}${d}`, ...dif.nivel }));
  }
  if (dif.xp) {
    const d = dif.xp.cliente - dif.xp.servidor;
    diga(vars('xp {delta}: o servidor tinha {servidor}, o cliente mandou {cliente}',
      { delta: `${d > 0 ? '+' : ''}${d}`, ...dif.xp }));
  }
  const { aMais, aMenos, mudouDeLugar } = dif.itens;
  const onde = (i) => (i.onde.length ? ` (${i.onde.map((l) => t(l)).join(t(' e '))})` : '');
  if (aMais.length) {
    diga(vars('itens a mais no cliente: {lista}',
      { lista: lista(aMais, (i) => `${i.nome} +${i.delta}${onde(i)}`, 6, t) }));
  }
  if (aMenos.length) {
    diga(vars('itens que o cliente não tinha mais: {lista}',
      { lista: lista(aMenos, (i) => `${i.nome} ${i.delta}${onde(i)}`, 6, t) }));
  }
  if (mudouDeLugar.length) {
    diga(vars('só trocaram de lugar, mesma quantidade: {lista}', {
      lista: lista(mudouDeLugar,
        (i) => vars('{nome} x{qtd} ({de} para {para})', {
          nome: i.nome, qtd: i.qtd, de: i.de.map((l) => t(l)).join(t(' e ')), para: i.para.map((l) => t(l)).join(t(' e ')),
        }), 6, t),
    }));
  }
  for (const l of [descobertas(t('descobertas a mais no cliente'), dif.conhecimento.aMais, t),
    descobertas(t('descobertas que o cliente não tinha'), dif.conhecimento.aMenos, t)]) {
    if (l) diga(l);
  }
  if (!linhas.length) {
    diga(t('nada diferente nos campos que o editor lê: a divergência está em outra parte do save'));
  }
  return linhas;
}
