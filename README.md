# DeskRecall

**Desktop- und Sitzungsmanager für Windows 11** – speichert Ihre Arbeitsumgebung (Programme, virtuelle Desktops, Fensterpositionen, Monitore, Explorer-Ordner) und stellt sie nach einem Neustart automatisch wieder her.

Version 1.0.0 · © 2026 Willi Gering, Gering Digital

---

## Schnellstart

1. **Portable Version:** `DeskRecall-1.0.0-portable-win-x64.zip` entpacken (z. B. nach `C:\Tools\DeskRecall`) und `DeskRecall.exe` starten. Es wird nichts installiert, .NET muss nicht vorhanden sein.
   Die Datei `portable.flag` neben der EXE sorgt dafür, dass alle Daten im Unterordner `Daten` liegen. Löschen Sie sie, wenn die Daten stattdessen unter `%LOCALAPPDATA%\DeskRecall` liegen sollen.
2. Beim ersten Start trägt sich DeskRecall in den Autostart ein (`HKCU\…\Run`, ohne Administratorrechte – abschaltbar unter *Einstellungen → Allgemein*).
3. Arbeiten Sie normal. DeskRecall sichert automatisch bei jeder Änderung (und zusätzlich alle 2 Minuten als Sicherheitsnetz).
4. Nach dem nächsten Windows-Start wartet DeskRecall 8 Sekunden (einstellbar) und stellt dann die letzte Sitzung im Hintergrund wieder her.

> **Windows SmartScreen:** Die EXE ist nicht signiert. Beim ersten Start ggf. „Weitere Informationen → Trotzdem ausführen“ wählen.

Alternativ: `DeskRecall-win-x64` (framework-abhängig, klein) benötigt die **.NET 10 Desktop Runtime x64** von https://dotnet.microsoft.com/download/dotnet/10.0.

## Bedienung

**Widget (rechter Bildschirmrand)**
- Klick auf eine Desktop-Karte → zu diesem Desktop wechseln.
- Klick auf eine Programmzeile → Fenster in den Vordergrund holen (wechselt ggf. den Desktop).
- Programmzeile per Drag-and-drop auf eine andere Karte (oder eine Nummer in der eingeklappten Leiste) ziehen → Programm auf diesen Desktop verschieben.
- Rechtsklick auf Karte/Programm → Kontextmenü (Umbenennen, Verschieben nach …, Auf allen Desktops anzeigen, Minimieren, Schließen, Desktop schließen).
- Doppelklick auf den Desktop-Namen → umbenennen (Enter bestätigt, Esc bricht ab).
- Chevron an der Karte → auf-/zuklappen.
- Unten: **+** neuer Desktop · **Duplizieren** (neuer Desktop mit gleichem Namen + gleichen Explorer-Ordnern) · **Papierkorb** Desktop schließen (Fenster wandern zum Nachbar-Desktop) · **Wiederherstellen** (ausgewähltes Profil bzw. letzte Sitzung) · **Sichern** · **Profil** · **Verlauf**.
- Statuszeile anklicken → Details zur Sicherung.
- Kopfzeile: Einstellungen, Anheften (immer im Vordergrund), Einklappen auf die ~40 px schmale Leiste.

Das Widget **nimmt niemals den Fokus** (WS_EX_NOACTIVATE), erscheint nicht in Alt-Tab/Taskleiste, verwendet keine Tastatur- oder Maus-Hooks und blendet sich bei Vollbildanwendungen (Spiele, Videos, Präsentationen) automatisch aus. Nur beim Umbenennen eines Desktops wird kurz Tastatureingabe angenommen; danach erhält das vorherige Programm den Fokus zurück.

**Infobereich (Tray):** Linksklick zeigt/klappt das Widget, Rechtsklick öffnet das Menü (Sichern, Wiederherstellen, Profil laden, Einstellungen, Beenden).

## Funktionen

