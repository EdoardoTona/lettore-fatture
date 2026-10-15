# Verifica della versione corrente

Eseguita il 15 ottobre 2026 su macOS ARM64, aggiornata con la prima UI Windows.

- 17 test automatici superati: XML multi-corpo, CMS valido/alterato, XMLDSig
  valido/alterato e metadati con algoritmo non supportato, PEC annidate,
  deduplicazione, HTML, paginazione, rifiuto DTD e integrazione esempi locali.
  Verificato anche il filtro degli invii SdI, mantenendo messaggi senza fatture
  estraibili ed email normali allegate.
  Quattro test riguardano Excel: tipi delle celle, IVA, deduplicazione, importi
  mancanti, descrizione di ripiego, limiti di precisione e lunghezza.
  Due test verificano il server JSON condiviso: recupero dopo richieste errate,
  ping ed export con deduplicazione ed esclusione delle email dopo serializzazione.
- Il bridge pubblicato e ottimizzato apre tutti i 18 file di `esempi/`:
  24 documenti PDF, nessun errore o avviso di estrazione; le pagine di trasporto
  SdI ridondanti non vengono generate.
- PDFKit legge pagine e testo selezionabile di tutti i 24 PDF.
- Verifica PDF con dati di prova IT/FR: nazione italiana omessa, estera mantenuta,
  totale con due punti e coordinate di sette colonne allineate a sinistra.
- Smoke test AppKit in una finestra reale: XML, P7M e PEC con email normali/PDF
  allegati; selezione, caricamento PDF e visibilità del pannello informazioni.
- Verifica AppKit della chiusura: X su una riga non selezionata mantenendo la
  selezione, X su una selezione multipla, pulsante «Chiudi selezionati», rimozione
  dei soli PDF temporanei e pulizia dei riferimenti ai documenti chiusi.
- Chiusura della finestra principale: verificata l’uscita dell’app e del processo
  C# sia con un PDF aperto sia durante l’avvio della lettura, controllando i PID
  con `python3 packaging/macos/verify-window-close.py`.
- Tutti e tre i P7M degli esempi superano il controllo crittografico di integrità.
  L’XML XAdES con C14N 1.1 mostra certificato/data ed esito non supportato.
- Firma ad hoc del bundle verificata con `codesign --verify --deep --strict`.
- App registrata in LaunchServices per «Apri con…»; predefiniti non cambiati.
- Export dal bridge pubblicato sui 18 esempi: 13 fatture distinte, 3 copie
  duplicate escluse e 8 documenti non fattura ignorati. File `.xlsx` aperto con
  un lettore indipendente (openpyxl): 30 colonne, date/importi tipizzati,
  partite IVA testuali, nessuna formula, filtro e intestazione bloccata.
- Smoke test dell’export dalla UI AppKit al bridge: una fattura esportata,
  email esclusa, file letto e verificato con openpyxl.
- Libreria NativeAOT del bundle: tutti i 18 esempi XML/P7M/EML convertiti
  correttamente, nessun avviso di estrazione.
- Quick Look nel servizio reale macOS, ospitato da un’app AppKit di verifica:
  fattura XML e P7M mostrati come PDF, XML ordinario come testo. Estensione
  firmata, registrata e con entitlement sandbox conservati.
  Per EML il sistema sceglie il provider Apple Mail: l’anteprima personalizzata
  delle fatture contenute nella PEC non è attiva per questo tipo sul Mac provato.

Dimensione bundle: circa 35 MiB, incluso runtime .NET 10.0.13 e libreria NativeAOT.
Nel processo persistente: prima conversione circa 268 ms, mediana delle
aperture circa 11 ms sui documenti di esempio. Sono misure locali di
apertura/estrazione/generazione PDF, escluse avvio AppKit e pittura PDFKit.

Il runtime del Mac di sviluppo era 10.0.12. Per eseguire il linker aggiornato
è stata preparata una copia privata 10.0.13 in `.build/dotnet/`, che lo script
di packaging riconosce automaticamente. Il runtime di sistema non è stato
modificato. Su altri Mac di sviluppo occorre installare un runtime .NET
aggiornato oppure specificare `FATTURE_DOTNET`.

La build Intel è predisposta, ma non è stata eseguita su un Mac Intel.
Non è ancora verificata la distribuzione notarizzata.

## Windows: verificato e da verificare

La UI Windows è stata compilata da Mac per x64 senza avvisi o errori; lo script
PowerShell `packaging/windows/package.ps1` è stato eseguito realmente per
produrre l’eseguibile portabile e lo ZIP con licenze. Runtime Core e Desktop
10.0.13 inclusi, icona e manifest Windows incorporati, sottosistema GUI.
L’eseguibile è circa 56 MiB. I test del core e del protocollo passano sul Mac;
la build Mac è stata ricompilata dopo l’estrazione del server JSON comune.

Non è disponibile un ambiente Windows in questa sessione: **UI, rendering
Windows.Data.Pdf, stampa, associazioni, inoltro alla stessa istanza e chiusura
del worker Windows non sono ancora stati eseguiti su Windows**. Non sono state
misurate prestazioni o memoria effettive dell’app Windows. ARM64 è configurato
ma non provato.

`packaging/windows/verify.ps1` esegue una finestra reale su Windows con documenti
e certificati sintetici: XML, P7M valido, email, raster PDF nativo, inspector
chiuso all’avvio, export/deduplicazione Excel, selezione/chiusura multipla,
uscita del core e pulizia dei temporanei, anche durante l’importazione.
Il workflow `.github/workflows/windows.yml` esegue questa verifica su Windows
quando il codice viene pubblicato su GitHub; non è stato avviato qui.
Stampa, drag and drop, Apri con, apertura nella stessa istanza e layout sui
diversi DPI richiedono anche una prova manuale su PC.
