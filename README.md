# Print to Markdown 0.2.4

Virtueller Drucker für Windows 10 und 11 mit eigener XPS- und OpenXPS-Konvertierung. Er speichert sichtbare Textinhalte als UTF-8-Markdown für die Übergabe an eine KI.

## Installieren und drucken

1. Entpacke das ZIP in einen dauerhaften Ordner, zum Beispiel `D:\Tools\PrintToMarkdown`.
2. Starte `Setup.bat` per Doppelklick. Das Setup erstellt einmalig `PrintToMarkdown-0.2.4.exe` mit dem C#-Compiler des mitgelieferten .NET Framework. Anschließend startet es einen Selbsttest für beide XPS-Varianten.
3. Bestätige die Windows-Administratorabfrage für die Installation der Druckerwarteschlange. Alternativ kannst du `Setup.bat` per Rechtsklick als Administrator starten. Verwende das Windows-Konto, das später druckt. Das Setup benutzt den Microsoft-XPS-Treiber und kann die vorhandene optionale XPS-Funktion bei Bedarf aktivieren.
4. Bestätige **Installation complete**, öffne ein Dokument und wähle **Strg+P → Print to Markdown (.md)** und speichere den Druckauftrag als Markdown.

Der Empfänger läuft nach der Installation und bei der Anmeldung im Hintergrund. Über sein Symbol im Infobereich kannst du ihn beenden. Danach lässt er sich über das Startmenü wieder starten.

## Komponenten

Windows stellt den Druckdienst, den XPS-Treiber und .NET Framework bereit. Die mitgelieferte C#-Anwendung empfängt XPS-Druckdaten über `127.0.0.1:19101` und wandelt sie selbst in Markdown um. Der Datenstrom bleibt im Arbeitsspeicher. Die Installation und Konvertierung kommen ohne PDF, Microsoft Print to PDF, Python, Paketdownloads und Drittprogramme aus.

Wenn der XPS-Treiber fehlt, öffne `optionalfeatures.exe`, aktiviere **Microsoft XPS Document Writer** und führe das Setup erneut aus. Das Setup lädt die XPS-Funktion nicht von Windows Update herunter.

## Ausgabe

Der Konverter setzt Textzeilen zu Absätzen zusammen, leitet Überschriften aus Schriftgrößen ab, formatiert Aufzählungen und erkennt einfache, sauber ausgerichtete Zahlentabellen mit mindestens drei Spalten. Wiederholte Randzeilen werden ausgefiltert. Seitenmarkierungen stehen als Markdown-Kommentare in der Datei. Bei mehrspaltigen Seiten, komplexen Tabellen oder als Grafik gedrucktem Text kann eine Nachbearbeitung nötig sein. Textlose Seiten werden markiert. OCR ist in dieser Ausgabe nicht enthalten.

## Installationsprotokoll und Drucker fehlt

`Setup.bat` legt im entpackten Programmordner sofort `setup.log` an. Die Datei enthält auch Ausgaben eines fehlgeschlagenen PowerShell-Starts, den Compilerlauf, den Selbsttest, die Druckerregistrierung und den Exitcode. Weitere Versuche werden mit Datum und Uhrzeit angehängt. Für die Administratorphase gibt es zusätzlich je Lauf eine Datei `printer-*.log`. Die Deinstallation schreibt in `uninstall.log`.

Das Setup meldet die Installation erst als erfolgreich, nachdem es die registrierte Warteschlange mit dem installierenden Konto gefunden hat. Schließe einen bereits offenen Druckdialog und öffne ihn erneut. Falls das Setup einen Fehler meldet, ist `setup.log` die erste Datei für die Diagnose. Prüfe vor dem Teilen, ob sie lokale Benutzernamen oder Pfade enthält.

## Änderungen

Version 0.2.4 startet den Druckempfänger im Hintergrund und zeigt nach erfolgreicher Installation eine Abschlussmeldung.

Version 0.2.3 benennt die vorhandene Warteschlange beim Setup in **Print to Markdown (.md)** um. Eine vorherige Deinstallation ist nicht erforderlich.

Seit Version 0.2.2 legt das Setup den RAW-TCP-Port mit dem Windows-Werkzeug `prnport.vbs` und ausdrücklich deaktiviertem SNMP an. Als Ausweichweg verwendet es `Add-PrinterPort` mit den Windows-Standardwerten und deaktiviert SNMP anschließend bei Bedarf. In 0.2.1 brach `Add-PrinterPort` auf dem gemeldeten Windows-10-System mit `0x80070057` ab. Die Administratorphase startet jetzt mit einem lesbaren `-File`-Aufruf. Die Protokolle behalten die Fehlerdetails.

## Deinstallation und Teststand

`Uninstall.bat` entfernt die Druckerwarteschlange, ihren Port und die Startmenü- und Autostart-Verknüpfungen. Danach kannst du die App schließen und ihren Ordner löschen. Die ältere PDF-Ausgabe 0.1.0 verwendet eine eigene Warteschlange und kann separat deinstalliert werden.

Das Archiv enthält den C#-Quellcode und den Offline-Installer. Die EXE wird beim Setup auf Windows gebaut. Kompilierung, Selbsttest und echter Druckauftrag sind auf Windows zu bestätigen. ReactOS ist für diese Ausgabe nicht unterstützt.
