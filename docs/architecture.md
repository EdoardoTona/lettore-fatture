# Architettura e ricerca

## Componenti

```text
AppKit / PDFKit (Swift)
       │ stdin/stdout, JSON per riga
       ▼
Fatture.Cli (.NET, processo persistente)
       ▼
Fatture.Core
  DocumentEngine  → MIME / P7M / XML / PDF, limiti e deduplicazione
  InvoiceParser   → XML FPA12 e FPR12, più corpi
  Signatures      → CMS e XMLDSig, integrità crittografica
  PdfRenderer     → MigraDoc / PDFsharp, PDF A4 con testo selezionabile
  AccountingMapper → dati contabili e impronta del contenuto della fattura
  ExcelExporter   → foglio OOXML .xlsx, nessuna dipendenza aggiuntiva

Quick Look (estensione Swift, sandbox)
       │ ABI C, chiamata nello stesso processo
       ▼
Fatture.Preview (libreria C# NativeAOT)
       ▼
Fatture.Core (stesso parser e renderer)
```

Il motore non usa AppKit, PDFKit o percorsi specifici macOS, salvo la ricerca
dei font che prevede anche Windows e Linux. La UI Windows usa lo stesso
protocollo JSON tramite `Fatture.Cli.BridgeServer`.

I sorgenti macOS sono in `src/Fatture.Mac/`: `App.swift` gestisce finestra e
azioni, `DocumentViews.swift` i componenti AppKit/PDFKit, `Models.swift` i dati
condivisi con il core e `CoreBridge.swift` il processo C# e il suo ciclo di vita.
La sottocartella `QuickLook/` viene compilata separatamente come estensione;
plist, entitlement e strumenti di packaging restano in `packaging/macos/`.

Il bridge lavora su una coda seriale in background. La UI resta utilizzabile
durante importazione e conversione; ogni documento generato ha un PDF pronto
da visualizzare e salvare. Un solo processo .NET viene riutilizzato tra aperture.
Il passaggio da un documento già aperto all’altro non richiede conversione.

## Windows

`Fatture.Windows` usa Windows Forms con controlli di sistema, finestra a tre
pannelli e supporto DPI per monitor. `DesktopApplication` usa l’istanza singola
del framework per inoltrare le aperture da Esplora file alla finestra esistente.
`CoreBridge` avvia lo stesso eseguibile con `--core-bridge`, senza finestra: la
modalità riusa il server della CLI. Si distribuisce un solo runtime .NET Desktop
e si mantiene il ciclo di vita del core isolato come su Mac. Il processo viene
terminato anche se si chiude la finestra durante un’importazione.

`NativePdfView` usa [Windows.Data.Pdf](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfdocument)
con [rendering delle pagine in uno stream](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfpage.rendertostreamasync).
Dispone le pagine in un canvas scorrevole e renderizza soltanto quelle visibili,
con raster limitati a 2400 pixel sul lato maggiore. La cache usa una soglia di
32 MiB per espellere le pagine fuori vista; le pagine attualmente visibili sono
mantenute anche se superano insieme la soglia. Rendering e sostituzione del PDF
sono serializzati; le richieste obsolete vengono scartate e gli stream chiusi
prima di eliminare i file. Questo evita un runtime browser e DLL PDF aggiuntive.
L’anteprima è rasterizzata, quindi selezione e ricerca del testo richiedono un
lettore esterno; il salvataggio conserva il PDF originale. La stampa usa un
documento separato sul worker MTA e una pagina alla volta, fino a 3500 pixel,
tramite `PrintDocument` e il dialogo di sistema.

