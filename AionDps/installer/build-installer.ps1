# Publica o AtreiaDPS (exe único, sem precisar de .NET) e gera:
#   release\AtreiaDPS-Setup.exe  (instalador)
#   release\AtreiaDPS.zip        (versão portátil)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # pasta "DPS metter"
$project = Join-Path $root "AionDps"
$publish = Join-Path $root "release\AtreiaDPS"

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
# Publicação normal (exe + DLLs), SEM empacotar/comprimir num exe único: exes que se auto-extraem disparam
# falso positivo de antivírus com muito mais frequência.
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou" }
Copy-Item (Join-Path $PSScriptRoot "LEIA-ME.txt") $publish

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup não encontrado (winget install JRSoftware.InnoSetup)" }
& $iscc (Join-Path $PSScriptRoot "AtreiaDPS.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup falhou" }

$zip = Join-Path $root "release\AtreiaDPS.zip"
Compress-Archive -Path $publish -DestinationPath $zip -Force
Write-Host "Pronto: release\AtreiaDPS-Setup.exe e release\AtreiaDPS.zip"
