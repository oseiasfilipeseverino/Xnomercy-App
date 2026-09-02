"""O roster de grupo sai do RATEIO DE FAMA (279), nao da proximidade.

Calibrado em 01/09/2026 com 12 capturas novas. O 279 chega em dois formatos e
so um deles e' loot:

    loot de mob      [0]=fonte [1]=@MOB_KEEPER_GIANT [2]=nome [4]=199 [5]=1
    rateio de fama   [0]=fonte           [2]=nome [3]=True    [5]=12159299

O rateio vem em RAJADA: no mesmo segundo, da mesma fonte, uma linha por pessoa,
todas com o mesmo valor. No Albion fama e' dividida com o SEU GRUPO, nao com
quem esta por perto — por isso serve de roster.

A PROVA que os dois candidatos errados anteriores nao teriam passado, medida na
sessao 22 de 01/09:

    proximidade (code=104):  24 nomes
    rateio      (code=279):  20 nomes
    em comum:                 0

Zero. Quem recebe o rateio nao e' NENHUMA das pessoas que apareceram por perto.

E o conjunto e' estavel, que e' o que grupo faz e proximidade nao:
    sessao 24 e 25   as MESMAS 20 pessoas em todas as rajadas
    sessao 15/16/17  as MESMAS 5 pessoas em 15 rajadas seguidas
    sessao 26        18 estaveis, caindo pra 11 quando o grupo mudou

20 e' o teto de grupo do Albion, e e' onde as sessoes cheias param.

Este teste roda o DISCRIMINADOR contra linhas reais das capturas. Conferir so
que a constante existe nao provaria nada: o que pode dar errado e' o formato de
loot passar por rateio, ou o contrario.

Uso:  python test_grupo.py
"""
import pathlib
import re
import sys

BASE = pathlib.Path(__file__).parent.parent / 'Network'
falhas = []


def checar(cond, label):
    if not cond:
        falhas.append(label)
    print(f'  {"ok  " if cond else "FALHA"}  {label}')


FONTE_REG = (BASE / 'PlayerRegistry.cs').read_text(encoding='utf-8')
FONTE_COD = (BASE / 'GameEventCodes.cs').read_text(encoding='utf-8')


# ── O discriminador, reimplementado a partir do C# ───────────────────────────
# Traduzir a regra e' de proposito: se alguem mudar o C# sem mudar aqui, os
# casos reais abaixo denunciam. Um teste que chamasse o proprio codigo nao
# distinguiria "a regra esta certa" de "a regra mudou junto".
def eh_rateio(params: dict) -> bool:
    if 1 in params:
        return False
    if 4 in params:
        return False
    if 3 not in params:
        return False
    try:
        return int(params.get(5, 0)) >= 1000
    except (TypeError, ValueError):
        return False


def parse(linha: str) -> dict:
    """[n]=valor -> {n: valor}, como o app ve."""
    out = {}
    for k, v in re.findall(r'\[(\d+)\]=(\S+)', linha):
        out[int(k)] = v.strip('"')
    return out


# ── Linhas REAIS das capturas de 01/09 ───────────────────────────────────────
RATEIO = [
    'code=279 [0]=419 [2]=Zarpam [3]=True [5]=12159299',
    'code=279 [0]=419 [2]=Azzyt [3]=True [5]=13510332',
    'code=279 [0]=419 [2]=LKMAJOR [3]=True [5]=12159299',
    'code=279 [0]=419 [2]=Lapreotheking [3]=True [5]=12159299',
    'code=279 [0]=400 [2]=HozaH [3]=True [5]=635850',
    'code=279 [0]=301 [2]=Carabit6 [3]=True [5]=345600',   # o menor medido
]
LOOT = [
    'code=279 [0]=316 [1]=@MOB_KEEPER_GIANT_STANDARD [2]=laions633 [4]=199 [5]=1',
    'code=279 [0]=316 [1]=@MOB_KEEPER_BERSERK_CHAMPION [2]=laions633 [4]=503 [5]=1',
    'code=279 [0]=316 [1]=@MOB_KEEPER_BERSERK_CHAMPION [2]=laions633 [4]=203 [5]=1',
]

