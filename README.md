# Lettore Fatture

Visualizzatore locale di fatture elettroniche italiane: core **C# / .NET 10**, app
**macOS nativa AppKit + PDFKit**. Il core è riutilizzabile su Windows; la UI Windows
è prevista dopo la revisione della versione Mac.

La finestra mostra i documenti a sinistra, il PDF al centro e un pannello
informazioni richiudibile a destra. Supporta apertura multipla, drag and drop,
Finder / Dock / «Apri con…», salvataggio PDF, stampa e zoom.
Il pannello informazioni parte chiuso e si apre dal pulsante nella barra strumenti.
Chiudendo la finestra si chiudono l’app e il processo C#; i PDF temporanei
di sessione vengono rimossi dopo l’arresto del motore.
Ogni riga ha una X per chiudere il documento. Con ⌘-clic o ⇧-clic puoi
selezionare più documenti e chiuderli insieme con la X di una riga selezionata,
il pulsante «Chiudi selezionati», Canc oppure ⌘W.

## Struttura del progetto

| Percorso | Contenuto |
| --- | --- |
| `src/Fatture.Core/` | Lettura documenti, firme, generazione PDF ed export Excel |
| `src/Fatture.Cli/` | CLI e protocollo JSON persistente usato dalla UI |
| `src/Fatture.Mac/` | App nativa Swift: finestra, componenti UI, modelli e bridge |
| `src/Fatture.Mac/QuickLook/` | Estensione Swift per le anteprime nel Finder |
| `src/Fatture.Preview/` | Libreria C# NativeAOT incorporata nell’estensione |
| `tests/Fatture.Tests/` | Test del core con documenti e certificati sintetici |
| `packaging/` | Script di build/verifica, plist, entitlement e licenze dipendenze |
| `docs/` | Architettura, scelte tecniche e risultati delle verifiche |

`LettoreFatture.slnx` raccoglie i progetti .NET; i sorgenti Swift vengono compilati
dallo script macOS. I lock file NuGet sono versionati. `.editorconfig` e
`.gitattributes` mantengono codifica UTF-8 e terminatori LF anche su Windows.

`artifacts/`, `.build/`, `bin/`, `obj/` e i documenti privati in `esempi/` sono
esclusi da Git. Gli esempi locali servono solo alle verifiche di integrazione:
i test sintetici del core funzionano anche senza quella cartella.

## Avvio su macOS

Requisiti di sviluppo: macOS 14+, Command Line Tools di Xcode e SDK .NET 10
con runtime 10.0.13 o successivo (serve anche al linker).
Il bundle generato include il runtime: l’utente finale non deve installare .NET.

```sh
bash packaging/macos/package.sh
open 'artifacts/macos/Lettore Fatture.app'
```

La build predefinita segue l’architettura del Mac. Per Intel:

```sh
FATTURE_OUTPUT_DIR="$PWD/artifacts/macos-intel" bash packaging/macos/package.sh osx-x64
```

`FATTURE_DOTNET` permette di scegliere un eseguibile `dotnet` diverso da quello
nel PATH, ad esempio un’installazione privata aggiornata.

La build sostituisce soltanto il precedente bundle generato nella destinazione,
dopo aver completato la nuova build. La firma è ad hoc, per lo sviluppo locale.
Per distribuire l’app ad altri Mac serviranno Developer ID e notarizzazione.

Per registrarla nel menu «Apri con…»:

```sh
bash packaging/macos/register.sh
```

Per renderla predefinita usare **File → Associa estensioni…** nell’app, oppure
Finder → Ottieni informazioni → Apri con → Modifica tutto. L’app non cambia
automaticamente le associazioni esistenti. L’associazione `.xml` riguarda tutti
gli XML, anche quelli che non sono fatture.

## Quick Look nel Finder

Dopo la registrazione dell’app, seleziona una fattura XML o P7M nel Finder e
premi **Spazio**: Quick Look mostra il PDF senza aprire la finestra del lettore.
L’estensione è inclusa nel bundle e usa lo stesso core C#, compilato come
libreria nativa. Gli XML che non sono fatture vengono mostrati come testo.

Il motore gestisce anche EML e PEC; tuttavia macOS può scegliere la propria
estensione Mail per gli EML. Sul Mac di sviluppo prevale l’anteprima email di
sistema: per leggere le fatture contenute nella PEC occorre aprirla nell’app.
La registrazione non modifica l’app predefinita per le email.

## Esportazione Excel

Usa il pulsante con la tabella oppure **File → Esporta fatture in Excel…**
(**⇧⌘E**). Esporta tutte le fatture aperte in un unico `.xlsx`, con una riga per
fattura, ignorando email, ricevute e PDF allegati. Le copie con identico contenuto
contabile aperte come XML, P7M o dentro una PEC vengono esportate una volta sola;
versioni diverse con lo stesso numero restano distinte.

Il foglio contiene data, numero e tipo documento, fornitore e partita IVA,
cliente, causale (oppure descrizioni delle righe), valuta, imponibile, IVA,
totale, dettaglio per aliquota e natura IVA, esigibilità/split payment, ritenute,
bollo, contributi previdenziali, importi e scadenze dei pagamenti, IBAN e file
sorgente. Intestazione bloccata e filtri facilitano la consultazione.

