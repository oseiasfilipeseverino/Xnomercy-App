"""Confere os codigos de evento do Albion contra o parser.

Nasceu de um erro real, achado em 07/08/2026.

`GameEventCodes.Unknown` era -1. `PhotonEvent.EventCode` tambem devolve -1 —
pra todo pacote SEM o parametro 252, que sao os eventos internos de transporte
do Photon e chegam sem parar durante a captura.

Os dois -1 significavam coisas diferentes ("nao calibramos esse codigo" e "esse
pacote nao e evento do jogo") e eram o mesmo numero. Resultado: toda comparacao
`evt.EventCode == GameEventCodes.<nao calibrado>` casava com o trafego de
transporte inteiro. Marcar um codigo como desconhecido nao o desligava — ligava
ele em tudo.

E o tipo de falha que nao aparece: compila, nao lanca excecao, e o estrago
(nome falso entrando no roster do grupo, pacote de transporte marcado como
"POSSIVEL LOOT") parece so ruido de captura.

Este teste le OS DOIS LADOS — o sentinela do parser e o do GameEventCodes — em
vez de fixar -1 aqui. Se o parser mudar de sentinela um dia, o teste continua
valendo em vez de virar mentira.

Uso:  python test_codigos.py
"""
import pathlib
import re
import sys

BASE = pathlib.Path(__file__).parent.parent / 'Network'
PARSER = BASE / 'PhotonMessageParser.cs'
CODIGOS = BASE / 'GameEventCodes.cs'

falhas = []


def checar(cond, label):
    if not cond:
        falhas.append(label)


def sentinelas_do_parser(texto: str) -> set[int]:
    """Valores que EventCode devolve quando o pacote NAO e evento do jogo.

    Pega o corpo do getter de EventCode e coleta todo `return <numero>;` — sao
    os caminhos de 'nao ha param 252' e 'param 252 ilegivel'.
    """
    m = re.search(r'public int EventCode\s*\{(.*?)\n    \}', texto, re.S)
    if not m:
        return set()
    return {int(v) for v in re.findall(r'return\s+(-?\d+)\s*;', m.group(1))}


def valores_dos_codigos(texto: str) -> tuple[int | None, dict[str, str]]:
    """(valor de Unknown, {nome: expressao}) — expressao crua, pode ser 'Unknown'."""
    m = re.search(r'public const int Unknown\s*=\s*([^;]+);', texto)
    unknown = None
    if m:
        bruto = m.group(1).strip()
        if bruto == 'int.MinValue':
            unknown = -2**31
        elif re.fullmatch(r'-?\d+', bruto):
            unknown = int(bruto)

    props = dict(re.findall(
        r'public static int (\w+)\s*\{\s*get;\s*set;\s*\}\s*=\s*([^;]+);', texto))
    return unknown, {k: v.strip() for k, v in props.items()}


parser_txt = PARSER.read_text(encoding='utf-8')
codigos_txt = CODIGOS.read_text(encoding='utf-8')

sentinelas = sentinelas_do_parser(parser_txt)
unknown, props = valores_dos_codigos(codigos_txt)

checar(bool(sentinelas), 'nao achei os `return` do getter EventCode no parser')
checar(unknown is not None, 'nao consegui ler o valor de GameEventCodes.Unknown')
checar(bool(props), 'nao achei nenhum codigo de evento em GameEventCodes')

# 1. O CERNE. Unknown nao pode ser um valor que o parser produz — senao "nao
#    calibrado" casa com todo pacote de transporte.
if unknown is not None and sentinelas:
    checar(unknown not in sentinelas,
           f'Unknown ({unknown}) e um valor que o parser devolve pra pacote que '
           f'NAO e evento do jogo {sorted(sentinelas)} — todo codigo nao '
           f'calibrado vai casar com trafego de transporte')

# 2. Dois codigos calibrados com o mesmo numero significam que um deles esta
#    errado: o mesmo evento nao e duas coisas.
calibrados = {n: int(v) for n, v in props.items() if re.fullmatch(r'\d+', v)}
repetidos = {}
for nome, valor in calibrados.items():
    repetidos.setdefault(valor, []).append(nome)

# Compartilhamento DECLARADO: um mesmo codigo pode carregar formatos diferentes,
# e ai dois nomes apontando pra ele e' correto — desde que quem consome saiba
# distinguir os formatos. Fora desta lista, codigo repetido continua sendo erro:
# quase sempre e' copiar/colar, e o sintoma e' um evento alimentar duas features
# ao mesmo tempo em silencio.
#
# 279 carrega loot de mob E rateio de fama do grupo. Quem separa os dois e' o
# PlayerRegistry.EhRateioDeGrupo, e o test_grupo cobre a separacao com linhas
# reais das capturas.
COMPARTILHADOS = {
    279: ({'GrabbedLoot', 'PartyFameShare'},
          'formatos diferentes, separados por EhRateioDeGrupo (ver test_grupo)'),
}
colisoes = {}
for v, ns in repetidos.items():
    if len(ns) <= 1:
        continue
    esperado = COMPARTILHADOS.get(v)
    if esperado and set(ns) == esperado[0]:
        continue                      # compartilhamento declarado, tudo bem
    colisoes[v] = ns
checar(not colisoes, f'codigos calibrados repetidos: {colisoes}')

