# Versioning progress & TODO
**Status:** Work-in-progress — implementazioni incrementali.  
**Note importanti:** NON rimuovere manifest_v2 in massa; nessun cleanup legacy automatico in questa fase. Le modifiche DEVONO essere non invasive.

## 1. Obiettivo
- Rendere il restore incrementale **chiaro e affidabile** per utenti non tecnici.
- Mantenere UI semplice di default; esporre opzioni avanzate solo in Advanced Mode.
- Backend: supportare ricostruzione automatica dello stato completo a partire da checkpoint + delta.

## 2. Priorità (ordine di lavoro)
1. Etichette/version metadata: marcare ogni Version come `Checkpoint` (completo) o `Incrementale` e mostrare chiaramente il tipo in UI.
2. Anteprima dei cambiamenti: per ogni Version mostrare la lista (o sintesi) dei file modificati.
3. Restore automatico: quando si restore una versione incrementale, il sistema deve individuare il checkpoint completo più vicino e applicare i delta in ordine, ricostruendo lo stato completo (back-end).
4. Funzione "Ricostruisci snapshot" (opzionale UI/Advanced): genera uno snapshot completo temporaneo per ispezionare i file.
5. Checkpoint periodici: opzione Advanced “forza checkpoint ogni N incrementali”.
6. Miglioramenti UI: wording chiaro (“Incrementale: N file modificati”), icone distintive, tooltip semplici.
7. Rebuild versions index (Advanced tool, non automatico): ricalcola fileCount/size leggendo cartelle Versions esistenti.
8. Testing & checklist: definire e automatizzare (se possibile) i test manuali elencati sotto.

## 3. Dettaglio task (tecnico + acceptance criteria)
### 3.1 Etichette e UI
- Task: aggiungere flag `isCheckpoint`/`type` in model Version (o usare esistente) e mostrare in UI come “Checkpoint” / “Incrementale”.
- Acceptance: nella tab Versions ogni voce mostra `Tipo: Checkpoint` o `Tipo: Incrementale`. Icona differente e testo esplicativo.

### 3.2 Anteprima dei cambiamenti (diff per versione)
- Task: salvare o calcolare (da manifest) la lista dei file effettivamente cambiati per ogni Version; aggiungere pulsante “Mostra file modificati”.
- Acceptance: clic su “Mostra file modificati” apre lista/tooltip con file e categoria (Controller / Anim / Material / Other); per versioni con molti file mostra conteggio per categoria + “Mostra dettaglio”.

### 3.3 Restore incrementale (reconstruction)
- Task: implementare flusso che, dato il target Version V:
  - individua l’ultimo checkpoint completo C ≤ V,
  - calcola applicazione dei delta da C a V,
  - applica gli asset in ordine per ricostruire lo stato completo in una cartella temporanea o direttamente (opzione).
- Acceptance: restore di V produce lo stesso stato che l’utente si aspetta senza che debba applicare manualmente i delta; ci sono messaggi chiari in UI se la ricostruzione richiede più tempo.

### 3.4 Ricostruisci snapshot (Advanced)
- Task: comando che crea una copia temporanea combinando checkpoint + delta per ispezione manuale.
- Acceptance: snapshot è creato in `Temp/ASB_Rebuilds/<version>` e UI fornisce link “Apri cartella temporanea”.

### 3.5 Checkpoint periodici (Advanced setting)
- Task: aggiungere setting `forceFullCheckpointEveryN` (int) e logica per eseguire checkpoint completo dopo N incrementali.
- Acceptance: parametro applicabile e testabile; non attivo di default.

### 3.6 Rebuild versions index (Advanced tool)
- Task: creare un tool (Advanced) che ricalcoli fileCount/size per tutte le versioni esistenti; mostra report. Non lanciare in automatico, richiede conferma.
- Acceptance: report delle modifiche e aggiornamento del record solo dopo conferma.

### 3.7 Messaggi / UX
- Task: quando una versione è incrementale mostra “Incrementale: N file modificati (ultimo checkpoint: yyyy-mm-dd)”.
- Task: per restore, mostra banner che spiega cosa succederà (ricostruzione da checkpoint o restore diretto se checkpoint).
- Acceptance: linguaggio semplice, nessun termine tecnico nella vista base.

