# DWG Flächen (WPF, C#)

Das Programm öffnet eine DWG- oder DXF-Datei, liest alle Flächenobjekte aus, zeigt sie in einer Tabelle und einer Grafik an und summiert sie, gesamt und je Layer. AutoCAD muss nicht installiert sein; zum Lesen wird die Open-Source-Bibliothek **ACadSharp** (MIT) verwendet.

Es gibt zwei Oberflächen mit **gleichem Funktionsumfang**:

| Oberfläche | Projekt | Läuft auf |
|---|---|---|
| **WinUI 3** (bevorzugt) | `DwgFlaechen.App` | Windows 10 ab 1809 (Build 17763), Windows 11, Windows Server 2019/2022/2025 |
| **WPF** (für ältere Systeme) | `DwgFlaechen.Wpf` | zusätzlich **Windows Server 2016** (Build 14393), auf dem WinUI 3 nicht startet |

Beide verwenden denselben Kern (`DwgFlaechen.Core`). Neue Funktionen werden **immer in beiden Oberflächen** umgesetzt.

## Voraussetzungen zum Entwickeln

- Visual Studio 2026 mit der Workload **„.NET-Desktopentwicklung"**
- für die WinUI-Version zusätzlich die Workload **„WinUI-Anwendungsentwicklung"** (Windows App SDK)
- Zielframework .NET 8 (net8.0 / net8.0-windows), Plattform x64

## Starten in Visual Studio

1. `DwgFlaechen.slnx` öffnen.
2. **DwgFlaechen.App** (WinUI) oder **DwgFlaechen.Wpf** als Startprojekt festlegen.
3. F5 drücken. Beim ersten Build wird das NuGet-Paket ACadSharp geladen.

## Auf den Server bringen (Windows Server 2016)

Am einfachsten ist eine **einzelne .exe, die die .NET-Runtime enthält**. Auf dem Server muss dann nichts installiert werden.

- In VS: Rechtsklick auf **DwgFlaechen.Wpf**, dann **Veröffentlichen**, Profil **win-x64**, dann **Veröffentlichen**.
- Oder auf der Kommandozeile:

  ```
  dotnet publish src\DwgFlaechen.Wpf -p:PublishProfile=win-x64
  ```

Das Ergebnis liegt unter `src\DwgFlaechen.Wpf\bin\publish\win-x64\DwgFlaechen.exe`. Diese Datei auf den Server kopieren und starten.

Alternativ ist die .exe kleiner, wenn auf dem Server die **.NET 8 Desktop Runtime (x64)** installiert ist. Dann genügt ein normaler Release-Build.

## WinUI-Version veröffentlichen (Windows 10/11, Server ab 2019)