| Bereich | Umsetzung |
|---|---|
| Sicherung | Bei Änderungen (WinEvent-Hooks, out-of-context, gebündelt), 30 s, 1 min, 5 min oder nur manuell; zusätzlich beim Abmelden/Herunterfahren (`WM_QUERYENDSESSION`). Atomar: temporäre Datei + `File.Replace` mit `.bak`. |
| Schutz | Eine deutlich kleinere Sicherung beim Herunterfahren ersetzt die gute periodische Sicherung nicht. Nach dem Autostart überschreibt der fast leere Startzustand die Sitzung vor dem Neustart nicht, bevor die Wiederherstellung fertig ist. |
| Gespeichert | Prozessname, EXE-Pfad bzw. AppUserModelID (Store-Apps), Fensterklasse und -titel, virtueller Desktop (GUID + Index), Normalposition/-größe, Zustand (normal/minimiert/maximiert), Monitor (Gerätepfad), Dokumentpfade aus der Befehlszeile (nur echte, existierende Dateien), Explorer-Ordner, Zeitpunkt. |
| Wiederherstellung | Desktops abgleichen (GUID, dann Reihenfolge) → fehlende anlegen, Namen/Reihenfolge setzen → laufende Fenster zuordnen → nur fehlende Programme starten → auf Fenster warten (Splashscreens werden ignoriert, Zeitlimit) → Position/Zustand setzen → Fenster per HWND auf den Ziel-Desktop verschieben, **ohne** Desktop-Wechsel → am Ende einmal den zuletzt aktiven Desktop aktivieren. |
| Doppelstarts | Läuft ein Programm schon, wird es nie erneut gestartet. Single-Instance-Apps (Steam, Discord, Spotify …) und Browser werden genau einmal gestartet. |
| Browser | Firefox, Chrome, Edge, Brave, Vivaldi, Opera werden ohne URLs gestartet – ihre eigene Sitzungswiederherstellung öffnet Fenster und Tabs. DeskRecall ordnet die Fenster per Titel/Größe den Desktops zu. Es werden **keine** Browserdaten, Passwörter oder Cookies gelesen. |
| Regeln | Pro Programm (optional Titel): Ziel-Desktop, Monitor, Startzustand, nie automatisch starten, ausschließen, live anwenden. Einzeln schaltbar, Vorrang vor der Sitzung, oberste passende Regel gewinnt je Eigenschaft. |
| Profile | Erstellen, laden, aktualisieren, umbenennen, duplizieren, löschen, exportieren, importieren. Beim Laden wird nichts geschlossen; zusätzliche Fenster werden nur nach ausdrücklicher Bestätigung geschlossen (höflich per `WM_CLOSE`). |
| Verlauf | Einstellbare Anzahl (Standard 25), unveränderte automatische Sicherungen werden nicht dupliziert, jede frühere Sitzung wiederherstellbar. |
| Fehler | Deinstallierte Programme, geänderte Pfade (Squirrel-Updates wie Discord, versionierte Ordner, „App Paths“), fehlende Monitore, geänderte Auflösung (proportional), Fenster außerhalb des Bildschirms, verzögerte Starts, Administratorrechte (nur nach Zustimmung) – jedes Problem wird übersprungen und protokolliert, der Rest läuft weiter. |
| Datenschutz | Alles lokal, keine Telemetrie, keine Netzwerkzugriffe, keine Registrierung, keine Administratorrechte. |

## Unterstützte Windows-Versionen

| Windows | Build | Virtuelle Desktops |
|---|---|---|
| Windows 11 24H2 / 25H2 | 26100 / 26200 | voll (Schnittstellensatz „24H2“) |
| Windows 11 23H2 | 22631 ab UBR 3085 | voll („23H2“) |
| Windows 11 22H2 / 23H2 (frühe Updates) | 22621/22631 UBR 2215–3084 | voll („22H2“) |
| Windows 11 21H2 / frühes 22H2 | 22000, 22621 < 2215 | voll („Legacy“, ungetestet) |
| Windows 10 | 19041+ | nur lesen: Fenster werden positioniert, aber nicht zwischen Desktops verschoben; nicht offiziell unterstützt |

Die internen Desktop-Schnittstellen von Windows sind **nicht dokumentiert** und ändern sich mit Windows-Updates. DeskRecall wählt den Schnittstellensatz anhand von Build und UBR, prüft ihn beim Start per Selbsttest gegen die Registry und fällt bei Fehlern auf einen eingeschränkten Modus (nur lesen) zurück – die App stürzt dabei nicht ab. Bricht ein künftiges Update die Schnittstelle, kann unter *Einstellungen → Virtuelle Desktops → Kompatibilitätsmodus* ein anderer Satz gewählt werden; neue Sätze lassen sich in `VdBackends.cs`/`ComInterfaces.cs` ergänzen.

