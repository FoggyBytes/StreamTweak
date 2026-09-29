# StreamTweak 9.0.0 — note di consegna del redesign

Branch: `claude/ui-9.0` (separato da `main`). Documento di lavoro per la prima build su Windows:
si può cancellare prima del merge.

## Cosa c'è nel branch

| Commit | Contenuto |
|---|---|
| docs: mockup | `docs/ui-9.0-mockup.html`, il mockup navigabile di riferimento |
| 9.0: version bump | versione 9.0.0 (csproj + Installer.iss), design system `Themes/StreamTweak9.xaml`, controlli `ResponsiveGrid`, `MiniSparkline`, `GradeBar` |
| 9.0 core | per sessione: intervalli di gioco (`GameSpans`), fps di destinazione, esito dei 4 controlli del voto |
| 9.0 shell | menu raggruppato (Streaming / Host) con modalità automatiche, pillole nella barra del titolo, glossario come pannello laterale (F1) con ricerca, badge su Clients |
| 9.0 dashboard | griglia di card adattiva, hero col gioco in streaming, sparkline dei parametri host, ultime sessioni, scaffale giochi |
| 9.0 sessions | lista per giorno con filtri e riepilogo, master–detail su schermi larghi, card "Verdict", nuovo controllo `TimelineChart`, Compare ridisegnato; il bitrate target ora viene salvato nella sessione |
| 9.0 library | muro di copertine dimensionato sulla finestra, hero, statistiche per gioco dalla cronologia, filtri/ordinamento, vista lista, scheda laterale del gioco |
| 9.0 host pages, clients, settings | intestazioni 9.0, margini adattivi, card su due colonne; Clients con una card per dispositivo e PIN visibile |
| changelog | sezione 9.0.0 in `changelog.txt`, "What's New" nel README, questo file |

## Come è stato verificato (e cosa manca)

Il compilatore XAML di WinUI non gira su Linux, quindi qui ho usato un harness mio:

- **XAML**: parsing di tutti i file, controllo di stili/risorse/converter referenziati, tipi e proprietà
  usati nei binding `x:Bind` (generando il codice dei binding), gestori di evento esistenti nel code-behind.
- **C#**: compilazione completa di `StreamTweakUI` (con il codice generato dei binding) contro le reference
  di Windows App SDK, più `StreamTweak.Core`.

Tutto passa senza errori, ma **l'app non è mai stata eseguita**: la prima build vera e il primo avvio sono
sul tuo PC. Se il compilatore XAML di Visual Studio segnala qualcosa che l'harness non ha visto, di solito
è uno di questi punti (li ho scritti apposta in modo standard, ma sono i più "nuovi"):

- `ctrl:ResponsiveGrid` usato come `ItemsPanel` di un `ItemsControl` (ClientsView);
- `MenuFlyoutItem` con `Tag="{x:Bind Id}"` dentro un `ContextFlyout` di un DataTemplate (LogsView);
- `ProgressBar Maximum="{x:Bind Max}"` con proprietà `int` (GameLibraryView, barre per store);
- `ItemsWrapGrid` con `ItemWidth/ItemHeight` impostati da code-behind (GameLibraryView).

## Build

```
git fetch origin
git checkout claude/ui-9.0
dotnet build StreamTweak.sln -c Release -p:Platform=x64
```

(oppure da Visual Studio come al solito).

## Checklist di prova

Conviene provare a tre larghezze: **finestra minima (800×560)**, **1280×800** (tipo handheld a 1080p con
scala 150%) e **massimizzata su 4K**. Per avere dati "vivi" senza uno stream vero: *Settings → Maintenance →
Debug Mode* (crea una sessione finta di 30 minuti con due giochi e serie con qualche picco).