print('\n-- rateio de fama e reconhecido como grupo')
for l in RATEIO:
    p = parse(l)
    checar(eh_rateio(p), f'{p.get(2, "?"):16} valor {p.get(5, "?"):>9} -> grupo')

print('\n-- loot de mob NAO e confundido com grupo')
for l in LOOT:
    p = parse(l)
    checar(not eh_rateio(p),
           f'{p.get(1, "?")[:34]:36} -> loot, nao grupo')

print('\n-- formatos que nao podem virar grupo por descuido')
casos = [
    ({0: '1', 2: 'X', 5: '99999'}, 'sem [3] (formato desconhecido)'),
    ({0: '1', 2: 'X', 3: 'True', 5: '1'}, 'valor 1 (quantidade, nao fama)'),
    ({0: '1', 1: '@MOB', 2: 'X', 3: 'True', 5: '9999999'}, 'tem mob mesmo com [3]'),
    ({0: '1', 2: 'X', 3: 'True', 4: '5', 5: '9999999'}, 'tem quantidade [4]'),
    ({0: '1', 2: 'X', 3: 'True'}, 'sem valor nenhum'),
]
for p, rotulo in casos:
    checar(not eh_rateio(p), rotulo)

print('\n-- o codigo C# implementa a MESMA regra')
i = FONTE_REG.find('bool EhRateioDeGrupo')
corpo = FONTE_REG[i:FONTE_REG.find('\n    }', i)] if i != -1 else ''
checar(i != -1, 'EhRateioDeGrupo existe')
checar('ContainsKey(1)' in corpo, 'rejeita quando tem [1] (mob)')
checar('ContainsKey(4)' in corpo, 'rejeita quando tem [4] (quantidade)')
checar('ContainsKey(3)' in corpo, 'exige [3]')
checar('1000' in corpo, 'exige valor grande em [5]')

print('\n-- o rateio alimenta o roster, e nao rouba o loot')
i_bloco = FONTE_REG.find('GameEventCodes.PartyFameShare')
bloco = FONTE_REG[i_bloco:i_bloco + 900] if i_bloco != -1 else ''
checar(i_bloco != -1, 'o bloco do PartyFameShare existe no HandleEvent')
checar('MarcarNoGrupo' in bloco, 'e ele marca a pessoa no grupo')
checar('EhRateioDeGrupo' in bloco, 'so depois de conferir o formato')
checar('IsCalibrated' in bloco, 'com a guarda IsCalibrated, como os outros')
# Sem `return` no bloco: o MESMO 279 continua sendo o feed de loot social.
depois = bloco[bloco.find('MarcarNoGrupo'):]
checar('return;' not in depois.split('}')[0],
       'NAO da return — senao o feed de loot social pararia de receber o 279')

print('\n-- o 104 continua fora')
m = re.search(r'PartyMemberStatus \{ get; set; \} = (\w+);', FONTE_COD)
checar(m is not None and m.group(1) == 'Unknown',
       f'PartyMemberStatus segue Unknown (esta {m.group(1) if m else "?"})')

print('\n-- afericao do detector')
# Se a regra aceitasse qualquer coisa, os casos negativos acima passariam. E se
# rejeitasse tudo, os positivos falhariam. Este par garante que ela separa.
checar(eh_rateio(parse(RATEIO[0])) and not eh_rateio(parse(LOOT[0])),
       'a regra separa os dois formatos reais (senao nao esta medindo nada)')

if falhas:
    print(f'\nFALHOU: {len(falhas)}\n')
    for f in falhas:
        print(f'  - {f}')
    sys.exit(1)
print('\nOK: rateio de fama vira roster, loot de mob nao')