# A lista de compartilhados so vale se for EXATA: se alguem acrescentar um 3o
# nome ao 279, tem que reprovar em vez de passar pela excecao.
for v, (nomes_ok, _) in COMPARTILHADOS.items():
    achados = set(repetidos.get(v, []))
    checar(achados == nomes_ok,
           f'o compartilhamento declarado de {v} bate com o codigo '
           f'(esperado {sorted(nomes_ok)}, achei {sorted(achados)})')

# 3. Codigo calibrado nao pode valer um sentinela do parser (mesmo problema do
#    item 1, pela porta dos fundos: alguem escrever `= -1` na mao).
maus = {n: v for n, v in calibrados.items() if v in sentinelas}
checar(not maus, f'codigo calibrado com valor de sentinela do parser: {maus}')

# 4. O detector so vale se acusa de verdade: refaz a checagem 1 com o valor
#    antigo (-1) e confirma que ela reprovaria.
if sentinelas:
    _antigo = -1
    checar(_antigo in sentinelas,
           'o teste nao reproduz mais o erro original — o parser deixou de '
           'devolver -1, entao reveja se esta checagem ainda faz sentido')

# 5. CODIGOS JA DESCARTADOS nao podem voltar a valer como grupo.
#    Cada um destes foi investigado nominalmente contra as 33.807 linhas de
#    27-29/08 e tem outra explicacao. O 104 e' o caso que motiva este bloco: ele
#    ficou 22 dias valendo como PartyMemberStatus com uma justificativa que
#    parecia forte ("os 10 nomes sao exatamente os da CTA") e era circular —
#    numa CTA, quem esta PERTO de voce sao seus companheiros de guild, entao
#    proximidade e composicao de grupo tem a mesma cara. So uma captura grande
#    distingue: 81 nomes distintos, um a cada 2 segundos.
#
#    Se alguem reatribuir um destes a um codigo de party, o teste reprova e o
#    comentario acima explica por que.
DESCARTADOS = {
    104: 'entrada no alcance de renderizacao (81 nomes, 1 a cada 2s)',
    182: 'movimento (coordenadas + nome)',
    619: 'trofeu de kill na ilha',
    210: 'lista de acesso de bau/ilha',
    140: '{alianca, guild}',
    144: 'ranking', 145: 'ranking', 146: 'ranking', 147: 'ranking',
}
PARTY = [n for n in props if n.lower().startswith('party')]
_reativados = []
for nome in PARTY:
    v = props[nome]
    # props guarda TEXTO ('104', 'Unknown'). A 1a versao comparava com int e o
    # isinstance(v, int) era sempre False: o teste passava sem medir nada, e so
    # apareceu quando religuei o 104 de proposito pra ver se ele acusava.
    try:
        n = int(str(v).strip())
    except ValueError:
        continue
    if n in DESCARTADOS:
        _reativados.append(f'{nome}={n} ({DESCARTADOS[n]})')
checar(not _reativados,
       'nenhum codigo ja descartado voltou a valer como party'
       + (': ' + '; '.join(_reativados) if _reativados else ''))
checar(True, f'nenhum dos {len(DESCARTADOS)} codigos descartados esta em uso como party')

# 6. E o log de calibracao tem que IGNORAR os descartados, senao eles comem o
#    orcamento do arquivo que existe pra achar o que ainda falta.
_pcl = BASE / 'PartyCalibrationLog.cs'      # BASE ja aponta pro Network/
checar(_pcl.exists(), 'achei o PartyCalibrationLog.cs')
if _pcl.exists():
    _txt = _pcl.read_text(encoding='utf-8')
    _ini = _txt.find('IgnoredEventCodes')
    _fim = _txt.find('};', _ini) if _ini != -1 else -1
    # Se o bloco mudar de nome, o teste tem que ACUSAR — nao passar calado
    # medindo um trecho vazio, que foi exatamente o que aconteceu na 1a versao
    # (o regex saiu do editor com um byte de backspace no lugar do limite de
    # palavra, extraiu zero codigos, e a reprovacao apontou pro lugar errado).
    checar(_ini != -1 and _fim != -1, 'achei o bloco IgnoredEventCodes')
    _ignorados = {int(x) for x in re.findall(r'\d+', _txt[_ini:_fim])} if _fim != -1 else set()
    checar(len(_ignorados) > 10,
           f'extrai a lista de ignorados de verdade ({len(_ignorados)} codigos) — '
           f'lista vazia significa que o teste parou de medir')
    _faltando = [c for c in DESCARTADOS if c not in _ignorados]
    checar(not _faltando,
           'o log de calibracao ignora os codigos ja descartados'
           + (f' faltam {_faltando}' if _faltando else ''))

nao_calibrados = sorted(n for n, v in props.items() if v == 'Unknown')
print(f'   {len(props)} codigos | {len(calibrados)} calibrados | '
      f'{len(nao_calibrados)} nao calibrados')
print(f'   sentinela do parser: {sorted(sentinelas)} | Unknown: {unknown}')
if nao_calibrados:
    print(f'   aguardando captura: {", ".join(nao_calibrados)}')

if falhas:
    print(f'\nFALHOU: {len(falhas)}\n')
    for f in falhas:
        print(f'  - {f}')
    sys.exit(1)
print('\nOK: nenhum codigo colide com o sentinela do parser')