Importi e date sono celle Excel utilizzabili nei calcoli; partite IVA, codici
fiscali e numeri documento rimangono testo, conservando gli zeri iniziali.
Gli importi mancanti restano vuoti. I segni degli importi delle note di credito
rimangono quelli dell’XML: non vengono invertiti automaticamente. La colonna
«Avvisi export» segnala eventuali testi abbreviati o importi conservati come
testo per superamento della precisione numerica di Excel.

## Formati

- **XML FPA12 / FPR12**: ogni `FatturaElettronicaBody` diventa un documento PDF.
  Fornitore, cliente, righe, IVA, totale e pagamenti sono impaginati; gli altri
  campi sono mantenuti nella sezione «Dati aggiuntivi del documento».
- **P7M CAdES / CMS**: estrazione locale del contenuto, anche con firme annidate;
  certificati e risultato del controllo crittografico nel pannello informazioni.
- **XML firmato XAdES / XMLDSig**: rilevamento e controllo crittografico con il
  certificato incorporato, quando algoritmi e trasformazioni sono supportati.
- **EML / PEC**: navigazione ricorsiva dei messaggi allegati, fatture XML/P7M
  estratte, PDF allegati visualizzabili e copie duplicate escluse all’interno
  dell’email. Ricevute PEC e metadati SdI non vengono scambiati per fatture.
  Le email restano selezionabili come documenti separati, salvo i messaggi
  tecnici «Invio File» dello SdI e le relative buste PEC/inoltri quando la
  fattura è stata estratta correttamente. Ricevute ed email normali restano visibili.
- **Email senza fattura**: PDF con oggetto, mittente, destinatari, data, corpo e
  nomi degli allegati. Per le email HTML si ricava il testo strutturato: il layout
  originale HTML e le immagini non vengono riprodotti.
- **PDF**: visualizzazione diretta e conservazione dei byte originali al salvataggio.

Il PDF generato è una **copia di cortesia**. Totali e importi provengono dall’XML:
non si ricalcola né si inventa il totale se assente. Il parser è un lettore,
non un validatore fiscale/XSD o un servizio di conservazione.

Tutto avviene localmente, senza rete o servizi esterni. I PDF temporanei sono
salvati in una cartella privata di sessione e rimossi all’uscita normale.
Le email HTML non eseguono codice e non scaricano immagini remote.

### Limiti attuali

Fatture semplificate FSM10, allegati compressi, buste cifrate, verifica delle
firme S/MIME delle PEC, catene di fiducia, revoca e marche temporali non sono
ancora supportati. «Integrità verificata» indica soltanto il controllo
crittografico; la data di firma mostrata è quella dichiarata dal firmatario.
Per XAdES con canonicalizzazione C14N 1.1 o trasformazioni XSLT si mostrano certificato e data,
con esito «Verifica non supportata».
Gli allegati diversi da XML/P7M/EML/PDF sono elencati nell’email, senza anteprima.

Limiti per apertura: file 64 MB, contenuti decodificati complessivi 128 MB,
256 documenti, 10.000 righe per fattura, corpo email di un milione di caratteri.
Gli errori su un allegato non impediscono di leggere gli altri documenti:
la finestra mostra un avviso con i dettagli nel tooltip.

## Core, CLI e verifiche

```sh
dotnet test LettoreFatture.slnx
dotnet run --project src/Fatture.Cli -- open esempi/IT04270090261_0002I.xml --output artifacts/pdf
```

La CLI restituisce JSON con i documenti, i percorsi PDF, i metadati e le firme.
`fatture bridge` mantiene un processo attivo e usa richieste/risposte JSON,
una per riga; vedi [architettura e scelte](docs/architecture.md).

Verifica il bundle pubblicato usando gli esempi locali:

```sh
python3 packaging/macos/verify-publish.py
python3 packaging/macos/verify-quicklook.py
swift packaging/macos/verify-pdfs.swift artifacts/verification
FATTURE_SMOKE_INPUT="$PWD/esempi/IT04270090261_0002I.xml" \
  FATTURE_SMOKE_EXPORT_PATH="$PWD/artifacts/fatture-ui.xlsx" \
  'artifacts/macos/Lettore Fatture.app/Contents/MacOS/LettoreFatture' --smoke-test
```

I test usano certificati di prova locali per firme CMS/XML, verificano la
rilevazione delle alterazioni, XML con più corpi, PEC annidate, deduplicazione,
email HTML, XML con DTD e fatture lunghe. Se `esempi/` esiste, il test di
integrazione apre tutti i suoi file; i contenuti di esempio e gli artefatti
sono esclusi da Git perché possono contenere dati personali.
I test Excel controllano deduplicazione, celle numeriche/date, identificativi
testuali, dati mancanti e limiti di precisione/lunghezza. La verifica Quick Look
prova la libreria nativa sui campioni e l’estensione nel servizio reale macOS.

Risultati e misure della build corrente: [verifiche eseguite](docs/verification.md).

Dipendenze: PDFsharp/MigraDoc e MimeKitLite (MIT), librerie crittografiche .NET.
I font Arial sono letti dal sistema e incorporati nel PDF; nessun font di sistema
viene ridistribuito nel bundle.
