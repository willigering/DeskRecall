# DeskRecall

**Desktop- und Sitzungsmanager für Windows 11** – speichert Ihre Arbeitsumgebung (Programme, virtuelle Desktops, Fensterpositionen, Monitore, Explorer-Ordner) und stellt sie nach einem Neustart automatisch wieder her.

Version 1.2.0 · © 2026 Willi Gering, Gering Digital

---

## Schnellstart

1. **Portable Version:** `DeskRecall-1.2.0-portable-win-x64.zip` entpacken (z. B. nach `C:\Tools\DeskRecall`) und `DeskRecall.exe` starten. Es wird nichts installiert, .NET muss nicht vorhanden sein.
   Die Datei `portable.flag` neben der EXE sorgt dafür, dass alle Daten im Unterordner `Daten` liegen. Löschen Sie sie, wenn die Daten stattdessen unter `%LOCALAPPDATA%\DeskRecall` liegen sollen.
2. Beim ersten Start trägt sich DeskRecall in den Autostart ein (`HKCU\…\Run`, ohne Administratorrechte – abschaltbar unter *Einstellungen → Allgemein*).
3. Arbeiten Sie normal. DeskRecall sichert automatisch bei jeder Änderung (und zusätzlich alle 2 Minuten als Sicherheitsnetz).
4. Nach dem nächsten Windows-Start wartet DeskRecall 8 Sekunden (einstellbar) und stellt dann die letzte Sitzung im Hintergrund wieder her.

> **Windows SmartScreen:** Die EXE ist nicht signiert. Beim ersten Start ggf. „Weitere Informationen → Trotzdem ausführen“ wählen.

Alternativ: `DeskRecall-win-x64` (framework-abhängig, klein) benötigt die **.NET 10 Desktop Runtime x64** von https://dotnet.microsoft.com/download/dotnet/10.0.

## Bedienung

**Widget (rechter Bildschirmrand)**
- Klick auf eine Desktop-Karte → sofort zu diesem Desktop wechseln (ohne Wartezeit; die Karte wird sofort als aktiv markiert, der Wechsel läuft im Hintergrund).
- Klick auf eine Programmzeile mit einem Fenster → dieses Fenster in den Vordergrund holen (wechselt ggf. den Desktop). Hat die App mehrere Fenster, bleibt die Liste zunächst zugeklappt; ein Klick auf die Zeile, das Zählabzeichen oder den Chevron klappt die Fenstertitel auf. Ein Klick auf eine Unterzeile holt genau dieses Fenster nach vorn.
- **Browser** (Chrome, Edge, Firefox, Brave, Vivaldi, Opera und andere Chromium-/Firefox-Browser): Jedes Browserfenster erscheint als Unterzeile, darunter seine **Tabs** (zugeklappt; Zählabzeichen oder Chevron klappt auf). Bei nur einem Fenster stehen die Tabs direkt unter dem Programm. Klick auf einen Tab → Desktop wechseln, Fenster nach vorn, Tab auswählen.
- **Tabs umbenennen:** Doppelklick auf einen Tab oder Rechtsklick → „Umbenennen“ (der Text ist ausgewählt, Tippen ersetzt ihn; Enter bestätigt, Esc bricht ab). Der Name gilt nur in DeskRecall und wird lokal in `tab-names.json` gespeichert; „Ursprünglichen Namen wiederherstellen“ setzt ihn zurück. Ein Stift-Symbol markiert umbenannte Tabs, der Tooltip zeigt den Originaltitel.
- Programmzeile per Drag-and-drop auf eine andere Karte (oder eine Nummer in der eingeklappten Leiste) ziehen → Programm auf diesen Desktop verschieben.
- **+ Programm hinzufügen** auf einer Karte, oder eine `.exe`/`.lnk` auf eine Karte ziehen → Programm an diesen Desktop anheften. Im Auswahldialog: **Hinzufügen** (nur anheften) oder **Hinzufügen und starten** (anheften und sofort auf diesem Desktop starten). Entfernen über das Kontextmenü der Zeile.
- **Papierkorb** unten rechts in jeder Karte → diesen Desktop schließen (Fenster wandern zum Nachbar-Desktop).
- **+ Neuer Desktop** direkt unter der letzten Karte (bzw. **+** in der eingeklappten Leiste).
- Über das Karten-Kontextmenü lässt sich die gespeicherte Fensteranordnung wiederherstellen.
- Rechtsklick auf Karte/Programm → Kontextmenü (Umbenennen, Programm hinzufügen, Fensteranordnung wiederherstellen, Neuer Desktop, **Desktop duplizieren** (gleicher Name + gleiche Explorer-Ordner), Verschieben nach …, Auf allen Desktops anzeigen, Minimieren, Schließen, Desktop schließen).
- Doppelklick auf den Desktop-Namen → umbenennen (Enter bestätigt, Esc bricht ab). Bei einer anderen Karte wechselt schon der erste Klick sofort den Desktop; während des Umbenennens wechselt ein Klick nicht.
- Chevron an der Karte → auf-/zuklappen.
- Unten: **Profil** · **Verlauf** (jede frühere Sicherung wiederherstellen).
- Statuszeile anklicken → Details zur Sicherung mit **Wiederherstellen** (ausgewähltes Profil bzw. letzte Sitzung) und **Jetzt sichern**. Beides gibt es auch im Tray-Menü; „Jetzt sichern“ zusätzlich unter Einstellungen → Sicherung.
- Kopfzeile: Einstellungen, Anheften (immer im Vordergrund), Einklappen auf die ~40 px schmale Leiste.

