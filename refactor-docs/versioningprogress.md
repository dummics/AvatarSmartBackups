# Versioning & Restore Roadmap
**Last update:** 2025-09-18

## Goals
- Unificare la finestra di Restore con l'attuale Show Changes mantenendo layout compatto, badge e highlight.
- Rendere il flusso di backup/restore chiaro per utenti Easy mode e completo in Advanced.
- Preparare il terreno per ottimizzazioni future (diff in background, move tracking, repository locale).

## TODO (ordine operativo)
## TODO (ordine operativo)
1. **Analisi UI & architettura**
   - [x] Mappare le funzionalità core di `RestorePreviewWindow` e `VersionChangesWindow`.
   - [x] Definire layout unificato (lista + pannello azioni/info) e scheletro dati condiviso.
2. **Finestra unificata Restore+Changes**
   - [x] Portare nella nuova finestra i componenti migliori di Show Changes (badge, ricerca, toggle filename, highlight).
   - [x] Integrare controlli di Restore: grouping per categorie, filtri extra, top extensions, selezione e pulsanti Restore/Restore as copy.
   - [x] Aggiornare localizzazioni e entry point (tab Versions, pulsante "Preview & Restore").
3. **Fix immediati**
   - [x] Pulsante "Open" dell'EditorWindow deve sempre aprire la tab Backup.
   - [x] Sistemare corruzione titolo/nome di backup nelle version card.
   - [x] Spostare throughput/benchmark e toggle debug sotto debug mode.
4. **Rework Advanced Settings**
   - [x] Ridisegnare sezione performance/versioning con nuova "Versioning policy" e benchmark contestualizzato.
   - [x] Snellire strumenti rari dietro fold "Debug tools".
5. **Easy vs Advanced Mode**
   - [x] Allineare Easy mode alla nuova UI unificata (solo azioni essenziali).
   - [x] Verificare onboarding forzato e possibilità di switch con feedback.
   - [x] Aggiornare copy/tooltips per linguaggio semplice.
   - [x] Correggere encoding/localizzazione italiana per messaggi Easy/Advanced.
6. **Performance & backend (fase successiva)**
   - [ ] Progettare diff loader asincrono (scan in background + cache).
   - [ ] Prototipare tracking rename/move (hash + metadata).
   - [ ] Studi preliminari su repository locale (DB degli snapshot/commit).
7. **QA & Docs**
   - [ ] Aggiornare checklist test (backup incrementale, restore, easy/advanced).
   - [ ] Aggiornare documentazione utenti e changelog.
