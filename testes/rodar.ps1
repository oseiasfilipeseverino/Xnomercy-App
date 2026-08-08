# Roda a suite do app. Use ANTES de publicar release e depois de mexer em
# MainWindow.*.cs, no XAML ou nos codigos de evento.
#
# O PYTHONIOENCODING nao e detalhe: sem ele o Python no Windows escreve em
# cp1252 e a suite quebra no primeiro acento, parecendo falha de codigo.
$env:PYTHONIOENCODING = 'utf-8'

$falhou = 0
foreach ($t in 'test_xaml', 'test_codigos') {
    Write-Host "`n--- $t ---"
    python "$PSScriptRoot\$t.py"
    if ($LASTEXITCODE -ne 0) { $falhou++ }
}

if ($falhou -gt 0) {
    Write-Host "`n$falhou teste(s) reprovaram" -ForegroundColor Red
    exit 1
}
Write-Host "`nTudo passou" -ForegroundColor Green