**Shell**
- [ ] Il menu passa da esteso → icone → pulsante al restringersi (soglie 1100 / 760 px).
- [ ] Barra del titolo: pillola stato (Idle / gioco + timer in streaming), velocità link, IP Tailscale.
- [ ] F1 e le ⓘ aprono il glossario a destra, sul termine giusto; la ricerca filtra; clic fuori o ✕ chiude.
- [ ] Badge su Clients quando un dispositivo è in attesa.

**Dashboard**
- [ ] A riposo: hero "Host monitor" con le mini-sparkline; in Debug Mode: copertina del gioco, sfondo sfocato, timer, pulsante "Return to client".
- [ ] Le card si ridispongono a 1, 2, 3, 4 e 6 colonne allargando la finestra.
- [ ] "Open session ›", le righe delle ultime sessioni e lo scaffale portano alla sessione / al gioco giusto.

**Sessions**
- [ ] Pagina (esclusa la barra del menu) sotto 1400 px: lista sola, la sessione si apre sopra con freccia indietro (ed Esc).
- [ ] Da 1400 px: lista a sinistra e sessione a destra; la prima sessione è già selezionata.
- [ ] Filtri per gioco e per voto, periodo, riepilogo in alto, raggruppamento per giorno.
- [ ] Verdict: i 4 controlli con soglie; nota per sessioni vecchie senza fps ("60 fps assumed").
- [ ] Timeline: crosshair che legge tutte le corsie, zoom trascinando sulle corsie e sulla striscia sotto, Ctrl+rotella, doppio clic per resettare, "Whole session", clic su un gioco nella corsia in alto.
- [ ] Focus: scelta della serie, soglie tratteggiate, percentili; nota sul frame budget.
- [ ] Spunta due sessioni → barra Compare → confronto; "Compare with…" dal dettaglio; menu contestuale (tasto destro) su una riga.
- [ ] Elimina sessione chiede conferma; "Clear history…" dal menu ⋯.

**Library**
- [ ] Le copertine riempiono la riga senza bordo destro frastagliato; 2 colonne su finestra stretta.
- [ ] Hover su una copertina: Play / cartella / dettagli; clic apre la scheda laterale.
- [ ] Scheda: gioca, cartella, statistiche, interruttore "Show in Sunshine", sessioni recenti (clic → Sessions), Remove con conferma.
- [ ] Ricerca, chip per store e "Hidden from …", ordinamenti, vista lista.
- [ ] Card sync (toggle, conteggi, barre per store) e card host tiles (anteprime `Resources/desktop.png` e `steam.png`).
- [ ] Libreria grande: scorrimento fluido e memoria contenuta (le copertine si caricano solo quando visibili).

**Host / Clients / Settings**
- [ ] Network, Display & audio, NVIDIA Sentinel, Managed apps: tutto funziona come in 8.7; su schermo largo le card vanno su due colonne.
- [ ] Clients: card per dispositivo, PIN sui dispositivi in attesa, Approve / Revoke.
- [ ] Settings: card su due colonne da ~1150 px; Debug Mode, log, cancellazione cronologia.

## Limiti noti e cose da decidere

- **Zoom touch della timeline**: col dito si zooma sulla striscia sotto le corsie; sulle corsie un tocco legge i valori (trascinare lì farebbe conflitto con lo scorrimento della pagina).
- **Solo tema scuro**, come deciso nel mockup.
- Le sessioni registrate prima della 9.0 non hanno gli orari dei giochi: la corsia in alto mostra le copertine con "times not recorded"; non hanno il bitrate target: niente linea tratteggiata.
- Le pagine Host sono state **restilizzate**, non riscritte: stessi controlli dell'8.x dentro la nuova impaginazione. Se vuoi portarle fino in fondo al mockup (es. la "scala" delle velocità del link) è un passo successivo.
- Il controllo `SparklineControl` resta usato dalla card Performance della Dashboard; il grafico a schermo intero delle sessioni è stato sostituito dalla modalità Focus.
- La data della 9.0.0 nel changelog è quella di oggi: da aggiornare al rilascio.