## Technische Grenzen (bitte lesen)

DeskRecall stellt die **Fensteranordnung** wieder her, nicht den **inneren Zustand** von Programmen.

- **Nicht** wiederherstellbar: ungespeicherte Dokumente, laufende Berechnungen/Renderings, Spielstände, Zwischenablage, beliebiger Programmspeicher.
- Programme öffnen nach dem Start, was **sie selbst** vorsehen. Browser stellen Tabs über ihre eigene Sitzungswiederherstellung wieder her (in Chrome/Edge/Brave/Vivaldi/Opera ggf. „Beim Start: Da weitermachen, wo ich aufgehört habe“ aktivieren; in Firefox „Vorherige Sitzung wiederherstellen“).
- Dokumente/Projekte werden nur dann wieder geöffnet, wenn das Programm sie als Befehlszeilenargument erhalten hatte (z. B. VS Code, Visual Studio, Notepad++, Photoshop beim Öffnen per Doppelklick). Programme, die Projekte intern öffnen (z. B. DaVinci Resolve), starten mit ihrer eigenen Startseite/letztem Projekt.
- Programme mit mehreren gleichartigen Fenstern ohne aussagekräftigen Titel werden bestmöglich nach Größe/Position zugeordnet.
- Explorer: pro Explorer-Fenster wird der Ordner des aktiven Tabs wiederhergestellt, nicht alle Tabs.
- Programme, die mit Administratorrechten liefen, startet DeskRecall nur nach Ihrer Zustimmung (UAC). Fenster solcher Programme kann eine Anwendung ohne Adminrechte nicht verschieben (Windows-Sicherheitsgrenze).
- Store-/UWP-Apps werden über ihre AppUserModelID gestartet; einige Apps lassen sich nicht minimiert starten.
- Spiele mit eigenem Launcher/Anti-Cheat sollten über Regeln mit „Nie automatisch starten“ behandelt werden.
- Bricht ein Windows-Update die internen Desktop-Schnittstellen, arbeitet DeskRecall eingeschränkt weiter (siehe oben).

## Daten

`%LOCALAPPDATA%\DeskRecall\` (bzw. `Daten\` neben der EXE im portablen Modus):

```
settings.json                Einstellungen (unbekannte Felder bleiben bei Updates erhalten)
sessions\last-session.json   letzte Sitzung (+ .bak)
sessions\history\*.json      Verlauf
profiles\*.profile.json      Profile
logs\deskrecall-JJJJMMTT.log Protokoll (14 Tage)
```

## Bauen

Voraussetzung: .NET 10 SDK.

```powershell
# Windows
.\build.ps1              # erhöht Build-Nummer, baut, testet, erstellt publish\DeskRecall-win-x64 und publish\DeskRecall-portable-win-x64
.\build.ps1 -Installer   # zusätzlich Installer mit Inno Setup 6 (installer\DeskRecall.iss)
```
```bash
# Linux/macOS/WSL (Cross-Build)
./build.sh
```
Oder in Visual Studio 2026 `DeskRecall.sln` öffnen, Startprojekt `DeskRecall`.

## Architektur

```
src/DeskRecall.Core      (net10.0, plattformunabhängig, voll getestet)
  RuleEngine, SessionRecorder, SessionRestorer, ProfileManager, SettingsService, LoggingService,
  SessionStore (atomar, Verlauf, Deduplizierung), WindowMatcher, DesktopPlanner, LaunchPlanner,
  MonitorRemapper, PlacementCorrector, AppLocator, VirtualDesktopApiResolver
src/DeskRecall.Platform  (net10.0-windows, Win32/COM)
  WindowsVirtualDesktopService (Kompatibilitätsschicht, eigener STA-Thread), WindowManager,
  ProcessManager, MonitorManager, CompatibilityService, ExplorerFolderService, WinEventMonitor,
  AutostartService, FullscreenDetector
src/DeskRecall           (WPF, MVVM + Microsoft.Extensions.DependencyInjection)
  AppController, DesktopManager, TrayService, IconService, DialogService,
  WidgetWindow (WidgetUI), SettingsWindow, ViewModels
tests/DeskRecall.Core.Tests  (xUnit, Unit- und Integrationstests mit simuliertem Windows)
```