Das Widget **nimmt niemals den Fokus** (WS_EX_NOACTIVATE), erscheint nicht in Alt-Tab/Taskleiste, verwendet keine Tastatur- oder Maus-Hooks und blendet sich bei Vollbildanwendungen (Spiele, Videos, Präsentationen) automatisch aus. Nur beim Umbenennen eines Desktops oder Tabs wird kurz Tastatureingabe angenommen; danach erhält das vorherige Programm den Fokus zurück.

**Infobereich (Tray):** Linksklick zeigt/klappt das Widget, Rechtsklick öffnet das Menü (Sichern, Wiederherstellen, Profil laden, Einstellungen, Beenden).

## Funktionen

| Bereich | Umsetzung |
|---|---|
| Sicherung | Bei Änderungen (WinEvent-Hooks, out-of-context, gebündelt), 30 s, 1 min, 5 min oder nur manuell; zusätzlich beim Abmelden/Herunterfahren (`WM_QUERYENDSESSION`). Atomar: temporäre Datei + `File.Replace` mit `.bak`. |
| Schutz | Eine deutlich kleinere Sicherung beim Herunterfahren ersetzt die gute periodische Sicherung nicht. Nach dem Autostart überschreibt der fast leere Startzustand die Sitzung vor dem Neustart nicht, bevor die Wiederherstellung fertig ist. |
| Gespeichert | Prozessname, EXE-Pfad bzw. AppUserModelID (Store-Apps), Fensterklasse und -titel, virtueller Desktop (GUID + Index), Normalposition/-größe, Zustand (normal/minimiert/maximiert), Monitor (Gerätepfad), Dokumentpfade aus der Befehlszeile (nur echte, existierende Dateien), Explorer-Ordner, Zeitpunkt. |
| Wiederherstellung | Desktops abgleichen (GUID, dann Reihenfolge) → fehlende anlegen, Namen/Reihenfolge setzen → laufende Fenster zuordnen → nur fehlende Programme starten → auf Fenster warten (Splashscreens werden ignoriert, Zeitlimit) → Position/Zustand setzen → Fenster per HWND auf den Ziel-Desktop verschieben, **ohne** Desktop-Wechsel → am Ende einmal den zuletzt aktiven Desktop aktivieren. |
| Doppelstarts | Läuft ein Programm schon, wird es nie erneut gestartet. Single-Instance-Apps (Steam, Discord, Spotify …) und Browser werden genau einmal gestartet. |
| Browser | Firefox, Chrome, Edge, Brave, Vivaldi, Opera werden ohne URLs gestartet – ihre eigene Sitzungswiederherstellung öffnet Fenster und Tabs. DeskRecall ordnet die Fenster per Titel/Größe den Desktops zu. Im Widget werden Fenster und Tab-Titel über UI Automation angezeigt (im Hintergrund, gedrosselt, nur solange das Widget sichtbar ist). Es werden **keine** Browserdaten, URLs, Verläufe, Passwörter oder Cookies gelesen. |
| Regeln | Pro Programm (optional Titel): Ziel-Desktop, Monitor, Startzustand, nie automatisch starten, ausschließen, live anwenden. Einzeln schaltbar, Vorrang vor der Sitzung, oberste passende Regel gewinnt je Eigenschaft. |
| Profile | Erstellen, laden, aktualisieren, umbenennen, duplizieren, löschen, exportieren, importieren. Beim Laden wird nichts geschlossen; zusätzliche Fenster werden nur nach ausdrücklicher Bestätigung geschlossen (höflich per `WM_CLOSE`). |
| Verlauf | Einstellbare Anzahl (Standard 25), unveränderte automatische Sicherungen werden nicht dupliziert, jede frühere Sitzung wiederherstellbar. |
| Fehler | Deinstallierte Programme, geänderte Pfade (Squirrel-Updates wie Discord, versionierte Ordner, „App Paths“), fehlende Monitore, geänderte Auflösung (proportional), Fenster außerhalb des Bildschirms, verzögerte Starts, Administratorrechte (nur nach Zustimmung) – jedes Problem wird übersprungen und protokolliert, der Rest läuft weiter. |
| Angeheftete Programme | Über die App-Auswahl (Startmenü, Suche, Durchsuchen) oder per Drag-and-drop einer Verknüpfung/EXE an einen Desktop binden. Beim Wiederherstellen werden fehlende Einträge gestartet, laufende Fenster bei Bedarf auf den Desktop verschoben. |
| Fensteranordnung | Über das Kontextmenü der Desktop-Karte „Fensteranordnung wiederherstellen“: Fenster aus der letzten Sicherung werden den geöffneten zugeordnet und positioniert. |
| Aktualisierung | Nach dem Start und etwa alle 6 Stunden Prüfung der öffentlichen GitHub-Releases (abstellbar). Hinweis im Widget und im Infobereich, Download der passenden Variante (portabel oder framework-abhängig), Prüfung von Größe und – sofern veröffentlicht – SHA-256, Austausch der EXE ohne Administratorrechte, Neustart, Benutzerdaten bleiben unberührt. Die Feed-URL ist unter Einstellungen änderbar. |
| Erscheinungsbild | Transparenz wirkt live auf Acryl/Tönung (Text bleibt lesbar). Breite in drei Stufen (Schmal / Normal / Breit). Größenwechsel wird einmalig weich animiert, ohne den Acryl-Effekt bei jedem Tick neu aufzubauen. |
| Datenschutz | Alles lokal, keine Telemetrie, keine Registrierung, keine Administratorrechte. Die optionale Update-Prüfung ist der einzige Netzwerkzugriff (siehe *Rechtliches & Datenschutz*). |

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
- Browser-Tabs: DeskRecall sieht nur, was die Tableiste per UI Automation zeigt (Titel und Auswahl, keine URLs). Eigene Tab-Namen hängen daher am Titel – ändert eine Seite ihren Titel stark, greift der eigene Name nicht mehr. Tabs in Firefox lassen sich je nach Version etwas weniger zuverlässig auswählen als in Chromium-Browsern. Tab-Gruppen und eingeklappte Gruppen zeigt der Browser ggf. nicht als einzelne Tabs.
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
tab-names.json               eigene Tab-Namen (nur in DeskRecall)
logs\deskrecall-JJJJMMTT.log Protokoll (14 Tage)
```


## Rechtliches & Datenschutz

DeskRecall ist kostenlos und wird **ohne Gewähr** bereitgestellt. Eine Haftung besteht nur bei Vorsatz und grober Fahrlässigkeit.

Alle Sitzungs- und Einstellungsdaten bleiben **lokal** auf diesem Rechner. Es gibt keine Telemetrie, kein Benutzerkonto und keine Registrierung.

Die **einzige Netzwerkverbindung** ist die optionale Update-Prüfung gegen `api.github.com` (Standard: die öffentlichen GitHub-Releases dieses Projekts). Dabei wird typischerweise Ihre IP-Adresse an GitHub übermittelt; es werden keine Sitzungsdaten oder Nutzungsstatistiken gesendet. Die Prüfung lässt sich unter *Einstellungen → Allgemein* abschalten.

Mitgelieferter Code Dritter steht unter der MIT-Lizenz; Einzelheiten stehen in `THIRD-PARTY-NOTICES.md`. Die Lizenzdateien liegen dem Release bei und sind in der App unter *Einstellungen → Info* erreichbar.

Windows ist eine Marke der Microsoft Corporation.

Die EXE ist nicht signiert. Windows SmartScreen kann beim ersten Start warnen; gegebenenfalls „Weitere Informationen → Trotzdem ausführen“ wählen.

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
  MonitorRemapper, PlacementCorrector, AppLocator, VirtualDesktopApiResolver,
  UpdateService, InstalledAppCatalog, ArrangementPreview, WidgetAppearance
src/DeskRecall.Platform  (net10.0-windows, Win32/COM)
  WindowsVirtualDesktopService (Kompatibilitätsschicht, eigener STA-Thread), WindowManager,
  ProcessManager, MonitorManager, CompatibilityService, ExplorerFolderService, WinEventMonitor,
  AutostartService, FullscreenDetector
src/DeskRecall           (WPF, MVVM + Microsoft.Extensions.DependencyInjection)
  AppController, DesktopManager, TrayService, IconService, DialogService, UpdateController,
  WidgetWindow (WidgetUI), SettingsWindow, AppPickerWindow, ViewModels
tests/DeskRecall.Core.Tests  (xUnit, Unit- und Integrationstests mit simuliertem Windows)
```
