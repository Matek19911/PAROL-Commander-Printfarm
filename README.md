# PAROL6 Printfarm

Windows-Programm, mit dem ein **PAROL6**-Roboterarm gedruckte Teile aus Bambu-Lab-Druckern entnimmt.
Das Fernziel ist eine Druckfarm-Software, die Druckaufträge startet, auf Druckende und
Release-Temperatur wartet und den Drucker mit dem Roboter abräumt.

> **Status:** Phase 1 (Roboter, Teachen, Programme) und eine erste Version von Phase 2 (Druckfarm
> mit Bambu-Lab-Anbindung) sind fertig. Beides ist noch nicht an echter Hardware getestet.

## Programm herunterladen

Jede Änderung im Repository baut automatisch eine fertige `ParolFarm.exe`. Python, Batch-Dateien
oder eine Installation sind nicht nötig.

1. Auf GitHub den Reiter **Actions** öffnen und den neuesten grünen Lauf von **Build** anklicken.
2. Unten unter **Artifacts** die Datei `ParolFarm-win-x64` herunterladen und entpacken.
3. `ParolFarm.exe` per Doppelklick starten.

Bei Versions-Tags (z. B. `v0.1.0`) liegt die `.exe` zusätzlich unter **Releases**.

Einstellungen, Punkte und Programme liegen unter `%APPDATA%\ParolFarm\`.

## Erste Schritte

1. **Ohne Roboter testen:** Auf **Simulation** klicken, dann **Referenzfahrt** und **Freigeben**.
   Jetzt lassen sich Jogging, Teachen und Programme gefahrlos ausprobieren.
2. **Echter Roboter:** COM-Port wählen, **Verbinden**, **Referenzfahrt**, **Freigeben**.
   Den Geschwindigkeits-Regler für die ersten Versuche niedrig lassen (Standard 25 %).
3. **Teachen:** Den Roboter mit der SpaceMouse oder den Jog-Tasten an die gewünschte Stelle fahren,
   einen Namen eingeben und **Aktuelle Position speichern** klicken.
4. **Punkt anfahren:** Den Punkt auswählen und **Anfahren** gedrückt halten (wie *Move here* am UR).
   Beim Loslassen bremst der Roboter sanft ab.
5. **Programm:** Im Reiter *Programme* ein Programm anlegen und die Schritte MoveJ, MoveL,
   Greifer ZU/AUF und Warten einfügen. Mit **▶ Start** ausführen.

### SpaceMouse

- Die SpaceMouse wird direkt über Windows (Raw Input) gelesen. **Der 3DxWare-Treiber ist nicht nötig.**
- Sie steuert den TCP kartesisch, wahlweise im Basis- oder im Werkzeug-Koordinatensystem.
- Linke Taste: Greifer auf/zu. Rechte Taste: zwischen Basis und Werkzeug umschalten.
- Der Roboter bewegt sich nur, solange das Programmfenster aktiv ist. Verliert das Fenster den
  Fokus oder bleiben Eingaben länger als 250 ms aus, stoppt er.
- Achsrichtung, Totzone, Kennlinie und der Modus *dominante Achse* sind unter
  *Einstellungen* einstellbar.

### Druckfarm (Reiter *Druckfarm*)

1. **Drucker einrichten:** Am Drucker den LAN-Modus und den Entwicklermodus einschalten.
   Dann IP-Adresse, Seriennummer und Zugangscode eintragen und die Option
   *Wird vom PAROL6 abgeräumt* sowie das *Entnahme-Programm* wählen.
2. **Druckdatei:** In Bambu Studio slicen und über *Datei → Exportieren → Plattengeslicete Datei*
   eine `.3mf` erzeugen. Diese im Reiter *Druckfarm* mit Platte und Stückzahl als Auftrag hinzufügen.
3. **Farm starten.** Nach jedem Programmstart fragt die Farm zuerst, ob die Platte leer ist.
   Ohne diese Bestätigung startet kein Druck.

Ablauf pro Druck: Datei per FTPS hochladen (nur beim ersten Mal) → Druck per MQTT starten → Druckende
→ Bett-G-Code (Bett nach unten) → warten, bis die Betttemperatur die Release-Temperatur erreicht →
Entnahme-Programm des Roboters → nächster Druck.
Drucker ohne Roboter warten nach dem Druck auf die Bestätigung *Platte ist leer*.

Die Farm-Daten (inklusive Zugangscodes) liegen nur lokal unter `%APPDATA%\ParolFarm\farm.json`.

### Hinweis: Singularität in der Standby-Pose

Nach der Referenzfahrt steht der PAROL6 mit **J5 = 0°**. In dieser Stellung fluchten J4 und J6
(Handgelenk-Singularität). Kartesische Bewegungen mit Drehung können dort eine Warnung auslösen.
Abhilfe: J5 einmal per Gelenk-Jog etwas verdrehen, z. B. auf 30°, oder eine geteachte
Arbeitsposition per MoveJ anfahren. Danach arbeitet das kartesische Jogging normal.

## Technik

| Bereich | Umsetzung |
|---|---|
| Sprache | C# / .NET 8, Oberfläche mit WPF, ausgeliefert als einzelne `.exe` |
| Robotertreiber | Serielles Protokoll der originalen PAROL6-Platine, 100-Hz-Regelschleife |
| Bewegung | Befehl 156: kommandierter Sollpfad mit Position und Geschwindigkeits-Vorsteuerung. Der Original-Commander rechnet stattdessen ab der gemessenen Position, was beim kartesischen Jog zu Drift führt. |
| Kinematik | DH-Modell aus dem Original, numerische IK (Levenberg-Marquardt), die in der aktuellen Konfiguration bleibt |
| Bahnen | MoveJ und MoveL mit Trapezprofil. Die Bahn wird vor dem Start komplett berechnet und auf Erreichbarkeit, Gelenkgrenzen und Singularitäten geprüft. |
| Sicherheit | Not-Aus wird überwacht. Bei Ausfall der Rückmeldung bricht die Bewegung ab. Jog und *Anfahren* haben eine Totmann-Funktion. |

Projektstruktur:

```
src/ParolFarm.Core   Kinematik, Protokoll, Controller, Programme (plattformunabhängig, getestet)
src/ParolFarm.App    Windows-Oberfläche (WPF), SpaceMouse
tests/               Unit-Tests inkl. Simulation der Roboterplatine
```

Selbst bauen (optional, mit .NET 8 SDK):

```bash
dotnet test tests/ParolFarm.Core.Tests
```

```bash
dotnet publish src/ParolFarm.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Fahrplan

- [x] Phase 1: Robotertreiber, Kinematik, SpaceMouse-Teachen, Programme, Simulation
- [x] Phase 2: Bambu-Lab-Anbindung (LAN-Modus, MQTT und FTPS), Release-Temperatur, Bett-G-Code,
      Warteschlange mit Stückzahlen, pro Drucker einstellbar, ob ein PAROL6 abräumt (noch ungetestet an Hardware)
- [ ] Phase 3: Tests an X2D, P2S und P1P, Feinschliff, eventuell Kamera-Prüfung, ob die Platte leer ist

## Lizenz

GPL-3.0. Das Robotermodell und das Platinenprotokoll stammen aus der
[PAROL6-Commander-Software](https://github.com/PCrnjak/PAROL-commander-software) (GPL-3.0).