- In VS: Rechtsklick auf **DwgFlaechen.App**, dann **Veröffentlichen**, Profil **FolderProfile**, dann **Veröffentlichen**.
- Ergebnis: `src\DwgFlaechen.App\bin\Release\net8.0-windows10.0.19041.0\publish\`. Die App ist unverpackt (kein MSIX); Windows App SDK und .NET-Runtime werden mitgeliefert.

## Funktionen

- DWG/DXF öffnen: über den Dialog (Strg+O), per Drag & Drop oder als Kommandozeilenargument
- Erkannte Objekte:
  - geschlossene Polylinien (LWPOLYLINE/POLYLINE), deren **Bögen exakt berechnet** werden (Bulges)
  - Schraffuren (HATCH): Die Fläche wird aus den Umgrenzungen berechnet, **Inseln werden abgezogen**. Der Schraffurstil (Normal/Außen/Ignorieren) wird beachtet.
  - Kreise (exakt), Ellipsen, 2D-Solids und geschlossene Splines
  - Blockreferenzen, auch verschachtelt, skaliert, gedreht, gespiegelt oder als MInsert. Objekte auf Layer „0" übernehmen den Layer der Blockreferenz.
  - **Storeplanner-Flächen** (DFStoreplanner/PundC, siehe unten)
- Nur sichtbare Layer auswerten (ein und nicht gefroren); das lässt sich im Menü „Objekte" abschalten.
- Tabelle mit Nr, Typ, Layer, Block, Fläche (in Zeichnungseinheit² und m²) und Umfang:
  - Sortieren per Klick auf die Spaltenüberschrift
  - Textfilter
- Summe je Layer; einzelne Layer lassen sich per Häkchen aus der Summe herausnehmen.
- Die Summe der Mehrfachauswahl (Strg/Umschalt) steht in der Statusleiste.
- Grafische Vorschau:
  - Mausrad: Zoom
  - Ziehen: Verschieben
  - Klick: Fläche auswählen (mit Strg: mehrere)
  - Doppelklick: Ansicht einpassen
  - Doppelklick auf eine Tabellenzeile: Zoom auf diese Fläche
  - Klick auf einen Eckpunkt: X/Y-Koordinaten werden am Punkt und in der Statusleiste angezeigt (Fangradius 7 Pixel, der Punkt wird beim Überfahren blau markiert)
  - Umschalt+Klick auf weitere Eckpunkte: mehrere Punkte; die Statusleiste zeigt ΔX, ΔY und Abstand zum vorigen Punkt
  - Esc: Markierungen löschen · Strg+C (in der Vorschau) oder „kopieren“ in der Statusleiste: Koordinaten für Excel kopieren
  - Oben rechts: Koordinaten des Mauszeigers
  - Als Ecken gelten die Scheitelpunkte von Polylinien, Solids und Schraffur-Umgrenzungen; Kreise, Ellipsen und Splines haben keine Ecken
- Einheit automatisch aus `$INSUNITS` oder manuell wählbar (mm/cm/dm/m/in/ft)
- CSV-Export (Semikolon, deutsches Zahlenformat, UTF-8 mit BOM, für Excel) und Kopieren in die Zwischenablage

## Vergleich zweier Zeichnungen

Mit **„Vergleichen"** (Strg+D) wird zur geladenen Zeichnung **A** eine zweite Zeichnung **B** geöffnet, z. B. ein neuerer Planstand desselben Geschosses. Beide werden im selben Koordinatensystem übereinander gezeichnet.

- **Zuordnung:** Zuerst über das gleiche Handle, denn Planstände derselben DWG behalten ihre Handles. Voraussetzung ist eine ähnliche Lage. Danach werden die übrigen Flächen geometrisch zugeordnet, nach Überdeckung der Umrisse und dem Flächenverhältnis.
- **Status und Farbe:**
  - **Neu** (nur in B): grün
  - **Entfallen** (nur in A): rot
  - **Geändert:** blau, die alte Kontur aus A rot gestrichelt. Die Spalte „Änderung“ nennt, was sich unterscheidet: Kontur, Fläche, Layer, Objektart, Beschriftung oder Block.
  - **Unverändert:** grau
- **Toleranz:** Konturen gelten als gleich, wenn alle Punkte höchstens ein Millionstel der Zeichnungsdiagonale voneinander abweichen.
- **Tabelle:** Fläche A, Fläche B, Δ und Δ in m². Filtern lässt sich nach Status (Häkchen in der Vergleichsleiste), Layer und Text. In der Statusleiste stehen Summe A, Summe B, Δ und die Werte der Auswahl.
- **Export:** CSV-Export und Zwischenablage liefern die Vergleichstabelle.
- **Neu laden** (F5) liest beide Zeichnungen neu.
- **Kommandozeile:** `DwgFlaechen.Cli a.dwg --vergleich b.dwg [--csv vergleich.csv]`

## Storeplanner-Flächen (GIN_DBAREA)

Die Flächen des DFStoreplanners (PundC) sind das C++-Objekt `GIN_DbArea` (DXF-Klasse `GIN_DBAREA`, Anwendung `GIN_STOREPLANNER`, abgeleitet von `OdDbCurve`). Ohne das Storeplanner-Modul erscheint es in AutoCAD als Proxy-Objekt. Die eigentlichen Objektdaten sind binär und nicht dokumentiert. Ausgewertet wird deshalb die **Proxy-Grafik**, die beim Speichern mit in die DWG geschrieben wird:

- **Randlinien** (Linienzüge und Kreisbögen) bilden die Außenkontur und die Inseln. Sie werden zu geschlossenen Konturen verkettet, daraus wird die Fläche berechnet. Inseln werden abgezogen.
- **Gefülltes Dreiecksnetz** (Shell): Dient als Gegenprobe. Es wird als Fläche verwendet, wenn Randlinien fehlen oder nicht schließen. Bei Kreisbögen ist es etwas ungenauer.
- **Gangflächen** (Layer `SP_AREAS_CORRIDOR`) haben keine Füllung. Ihre Fläche kommt nur aus den Randlinien.
- **Beschriftung:** Das Objekt `GIN_DBENTIYREF` steht direkt hinter der Fläche (Handle + 1). Es enthält Name, Sortiment, Marke, Nummer und die Fläche („84.34 qm“). In Tabelle und CSV erscheinen die Spalten „Beschriftung“ und „lt. Beschr. [m²]“ zum Abgleich.
- **Einheit:** `$INSUNITS` ist in Storeplanner-Zeichnungen oft falsch gesetzt, z. B. auf Zoll. Passen die Flächen zu den „qm“-Angaben der Beschriftungen, wird die Einheit daraus abgeleitet (meist Meter). Es erscheint dann ein Hinweis.
- Ein- und ausschalten lässt sich das im Menü „Objekte“ unter „Storeplanner-Flächen“.

Getestet mit `941_3234_2026_05_3OG.dwg`: Alle 52 Flächen werden erkannt. Alle 39 beschrifteten Flächen stimmen auf zwei Nachkommastellen mit ihrer Beschriftung überein.

Voraussetzung: Die DWG muss mit Proxy-Grafik gespeichert sein (`PROXYGRAPHICS = 1`, Standard). ACadSharp ab 3.7.16 (getestet mit 3.8.0).

## Projektstruktur

| Projekt | Inhalt |
|---|---|
| `DwgFlaechen.Core` | Einlesen, Geometrie und Vergleich (`DwgAreaExtractor`, `DwgComparer`, `Loop`, `Affine3`, `CsvExporter`), unabhängig von der Oberfläche |
| `DwgFlaechen.App` | WinUI-3-Oberfläche (`MainWindow` mit Vorschau und Eckpunkt-Overlay), bevorzugt |
| `DwgFlaechen.Wpf` | WPF-Oberfläche (`MainWindow`, `PreviewCanvas`) für Windows Server 2016 |
| `DwgFlaechen.Cli` | Konsolenvariante: `DwgFlaechen.Cli plan.dwg --csv flaechen.csv [--einheit mm] [--keine-bloecke]` |

## Genauigkeit und Grenzen

- Polylinien mit Bögen, Kreise und Kreisbogenkanten in Schraffuren werden analytisch berechnet.
- Ellipsen und Splines werden fein polygonisiert; der relative Fehler liegt bei etwa 3·10⁻⁶.
- Bei nicht-uniform skalierten Blöcken werden Bögen vor der Transformation polygonisiert.
- **Nicht ausgewertet:** REGION und 3D-Solids (ACIS-Geometrie; sie werden als Hinweis gemeldet), 3D-Polylinien und der Papierbereich (Layouts).
- Schraffuren und Polylinien, die dieselbe Fläche beschreiben, werden beide gezählt. Im Menü „Objekte" lässt sich eine der beiden Objektarten ausschließen.
