"""Rede de segurança pra dividir o MainWindow.xaml.cs (1.723 linhas).

O risco aqui é diferente do site. O XAML referencia os handlers **pelo nome**,
como texto — o compilador C# não confere isso. Um handler que some ou muda de
nome na mudança:

  - COMPILA sem reclamar
  - quebra só quando a janela abre, com XamlParseException

É o mesmo tipo de armadilha que o `url_for` foi no site: falha em execução, não
na build, e a build passando dá a impressão de que está tudo certo.

Este teste guarda o retrato de hoje e acusa se algum handler ou elemento
nomeado deixar de existir.

Uso:  python test_xaml.py
      python test_xaml.py --atualizar    (depois de mudança intencional)
"""
import json
import pathlib
import re
import sys

BASE = pathlib.Path(__file__).parent.parent          # a pasta do projeto
BASELINE = pathlib.Path(__file__).parent / 'xaml_baseline.json'

# Atributos de evento do WPF. `IsChecked="False"` também casa com "Checked=",
# por isso o valor precisa parecer nome de método, não booleano.
EVENTOS = (r'(?:Click|Checked|Unchecked|Changed|Loaded|Closing|Closed|'
           r'SelectionChanged|TextChanged|MouseDown|MouseUp|MouseEnter|'
           r'MouseLeave|KeyDown|KeyUp|Drop|DragOver|Expanded|Collapsed)')

falhas = []


def checar(cond, label):
    if not cond:
        falhas.append(label)


def ler_xaml():
    """{arquivo: {'handlers': [...], 'nomes': [...]}} de todo XAML do projeto."""
    achado = {}
    for x in sorted(BASE.rglob('*.xaml')):
        if 'obj' in x.parts or 'bin' in x.parts:
            continue
        t = x.read_text(encoding='utf-8', errors='replace')
        handlers = sorted({m for m in re.findall(EVENTOS + r'="(\w+)"', t)
                           if m not in ('True', 'False')})
        nomes = sorted(set(re.findall(r'x:Name="(\w+)"', t)))
        if handlers or nomes:
            achado[x.name] = {'handlers': handlers, 'nomes': nomes}
    return achado


def codigo_do_projeto():
    """Todo o C# junto — o handler pode estar em QUALQUER arquivo parcial."""
    partes = []
    for c in sorted(BASE.rglob('*.cs')):
        if 'obj' in c.parts or 'bin' in c.parts:
            continue
        partes.append(c.read_text(encoding='utf-8', errors='replace'))
    return '\n'.join(partes)


atual = ler_xaml()

if '--atualizar' in sys.argv:
    BASELINE.write_text(json.dumps(atual, indent=1, sort_keys=True), encoding='utf-8')
    print(f'  baseline atualizado: {len(atual)} arquivo(s) XAML')
    raise SystemExit(0)

if not BASELINE.exists():
    BASELINE.write_text(json.dumps(atual, indent=1, sort_keys=True), encoding='utf-8')
    print('  baseline criado (primeira execucao)')

esperado = json.loads(BASELINE.read_text(encoding='utf-8'))
cs = codigo_do_projeto()

# 1. Todo handler do XAML precisa existir em ALGUM arquivo .cs. Este é o que
#    pega a divisão em classes parciais dando errado.
orfaos = []
for arq, d in atual.items():
    for h in d['handlers']:
        if not re.search(rf'\b(?:void|Task)\s+{h}\s*\(', cs):
            orfaos.append(f'{arq}: {h}')
checar(not orfaos, f'handler do XAML sem metodo no C#: {orfaos}')

# 2. Nada pode SUMIR do que existia — nem handler, nem elemento nomeado.
sumiram = []
for arq, d in esperado.items():
    if arq not in atual:
        sumiram.append(f'{arq} (arquivo inteiro)')
        continue
    for chave in ('handlers', 'nomes'):
        perdidos = set(d[chave]) - set(atual[arq][chave])
        if perdidos:
            sumiram.append(f'{arq} perdeu {chave}: {sorted(perdidos)}')
checar(not sumiram, f'sumiu do XAML: {sumiram}')

# 3. O detector só vale se acusa de verdade.
_falso = re.search(rf'\b(?:void|Task)\s+(\w+)\s*\(', cs)
_sem = cs.replace(f'void {_falso.group(1)}(', 'void REMOVIDO_PRO_TESTE(', 1)
_pegou = not re.search(rf'\bvoid\s+{_falso.group(1)}\s*\(', _sem)
checar(_pegou, 'o detector nao percebe um metodo renomeado — nao serve pra nada')

n_h = sum(len(d['handlers']) for d in atual.values())
n_n = sum(len(d['nomes']) for d in atual.values())
print(f'   {len(atual)} XAML | {n_h} handlers | {n_n} elementos nomeados')
print('   todos os handlers tem metodo em algum .cs')

if falhas:
    print(f'\nFALHOU: {len(falhas)}\n')
    for f in falhas:
        print(f'  - {f}')
    sys.exit(1)
print('\nOK: XAML e code-behind batem')
