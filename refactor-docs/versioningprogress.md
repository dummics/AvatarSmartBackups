# Versioning & Restore Roadmap
**Last update:** 2025-09-17

## Goals
- Unificare la finestra di Restore con l'attuale Show Changes mantenendo layout compatto, badge e highlight.
- Rendere il flusso di backup/restore chiaro per utenti Easy mode e completo in Advanced.
- Preparare il terreno per ottimizzazioni future (diff in background, move tracking, repository locale).

## TODO (ordine operativo)
1. **Analisi UI & architettura**  
   - Mappare le funzionalità core di `RestorePreviewWindow` e `VersionChangesWindow`.  
   - Definire layout unificato (lista + pannello azioni/info) e scheletro dati condiviso.
2. **Finestra unificata Restore+Changes**  
   - Portare nella nuova finestra i componenti migliori di Show Changes (badge, ricerca, toggle filename, highlight).  
   - Integrare controlli di Restore: grouping per categorie, filtri extra, top extensions, selezione e pulsanti Restore/Restore as copy.  
   - Aggiornare localizzazioni e entry point (tab Versions, pulsante "Preview & Restore").
3. **Fix immediati**  
   - Pulsante "Open" dell'EditorWindow deve sempre aprire la tab Backup.  
   - Sistemare corruzione titolo/nome di backup nelle version card.  
   - Spostare throughput/benchmark e toggle debug sotto debug mode.
4. **Rework Advanced Settings**  
   - Ridisegnare sezione performance/versioning: throughput solo in debug, benchmark funzionante (o disabilitato con messaggio), rimozione snapshot policy legacy e sostituzione con "Versioning policy" moderna (checkpoint automatici).  
   - Snellire strumenti rari dietro fold “Debug tools”.
5. **Easy vs Advanced Mode**  
   - Allineare Easy mode alla nuova UI unificata (solo azioni essenziali).  
   - Verificare onboarding forzato e possibilità di switch con feedback.  
   - Aggiornare copy/tooltips per linguaggio semplice.
6. **Performance & backend (fase successiva)**  
   - Progettare diff loader asincrono (scan in background + cache).  
   - Prototipare tracking rename/move (hash + metadata).  
   - Studi preliminari su repository locale (DB degli snapshot/commit).
7. **QA & Docs**  
   - Aggiornare checklist test (backup incrementale, restore, easy/advanced).  
   - Aggiornare documentazione utenti e changelog.

## Note
- Tutti i cleanup legacy (manifest_v2 ecc.) restano fuori scope.  
- Preferire incrementi non invasivi; ogni passo deve mantenere restore funzionante.

## Parking Lot / Idee
- Diff sempre pronto: scanner in idle con update continuo tipo Git UI.  
- Move detection per ripristini intelligenti.  
- Backend locale stile VCS con mapping hash → file, per features avanzate future.
