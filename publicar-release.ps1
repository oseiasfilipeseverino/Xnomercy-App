# Gera e publica uma release do XnomercyApp.
#
#   .\publicar-release.ps1 1.0.52
#   .\publicar-release.ps1 1.0.52 -SoGerar     # monta o pacote e NAO publica
#
# A checagem de tamanho no meio existe por um motivo: sem --self-contained o
# dotnet gera um build que depende do .NET instalado na maquina. Ele compila,
# empacota e sobe sem erro nenhum — e o pacote sai com ~3 MB em vez de ~70 MB.
# Quem atualizasse ficaria com um app que nao abre. Por isso o script para
# sozinho se a pasta publicada vier pequena demais.

param(
    [Parameter(Mandatory = $true)][string]$Versao,
    [switch]$SoGerar
)

$ErrorActionPreference = 'Stop'

# Acha o projeto esteja o script dentro dele ou na pasta de cima. O script
# morava fora do repositorio, o que o deixava sem versionamento nenhum — junto
# com o motivo do --self-contained explicado la em cima, que custou uma release
# quebrada pra descobrir. Agora mora dentro, e esta deteccao existe pra quem ja
# tem o caminho antigo na memoria continuar rodando igual.
$proj = if (Test-Path (Join-Path $PSScriptRoot 'XnomercyApp.csproj')) { $PSScriptRoot }
        else { Join-Path $PSScriptRoot 'XnomercyApp' }
if (-not (Test-Path (Join-Path $proj 'XnomercyApp.csproj'))) {
    throw "nao achei XnomercyApp.csproj a partir de $PSScriptRoot"
}
$pub  = Join-Path $proj 'publish'
$rel  = Join-Path $proj 'Releases'
$repo = 'https://github.com/oseiasfilipeseverino/Xnomercy-App'
$vpk  = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'

if (-not (Test-Path $vpk)) {
    throw "vpk nao encontrado em $vpk. Instale com: dotnet tool install -g vpk"
}

Write-Host "`n=== 1/4  Compilando $Versao (self-contained) ===" -ForegroundColor Cyan
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish "$proj\XnomercyApp.csproj" -c Release -r win-x64 --self-contained true `
    -p:Version=$Versao -o $pub
if ($LASTEXITCODE -ne 0) { throw 'falhou no dotnet publish' }

Write-Host "`n=== 2/4  Conferindo o build ===" -ForegroundColor Cyan
$mb = [math]::Round(((Get-ChildItem $pub -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "  pasta publicada: $mb MB"
if ($mb -lt 50) {
    throw "build de $mb MB — veio SEM o runtime embutido. Um pacote assim instala e nao abre. Abortado."
}
$exe = Join-Path $pub 'XnomercyApp.exe'
if (-not (Test-Path $exe)) { throw "XnomercyApp.exe nao foi gerado em $pub" }
Write-Host "  ok: runtime embutido e exe no lugar" -ForegroundColor Green

Write-Host "`n=== 3/4  Empacotando com Velopack ===" -ForegroundColor Cyan
& $vpk pack --packId XnomercyApp --packVersion $Versao --packDir $pub `
    --mainExe XnomercyApp.exe --outputDir $rel
if ($LASTEXITCODE -ne 0) { throw 'falhou no vpk pack' }

$full = Join-Path $rel "XnomercyApp-$Versao-full.nupkg"
$fullMb = [math]::Round(((Get-Item $full).Length / 1MB), 1)
Write-Host "  pacote completo: $fullMb MB"
if ($fullMb -lt 50) { throw "pacote de $fullMb MB — pequeno demais. Nao publique." }
Write-Host "  ok: tamanho compativel com as releases anteriores (~70 MB)" -ForegroundColor Green

if ($SoGerar) {
    Write-Host "`nPacote pronto em $rel (nao publicado, -SoGerar)." -ForegroundColor Yellow
    Write-Host "Teste o Setup.exe antes de publicar."
    exit 0
}

Write-Host "`n=== 4/4  Publicando no GitHub ===" -ForegroundColor Cyan
$token = (gh auth token).Trim()
if (-not $token) { throw 'gh sem token. Rode: gh auth login' }
& $vpk upload github --repoUrl $repo --token $token --publish `
    --releaseName $Versao --tag $Versao --outputDir $rel
if ($LASTEXITCODE -ne 0) { throw 'falhou no vpk upload' }

Write-Host "`nPublicado: $repo/releases/tag/$Versao" -ForegroundColor Green
Write-Host "Quem tem o app instalado ve o aviso de atualizacao na proxima abertura."
