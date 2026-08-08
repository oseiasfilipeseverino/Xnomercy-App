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
colisoes = {v: ns for v, ns in repetidos.items() if len(ns) > 1}
checar(not colisoes, f'codigos calibrados repetidos: {colisoes}')

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
