#!/usr/bin/env bash
# DeskRecall – Build-Skript für Linux/macOS/WSL (Cross-Build für Windows, EnableWindowsTargeting ist gesetzt).
# Erhöht die Build-Nummer, testet die Kernlogik und erstellt beide win-x64-Varianten unter ./publish.
set -euo pipefail
cd "$(dirname "$0")"
CONFIG="${CONFIG:-Release}"
NUM_FILE=build-number.txt
BUILD=$(cat "$NUM_FILE" 2>/dev/null || echo 0)
if [[ "${NO_INCREMENT:-0}" != "1" ]]; then BUILD=$((BUILD + 1)); printf '%s' "$BUILD" > "$NUM_FILE"; fi
echo "DeskRecall 1.1.1 – Build $BUILD"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build DeskRecall.sln -c "$CONFIG" -p:BuildNumber="$BUILD"
[[ "${NO_TESTS:-0}" == "1" ]] || dotnet test tests/DeskRecall.Core.Tests -c "$CONFIG" --no-build

APP=src/DeskRecall/DeskRecall.csproj
rm -rf publish
dotnet publish "$APP" -c "$CONFIG" -r win-x64 --self-contained false -p:BuildNumber="$BUILD" -o publish/DeskRecall-win-x64
dotnet publish "$APP" -c "$CONFIG" -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:BuildNumber="$BUILD" -o publish/DeskRecall-portable-win-x64
printf '%s\r\n' "Daten im Unterordner 'Daten' speichern. Datei loeschen, um %LOCALAPPDATA%\\DeskRecall zu verwenden." > publish/DeskRecall-portable-win-x64/portable.flag
cp README.md LICENSE THIRD-PARTY-NOTICES.md publish/DeskRecall-portable-win-x64/
cp README.md LICENSE THIRD-PARTY-NOTICES.md publish/DeskRecall-win-x64/
echo "Fertig: $(pwd)/publish"