## 4. Files da modificare (suggerimento)
- `Editor/Windows/AvatarSmartBackupWindow.cs` — viste e label.
- `Editor/Windows/RestorePreviewWindow.cs` — show-only-tracked toggle + preview file list + restore-as-copy.
- `Editor/Backup/FileBasedVersionManager.cs` (o equivalente) — logica reconstruct/apply del delta.
- `Editor/Backup/BackupManager.cs` — hook checkpoint forzato / policy.
- `Editor/Backup/FileScanner.cs` — assistenza per categorizzazione (controller/anim/material).
- `BackupSettings` — nuove opzioni Advanced: `forceFullCheckpointEveryN`, `showRebuildTool`, etc.
> Nota: lascia l’architettura principale intatta; preferisci aggiungere helper e feature toggle piuttosto che riscrivere.

## 5. Testing checklist (minima, manuale)
1. Backup automatico: crea X backup incrementali e verifica che la timeline li mostri come "Incrementale".
2. Backup manuale (Backup Now): genera checkpoint completo (quando richiesto) e verifica flag Checkpoint.
3. Verify mismatch: simula mismatch su un file (es. corrompi file in Current/) e verifica che Verify segnali l’errore e suggerisca restore.
4. Restore single asset as copy: da versione incrementale, usa “Ripristina come copia” e verifica che la cartella temporanea contenga il file atteso.
5. Export checkpoint zip: esporta checkpoint dalla tab Versions e verifica ZIP apribile e contenuto coerente.

## 6. Non fare (limiti in questa fase)
- NON lanciare cleanup legacy automatico che rimuove manifest_v2 o altri file storici.  
- NON eseguire rimozioni massicce di file in vecchie Versions.  
- NON considerare ottimizzazioni di spazio avanzate fino a quando i flussi di restore non sono stabili.

## 7. Deliverables & PR
- Nuovo file `refactor-docs/versioningprogress.md` (con questo contenuto).  
- Eliminazione / consolidamento di eventuali file TODO esistenti (vedi istruzione 2).  
- Branch: `feature/versioning-improvements`.  
- Commit message template: `feat(versioning): add incremental-restore UX + versioningprogress doc`  
- PR description: breve sommario + link a `refactor-docs/versioningprogress.md` + checklist di test.

## 8. Liberalità decisionale per l’agente
- Sei libero di scegliere implementazione tecnica (persistenza, caching, temp dir, ordine applicazione delta), purché:
  - il flusso utente sia chiaro come descritto sopra,
  - le modifiche siano incrementali e non invasive,
  - i test manuali della checklist passino.

---

Appendice (note migrate)

Da `VersioningProgress.md` (root):
- Stato attuale: schema esteso, migrazione, size/fileCount, pin/descrizione API pronte; Tab Versions integrato parzialmente (UI base funziona).
- Gap: diff per versione (added/changed/removed) mancante; cleanup non pinned-aware; restore da versione non integrato nel flusso “Preview & Restore”; residui SQLite marginali (verifica finale).
- Direzione UX: Tab Versions integrato, lista compatta con badge (pinned, incomplete), diff lazy, restore full/selettivo con safety snapshot, azioni rapide; hash opzionali on-demand.
- Principi: nessun breaking change API pubbliche; salvataggi atomici; operazioni lunghe in background; fallback robusto se index corrotto (rebuild).
- TODO sintetica (storico): cleanup residui SQLite, DiffService base, RestoreService skeleton, integrazione Tab Versions nella finestra principale, azioni extra (delete/open/crea manual), diff UI + restore selettivo, settings cleanup policy, hash deferred + cache manifest/diff, diagnostics + logging, onboarding + feedback, documentazione & QA.

Da `Assets/AvatarSmartBackups/VersioningProgress.md`:
- Core Done: file-based versions + atomic index; metadata estese (guid, size, fileCount, pinned, incomplete); auto-create versione dopo backup; pin/rename/integrity checks; finestra unificata + diff + restore selettivo (grouping, filters); MD5 manifest compare.
- Pending UI/Features: delete version da UI; export/import zip; cleanup policy con pin-respect.
- Performance roadmap: HashCache in restore (enable) + opzionale xxHash64; rimuovere enumeration completa in diff; hash precompute in idle; UI virtualization >5k files.
- Dedup roadmap (Git-lite): oggetti condivisi opzionali, dedupe retroattivo, delta per grandi YAML/testo (bassa priorità), GC oggetti non usati.
- Design: versioni lineari; preferenza hash xxHash64 mantenendo MD5 legacy; formato riferimento `ASB_REF:<hash>`.
- Diagnostics (planned): rebuild index, hash store scan + GC, log viewer shortcut.
- Next steps (storico): re-enable HashCache in restore; aggiungere cleanup policy UI; introdurre flag dedup sperimentale.