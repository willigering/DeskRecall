<#
  DeskRecall – Build-Skript (Windows, PowerShell 5+/7)
  Erhöht die Build-Nummer, führt die Tests aus und erstellt:
    publish\DeskRecall-win-x64\            (framework-abhängig, benötigt .NET 10 Desktop Runtime)
    publish\DeskRecall-portable-win-x64\   (eigenständige Einzeldatei, keine Installation nötig)
  Optional: -Installer erstellt mit Inno Setup 6 (iscc.exe) einen Installer.
#>
param(
    [switch]$NoTests,
    [switch]$NoIncrement,
    [switch]$Installer,
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$numberFile = Join-Path $PSScriptRoot "build-number.txt"
$build = [int]((Get-Content $numberFile -ErrorAction SilentlyContinue) -as [int])
if ($build -lt 1) { $build = 0 }
if (-not $NoIncrement) { $build++; Set-Content -Path $numberFile -Value $build -NoNewline }
Write-Host "DeskRecall 1.2.2 – Build $build" -ForegroundColor Magenta

dotnet build DeskRecall.sln -c $Configuration -p:BuildNumber=$build
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen" }

if (-not $NoTests) {
    dotnet test tests/DeskRecall.Core.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen" }
}

$app = "src/DeskRecall/DeskRecall.csproj"
$out = Join-Path $PSScriptRoot "publish"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

dotnet publish $app -c $Configuration -r win-x64 --self-contained false -p:BuildNumber=$build -o "$out/DeskRecall-win-x64"
if ($LASTEXITCODE -ne 0) { throw "Publish (framework-abhängig) fehlgeschlagen" }

dotnet publish $app -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:BuildNumber=$build -o "$out/DeskRecall-portable-win-x64"
if ($LASTEXITCODE -ne 0) { throw "Publish (portabel) fehlgeschlagen" }
# portable.flag: Daten werden neben der EXE im Ordner „Daten“ gespeichert
Set-Content -Path "$out/DeskRecall-portable-win-x64/portable.flag" -Value "Daten im Unterordner 'Daten' speichern. Datei löschen, um %LOCALAPPDATA%\DeskRecall zu verwenden."
Copy-Item README.md, LICENSE, THIRD-PARTY-NOTICES.md "$out/DeskRecall-portable-win-x64/"
Copy-Item README.md, LICENSE, THIRD-PARTY-NOTICES.md "$out/DeskRecall-win-x64/"

if ($Installer) {
    $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
    & $iscc "/DBuildNumber=$build" "installer/DeskRecall.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer fehlgeschlagen" }
}
Write-Host "Fertig: $out" -ForegroundColor Green