La registrazione segue i [ProgID e le Capabilities di Windows](https://learn.microsoft.com/en-us/windows/win32/shell/default-programs),
in `HKCU`, con `OpenWithProgids` e comando eseguibile/percorso tra virgolette.
Non modifica `UserChoice` o il valore predefinito delle estensioni: l’utente
sceglie in Impostazioni. `--unregister` rimuove solo le chiavi dell’app.

La distribuzione predefinita è un eseguibile self-contained compresso, con
runtime Core e Desktop 10.0.13. Non si usa trimming su Windows Forms. Il progetto
seleziona le versioni .NET senza forzare quella della proiezione Windows SDK,
che ha una numerazione indipendente. Il [targeting Windows su Mac/Linux](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100)
permette la compilazione locale; il test grafico richiede Windows.

## Ricerca sulle librerie

Riferimenti consultati il 15 ottobre 2026:

- [Specifiche tecniche 1.7.1 indicate dall’utente](https://www.agenziaentrate.gov.it/portale/specifiche-tecniche-versione-1.7.1).
  Il portale AdE e la pagina documentazione FatturaPA hanno restituito HTTP 403
  al browser di ricerca. Non è stata eseguita una verifica completa di conformità
  al documento; i tracciati di riferimento iniziali sono FPA12/FPR12 degli esempi.
- [FatturaElettronica.NET](https://github.com/FatturaElettronica/FatturaElettronica.NET):
  lettura, scrittura e validazione del tracciato; non offre un renderer PDF tra
  le funzionalità documentate. Per questo primo lettore si usa LINQ to XML,
  mantenendo anche i campi opzionali o non ancora riconosciuti nel PDF.
- [PDFsharp Core](https://docs.pdfsharp.net/PDFsharp/Overview/Specifications.html):
  generazione PDF multipiattaforma; MigraDoc aggiunge paragrafi, tabelle,
  intestazioni ripetute e paginazione. Licenza
  [MIT](https://github.com/empira/PDFsharp/blob/master/LICENSE).
- [Gestione font](https://docs.pdfsharp.net/PDFsharp/Topics/Fonts/Font-Resolving.html):
  resolver esplicito richiesto dal build Core; si usano Arial regular/bold del
  sistema macOS/Windows e DejaVu su Linux.
- [QuestPDF](https://www.questpdf.com/invoice-tutorial.html): alternativa valida
  per documenti impaginati. Non necessaria per questa versione.
- [MimeKitLite](https://www.nuget.org/packages/MimeKitLite/4.18.1): parser MIME,
  messaggi annidati, transfer encoding e charset senza motore browser.
  La crittografia P7M è affidata alle API `SignedCms` di .NET.

La conversione diretta C# → PDF permette di riutilizzare il medesimo renderer
su Windows e di distribuire un’app senza browser incorporato o runtime web.
I template sono locali e modificabili in `PdfRenderer.cs`.

## Excel e Quick Look

L’export crea un pacchetto ZIP nel formato
[SpreadsheetML / OOXML](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/structure-of-a-spreadsheetml-document),
con un unico foglio, stili, filtri e intestazione bloccata. Non richiede Excel,
automazione Office o librerie esterne. Scrive prima un file temporaneo accanto
alla destinazione e lo sostituisce solo dopo il completamento del pacchetto.
Le stringhe sono celle `inlineStr`, senza formule. Date e importi compatibili
con la precisione Excel sono numerici; gli identificativi rimangono stringhe.
`AccountingMapper` conserva i valori originali e somma soltanto riepiloghi IVA
o pagamenti completi. SHA-256 di header e body normalizzati deduplica le copie
della stessa fattura tra contenitori diversi, conservando i nomi delle sorgenti.

L’estensione usa l’API moderna
[QLPreviewProvider](https://developer.apple.com/documentation/quicklookui/qlpreviewprovider):
restituisce a Quick Look dati PDF prodotti localmente. Il core viene compilato
in `FatturePreviewCore.dylib` e caricato una sola volta con `dlopen`; non si avvia
un processo CLI nel sandbox. La chiamata `fatture_preview` serializza il rendering,
intercetta le eccezioni prima del confine ABI e produce un manifest temporaneo.
Swift combina le pagine tramite PDFKit e rimuove tutti i temporanei al termine.
L’accesso è limitato al file selezionato; non sono concessi rete o accesso globale
ai documenti. Anteprima limitata a 64 MB in ingresso e 500 pagine.

Gli XML ordinari restano leggibili come testo. La selezione del provider per un
tipo condiviso dipende da macOS: sul Mac di sviluppo gli EML continuano a essere
visualizzati dal provider Apple Mail, anche con l’estensione registrata per quel
tipo. Il motore NativeAOT converte comunque gli EML quando viene invocato.

## Lettura e sicurezza dei dati

Il caricamento XML vieta DTD ed entità esterne e non recupera XSD/stylesheet
dalla rete. Non si applicano arrotondamenti contabili: i valori decimali si
formattano in italiano mantenendo fino a otto cifre decimali, come il tracciato.
L’assenza del totale è esplicita. L’output non sostituisce l’originale elettronico.

I file allegati ricevono nomi PDF casuali all’interno della directory di output:
un nome MIME non viene mai usato come percorso di estrazione. I nomi proposti
per il salvataggio sono ripuliti da separatori, caratteri di controllo e
caratteri vietati su Windows. Hash SHA-256 evitano duplicati XML/PDF nello
stesso file; le aperture distinte rimangono indipendenti.

Il trimming conserva esplicitamente gli assembly crittografici necessari
alla risoluzione dinamica degli algoritmi XMLDSig e i due assembly DOM/rendering
MigraDoc. Quattro annotazioni mirate in `ILLink.LinkAttributes.xml` gestiscono
le reflection su questi assembly preservati; gli altri avvisi restano errori.
La verifica della build pubblicata è necessaria oltre ai test del core.
NativeAOT preserva gli stessi assembly e i metadati completi richiesti da
MigraDoc. I quattro avvisi noti di reflection MigraDoc sono visibili ma esclusi
da `WarningsAsErrors` nel solo progetto Preview; gli altri restano errori.
Le trasformazioni XMLDSig XSLT vengono rifiutate prima della verifica, perché
richiederebbero generazione dinamica di codice, indisponibile con NativeAOT.

Le firme sono verificate in modalità locale senza consultare trust store,
OCSP o CRL. Un certificato scaduto oggi può firmare una fattura storica:
si mostrano periodo di validità e data dichiarata senza dichiararne la
validità legale o lo stato storico di revoca. XMLDSig accetta solo riferimenti
interni e richiede che uno dei riferimenti copra l’intero documento XML.

## Protocollo

Richiesta:

```json
{"id":"1","operation":"open","path":"/path/documento.eml","outputDirectory":"/path/sessione/1"}
```

Risposta riuscita:

```json
{"id":"1","ok":true,"result":{"documents":[],"warnings":[]},"error":null}
```

Ogni documento contiene `id`, `kind` (`invoice`, `email`, `pdf`), `title`,
`subtitle`, `sourceName`, `pdfPath`, `suggestedFileName`, `metadata`, `signatures`.
Solo le fatture contengono inoltre `invoice`, con `fingerprint` e dizionario
`values` dei dati contabili. L’operazione `exportExcel` accetta `documents` (gli
oggetti già aperti) ed `exportPath`. Restituisce `export` con `invoiceCount`,
`duplicateCount` e `path`; filtra e deduplica i documenti nel core.
`{"id":"2","operation":"ping"}` verifica il processo. Gli errori sono risposte
`ok:false` e non interrompono il bridge; stdout è riservato al protocollo.

## Prossime iterazioni

La prossima revisione riguarda la UI Windows su un PC reale: layout, DPI,
scorrimento, stampa e integrazione con Esplora file.
Possibili estensioni indipendenti: FSM10, firme S/MIME, esportazione degli
allegati non PDF, PDF/A e validazione XSD facoltativa.
