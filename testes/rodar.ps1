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

if ($falhou -gt 0) {
    Write-Host "`n$falhou teste(s) reprovaram" -ForegroundColor Red
    exit 1
}
Write-Host "`nTudo passou" -ForegroundColor Green
