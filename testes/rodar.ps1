# Roda a suite do app. Use ANTES de publicar release e depois de mexer em
# MainWindow.*.cs, no XAML ou nos codigos de evento.
#
# O PYTHONIOENCODING nao e detalhe: sem ele o Python no Windows escreve em
# cp1252 e a suite quebra no primeiro acento, parecendo falha de codigo.
$env:PYTHONIOENCODING = 'utf-8'

$falhou = 0
foreach ($t in 'test_xaml', 'test_codigos', 'test_grupo') {
    Write-Host "`n--- $t ---"
    python "$PSScriptRoot\$t.py"
    if ($LASTEXITCODE -ne 0) { $falhou++ }
}

# -- Medidor de dano: roda o tracker REAL, nao le o fonte -----------------------
# Os testes acima leem o codigo. Este executa a DLL compilada, porque o bug que
# ele trava (dano de quem ainda nao tinha nome sumindo do ranking) so aparece
# rodando - lendo o fonte ele parecia correto.
#
# no-incremental de proposito: sem ele, trocar o arquivo por uma versao antiga e
# voltar deixa a DLL velha no lugar (Copy-Item preserva a data do arquivo e o
# MSBuild conclui que nao ha nada pra recompilar). Isso ja fez este teste
# reprovar contra o codigo certo uma vez.
#
# ESTE ARQUIVO PRECISA FICAR EM ASCII PURO. O PowerShell 5.1 le .ps1 sem BOM como
# cp1252: um travessao em UTF-8 vira os bytes E2 80 94, e 0x94 em cp1252 e ASPAS.
# A string fecha no meio e o script inteiro para de parsear.
Write-Host "`n--- harness_dano (compila o app e roda o tracker) ---"
$app = Split-Path $PSScriptRoot -Parent
dotnet build "$app\XnomercyApp.csproj" -c Release --nologo -v quiet --no-incremental | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  o app nao compilou - harness nao rodou" -ForegroundColor Red
    $falhou++
} else {
    dotnet run --project "$PSScriptRoot\harness_dano\harness_dano.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { $falhou++ }
}

# -- Grupo exato (eventos de grupo do jogo, 18/10/2026): roda o PartyTracker real
Write-Host "`n--- harness_grupo (grupo exato pelos eventos do jogo) ---"
dotnet run --project "$PSScriptRoot\harness_grupo\harness_grupo.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- Registro de energia da guild lido do jogo (18/10/2026)
Write-Host "`n--- harness_energia (registro de energia da guild) ---"
dotnet run --project "$PSScriptRoot\harness_energia\harness_energia.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- Aba Coleta (18/10/2026)
Write-Host "`n--- harness_coleta (aba Coleta) ---"
dotnet run --project "$PSScriptRoot\harness_coleta\harness_coleta.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- Aba Vendas (cartas do mercado, 18/10/2026)
Write-Host "`n--- harness_vendas (aba Vendas) ---"
dotnet run --project "$PSScriptRoot\harness_vendas\harness_vendas.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- Correcoes da revisao de 18/10/2026 (prata liquida, fragmentos, saude, privacidade...)
Write-Host "`n--- harness_revisao (correcoes da revisao) ---"
dotnet run --project "$PSScriptRoot\harness_revisao\harness_revisao.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- Valor do loot (ItemValues, 18/10/2026)
Write-Host "`n--- harness_valor (valor do loot) ---"
dotnet run --project "$PSScriptRoot\harness_valor\harness_valor.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- Mapas e Dungeons (MapHistory, 18/10/2026)
Write-Host "`n--- harness_mapas (mapas e dungeons) ---"
dotnet run --project "$PSScriptRoot\harness_mapas\harness_mapas.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- Grupo e IP (GroupGear/ItemPowerCatalog, 18/10/2026)
Write-Host "`n--- harness_ip (grupo e IP) ---"
dotnet run --project "$PSScriptRoot\harness_ip\harness_ip.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- Captura sem Npcap (raw socket, 18/10/2026)
Write-Host "`n--- harness_semnpcap (captura sem Npcap) ---"
dotnet run --project "$PSScriptRoot\harness_semnpcap\harness_semnpcap.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- calibracao dos numeros baixos e Leave
Write-Host "`n--- harness_calibracao (calibracao dos numeros baixos e Leave) ---"
dotnet run --project "$PSScriptRoot\harness_calibracao\harness_calibracao.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

# -- lutas salvas e sessoes
Write-Host "`n--- harness_lutas (lutas salvas e sessoes) ---"
dotnet run --project "$PSScriptRoot\harness_lutas\harness_lutas.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { $falhou++ }

if ($falhou -gt 0) {
    Write-Host "`n$falhou teste(s) reprovaram" -ForegroundColor Red
    exit 1
}
Write-Host "`nTudo passou" -ForegroundColor Green
